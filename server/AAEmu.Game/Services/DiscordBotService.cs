using System.Drawing;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Chat;
using AAEmu.Game.Models.Game.Crime;
using AAEmu.Game.Models.Game.Housing;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Mails;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.StaticValues;
using AAEmu.Game.Models.Tasks;
using Task = System.Threading.Tasks.Task;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;

namespace AAEmu.Game.Services;

/// <summary>
/// AAEmu <-> Node.js Discord bridge.
/// AAEmu posts game events to Node, and also polls Node for safe localhost-only
/// Discord -> game commands so no additional inbound AAEmu port is required.
/// </summary>
public class DiscordBotService : IHostedService
{
    private static readonly HttpClient Http = new();

    public static DiscordBotService Instance { get; private set; }

    private readonly string _bridgeUrl;
    private readonly string _secret;
    private CancellationTokenSource _pollCts;
    private Task _pollTask;
    private Task _heartbeatTask;
    private Task _housingTaxTask;
    private Task _mailNotificationTask;
    private Task _progressionNotificationTask;
    private readonly Dictionary<uint, ProgressionSnapshot> _knownProgression = [];
    private static readonly int[] CurrencyMilestones =
        [1_000, 5_000, 10_000, 25_000, 50_000, 100_000, 250_000, 500_000, 1_000_000];
    private readonly HashSet<string> _knownDeliveredMailKeys = [];
    private bool _mailNotificationInitialized;
    private static readonly object LoginRestartLock = new();
    private static CancellationTokenSource _loginRestartCts;
    private static DateTime _loginRestartAt;
    private static string _loginRestartReason = string.Empty;
    private static string _loginRestartRequestedBy = string.Empty;
    private readonly HashSet<uint> _knownPrisoners = [];
    private bool _justicePresenceInitialized;
    private static readonly string ServerBuild =
        typeof(DiscordBotService).Assembly.GetName().Version?.ToString() ?? "Unknown";

    public DiscordBotService()
    {
        Instance = this;

        _bridgeUrl = (
            Environment.GetEnvironmentVariable("AAEMU_DISCORD_BRIDGE_URL")
            ?? "http://127.0.0.1:3010"
        ).TrimEnd('/');

        _secret =
            Environment.GetEnvironmentVariable("AAEMU_DISCORD_BRIDGE_SECRET")
            ?? string.Empty;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_secret))
            return Task.CompletedTask;

        _ = PostEventAsync(new
        {
            type = "aaemu_started",
            serverStartedAt = IsoUtc(DateTime.UtcNow.AddSeconds(-Program.UpTime)),
            serverUptimeSeconds = Program.UpTime,
            serverBuild = ServerBuild
        });

        _pollCts = new CancellationTokenSource();
        _pollTask = Task.Run(() => PollCommandsAsync(_pollCts.Token));
        _heartbeatTask = Task.Run(() => HeartbeatLoopAsync(_pollCts.Token));
        _housingTaxTask = Task.Run(() => HousingTaxLoopAsync(_pollCts.Token));
        InitializeMailNotificationWatcher();
        _mailNotificationTask = Task.Run(() => MailNotificationLoopAsync(_pollCts.Token));
        _progressionNotificationTask = Task.Run(() => ProgressionNotificationLoopAsync(_pollCts.Token));

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_pollCts == null)
            return;

        // Give Discord an immediate clean shutdown signal. If Node is unavailable,
        // never let this hold AAEmu shutdown for more than two seconds. A hard crash
        // is still detected by Node through the heartbeat timeout.
        try
        {
            await PostEventAsync(new { type = "aaemu_stopping" })
                .WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (TimeoutException)
        {
            // Continue shutdown.
        }

        _pollCts.Cancel();

        foreach (var task in new[] { _pollTask, _heartbeatTask, _housingTaxTask, _mailNotificationTask, _progressionNotificationTask })
        {
            if (task == null)
                continue;

            try
            {
                await task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown.
            }
        }

        _pollCts.Dispose();
        _pollCts = null;
        _pollTask = null;
        _heartbeatTask = null;
        _housingTaxTask = null;
        _mailNotificationTask = null;
        _progressionNotificationTask = null;
    }

    private sealed record ProgressionSnapshot(
        int CharacterLevel,
        Dictionary<AbilityType, int> SkillsetLevels,
        HashSet<uint> AppellationIds,
        int HonorPoints,
        int VocationPoints);

    private sealed record PortalRankingRow(
        uint CharacterId,
        string CharacterName,
        int Level,
        int HonorPoints,
        string GuildName,
        string FactionName);

    private static List<PortalRankingRow> LoadPortalRankings()
    {
        var rows = new List<PortalRankingRow>();
        using var connection = MySQL.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                c.id,
                c.name,
                c.level,
                c.honor_point,
                c.faction_name,
                COALESCE(e.name, '') AS guild_name
            FROM characters c
            LEFT JOIN expeditions e ON e.id = c.expedition_id
            WHERE c.deleted = 0
            ORDER BY c.honor_point DESC, c.level DESC, c.name ASC
            LIMIT 100
            """;

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new PortalRankingRow(
                reader.GetUInt32("id"),
                reader.GetString("name"),
                reader.GetByte("level"),
                reader.GetInt32("honor_point"),
                reader.GetString("guild_name"),
                reader.GetString("faction_name")));
        }

        return rows;
    }

    /// <summary>
    /// Read-only progression watcher. First sight of a character establishes a
    /// baseline, so AAEmu/Node restarts never replay historical level gains.
    /// </summary>
    private async Task ProgressionNotificationLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var onlineCharacters = WorldManager.Instance.GetAllCharacters()
                    .Where(c => c != null && c.IsOnline)
                    .ToArray();

                foreach (var character in onlineCharacters)
                {
                    var skillsetLevels = character.Abilities.Values.ToDictionary(
                        ability => ability.Id,
                        ability => (int)character.Abilities.GetAbilityLevel(ability.Id));
                    var appellationIds = character.Appellations.Appellations.ToHashSet();
                    var current = new ProgressionSnapshot(
                        character.Level,
                        skillsetLevels,
                        appellationIds,
                        character.HonorPoint,
                        character.VocationPoint);

                    if (_knownProgression.TryGetValue(character.Id, out var previous))
                    {
                        if (current.CharacterLevel > previous.CharacterLevel)
                        {
                            await PostEventAsync(new
                            {
                                type = "character_level_up",
                                accountId = character.AccountId,
                                characterId = character.Id,
                                characterName = character.Name,
                                oldLevel = previous.CharacterLevel,
                                newLevel = current.CharacterLevel
                            });
                        }

                        foreach (var (skillsetId, newLevel) in current.SkillsetLevels)
                        {
                            if (!previous.SkillsetLevels.TryGetValue(skillsetId, out var oldLevel) || newLevel <= oldLevel)
                                continue;

                            await PostEventAsync(new
                            {
                                type = "skillset_level_up",
                                accountId = character.AccountId,
                                characterId = character.Id,
                                characterName = character.Name,
                                skillsetId = (byte)skillsetId,
                                skillsetInternalName = skillsetId.ToString(),
                                oldLevel,
                                newLevel
                            });
                        }

                        foreach (var appellationId in current.AppellationIds.Except(previous.AppellationIds))
                        {
                            await PostEventAsync(new
                            {
                                type = "title_earned",
                                accountId = character.AccountId,
                                characterId = character.Id,
                                characterName = character.Name,
                                titleId = appellationId,
                                titleName = ResolveAppellationName(appellationId)
                            });
                        }

                        var honorMilestone = HighestCrossedMilestone(previous.HonorPoints, current.HonorPoints);
                        if (honorMilestone > 0)
                        {
                            await PostEventAsync(new
                            {
                                type = "honor_milestone",
                                accountId = character.AccountId,
                                characterId = character.Id,
                                characterName = character.Name,
                                previousAmount = previous.HonorPoints,
                                currentAmount = current.HonorPoints,
                                milestone = honorMilestone
                            });
                        }

                        var vocationMilestone = HighestCrossedMilestone(previous.VocationPoints, current.VocationPoints);
                        if (vocationMilestone > 0)
                        {
                            await PostEventAsync(new
                            {
                                type = "vocation_milestone",
                                accountId = character.AccountId,
                                characterId = character.Id,
                                characterName = character.Name,
                                previousAmount = previous.VocationPoints,
                                currentAmount = current.VocationPoints,
                                milestone = vocationMilestone
                            });
                        }
                    }

                    _knownProgression[character.Id] = current;
                }

                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Logger.Warn($"Discord progression watcher failed: {ex.Message}");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private static int HighestCrossedMilestone(int previousAmount, int currentAmount)
    {
        if (currentAmount <= previousAmount)
            return 0;

        return CurrencyMilestones
            .Where(milestone => milestone > previousAmount && milestone <= currentAmount)
            .DefaultIfEmpty(0)
            .Max();
    }

    private static string ResolveAppellationName(uint appellationId)
    {
        try
        {
            var dbPath = FindCompactDbPath();
            if (string.IsNullOrWhiteSpace(dbPath))
                return $"Title {appellationId}";

            using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT name FROM appellations WHERE id = @id LIMIT 1";
            command.Parameters.AddWithValue("@id", appellationId);
            var rawName = command.ExecuteScalar()?.ToString() ?? string.Empty;
            var localized = LocalizationManager.Instance.Get("appellations", "name", appellationId, rawName) ?? rawName;
            return string.IsNullOrWhiteSpace(localized) ? $"Title {appellationId}" : localized;
        }
        catch (Exception ex)
        {
            Logger.Debug($"Discord title name lookup failed for {appellationId}: {ex.Message}");
            return $"Title {appellationId}";
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var portalRankings = LoadPortalRankings();
                var savedPortalCharacters = portalRankings.ToDictionary(row => row.CharacterId);
                var onlineCharacters = WorldManager.Instance.GetAllCharacters()
                    .Where(c => c != null && c.IsOnline)
                    .Select(c => new
                    {
                        accountId = c.AccountId,
                        characterId = c.Id,
                        characterName = c.Name,
                        level = (int)c.Level,
                        honor = c.HonorPoint,
                        guildName = savedPortalCharacters.GetValueOrDefault(c.Id)?.GuildName ?? string.Empty,
                        factionName = c.Faction?.Name ??
                                      savedPortalCharacters.GetValueOrDefault(c.Id)?.FactionName ?? string.Empty
                    })
                    .ToArray();

                // Live honor and level win over the last database save. This makes the
                // public ranking update immediately while a character is online.
                var liveById = onlineCharacters.ToDictionary(character => character.characterId);
                var rankings = portalRankings
                    .Select(row => liveById.TryGetValue(row.CharacterId, out var live)
                        ? new
                        {
                            characterId = row.CharacterId,
                            name = live.characterName,
                            level = live.level,
                            honor = live.honor,
                            guildName = row.GuildName,
                            factionName = live.factionName
                        }
                        : new
                        {
                            characterId = row.CharacterId,
                            name = row.CharacterName,
                            level = row.Level,
                            honor = row.HonorPoints,
                            guildName = row.GuildName,
                            factionName = row.FactionName
                        })
                    .OrderByDescending(row => row.honor)
                    .ThenByDescending(row => row.level)
                    .ThenBy(row => row.name)
                    .Take(100)
                    .Select((row, index) => new
                    {
                        rank = index + 1,
                        row.characterId,
                        row.name,
                        row.level,
                        row.honor,
                        row.guildName,
                        row.factionName
                    })
                    .ToArray();

                await PostEventAsync(new
                {
                    type = "aaemu_heartbeat",
                    playerCount = onlineCharacters.Length,
                    onlineCharacters,
                    rankings,
                    serverStartedAt = IsoUtc(DateTime.UtcNow.AddSeconds(-Program.UpTime)),
                    serverUptimeSeconds = Program.UpTime,
                    serverBuild = ServerBuild
                });

                // Read-only justice transition tracking. Prisoner buffs expire in the normal
                // AAEmu buff system, so comparing online prisoner state lets Discord record
                // a release without modifying or extending the sentence.
                var onlineCharacterObjects = WorldManager.Instance.GetAllCharacters()
                    .Where(c => c != null && c.IsOnline)
                    .ToArray();
                var currentPrisoners = onlineCharacterObjects
                    .Where(c => c.Buffs.CheckBuffTag((uint)BuffConstants.TagPrisoner))
                    .Select(c => c.Id)
                    .ToHashSet();

                if (_justicePresenceInitialized)
                {
                    foreach (var releasedId in _knownPrisoners.Where(id => !currentPrisoners.Contains(id)).ToArray())
                    {
                        var released = onlineCharacterObjects.FirstOrDefault(c => c.Id == releasedId);
                        if (released == null)
                            continue; // Offline is not the same as released.

                        await PostEventAsync(new
                        {
                            type = "justice_event",
                            eventType = "jail_released",
                            characterId = released.Id,
                            characterName = released.Name,
                            accountId = released.AccountId,
                            side = released.Faction?.MotherId == FactionsEnum.HaranyaAlliance ? "haranya" :
                                   released.Faction?.MotherId == FactionsEnum.NuiaAlliance ? "nuia" : "unknown"
                        });
                    }
                }
                else
                {
                    _justicePresenceInitialized = true;
                }

                _knownPrisoners.Clear();
                foreach (var id in currentPrisoners)
                    _knownPrisoners.Add(id);

                await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Logger.Warn($"Discord heartbeat failed: {ex.Message}");

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task HousingTaxLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await PostHousingTaxSnapshotAsync();
                await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Logger.Warn($"Discord housing tax snapshot failed: {ex.Message}");

                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private BaseMail[] GetMailNotificationSnapshot()
    {
        try
        {
            var mails = MailManager.Instance?.AllPlayerMails;
            return mails == null ? [] : mails.Values.ToArray();
        }
        catch (Exception ex)
        {
            Logger.Debug($"Discord mail snapshot deferred: {ex.Message}");
            return [];
        }
    }

    private static string MailNotificationKey(BaseMail mail)
    {
        return $"{mail.Id}:{mail.Header.ReceiverId}:{mail.Body.SendDate.Ticks}";
    }

    private void InitializeMailNotificationWatcher()
    {
        if (_mailNotificationInitialized)
            return;

        var snapshot = GetMailNotificationSnapshot();
        if (MailManager.Instance?.AllPlayerMails == null)
            return;

        var now = DateTime.UtcNow;
        foreach (var mail in snapshot)
        {
            if (mail?.Body != null && mail.Body.RecvDate <= now)
                _knownDeliveredMailKeys.Add(MailNotificationKey(mail));
        }
        _mailNotificationInitialized = true;
        Logger.Info($"Discord mail notifications initialized with {_knownDeliveredMailKeys.Count} existing delivered mail(s) suppressed.");
    }

    private async Task MailNotificationLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                InitializeMailNotificationWatcher();
                if (_mailNotificationInitialized)
                {
                    var now = DateTime.UtcNow;
                    foreach (var mail in GetMailNotificationSnapshot())
                    {
                        if (mail?.Header == null || mail.Body == null || mail.Body.RecvDate > now)
                            continue;

                        var key = MailNotificationKey(mail);
                        if (_knownDeliveredMailKeys.Contains(key))
                            continue;

                        if (!TryResolveMailRecipient(mail.Header.ReceiverId, out var accountId, out var receiverName))
                            continue;

                        _knownDeliveredMailKeys.Add(key);

                        await PostEventAsync(new
                        {
                            type = "mail_received",
                            accountId,
                            mailId = mail.Id,
                            mailType = (byte)mail.MailType,
                            mailTypeName = mail.MailType.ToString(),
                            mailStatus = mail.Header.Status.ToString(),
                            receiverCharacterId = mail.Header.ReceiverId,
                            receiverCharacterName = receiverName,
                            senderName = mail.Header.SenderName ?? string.Empty,
                            title = mail.Title ?? string.Empty,
                            attachmentCount = mail.Header.Attachments,
                            itemAttachmentCount = mail.Body.Attachments?.Count ?? 0,
                            attachedCopper = mail.Body.CopperCoins,
                            hasPaymentDue = mail.Header.Status == MailStatus.Unpaid,
                            paymentAmount = mail.Body.BillingAmount,
                            receivedAt = IsoUtc(mail.Body.RecvDate)
                        });
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Logger.Warn($"Discord mail notification watcher failed: {ex.Message}");
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            }
        }
    }

    private static bool TryResolveMailRecipient(uint characterId, out uint accountId, out string characterName)
    {
        accountId = 0;
        characterName = string.Empty;

        var character = WorldManager.Instance.GetCharacterById(characterId);
        if (character != null)
        {
            accountId = character.AccountId;
            characterName = character.Name ?? string.Empty;
            if (accountId > 0)
                return true;
        }

        try
        {
            using var connection = MySQL.CreateConnection();
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT account_id, name
FROM characters
WHERE id = @character_id
  AND deleted = 0
LIMIT 1";
            command.Parameters.AddWithValue("@character_id", characterId);
            command.Prepare();
            using var reader = command.ExecuteReader();
            if (!reader.Read())
                return false;
            accountId = reader.GetUInt32("account_id");
            characterName = reader.GetString("name");
            return accountId > 0;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Discord could not resolve mail recipient {characterId}: {ex.Message}");
            return false;
        }
    }

    private async Task PostHousingTaxSnapshotAsync()
    {
        var properties = new List<object>();
        var houses = HousingManager.Instance?.GetOwnedHousesSnapshot() ?? Array.Empty<House>();

        foreach (var house in houses)
        {
            try
            {
                var taxAmount = 0;
                if (house.Template != null)
                {
                    HousingManager.Instance.CalculateBuildingTaxInfo(
                        house.AccountId,
                        house.Template,
                        false,
                        out taxAmount,
                        out _,
                        out _,
                        out _,
                        out _
                    );
                }

                var x = house.Transform.World.Position.X;
                var y = house.Transform.World.Position.Y;
                var z = house.Transform.World.Position.Z;
                uint zoneKey = 0;
                uint zoneGroupId = 0;
                var zoneName = string.Empty;
                try
                {
                    if (house.ParentWorld?.Template != null)
                    {
                        zoneKey = WorldManager.Instance.GetZoneId(house.ParentWorld.Template, x, y);
                        var zone = ZoneManager.Instance.GetZoneByKey(zoneKey);
                        zoneGroupId = zone?.GroupId ?? 0;
                        zoneName = zoneGroupId > 0
                            ? ZoneManager.Instance.GetZoneGroupById(zoneGroupId)?.Name ?? zone?.Name ?? string.Empty
                            : zone?.Name ?? string.Empty;
                    }
                }
                catch { }

                var guild = house.Expedition;
                properties.Add(new
                {
                    propertyId = house.Id,
                    accountId = house.AccountId,
                    ownerCharacterId = house.OwnerId,
                    ownerCharacterName = NameManager.Instance.GetCharacterName(house.OwnerId) ?? string.Empty,
                    propertyName = house.Name ?? string.Empty,
                    templateId = house.TemplateId,
                    templateName = house.Template?.Name ?? string.Empty,
                    permission = house.Permission.ToString(),
                    guildId = guild?.Id ?? 0,
                    guildName = guild?.Name ?? string.Empty,
                    x,
                    y,
                    z,
                    zoneKey,
                    zoneGroupId,
                    zoneName,
                    gardenRadius = house.Template?.GardenRadius ?? 0f,
                    heavyTax = house.Template?.HeavyTax ?? false,
                    sellable = house.Template?.IsSellable ?? false,
                    taxDueAt = IsoUtc(house.TaxDueDate),
                    protectionEndAt = IsoUtc(house.ProtectionEndDate),
                    placedAt = IsoUtc(house.PlaceDate),
                    taxAmount,
                    isTaxDue = house.TaxDueDate <= DateTime.UtcNow,
                    isProtectionExpired = house.ProtectionEndDate <= DateTime.UtcNow
                });
            }
            catch (Exception ex)
            {
                Logger.Warn($"Could not build Discord tax status for house {house?.Id}: {ex.Message}");
            }
        }

        await PostEventAsync(new
        {
            type = "housing_tax_snapshot",
            generatedAt = DateTime.UtcNow.ToString("O"),
            properties
        });
    }

    private static string IsoUtc(DateTime value)
    {
        var utc = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };

        return utc.ToString("O");
    }

    public async Task PostEventAsync(object payload)
    {
        if (string.IsNullOrWhiteSpace(_secret))
            return;

        try
        {
            using var request =
                new HttpRequestMessage(HttpMethod.Post, $"{_bridgeUrl}/api/event");

            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", _secret);

            request.Content =
                new StringContent(
                    JsonSerializer.Serialize(payload),
                    Encoding.UTF8,
                    "application/json"
                );

            using var response = await Http.SendAsync(request);

            if (!response.IsSuccessStatusCode)
            {
                Logger.Error(
                    $"Discord bridge returned HTTP {(int)response.StatusCode}"
                );
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"Discord bridge unavailable: {ex.Message}");
        }
    }

    public Task CompleteLinkAsync(
        string code,
        uint accountId,
        uint characterId,
        string characterName,
        string faction,
        uint factionId,
        uint motherFactionId,
        string side,
        uint guildId,
        string guildName,
        uint familyId,
        byte level,
        IReadOnlyList<object> characters)
    {
        return PostAsync(
            "/api/link/complete",
            new
            {
                code,
                accountId,
                characterId,
                characterName,
                faction,
                factionId,
                motherFactionId,
                side,
                guildId,
                guildName,
                familyId,
                level,
                characters
            }
        );
    }

    private async Task PollCommandsAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var request =
                    new HttpRequestMessage(HttpMethod.Post, $"{_bridgeUrl}/api/commands/poll");

                request.Headers.Authorization =
                    new AuthenticationHeaderValue("Bearer", _secret);

                request.Content = new StringContent("{}", Encoding.UTF8, "application/json");

                using var response = await Http.SendAsync(request, cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync(cancellationToken);
                    using var doc = JsonDocument.Parse(json);

                    if (doc.RootElement.TryGetProperty("commands", out var commands) &&
                        commands.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var command in commands.EnumerateArray())
                        {
                            try
                            {
                                HandleCommand(command);
                            }
                            catch (Exception ex)
                            {
                                Logger.Warn($"Discord command rejected: {ex.Message}");
                            }
                        }
                    }
                }

                await Task.Delay(250, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Logger.Warn($"Discord command poll unavailable: {ex.Message}");

                try
                {
                    await Task.Delay(1000, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private static void HandleCommand(JsonElement command)
    {
        if (!command.TryGetProperty("type", out var typeElement))
            return;

        var type = typeElement.GetString() ?? string.Empty;

        switch (type.ToLowerInvariant())
        {
            case "team_chat":
                HandleTeamChat(command);
                break;

            case "guild_chat":
                HandlePersistentChat(command, "guild");
                break;

            case "family_chat":
                HandlePersistentChat(command, "family");
                break;

            case "faction_chat":
                HandlePersistentChat(command, "faction");
                break;

            case "public_chat":
                HandlePublicChat(command);
                break;

            case "nation_chat":
                HandleNationChat(command);
                break;

            case "local_chat":
                HandleLocalChat(command);
                break;

            case "announce":
                HandleAnnouncement(command);
                break;

            case "maintenance_control":
                HandleMaintenanceControl(command);
                break;

            case "character_profile_request":
                HandleCharacterProfileRequest(command);
                break;

            case "justice_history_request":
                HandleJusticeHistoryRequest(command);
                break;

            case "item_lookup_request":
                HandleItemLookupRequest(command);
                break;

            case "npc_lookup_request":
                HandleNpcLookupRequest(command);
                break;

            case "quest_lookup_request":
                HandleQuestLookupRequest(command);
                break;

            case "skill_lookup_request":
                HandleSkillLookupRequest(command);
                break;

            case "recipe_lookup_request":
                HandleRecipeLookupRequest(command);
                break;

            case "title_lookup_request":
                HandleTitleLookupRequest(command);
                break;

            case "achievement_lookup_request":
                HandleAchievementLookupRequest(command);
                break;

            case "vocation_lookup_request":
                HandleVocationLookupRequest(command);
                break;

            case "honor_lookup_request":
                HandleHonorLookupRequest(command);
                break;

            case "link_welcome_gift":
                HandleLinkWelcomeGift(command);
                break;

            case "gift_campaign_recipient_request":
                HandleGiftCampaignRecipientRequest(command);
                break;

            case "gift_campaign_delivery":
                HandleGiftCampaignDelivery(command);
                break;
        }
    }

    private static void HandleGiftCampaignRecipientRequest(JsonElement command)
    {
        var requestId = GetString(command, "requestId");
        if (string.IsNullOrWhiteSpace(requestId))
            return;

        try
        {
            var recipients = new List<object>();
            var seenAccounts = new HashSet<uint>();
            using var connection = MySQL.CreateConnection();
            using var query = connection.CreateCommand();
            // One deterministic recipient per account. Highest level wins, then
            // lowest character id. Actual delivery switches to whichever character
            // on that account is online, so gifts never cross account ownership.
            query.CommandText = @"
SELECT account_id, id, name
FROM characters
WHERE deleted = 0
ORDER BY account_id ASC, level DESC, id ASC";
            using var reader = query.ExecuteReader();
            while (reader.Read())
            {
                var accountId = Convert.ToUInt32(reader["account_id"]);
                if (accountId == 0 || !seenAccounts.Add(accountId))
                    continue;
                recipients.Add(new
                {
                    accountId,
                    characterId = Convert.ToUInt32(reader["id"]),
                    characterName = reader["name"]?.ToString() ?? string.Empty
                });
            }

            _ = Instance?.PostEventAsync(new
            {
                type = "gift_campaign_recipient_snapshot",
                requestId,
                ok = true,
                recipients
            });
        }
        catch (Exception ex)
        {
            Logger.Warn($"Discord gift campaign recipient lookup failed: {ex.Message}");
            _ = Instance?.PostEventAsync(new
            {
                type = "gift_campaign_recipient_snapshot",
                requestId,
                ok = false,
                error = ex.Message,
                recipients = Array.Empty<object>()
            });
        }
    }

    private static void HandleGiftCampaignDelivery(JsonElement command)
    {
        var campaignId = GetString(command, "campaignId").Trim();
        TryGetUInt(command, "accountId", out var accountId);
        TryGetUInt(command, "characterId", out var characterId);
        var characterName = GetString(command, "characterName").Trim();
        var sender = GetString(command, "sender").Trim();
        var subject = GetString(command, "subject").Trim();
        var message = GetString(command, "message").Trim();
        var delivered = false;
        var error = string.Empty;
        var createdItems = new List<Item>();
        Character giftCharacter = null;

        try
        {
            if (string.IsNullOrWhiteSpace(campaignId) || accountId == 0 || characterId == 0)
                throw new InvalidOperationException("Campaign, account, and character are required.");

            using (var connection = MySQL.CreateConnection())
            using (var verify = connection.CreateCommand())
            {
                verify.CommandText = @"
SELECT name
FROM characters
WHERE id = @character_id
  AND account_id = @account_id
  AND deleted = 0
LIMIT 1";
                verify.Parameters.AddWithValue("@character_id", characterId);
                verify.Parameters.AddWithValue("@account_id", accountId);
                var verifiedName = verify.ExecuteScalar()?.ToString();
                if (string.IsNullOrWhiteSpace(verifiedName))
                    throw new InvalidOperationException("The campaign recipient does not belong to the requested AA account.");
                characterName = verifiedName;
            }

            giftCharacter = WorldManager.Instance.GetCharacterById(characterId);
            if (giftCharacter == null || !giftCharacter.IsOnline || giftCharacter.AccountId != accountId)
                throw new InvalidOperationException("The recipient character went offline before delivery; the campaign can be retried.");

            if (!command.TryGetProperty("items", out var itemsElement) || itemsElement.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("The campaign contains no item list.");

            var itemEntries = itemsElement.EnumerateArray().Take(10).ToArray();
            if (itemEntries.Length == 0)
                throw new InvalidOperationException("The campaign contains no items.");

            foreach (var entry in itemEntries)
            {
                if (!TryGetUInt(entry, "itemTemplateId", out var itemTemplateId) || itemTemplateId == 0)
                    throw new InvalidOperationException("A campaign item has an invalid template id.");
                var requestedQuantity = entry.TryGetProperty("quantity", out var quantityElement) && quantityElement.TryGetInt32(out var parsedQuantity)
                    ? parsedQuantity
                    : 1;
                var template = ItemManager.Instance.GetTemplate(itemTemplateId);
                if (template == null)
                    throw new InvalidOperationException($"Item template {itemTemplateId} does not exist.");
                var quantity = Math.Min(Math.Max(1, requestedQuantity), Math.Max(1, template.MaxCount));
                if (!giftCharacter.Inventory.MailAttachments.AcquireDefaultItemEx(
                        ItemTaskType.Invalid,
                        itemTemplateId,
                        quantity,
                        template.FixedGrade,
                        out var entryItems,
                        out _,
                        0,
                        -1) || entryItems.Count == 0)
                {
                    throw new InvalidOperationException($"AAEmu could not create item {itemTemplateId} x{quantity}.");
                }
                createdItems.AddRange(entryItems);
            }

            if (createdItems.Count > 10)
                throw new InvalidOperationException("The campaign produced more than 10 mail attachments.");

            var mail = new MailPlayerToPlayer(giftCharacter, characterName)
            {
                MailType = MailType.SysExpress,
                Title = string.IsNullOrWhiteSpace(subject) ? "Community Gift" : subject[..Math.Min(subject.Length, 100)],
                Header =
                {
                    SenderId = 0,
                    SenderName = string.IsNullOrWhiteSpace(sender) ? "GamingLiberated" : sender[..Math.Min(sender.Length, 64)],
                    Extra = 0
                },
                Body =
                {
                    Text = string.IsNullOrWhiteSpace(message) ? "Thank you for playing!" : message[..Math.Min(message.Length, 1000)],
                    SendDate = DateTime.UtcNow,
                    RecvDate = DateTime.UtcNow
                }
            };
            mail.Body.Attachments.AddRange(createdItems);
            if (!mail.FinalizeAttachments())
                throw new InvalidOperationException("AAEmu could not finalize the campaign attachments.");
            if (!mail.Send())
                throw new InvalidOperationException("AAEmu rejected the campaign mail.");

            delivered = true;
            Logger.Info($"Discord gift campaign delivered: {campaignId}, account {accountId}, character {characterName} ({characterId})");
        }
        catch (Exception ex)
        {
            error = ex.Message;
            if (!delivered && giftCharacter != null)
            {
                foreach (var item in createdItems.ToArray())
                {
                    if (item?._holdingContainer != null)
                        item._holdingContainer.RemoveItem(ItemTaskType.Invalid, item, true);
                    else if (item != null)
                        ItemManager.Instance.ReleaseId(item.Id);
                }
            }
            Logger.Warn($"Discord gift campaign delivery failed for {campaignId}, account {accountId}: {ex.Message}");
        }

        _ = Instance?.PostEventAsync(new
        {
            type = "gift_campaign_delivery_result",
            campaignId,
            ok = delivered,
            accountId,
            characterId,
            characterName,
            error
        });
    }

    private static void HandleLinkWelcomeGift(JsonElement command)
    {
        TryGetUInt(command, "accountId", out var accountId);
        TryGetUInt(command, "characterId", out var characterId);
        TryGetUInt(command, "itemTemplateId", out var itemTemplateId);
        var requestedQuantity = command.TryGetProperty("quantity", out var quantityElement) && quantityElement.TryGetInt32(out var parsedQuantity)
            ? parsedQuantity
            : 1;
        var characterName = GetString(command, "characterName").Trim();
        var delivered = false;
        var error = string.Empty;
        var finalQuantity = Math.Max(1, requestedQuantity);
        var createdItems = new List<Item>();
        Character giftCharacter = null;

        try
        {
            if (accountId == 0 || characterId == 0)
                throw new InvalidOperationException("A verified linked account and character are required.");
            if (itemTemplateId != 25968)
                throw new InvalidOperationException("The requested welcome-gift item is not allowed.");

            using (var connection = MySQL.CreateConnection())
            using (var verify = connection.CreateCommand())
            {
                verify.CommandText = @"
SELECT name
FROM characters
WHERE id = @character_id
  AND account_id = @account_id
  AND deleted = 0
LIMIT 1";
                verify.Parameters.AddWithValue("@character_id", characterId);
                verify.Parameters.AddWithValue("@account_id", accountId);
                var verifiedName = verify.ExecuteScalar()?.ToString();
                if (string.IsNullOrWhiteSpace(verifiedName))
                    throw new InvalidOperationException("The gift character does not belong to the linked AA account.");
                characterName = verifiedName;
            }

            var template = ItemManager.Instance.GetTemplate(itemTemplateId);
            if (template == null)
                throw new InvalidOperationException($"Item template {itemTemplateId} does not exist.");

            finalQuantity = Math.Min(finalQuantity, Math.Max(1, template.MaxCount));
            giftCharacter = WorldManager.Instance.GetCharacterById(characterId);
            if (giftCharacter == null || !giftCharacter.IsOnline)
                throw new InvalidOperationException("The linked character must remain online until the welcome gift is delivered.");

            if (!giftCharacter.Inventory.MailAttachments.AcquireDefaultItemEx(
                    ItemTaskType.Invalid,
                    itemTemplateId,
                    finalQuantity,
                    template.FixedGrade,
                    out createdItems,
                    out _,
                    0,
                    -1) || createdItems.Count == 0)
            {
                throw new InvalidOperationException("AAEmu could not create the welcome-gift attachment.");
            }

            // Match AAEmu 1.2's proven quest-reward mail path. Promotion mail is
            // counted as commercial mail and is hidden from the normal mailbox UI.
            var mail = new MailPlayerToPlayer(giftCharacter, characterName)
            {
                MailType = MailType.SysExpress,
                Title = "Discord Link Welcome Gift",
                Header =
                {
                    SenderId = 0,
                    SenderName = "GamingLiberated",
                    Extra = 0
                },
                Body =
                {
                    Text = "Thank you for linking your ArcheAge account with Discord! Enjoy your Loyalty Labor Pack.",
                    SendDate = DateTime.UtcNow,
                    RecvDate = DateTime.UtcNow
                }
            };
            mail.Body.Attachments.AddRange(createdItems);

            if (!mail.FinalizeAttachments())
                throw new InvalidOperationException("AAEmu could not finalize the welcome-gift attachment.");

            if (!mail.Send())
                throw new InvalidOperationException("AAEmu rejected the welcome-gift mail.");

            delivered = true;
            Logger.Info($"Discord link welcome gift delivered: account {accountId}, character {characterName} ({characterId}), item {itemTemplateId} x{finalQuantity}");
        }
        catch (Exception ex)
        {
            error = ex.Message;
            if (!delivered && giftCharacter != null)
            {
                foreach (var item in createdItems.ToArray())
                {
                    if (item?._holdingContainer != null)
                        item._holdingContainer.RemoveItem(ItemTaskType.Invalid, item, true);
                    else if (item != null)
                        ItemManager.Instance.ReleaseId(item.Id);
                }
            }
            Logger.Warn($"Discord link welcome gift failed for account {accountId}, character {characterId}: {ex.Message}");
        }

        _ = Instance?.PostEventAsync(new
        {
            type = "link_welcome_gift_result",
            ok = delivered,
            accountId,
            characterId,
            characterName,
            itemTemplateId,
            quantity = finalQuantity,
            error
        });
    }

    private static void HandleCharacterProfileRequest(JsonElement command)
    {
        var requestId = GetString(command, "requestId");
        if (string.IsNullOrWhiteSpace(requestId))
            return;

        if (!TryGetUInt(command, "accountId", out var accountId) || accountId == 0 ||
            !TryGetUInt(command, "characterId", out var characterId) || characterId == 0)
        {
            _ = Instance?.PostEventAsync(new
            {
                type = "character_profile_snapshot",
                requestId,
                ok = false,
                error = "Invalid account or character id."
            });
            return;
        }

        try
        {
            using var connection = MySQL.CreateConnection();
            using var dbCommand = connection.CreateCommand();
            dbCommand.CommandText =
                """
                SELECT
                    c.id,
                    c.account_id,
                    c.name,
                    c.race,
                    c.gender,
                    c.level,
                    c.ability1,
                    c.ability2,
                    c.ability3,
                    c.world_id,
                    c.zone_id,
                    c.x,
                    c.y,
                    c.z,
                    c.faction_id,
                    c.faction_name,
                    c.expedition_id,
                    c.family,
                    c.dead_count,
                    c.honor_point,
                    c.vocation_point,
                    c.crime_point,
                    c.crime_record,
                    c.jury_point,
                    c.hostile_faction_kills,
                    c.pvp_honor,
                    c.online_time,
                    c.created_at,
                    COALESCE(a.labor, 0) AS account_labor,
                    COALESCE(a.credits, 0) AS account_credits,
                    COALESCE(a.loyalty, 0) AS account_loyalty,
                    COALESCE(e.name, '') AS guild_name
                FROM characters c
                LEFT JOIN accounts a ON a.account_id = c.account_id
                LEFT JOIN expeditions e ON e.id = c.expedition_id
                WHERE c.account_id = @account_id
                  AND c.id = @character_id
                  AND c.deleted = 0
                LIMIT 1
                """;

            dbCommand.Parameters.AddWithValue("@account_id", accountId);
            dbCommand.Parameters.AddWithValue("@character_id", characterId);
            dbCommand.Prepare();

            using var reader = dbCommand.ExecuteReader();
            if (!reader.Read())
            {
                _ = Instance?.PostEventAsync(new
                {
                    type = "character_profile_snapshot",
                    requestId,
                    ok = false,
                    error = "Character was not found on the linked AA account."
                });
                return;
            }

            var characterName = reader.GetString("name");
            var raceId = reader.GetByte("race");
            var genderId = reader.GetByte("gender");
            var level = reader.GetByte("level");
            var ability1Id = reader.GetByte("ability1");
            var ability2Id = reader.GetByte("ability2");
            var ability3Id = reader.GetByte("ability3");
            var worldId = reader.GetUInt32("world_id");
            var zoneId = reader.GetUInt32("zone_id");
            var x = reader.GetFloat("x");
            var y = reader.GetFloat("y");
            var z = reader.GetFloat("z");
            var factionId = reader.GetUInt32("faction_id");
            var factionName = reader.GetString("faction_name");
            var guildId = reader.GetInt32("expedition_id");
            var guildName = reader.GetString("guild_name");
            var familyId = reader.GetUInt32("family");
            var deadCount = reader.GetUInt32("dead_count");
            var honorPoint = reader.GetInt32("honor_point");
            var vocationPoint = reader.GetInt32("vocation_point");
            var crimePoint = reader.GetInt32("crime_point");
            var crimeRecord = reader.GetInt32("crime_record");
            var juryPoint = reader.GetInt32("jury_point");
            var hostileFactionKills = reader.GetInt32("hostile_faction_kills");
            var pvpHonor = reader.GetInt32("pvp_honor");
            var playTimeSeconds = reader.GetUInt32("online_time");
            var createdAt = reader.GetDateTime("created_at");
            var accountLabor = reader.GetInt32("account_labor");
            var accountCredits = reader.GetInt32("account_credits");
            var accountLoyalty = reader.GetInt32("account_loyalty");

            // If the character is online, use live position/level data instead of the
            // last database save. The database remains authoritative for persistent stats.
            var onlineCharacter = WorldManager.Instance.GetCharacterById(characterId);
            var isOnline = onlineCharacter != null &&
                           onlineCharacter.IsOnline &&
                           onlineCharacter.AccountId == accountId;

            if (isOnline)
            {
                level = onlineCharacter.Level;
                zoneId = onlineCharacter.Transform.ZoneId;
                worldId = onlineCharacter.Transform.WorldId;
                x = onlineCharacter.Transform.World.Position.X;
                y = onlineCharacter.Transform.World.Position.Y;
                z = onlineCharacter.Transform.World.Position.Z;
                playTimeSeconds = (uint)Math.Max(0, onlineCharacter.OnlineTime.TotalSeconds);
            }

            var faction = FactionManager.Instance.GetFaction((FactionsEnum)factionId);
            var motherFactionId = faction?.MotherId ?? FactionsEnum.Invalid;
            if (motherFactionId == FactionsEnum.Invalid)
                motherFactionId = faction?.Id ?? FactionsEnum.Invalid;

            var side = FactionSide(motherFactionId);

            var zone = ZoneManager.Instance.GetZoneByKey(zoneId);
            var zoneName = zone?.Name ?? string.Empty;
            var zoneGroupId = zone?.GroupId ?? 0;
            var zoneGroupName = zoneGroupId > 0
                ? ZoneManager.Instance.GetZoneGroupById(zoneGroupId)?.Name ?? string.Empty
                : string.Empty;

            var jailStatusKnown = false;
            var isJailed = false;
            long jailRemainingSeconds = 0;

            if (isOnline && onlineCharacter != null)
            {
                jailStatusKnown = true;
                var prisonerBuffIds = SkillManager.Instance.GetBuffsByTagId((uint)BuffConstants.TagPrisoner);
                if (prisonerBuffIds != null && prisonerBuffIds.Count > 0)
                {
                    var goodBuffs = new List<Buff>();
                    var badBuffs = new List<Buff>();
                    var hiddenBuffs = new List<Buff>();
                    onlineCharacter.Buffs.GetAllBuffs(goodBuffs, badBuffs, hiddenBuffs, true);

                    var prisonerBuff = goodBuffs
                        .Concat(badBuffs)
                        .Concat(hiddenBuffs)
                        .FirstOrDefault(buff =>
                            buff?.Template != null && prisonerBuffIds.Contains(buff.Template.BuffId));

                    if (prisonerBuff != null)
                    {
                        isJailed = true;
                        var remainingMs = prisonerBuff.GetTimeLeft();
                        if (remainingMs > 0)
                            jailRemainingSeconds = (long)Math.Ceiling(remainingMs / 1000d);
                    }
                }
            }

            _ = Instance?.PostEventAsync(new
            {
                type = "character_profile_snapshot",
                requestId,
                ok = true,
                accountId,
                characterId,
                characterName,
                isOnline,
                level,
                raceId,
                race = ((Race)raceId).ToString(),
                genderId,
                gender = ((Gender)genderId).ToString(),
                ability1Id,
                ability1 = ((AbilityType)ability1Id).ToString(),
                ability2Id,
                ability2 = ((AbilityType)ability2Id).ToString(),
                ability3Id,
                ability3 = ((AbilityType)ability3Id).ToString(),
                worldId,
                zoneId,
                zoneName,
                zoneGroupId,
                zoneGroupName,
                x,
                y,
                z,
                factionId,
                factionName,
                motherFactionId = (uint)motherFactionId,
                side,
                guildId,
                guildName,
                familyId,
                accountLabor,
                accountCredits,
                accountLoyalty,
                honorPoint,
                vocationPoint,
                crimePoint,
                crimeRecord,
                juryPoint,
                wantedCrimePointThreshold = CrimeManager.WantedCrimePointThreshold,
                isWanted = crimePoint >= CrimeManager.WantedCrimePointThreshold,
                hostileFactionKills,
                pvpHonor,
                deadCount,
                playTimeSeconds,
                createdAt = IsoUtc(createdAt),
                jailStatusKnown,
                isJailed,
                jailRemainingSeconds
            });
        }
        catch (Exception ex)
        {
            Logger.Warn($"Discord character profile lookup failed for account {accountId}, character {characterId}: {ex.Message}");
            _ = Instance?.PostEventAsync(new
            {
                type = "character_profile_snapshot",
                requestId,
                ok = false,
                error = "AAEmu could not load the character profile."
            });
        }
    }

    private static void HandleJusticeHistoryRequest(JsonElement command)
    {
        var requestId = GetString(command, "requestId");
        if (string.IsNullOrWhiteSpace(requestId))
            return;

        if (!TryGetUInt(command, "accountId", out var accountId) || accountId == 0 ||
            !TryGetUInt(command, "characterId", out var characterId) || characterId == 0)
        {
            _ = Instance?.PostEventAsync(new
            {
                type = "justice_history_snapshot",
                requestId,
                ok = false,
                error = "Invalid account or character id."
            });
            return;
        }

        var limit = 10;
        if (command.TryGetProperty("limit", out var limitElement) &&
            limitElement.ValueKind == JsonValueKind.Number &&
            limitElement.TryGetInt32(out var requestedLimit))
        {
            limit = Math.Clamp(requestedLimit, 1, 20);
        }

        try
        {
            using var connection = MySQL.CreateConnection();

            // Verify the requested character belongs to the Discord-linked AA account.
            string characterName;
            using (var verifyCommand = connection.CreateCommand())
            {
                verifyCommand.CommandText =
                    "SELECT name FROM characters WHERE account_id = @account_id AND id = @character_id AND deleted = 0 LIMIT 1";
                verifyCommand.Parameters.AddWithValue("@account_id", accountId);
                verifyCommand.Parameters.AddWithValue("@character_id", characterId);
                verifyCommand.Prepare();

                var characterNameObj = verifyCommand.ExecuteScalar();
                if (characterNameObj == null || characterNameObj == DBNull.Value)
                {
                    _ = Instance?.PostEventAsync(new
                    {
                        type = "justice_history_snapshot",
                        requestId,
                        ok = false,
                        error = "Character was not found on the linked AA account."
                    });
                    return;
                }

                characterName = Convert.ToString(characterNameObj) ?? string.Empty;
            }

            using var dbCommand = connection.CreateCommand();
            dbCommand.CommandText =
                $"""
                SELECT
                    cr.id,
                    cr.criminal,
                    cr.victim,
                    cr.reporter,
                    cr.crime_type,
                    cr.zone_key,
                    cr.crime_time,
                    cr.report_time,
                    cr.msg,
                    cr.judgement_time,
                    COALESCE(v.name, '') AS victim_name,
                    COALESCE(r.name, '') AS reporter_name
                FROM crime cr
                LEFT JOIN characters v ON v.id = cr.victim
                LEFT JOIN characters r ON r.id = cr.reporter
                WHERE cr.criminal = @character_id
                ORDER BY COALESCE(cr.report_time, cr.crime_time) DESC, cr.id DESC
                LIMIT {limit}
                """;
            dbCommand.Parameters.AddWithValue("@character_id", characterId);
            dbCommand.Prepare();

            using var reader = dbCommand.ExecuteReader();
            var records = new List<object>();

            while (reader.Read())
            {
                var crimeTypeId = reader.IsDBNull(reader.GetOrdinal("crime_type"))
                    ? 0u
                    : reader.GetUInt32("crime_type");
                var crimeKind = crimeTypeId <= byte.MaxValue && Enum.IsDefined(typeof(CrimeKind), (byte)crimeTypeId)
                    ? ((CrimeKind)(byte)crimeTypeId).ToString()
                    : $"Crime Type {crimeTypeId}";

                var zoneGroupId = reader.IsDBNull(reader.GetOrdinal("zone_key"))
                    ? 0u
                    : reader.GetUInt32("zone_key");
                var zoneName = zoneGroupId > 0
                    ? ZoneManager.Instance.GetZoneGroupById(zoneGroupId)?.Name ?? string.Empty
                    : string.Empty;

                var victimId = reader.IsDBNull(reader.GetOrdinal("victim"))
                    ? 0u
                    : reader.GetUInt32("victim");
                var reporterId = reader.IsDBNull(reader.GetOrdinal("reporter"))
                    ? 0u
                    : reader.GetUInt32("reporter");

                var crimeTime = reader.IsDBNull(reader.GetOrdinal("crime_time"))
                    ? string.Empty
                    : IsoUtc(reader.GetDateTime("crime_time"));
                var reportTime = reader.IsDBNull(reader.GetOrdinal("report_time"))
                    ? string.Empty
                    : IsoUtc(reader.GetDateTime("report_time"));
                var judgementTime = reader.IsDBNull(reader.GetOrdinal("judgement_time"))
                    ? string.Empty
                    : IsoUtc(reader.GetDateTime("judgement_time"));

                records.Add(new
                {
                    id = reader.GetUInt32("id"),
                    criminalId = characterId,
                    victimId,
                    victimName = reader.GetString("victim_name"),
                    reporterId,
                    reporterName = reader.GetString("reporter_name"),
                    crimeTypeId,
                    crimeType = crimeKind,
                    zoneGroupId,
                    zoneName,
                    crimeTime,
                    reportTime,
                    message = reader.IsDBNull(reader.GetOrdinal("msg")) ? string.Empty : reader.GetString("msg"),
                    judgementTime
                });
            }

            _ = Instance?.PostEventAsync(new
            {
                type = "justice_history_snapshot",
                requestId,
                ok = true,
                accountId,
                characterId,
                characterName,
                records
            });
        }
        catch (Exception ex)
        {
            Logger.Warn($"Discord justice history lookup failed for account {accountId}, character {characterId}: {ex.Message}");
            _ = Instance?.PostEventAsync(new
            {
                type = "justice_history_snapshot",
                requestId,
                ok = false,
                error = "AAEmu could not load the crime history."
            });
        }
    }

    private static void HandleItemLookupRequest(JsonElement command)
    {
        var requestId = GetString(command, "requestId");
        if (string.IsNullOrWhiteSpace(requestId))
            return;

        var query = GetString(command, "query").Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            _ = Instance?.PostEventAsync(new
            {
                type = "item_lookup_snapshot",
                requestId,
                ok = false,
                error = "Item query is empty."
            });
            return;
        }

        try
        {
            Logger.Info($"Discord item lookup request received: '{query}' ({requestId})");
            var matches = new List<AAEmu.Game.Models.Game.Items.Templates.ItemTemplate>();

            if (uint.TryParse(query, out var templateId) && templateId > 0)
            {
                var template = ItemManager.Instance.GetTemplate(templateId);
                if (template != null)
                    matches.Add(template);
            }
            else
            {
                // Do not LINQ-filter/sort the entire item table. The previous v33
                // implementation localized every template before Take(10), which can
                // keep the bridge poll thread busy long enough for Discord to time out.
                // Scan incrementally and stop as soon as we have enough matches.
                var searchStartedAt = DateTime.UtcNow;
                foreach (var item in ItemManager.Instance.GetAllItems())
                {
                    if (item == null)
                        continue;

                    var rawName = item.Name ?? string.Empty;
                    var localizedName = LocalizationManager.Instance.Get("items", "name", item.Id, rawName) ?? rawName;

                    if (!rawName.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                        !localizedName.Contains(query, StringComparison.OrdinalIgnoreCase))
                    {
                        // Keep each Discord lookup bounded even on very large game-data sets.
                        if ((DateTime.UtcNow - searchStartedAt).TotalSeconds >= 5)
                            break;
                        continue;
                    }

                    matches.Add(item);
                    if (matches.Count >= 10)
                        break;

                    if ((DateTime.UtcNow - searchStartedAt).TotalSeconds >= 5)
                        break;
                }
            }

            var items = matches.Select(item => new
            {
                id = item.Id,
                name = LocalizationManager.Instance.Get("items", "name", item.Id, item.Name),
                rawName = item.Name,
                classType = item.ClassType?.Name ?? "Item",
                categoryId = item.CategoryId,
                level = item.Level,
                levelRequirement = item.LevelRequirement,
                levelLimit = item.LevelLimit,
                maxCount = item.MaxCount,
                sellable = item.Sellable,
                price = item.Price,
                refund = item.Refund,
                honorPrice = item.HonorPrice,
                vocationPrice = item.LivingPointPrice,
                bindType = item.BindType.ToString(),
                gradable = item.Gradable,
                fixedGrade = item.FixedGrade,
                disenchantable = item.Disenchantable,
                useSkillId = item.UseSkillId,
                buffId = item.BuffId
            }).ToList();

            Logger.Info($"Discord item lookup completed: '{query}' -> {items.Count} result(s) ({requestId})");
            _ = Instance?.PostEventAsync(new
            {
                type = "item_lookup_snapshot",
                requestId,
                ok = true,
                query,
                items
            });
        }
        catch (Exception ex)
        {
            Logger.Warn($"Discord item lookup failed for '{query}': {ex.Message}");
            _ = Instance?.PostEventAsync(new
            {
                type = "item_lookup_snapshot",
                requestId,
                ok = false,
                error = "AAEmu could not load item data."
            });
        }
    }


    private sealed class NpcLocationInfo
    {
        public string ZoneName { get; init; } = "Unknown zone";
        public string ZoneInternalName { get; init; } = string.Empty;
        public uint ZoneKey { get; init; }
        public uint ZoneGroupId { get; init; }
        public uint WorldId { get; init; }
        public double X { get; init; }
        public double Y { get; init; }
        public double Z { get; init; }
        public string WorldName { get; init; } = string.Empty;
        public string Source { get; init; } = string.Empty;
    }

    private sealed class NpcDropInfo
    {
        public uint ItemId { get; init; }
        public string ItemName { get; init; } = "Unknown item";
        public double BaseChancePercent { get; init; }
        public double ServerBaseChancePercent { get; init; }
        public double DataGroupChancePercent { get; init; }
        public double ServerLootRate { get; init; } = 1d;
        public int? MinAmount { get; init; }
        public int? MaxAmount { get; init; }
        public bool QuestDependent { get; init; }
        public bool RuntimeGuaranteed { get; init; }
        public string ChanceExplanation { get; init; } = string.Empty;
    }

    private sealed class NpcLootRow
    {
        public uint LootPackId { get; init; }
        public int GroupNo { get; init; }
        public long GroupRate { get; init; }
        public long LootId { get; init; }
        public uint ItemId { get; init; }
        public long ItemRate { get; init; }
        public bool AlwaysDrop { get; init; }
        public int? MinAmount { get; init; }
        public int? MaxAmount { get; init; }
        public bool QuestDependent { get; init; }
    }

    private static string FindCompactDbPath()
    {
        return new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Data", "compact.sqlite3"),
            Path.Combine(Directory.GetCurrentDirectory(), "Data", "compact.sqlite3"),
            Path.Combine(Directory.GetCurrentDirectory(), "AAEmu.Game", "Data", "compact.sqlite3")
        }.FirstOrDefault(File.Exists);
    }

    private static List<NpcLocationInfo> FindNpcLocations(uint npcTemplateId)
    {
        var results = new List<NpcLocationInfo>();
        var roots = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Data", "Worlds"),
            Path.Combine(Directory.GetCurrentDirectory(), "Data", "Worlds"),
            Path.Combine(Directory.GetCurrentDirectory(), "AAEmu.Game", "Data", "Worlds")
        }.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        var jsonOptions = new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        foreach (var root in roots)
        {
            foreach (var file in Directory.EnumerateFiles(root, "npc_spawns*.json", SearchOption.AllDirectories))
            {
                try
                {
                    var text = File.ReadAllText(file);
                    if (!text.Contains(npcTemplateId.ToString(), StringComparison.Ordinal))
                        continue;

                    using var document = JsonDocument.Parse(text, jsonOptions);
                    var worldName = Path.GetFileName(Path.GetDirectoryName(file) ?? string.Empty);
                    CollectNpcLocations(document.RootElement, npcTemplateId, worldName, root, file, results);
                }
                catch (Exception ex)
                {
                    Logger.Debug($"NPC location scan skipped {Path.GetFileName(file)}: {ex.Message}");
                }
            }
        }

        return results
            .Where(x => Math.Abs(x.X) > 0.001 || Math.Abs(x.Y) > 0.001 || Math.Abs(x.Z) > 0.001)
            .GroupBy(x => $"{x.WorldName}:{Math.Round(x.X, 2)}:{Math.Round(x.Y, 2)}:{Math.Round(x.Z, 2)}")
            .Select(g => g.First())
            .OrderBy(x => x.ZoneName)
            .ThenBy(x => x.X)
            .ThenBy(x => x.Y)
            .Take(50)
            .ToList();
    }

    private static void CollectNpcLocations(JsonElement element, uint npcTemplateId, string worldName, string root, string sourceFile, List<NpcLocationInfo> results)
    {
        if (results.Count >= 100)
            return;

        if (element.ValueKind == JsonValueKind.Object)
        {
            // AAEmu's own SpawnManager loads npc_spawns*.json with UnitId as the NPC template ID.
            if (TryGetPropertyIgnoreCase(element, "UnitId", out var unitValue) &&
                TryJsonUInt(unitValue, out var unitId) && unitId == npcTemplateId)
            {
                JsonElement position = default;
                if (TryGetPropertyIgnoreCase(element, "Position", out var pos) && pos.ValueKind == JsonValueKind.Object)
                    position = pos;

                var x = position.ValueKind == JsonValueKind.Object ? ReadDoubleProperty(position, "X", "x") : ReadDoubleProperty(element, "X", "x");
                var y = position.ValueKind == JsonValueKind.Object ? ReadDoubleProperty(position, "Y", "y") : ReadDoubleProperty(element, "Y", "y");
                var z = position.ValueKind == JsonValueKind.Object ? ReadDoubleProperty(position, "Z", "z") : ReadDoubleProperty(element, "Z", "z");

                if (x.HasValue && y.HasValue)
                {
                    var resolved = ResolveNpcSpawnZone(worldName, x.Value, y.Value);
                    results.Add(new NpcLocationInfo
                    {
                        ZoneName = resolved.ZoneName,
                        ZoneInternalName = resolved.ZoneInternalName,
                        ZoneKey = resolved.ZoneKey,
                        ZoneGroupId = resolved.ZoneGroupId,
                        WorldId = resolved.WorldId,
                        X = x.Value,
                        Y = y.Value,
                        Z = z ?? 0,
                        WorldName = worldName,
                        Source = Path.GetRelativePath(root, sourceFile)
                    });
                }
            }

            foreach (var property in element.EnumerateObject())
                CollectNpcLocations(property.Value, npcTemplateId, worldName, root, sourceFile, results);
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
                CollectNpcLocations(child, npcTemplateId, worldName, root, sourceFile, results);
        }
    }

    private static (string ZoneName, string ZoneInternalName, uint ZoneKey, uint ZoneGroupId, uint WorldId) ResolveNpcSpawnZone(string worldName, double x, double y)
    {
        try
        {
            // Use the exact same world-region lookup AAEmu's SpawnManager uses when loading NPC spawns.
            var worldTemplate = WorldManager.Instance.GetWorldTemplateByName(worldName);
            if (worldTemplate != null)
            {
                var zoneKey = WorldManager.Instance.GetZoneId(worldTemplate, (float)x, (float)y);
                var zone = ZoneManager.Instance.GetZoneByKey(zoneKey);
                var zoneGroupId = zone?.GroupId ?? 0;
                var group = zoneGroupId > 0 ? ZoneManager.Instance.GetZoneGroupById(zoneGroupId) : null;
                var internalName = group?.Name ?? zone?.Name ?? string.Empty;
                var displayName = !string.IsNullOrWhiteSpace(internalName) ? internalName : $"Zone {zoneGroupId}";
                return (displayName, internalName, zoneKey, zoneGroupId, worldTemplate.Id);
            }
        }
        catch (Exception ex)
        {
            Logger.Debug($"NPC zone resolution failed for {worldName} at {x:0.##},{y:0.##}: {ex.Message}");
        }

        // Fallback for unusual/custom worlds that are not loaded as a WorldTemplate.
        var fallback = ResolveZoneGroupByPosition(x, y);
        return (fallback.Name, fallback.Name, 0, fallback.Id, 0);
    }

    private static (uint Id, string Name) ResolveZoneGroupByPosition(double x, double y)
    {
        var dbPath = FindCompactDbPath();
        if (string.IsNullOrWhiteSpace(dbPath))
            return (0, "Unknown zone");

        try
        {
            using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
SELECT id, name
FROM zone_groups
WHERE w > 0 AND h > 0
  AND @x >= x AND @x <= (x + w)
  AND @y >= y AND @y <= (y + h)
ORDER BY (w * h) ASC
LIMIT 1";
            cmd.Parameters.AddWithValue("@x", x);
            cmd.Parameters.AddWithValue("@y", y);
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
                return (Convert.ToUInt32(reader["id"]), reader["name"]?.ToString() ?? "Unknown zone");
        }
        catch { }

        return (0, "Unknown zone");
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    private static double? ReadDoubleProperty(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetPropertyIgnoreCase(element, name, out var value))
                continue;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
                return number;
            if (value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), out number))
                return number;
        }
        return null;
    }

    private static bool TryJsonUInt(JsonElement value, out uint result)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetUInt32(out result))
            return true;
        if (value.ValueKind == JsonValueKind.String && uint.TryParse(value.GetString(), out result))
            return true;
        result = 0;
        return false;
    }

    private static long NormalizeLootRate(long rawRate)
    {
        // LootGameData performs this same normalization while loading AAEmu game data.
        return rawRate > 1 ? rawRate : 10_000_000L;
    }

    private static double ClampProbability(double value) => Math.Max(0d, Math.Min(1d, value));

    private static List<NpcDropInfo> FindNpcDrops(uint npcTemplateId)
    {
        var dbPath = FindCompactDbPath();
        if (string.IsNullOrWhiteSpace(dbPath))
            return [];

        var rows = new List<NpcLootRow>();
        try
        {
            using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
SELECT p.loot_pack_id,
       l.id AS loot_id,
       l.""group"" AS group_no,
       COALESCE(g.drop_rate, 0) AS group_drop_rate,
       l.item_id,
       l.drop_rate AS item_drop_rate,
       l.min_amount,
       l.max_amount,
       l.always_drop
FROM loot_pack_dropping_npcs p
JOIN loots l ON l.loot_pack_id = p.loot_pack_id
LEFT JOIN loot_groups g ON g.pack_id = p.loot_pack_id AND g.group_no = l.""group""
WHERE p.npc_id = @npc
ORDER BY p.loot_pack_id ASC, l.""group"" ASC, l.drop_rate DESC, l.id ASC";
            cmd.Parameters.AddWithValue("@npc", npcTemplateId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var itemId = Convert.ToUInt32(reader["item_id"]);
                var item = ItemManager.Instance.GetTemplate(itemId);
                var alwaysText = reader["always_drop"]?.ToString() ?? string.Empty;
                var alwaysDrop = alwaysText.Equals("t", StringComparison.OrdinalIgnoreCase) ||
                                 alwaysText.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                                 alwaysText == "1";

                rows.Add(new NpcLootRow
                {
                    LootPackId = Convert.ToUInt32(reader["loot_pack_id"]),
                    LootId = Convert.ToInt64(reader["loot_id"]),
                    GroupNo = Convert.ToInt32(reader["group_no"]),
                    GroupRate = NormalizeLootRate(Convert.ToInt64(reader["group_drop_rate"])),
                    ItemId = itemId,
                    ItemRate = NormalizeLootRate(Convert.ToInt64(reader["item_drop_rate"])),
                    AlwaysDrop = alwaysDrop,
                    MinAmount = reader["min_amount"] is DBNull ? null : Convert.ToInt32(reader["min_amount"]),
                    MaxAmount = reader["max_amount"] is DBNull ? null : Convert.ToInt32(reader["max_amount"]),
                    QuestDependent = (item?.LootQuestId ?? 0) > 0
                });
            }
        }
        catch (Exception ex)
        {
            Logger.Debug($"NPC drop lookup could not inspect compact.sqlite3: {ex.Message}");
            return [];
        }

        var serverLootRate = Math.Max(0d, AppConfiguration.Instance.World.LootRate);
        var chancesByItem = new Dictionary<uint, List<(double BaseChance, double ServerChance, double DataGroupChance, bool RuntimeGuaranteed, string Explanation, NpcLootRow Row)>>();

        foreach (var packGroup in rows.GroupBy(r => new { r.LootPackId, r.GroupNo }))
        {
            var groupRows = packGroup.OrderByDescending(r => r.ItemRate).ThenBy(r => r.LootId).ToList();
            var normalizedGroupRate = groupRows.FirstOrDefault()?.GroupRate ?? 10_000_000L;
            var dataGroupChance = packGroup.Key.GroupNo <= 0 ? 1d : ClampProbability(normalizedGroupRate / 10_000_000d);

            if (packGroup.Key.GroupNo <= 0)
            {
                foreach (var row in groupRows)
                {
                    var baseChance = row.AlwaysDrop ? 1d : ClampProbability(row.ItemRate / 10_000_000d);
                    var serverChance = row.AlwaysDrop ? 1d : ClampProbability(row.ItemRate / 10_000_000d * serverLootRate);
                    AddNpcRuntimeDropChance(chancesByItem, row, baseChance, serverChance, 1d,
                        row.AlwaysDrop || row.ItemRate >= 10_000_000L,
                        row.AlwaysDrop ? "Always-drop flag in AAEmu." : "Independent group-0 roll.");
                }
                continue;
            }

            // This follows the running AAEmu GeneratePackNewV2 code literally.
            // A 100% item rate (including DB values <= 1 normalized by LootGameData)
            // is inserted into selected group 0 before the loot-group roll is applied.
            var rolledRows = new List<NpcLootRow>();
            foreach (var row in groupRows)
            {
                if (row.AlwaysDrop || row.ItemRate >= 10_000_000L)
                {
                    AddNpcRuntimeDropChance(chancesByItem, row, 1d, 1d, dataGroupChance, true,
                        $"Current AAEmu runtime treats this item as guaranteed because its normalized item rate is 100%; the stored loot-group roll is {dataGroupChance * 100d:0.#####}% and is bypassed by that shortcut.");
                }
                else
                {
                    rolledRows.Add(row);
                }
            }

            if (rolledRows.Count == 0)
                continue;

            // Remaining items each make an independent GeneratePackNewV2 pass. If more
            // than one succeeds, AAEmu randomly picks one successful item from the group.
            // Compute each item's exact selection probability using a Poisson-binomial DP.
            var basePass = rolledRows.Select(r => ClampProbability(dataGroupChance * (r.ItemRate / 10_000_000d))).ToArray();
            var serverPass = rolledRows.Select(r => ClampProbability(dataGroupChance * (r.ItemRate / 10_000_000d) * serverLootRate)).ToArray();

            for (var i = 0; i < rolledRows.Count; i++)
            {
                var baseSelected = ExactSelectedChance(basePass, i);
                var serverSelected = ExactSelectedChance(serverPass, i);
                AddNpcRuntimeDropChance(chancesByItem, rolledRows[i], baseSelected, serverSelected, dataGroupChance, false,
                    "Rolled by AAEmu using loot-group rate × item rate; if several items pass, one successful item is chosen at random.");
            }
        }

        var results = new List<NpcDropInfo>();
        foreach (var (itemId, entries) in chancesByItem)
        {
            var baseNone = 1d;
            var serverNone = 1d;
            foreach (var entry in entries)
            {
                baseNone *= 1d - ClampProbability(entry.BaseChance);
                serverNone *= 1d - ClampProbability(entry.ServerChance);
            }

            var representative = entries[0];
            var item = ItemManager.Instance.GetTemplate(itemId);
            var itemName = item == null
                ? $"Item {itemId}"
                : LocalizationManager.Instance.Get("items", "name", item.Id, item.Name) ?? item.Name ?? $"Item {itemId}";

            results.Add(new NpcDropInfo
            {
                ItemId = itemId,
                ItemName = itemName,
                BaseChancePercent = ClampProbability(1d - baseNone) * 100d,
                ServerBaseChancePercent = ClampProbability(1d - serverNone) * 100d,
                DataGroupChancePercent = representative.DataGroupChance * 100d,
                ServerLootRate = serverLootRate,
                MinAmount = entries.Min(e => e.Row.MinAmount),
                MaxAmount = entries.Max(e => e.Row.MaxAmount),
                QuestDependent = entries.Any(e => e.Row.QuestDependent),
                RuntimeGuaranteed = entries.Any(e => e.RuntimeGuaranteed),
                ChanceExplanation = representative.Explanation
            });
        }

        return results
            .OrderByDescending(x => x.ServerBaseChancePercent)
            .ThenByDescending(x => x.BaseChancePercent)
            .ThenBy(x => x.ItemName)
            .Take(30)
            .ToList();
    }

    private static double ExactSelectedChance(IReadOnlyList<double> passProbabilities, int selectedIndex)
    {
        var selectedPass = ClampProbability(passProbabilities[selectedIndex]);
        if (selectedPass <= 0d)
            return 0d;

        var distribution = new double[passProbabilities.Count];
        distribution[0] = 1d;
        var processed = 0;

        for (var i = 0; i < passProbabilities.Count; i++)
        {
            if (i == selectedIndex)
                continue;
            var q = ClampProbability(passProbabilities[i]);
            for (var k = processed + 1; k >= 0; k--)
            {
                var stay = k <= processed ? distribution[k] * (1d - q) : 0d;
                var rise = k > 0 ? distribution[k - 1] * q : 0d;
                distribution[k] = stay + rise;
            }
            processed++;
        }

        var conditionalShare = 0d;
        for (var k = 0; k <= processed; k++)
            conditionalShare += distribution[k] / (k + 1d);

        return ClampProbability(selectedPass * conditionalShare);
    }

    private static void AddNpcRuntimeDropChance(
        Dictionary<uint, List<(double BaseChance, double ServerChance, double DataGroupChance, bool RuntimeGuaranteed, string Explanation, NpcLootRow Row)>> target,
        NpcLootRow row,
        double baseChance,
        double serverChance,
        double dataGroupChance,
        bool runtimeGuaranteed,
        string explanation)
    {
        if (!target.TryGetValue(row.ItemId, out var entries))
        {
            entries = [];
            target[row.ItemId] = entries;
        }

        entries.Add((ClampProbability(baseChance), ClampProbability(serverChance), ClampProbability(dataGroupChance), runtimeGuaranteed, explanation, row));
    }

    private static void HandleNpcLookupRequest(JsonElement command)
    {
        var requestId = GetString(command, "requestId");
        if (string.IsNullOrWhiteSpace(requestId))
            return;

        var query = GetString(command, "query").Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            _ = Instance?.PostEventAsync(new
            {
                type = "npc_lookup_snapshot",
                requestId,
                ok = false,
                error = "NPC query is empty."
            });
            return;
        }

        try
        {
            Logger.Info($"Discord NPC lookup request received: '{query}' ({requestId})");
            var matches = new List<AAEmu.Game.Models.Game.NPChar.NpcTemplate>();

            if (uint.TryParse(query, out var templateId) && templateId > 0)
            {
                var template = NpcManager.Instance.GetTemplate(templateId);
                if (template != null)
                    matches.Add(template);
            }
            else
            {
                // Same bounded lookup pattern as the item search: localize lazily,
                // stop once we have enough matches, and never monopolize the bridge
                // poll loop on a very large game-data set.
                var searchStartedAt = DateTime.UtcNow;
                foreach (var npc in NpcManager.Instance.GetAllTemplates().Values)
                {
                    if (npc == null)
                        continue;

                    var rawName = npc.Name ?? string.Empty;
                    var localizedName = LocalizationManager.Instance.Get("npcs", "name", npc.Id, rawName) ?? rawName;

                    if (!rawName.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                        !localizedName.Contains(query, StringComparison.OrdinalIgnoreCase))
                    {
                        if ((DateTime.UtcNow - searchStartedAt).TotalSeconds >= 5)
                            break;
                        continue;
                    }

                    matches.Add(npc);
                    if (matches.Count >= 10)
                        break;

                    if ((DateTime.UtcNow - searchStartedAt).TotalSeconds >= 5)
                        break;
                }
            }

            var detailedLookup = matches.Count == 1;
            var npcs = matches.Select(npc => new
            {
                id = npc.Id,
                name = LocalizationManager.Instance.Get("npcs", "name", npc.Id, npc.Name),
                rawName = npc.Name,
                level = npc.Level,
                grade = npc.NpcGradeId.ToString(),
                kind = npc.NpcKindId.ToString(),
                templateType = npc.NpcTemplateId.ToString(),
                factionId = (uint)npc.FactionId,
                modelId = npc.ModelId,
                aggression = npc.Aggression,
                showNameTag = npc.ShowNameTag,
                merchant = npc.Merchant,
                banker = npc.Banker,
                auctioneer = npc.Auctioneer,
                blacksmith = npc.Blacksmith,
                repairman = npc.Repairman,
                teleporter = npc.Teleporter,
                skillTrainer = npc.SkillTrainer,
                abilityChanger = npc.AbilityChanger,
                priest = npc.Priest,
                stabler = npc.Stabler,
                trader = npc.Trader,
                aiFileId = npc.AiFileId,
                baseSkillId = npc.BaseSkillId,
                honorPoint = npc.HonorPoint,
                petItemId = npc.PetItemId,
                locations = detailedLookup ? FindNpcLocations(npc.Id).Select(location => new
                {
                    zoneName = location.ZoneName,
                    zoneInternalName = location.ZoneInternalName,
                    zoneKey = location.ZoneKey,
                    zoneId = location.ZoneGroupId,
                    worldId = location.WorldId,
                    worldName = location.WorldName,
                    x = location.X,
                    y = location.Y,
                    z = location.Z
                }).ToList() : null,
                drops = detailedLookup ? FindNpcDrops(npc.Id).Select(drop => new
                {
                    itemId = drop.ItemId,
                    itemName = drop.ItemName,
                    baseChancePercent = drop.BaseChancePercent,
                    serverBaseChancePercent = drop.ServerBaseChancePercent,
                    dataGroupChancePercent = drop.DataGroupChancePercent,
                    serverLootRate = drop.ServerLootRate,
                    runtimeGuaranteed = drop.RuntimeGuaranteed,
                    chanceExplanation = drop.ChanceExplanation,
                    questDependent = drop.QuestDependent,
                    minAmount = drop.MinAmount,
                    maxAmount = drop.MaxAmount
                }).ToList() : null
            }).ToList();

            Logger.Info($"Discord NPC lookup completed: '{query}' -> {npcs.Count} result(s) ({requestId})");
            _ = Instance?.PostEventAsync(new
            {
                type = "npc_lookup_snapshot",
                requestId,
                ok = true,
                query,
                npcs
            });
        }
        catch (Exception ex)
        {
            Logger.Warn($"Discord NPC lookup failed for '{query}': {ex.Message}");
            _ = Instance?.PostEventAsync(new
            {
                type = "npc_lookup_snapshot",
                requestId,
                ok = false,
                error = "AAEmu could not load NPC data."
            });
        }
    }

    private sealed class QuestLookupInfo
    {
        public uint Id { get; init; }
        public string Name { get; init; } = "Unnamed Quest";
        public byte Level { get; init; }
        public uint CategoryId { get; init; }
        public string CategoryName { get; init; } = "Unknown";
        public uint ZoneId { get; init; }
        public string ZoneName { get; init; } = "Unknown zone";
        public uint DetailId { get; init; }
        public bool Repeatable { get; init; }
        public bool Successive { get; init; }
        public bool IsDaily { get; init; }
        public bool IsScheduled { get; init; }
        public bool IsWeeklyScheduled { get; init; }
        public bool ScheduleAvailableNow { get; init; }
        public List<QuestScheduleInfo> Schedules { get; init; } = [];
        public string QuestTypeLabel { get; init; } = "📘 One-time";
        public string Summary { get; init; } = string.Empty;
        public uint StartNpcId { get; init; }
        public string StartNpcName { get; init; } = string.Empty;
        public uint ReportNpcId { get; init; }
        public string ReportNpcName { get; init; } = string.Empty;
        public List<string> Objectives { get; init; } = [];
        public List<string> Rewards { get; init; } = [];

        public bool CharacterStatusKnown { get; set; }
        public string CharacterName { get; set; } = string.Empty;
        public bool CharacterOnline { get; set; }
        public bool CharacterQuestActive { get; set; }
        public bool CharacterCompleted { get; set; }
    }

    private sealed class QuestScheduleInfo
    {
        public int ScheduleId { get; init; }
        public bool IsWeekly { get; init; }
        public int DayOfWeekId { get; init; }
        public string DayName { get; init; } = string.Empty;
        public string StartTimeUtc { get; init; } = string.Empty;
        public string EndTimeUtc { get; init; } = string.Empty;
        public bool IsActiveNow { get; init; }
    }

    private static void HandleQuestLookupRequest(JsonElement command)
    {
        var requestId = GetString(command, "requestId");
        if (string.IsNullOrWhiteSpace(requestId))
            return;

        var query = GetString(command, "query").Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            _ = Instance?.PostEventAsync(new
            {
                type = "quest_lookup_snapshot",
                requestId,
                ok = false,
                error = "Quest query is empty."
            });
            return;
        }

        TryGetUInt(command, "accountId", out var linkedAccountId);
        TryGetUInt(command, "characterId", out var linkedCharacterId);

        try
        {
            Logger.Info($"Discord quest lookup request received: '{query}' ({requestId})");
            var dbPath = FindCompactDbPath();
            if (string.IsNullOrWhiteSpace(dbPath))
                throw new FileNotFoundException("compact.sqlite3 could not be found in the AAEmu data directories.");

            var matches = new List<QuestLookupInfo>();
            using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
            connection.Open();

            if (uint.TryParse(query, out var questId) && questId > 0)
            {
                var quest = LoadQuestLookupInfo(connection, questId, true);
                if (quest != null)
                    matches.Add(quest);
            }
            else
            {
                var started = DateTime.UtcNow;
                var matchingIds = new List<uint>();
                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = "SELECT id, name FROM quest_contexts ORDER BY id";
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read() && matchingIds.Count < 10)
                    {
                        var id = Convert.ToUInt32(reader["id"]);
                        var rawName = reader["name"]?.ToString() ?? string.Empty;
                        var localizedName = LocalizationManager.Instance.Get("quest_contexts", "name", id, rawName) ?? rawName;
                        if (!rawName.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                            !localizedName.Contains(query, StringComparison.OrdinalIgnoreCase))
                        {
                            if ((DateTime.UtcNow - started).TotalSeconds >= 6)
                                break;
                            continue;
                        }
                        matchingIds.Add(id);
                    }
                }

                foreach (var id in matchingIds)
                {
                    var quest = LoadQuestLookupInfo(connection, id, false);
                    if (quest != null)
                        matches.Add(quest);
                }
            }

            // If the search resolves to a single quest, enrich it with objectives/rewards.
            if (matches.Count == 1)
            {
                var full = LoadQuestLookupInfo(connection, matches[0].Id, true);
                if (full != null)
                {
                    if (linkedAccountId > 0 && linkedCharacterId > 0)
                        ApplyQuestCharacterStatus(full, linkedAccountId, linkedCharacterId);
                    matches.Clear();
                    matches.Add(full);
                }
            }

            Logger.Info($"Discord quest lookup completed: '{query}' -> {matches.Count} result(s) ({requestId})");
            _ = Instance?.PostEventAsync(new
            {
                type = "quest_lookup_snapshot",
                requestId,
                ok = true,
                query,
                quests = matches.Select(q => new
                {
                    id = q.Id,
                    name = q.Name,
                    level = q.Level,
                    categoryId = q.CategoryId,
                    categoryName = q.CategoryName,
                    zoneId = q.ZoneId,
                    zoneName = q.ZoneName,
                    detailId = q.DetailId,
                    repeatable = q.Repeatable,
                    successive = q.Successive,
                    isDaily = q.IsDaily,
                    isScheduled = q.IsScheduled,
                    isWeeklyScheduled = q.IsWeeklyScheduled,
                    scheduleAvailableNow = q.ScheduleAvailableNow,
                    schedules = q.Schedules.Select(s => new
                    {
                        scheduleId = s.ScheduleId,
                        isWeekly = s.IsWeekly,
                        dayOfWeekId = s.DayOfWeekId,
                        dayName = s.DayName,
                        startTimeUtc = s.StartTimeUtc,
                        endTimeUtc = s.EndTimeUtc,
                        isActiveNow = s.IsActiveNow
                    }).ToList(),
                    questTypeLabel = q.QuestTypeLabel,
                    characterStatusKnown = q.CharacterStatusKnown,
                    characterName = q.CharacterName,
                    characterOnline = q.CharacterOnline,
                    characterQuestActive = q.CharacterQuestActive,
                    characterCompleted = q.CharacterCompleted,
                    summary = q.Summary,
                    startNpcId = q.StartNpcId,
                    startNpcName = q.StartNpcName,
                    reportNpcId = q.ReportNpcId,
                    reportNpcName = q.ReportNpcName,
                    objectives = q.Objectives,
                    rewards = q.Rewards
                }).ToList()
            });
        }
        catch (Exception ex)
        {
            Logger.Warn($"Discord quest lookup failed for '{query}': {ex.Message}");
            _ = Instance?.PostEventAsync(new
            {
                type = "quest_lookup_snapshot",
                requestId,
                ok = false,
                error = "AAEmu could not load quest data."
            });
        }
    }

    private static string ResolveZoneName(uint zoneId)
    {
        if (zoneId == 0)
            return "Unknown zone";

        try
        {
            // Quest data can store either a zone key or, in some data sets, a zone-group id.
            // Prefer the zone -> group relationship so Discord shows the player-facing region name.
            var zone = ZoneManager.Instance.GetZoneByKey(zoneId);
            if (zone != null)
            {
                var groupId = zone.GroupId;
                if (groupId > 0)
                {
                    var groupName = ZoneManager.Instance.GetZoneGroupById(groupId)?.Name;
                    if (!string.IsNullOrWhiteSpace(groupName))
                        return groupName;
                }

                if (!string.IsNullOrWhiteSpace(zone.Name))
                    return zone.Name;
            }

            var directGroupName = ZoneManager.Instance.GetZoneGroupById(zoneId)?.Name;
            if (!string.IsNullOrWhiteSpace(directGroupName))
                return directGroupName;
        }
        catch (Exception ex)
        {
            Logger.Debug($"Quest zone resolution failed for {zoneId}: {ex.Message}");
        }

        return $"Zone {zoneId}";
    }

    private static QuestLookupInfo LoadQuestLookupInfo(SqliteConnection connection, uint questId, bool includeDetails)
    {
        uint id;
        string rawName;
        byte level;
        uint categoryId;
        uint zoneId;
        uint detailId;
        bool repeatable;
        bool successive;

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = @"
SELECT id, name, level, category_id, zone_id, detail_id, repeatable, successive
FROM quest_contexts
WHERE id = @id
LIMIT 1";
            cmd.Parameters.AddWithValue("@id", questId);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
                return null;

            id = Convert.ToUInt32(reader["id"]);
            rawName = reader["name"]?.ToString() ?? string.Empty;
            level = Convert.ToByte(reader["level"] is DBNull ? 0 : reader["level"]);
            categoryId = Convert.ToUInt32(reader["category_id"] is DBNull ? 0 : reader["category_id"]);
            zoneId = Convert.ToUInt32(reader["zone_id"] is DBNull ? 0 : reader["zone_id"]);
            detailId = Convert.ToUInt32(reader["detail_id"] is DBNull ? 0 : reader["detail_id"]);
            repeatable = ReadSqliteBool(reader["repeatable"]);
            successive = ReadSqliteBool(reader["successive"]);
        }

        var name = LocalizationManager.Instance.Get("quest_contexts", "name", id, rawName) ?? rawName;
        var categoryName = LoadLocalizedTableName(connection, "quest_categories", categoryId, "Unknown");
        var zoneName = ResolveZoneName(zoneId);
        var isDaily = IsDailyQuestDetail(detailId);
        var schedules = LoadQuestSchedules(connection, id);
        var isScheduled = schedules.Count > 0;
        var isWeeklyScheduled = schedules.Any(s => s.IsWeekly);
        var scheduleAvailableNow = schedules.Any(s => s.IsWeekly && s.IsActiveNow);
        var questTypeLabel = GetQuestTypeLabel(isDaily, isWeeklyScheduled, isScheduled, repeatable);
        var summary = includeDetails ? LoadQuestSummary(connection, id) : string.Empty;
        var (startNpcId, startNpcName) = includeDetails ? LoadQuestContact(connection, id, true) : (0u, string.Empty);
        var (reportNpcId, reportNpcName) = includeDetails ? LoadQuestContact(connection, id, false) : (0u, string.Empty);
        var objectives = includeDetails ? LoadQuestObjectives(connection, id) : [];
        var rewards = includeDetails ? LoadQuestRewards(connection, id, level) : [];

        return new QuestLookupInfo
        {
            Id = id,
            Name = name,
            Level = level,
            CategoryId = categoryId,
            CategoryName = categoryName,
            ZoneId = zoneId,
            ZoneName = zoneName,
            DetailId = detailId,
            Repeatable = repeatable,
            Successive = successive,
            IsDaily = isDaily,
            IsScheduled = isScheduled,
            IsWeeklyScheduled = isWeeklyScheduled,
            ScheduleAvailableNow = scheduleAvailableNow,
            Schedules = schedules,
            QuestTypeLabel = questTypeLabel,
            Summary = summary,
            StartNpcId = startNpcId,
            StartNpcName = startNpcName,
            ReportNpcId = reportNpcId,
            ReportNpcName = reportNpcName,
            Objectives = objectives,
            Rewards = rewards
        };
    }

    private static bool IsDailyQuestDetail(uint detailId)
    {
        // Matches CharacterQuests.ResetDailyQuests(): Daily, DailyHunt,
        // DailyLivelihood and DailyGroup are reset at the daily reset.
        return detailId is 7 or 10 or 11 or 12;
    }

    private static List<QuestScheduleInfo> LoadQuestSchedules(SqliteConnection connection, uint questId)
    {
        var result = new List<QuestScheduleInfo>();
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
SELECT gs.id, gs.day_of_week_id, gs.start_time, gs.start_time_min,
       gs.end_time, gs.end_time_min, gs.st_year, gs.st_month, gs.st_day,
       gs.ed_year, gs.ed_month, gs.ed_day
FROM game_schedule_quests gsq
JOIN game_schedules gs ON gs.id = gsq.game_schedule_id
WHERE gsq.quest_id = @id
ORDER BY gs.day_of_week_id, gs.start_time, gs.start_time_min, gs.id";
            cmd.Parameters.AddWithValue("@id", questId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var scheduleId = Convert.ToInt32(reader["id"]);
                var dayId = Convert.ToInt32(reader["day_of_week_id"] is DBNull ? 8 : reader["day_of_week_id"]);
                var startHour = Convert.ToInt32(reader["start_time"] is DBNull ? 0 : reader["start_time"]);
                var startMinute = Convert.ToInt32(reader["start_time_min"] is DBNull ? 0 : reader["start_time_min"]);
                var endHour = Convert.ToInt32(reader["end_time"] is DBNull ? 0 : reader["end_time"]);
                var endMinute = Convert.ToInt32(reader["end_time_min"] is DBNull ? 0 : reader["end_time_min"]);
                var hasFixedStartDate = Convert.ToInt32(reader["st_year"] is DBNull ? 0 : reader["st_year"]) > 0 &&
                                        Convert.ToInt32(reader["st_month"] is DBNull ? 0 : reader["st_month"]) > 0 &&
                                        Convert.ToInt32(reader["st_day"] is DBNull ? 0 : reader["st_day"]) > 0;
                var hasFixedEndDate = Convert.ToInt32(reader["ed_year"] is DBNull ? 0 : reader["ed_year"]) > 0 &&
                                      Convert.ToInt32(reader["ed_month"] is DBNull ? 0 : reader["ed_month"]) > 0 &&
                                      Convert.ToInt32(reader["ed_day"] is DBNull ? 0 : reader["ed_day"]) > 0;
                var isWeekly = dayId is >= 1 and <= 7 && !hasFixedStartDate && !hasFixedEndDate;

                result.Add(new QuestScheduleInfo
                {
                    ScheduleId = scheduleId,
                    IsWeekly = isWeekly,
                    DayOfWeekId = dayId,
                    DayName = QuestScheduleDayName(dayId),
                    StartTimeUtc = $"{startHour:00}:{startMinute:00}",
                    EndTimeUtc = $"{endHour:00}:{endMinute:00}",
                    IsActiveNow = isWeekly && IsWeeklyQuestWindowActive(dayId, startHour, startMinute, endHour, endMinute)
                });
            }
        }
        catch (Exception ex)
        {
            Logger.Debug($"Quest schedule lookup failed for quest {questId}: {ex.Message}");
        }

        return result;
    }

    private static string QuestScheduleDayName(int dayId) => dayId switch
    {
        1 => "Sunday",
        2 => "Monday",
        3 => "Tuesday",
        4 => "Wednesday",
        5 => "Thursday",
        6 => "Friday",
        7 => "Saturday",
        _ => "Dated event"
    };

    private static bool IsWeeklyQuestWindowActive(int scheduledDayId, int startHour, int startMinute, int endHour, int endMinute)
    {
        var now = DateTime.UtcNow;
        var currentDayId = (int)now.DayOfWeek + 1;
        var previousDayId = currentDayId == 1 ? 7 : currentDayId - 1;
        var currentMinute = now.Hour * 60 + now.Minute;
        var start = Math.Clamp(startHour, 0, 23) * 60 + Math.Clamp(startMinute, 0, 59);
        var end = Math.Clamp(endHour, 0, 23) * 60 + Math.Clamp(endMinute, 0, 59);

        if (start <= end)
            return currentDayId == scheduledDayId && currentMinute >= start && currentMinute <= end;

        // A window such as Saturday 22:00-02:00 remains active after UTC midnight Sunday.
        return (currentDayId == scheduledDayId && currentMinute >= start) ||
               (previousDayId == scheduledDayId && currentMinute <= end);
    }

    private static string GetQuestTypeLabel(bool isDaily, bool isWeeklyScheduled, bool isScheduled, bool repeatable)
    {
        if (isDaily)
            return "📅 Daily";
        if (isWeeklyScheduled)
            return "📆 Weekly Schedule";
        if (isScheduled)
            return "🗓️ Scheduled/Event";
        if (repeatable)
            return "🔁 Repeatable";
        return "📘 One-time";
    }

    private static void ApplyQuestCharacterStatus(QuestLookupInfo quest, uint accountId, uint characterId)
    {
        try
        {
            using var connection = MySQL.CreateConnection();

            string characterName;
            DateTime leaveTime;
            using (var characterCommand = connection.CreateCommand())
            {
                characterCommand.CommandText = @"
SELECT name, leave_time
FROM characters
WHERE id = @character_id
  AND account_id = @account_id
  AND deleted = 0
LIMIT 1";
                characterCommand.Parameters.AddWithValue("@character_id", characterId);
                characterCommand.Parameters.AddWithValue("@account_id", accountId);
                characterCommand.Prepare();
                using var reader = characterCommand.ExecuteReader();
                if (!reader.Read())
                    return;
                characterName = reader.GetString("name");
                leaveTime = reader.GetDateTime("leave_time");
            }

            var onlineCharacter = WorldManager.Instance.GetCharacterById(characterId);
            var isOnline = onlineCharacter != null && onlineCharacter.IsOnline && onlineCharacter.AccountId == accountId;

            bool active;
            bool completed;

            if (isOnline)
            {
                // Live in-memory quest state is authoritative while the character is online.
                active = onlineCharacter.Quests.HasQuest(quest.Id);
                completed = onlineCharacter.Quests.HasQuestCompleted(quest.Id);
            }
            else
            {
                using (var activeCommand = connection.CreateCommand())
                {
                    activeCommand.CommandText = "SELECT 1 FROM quests WHERE owner = @owner AND template_id = @quest_id LIMIT 1";
                    activeCommand.Parameters.AddWithValue("@owner", characterId);
                    activeCommand.Parameters.AddWithValue("@quest_id", quest.Id);
                    activeCommand.Prepare();
                    active = activeCommand.ExecuteScalar() != null;
                }

                completed = ReadCompletedQuestFlag(connection, characterId, quest.Id);

                // AAEmu resets these daily quest types at UTC midnight for online characters,
                // and on next login for characters that were offline across the reset. If the
                // DB still contains yesterday's bit while the character is offline, report the
                // effective current-cycle state rather than the stale persisted bit.
                if (quest.IsDaily && !active && leaveTime.Date < DateTime.UtcNow.Date)
                    completed = false;
            }

            quest.CharacterStatusKnown = true;
            quest.CharacterName = characterName;
            quest.CharacterOnline = isOnline;
            quest.CharacterQuestActive = active;
            quest.CharacterCompleted = completed;
        }
        catch (Exception ex)
        {
            Logger.Debug($"Quest character status lookup failed for account {accountId}, character {characterId}, quest {quest.Id}: {ex.Message}");
        }
    }

    private static bool ReadCompletedQuestFlag(MySql.Data.MySqlClient.MySqlConnection connection, uint characterId, uint questId)
    {
        var blockId = questId / 64u;
        var bitIndex = (int)(questId % 64u);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT data FROM completed_quests WHERE owner = @owner AND id = @block_id LIMIT 1";
        command.Parameters.AddWithValue("@owner", characterId);
        command.Parameters.AddWithValue("@block_id", blockId);
        command.Prepare();

        var value = command.ExecuteScalar();
        if (value is not byte[] data || data.Length == 0)
            return false;

        var byteIndex = bitIndex / 8;
        var bitInByte = bitIndex % 8;
        if (byteIndex >= data.Length)
            return false;

        return (data[byteIndex] & (1 << bitInByte)) != 0;
    }

    private static bool ReadSqliteBool(object value)
    {
        if (value == null || value is DBNull)
            return false;
        var text = value.ToString()?.Trim() ?? string.Empty;
        return text == "1" || text.Equals("t", StringComparison.OrdinalIgnoreCase) || text.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    private static string LoadLocalizedTableName(SqliteConnection connection, string table, uint id, string fallback)
    {
        if (id == 0)
            return fallback;
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"SELECT name FROM {table} WHERE id = @id LIMIT 1";
            cmd.Parameters.AddWithValue("@id", id);
            var raw = cmd.ExecuteScalar()?.ToString() ?? fallback;
            return LocalizationManager.Instance.Get(table, "name", id, raw) ?? raw;
        }
        catch
        {
            return fallback;
        }
    }

    private static string LoadQuestSummary(SqliteConnection connection, uint questId)
    {
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
SELECT id, text
FROM quest_context_texts
WHERE quest_context_id = @id AND quest_context_text_kind_id = 1
ORDER BY id
LIMIT 1";
            cmd.Parameters.AddWithValue("@id", questId);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
                return string.Empty;
            var textId = Convert.ToUInt32(reader["id"]);
            var raw = reader["text"]?.ToString() ?? string.Empty;
            var localized = LocalizationManager.Instance.Get("quest_context_texts", "text", textId, raw) ?? raw;
            return CleanQuestText(localized);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string CleanQuestText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;
        var text = value.Replace("\\n", " ").Replace("\r", " ").Replace("\n", " ").Trim();
        while (text.Contains("  ", StringComparison.Ordinal))
            text = text.Replace("  ", " ", StringComparison.Ordinal);
        return text.Length > 900 ? text[..897] + "..." : text;
    }

    private static (uint Id, string Name) LoadQuestContact(SqliteConnection connection, uint questId, bool start)
    {
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = start
                ? @"
SELECT COALESCE(a_npc.npc_id, c.npc_id) AS npc_id
FROM quest_components c
LEFT JOIN quest_acts a ON a.quest_component_id = c.id AND a.act_detail_type = 'QuestActConAcceptNpc'
LEFT JOIN quest_act_con_accept_npcs a_npc ON a_npc.id = a.act_detail_id
WHERE c.quest_context_id = @qid AND c.component_kind_id = 2
  AND COALESCE(a_npc.npc_id, c.npc_id, 0) > 0
ORDER BY c.id
LIMIT 1"
                : @"
SELECT COALESCE(r.npc_id, c.npc_id) AS npc_id
FROM quest_components c
LEFT JOIN quest_acts a ON a.quest_component_id = c.id AND a.act_detail_type = 'QuestActConReportNpc'
LEFT JOIN quest_act_con_report_npcs r ON r.id = a.act_detail_id
WHERE c.quest_context_id = @qid
  AND COALESCE(r.npc_id, c.npc_id, 0) > 0
  AND (a.id IS NOT NULL OR c.component_kind_id IN (6,8))
ORDER BY CASE WHEN a.id IS NOT NULL THEN 0 ELSE 1 END, c.id
LIMIT 1";
            cmd.Parameters.AddWithValue("@qid", questId);
            var value = cmd.ExecuteScalar();
            if (value == null || value is DBNull)
                return (0, string.Empty);
            var npcId = Convert.ToUInt32(value);
            var npc = NpcManager.Instance.GetTemplate(npcId);
            var name = npc == null
                ? $"NPC {npcId}"
                : LocalizationManager.Instance.Get("npcs", "name", npc.Id, npc.Name) ?? npc.Name ?? $"NPC {npcId}";
            return (npcId, name);
        }
        catch
        {
            return (0, string.Empty);
        }
    }

    private static List<string> LoadQuestObjectives(SqliteConnection connection, uint questId)
    {
        var results = new List<string>();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
SELECT a.act_detail_id, a.act_detail_type
FROM quest_components c
JOIN quest_acts a ON a.quest_component_id = c.id
WHERE c.quest_context_id = @qid AND c.component_kind_id = 4
ORDER BY c.id, a.id";
        cmd.Parameters.AddWithValue("@qid", questId);
        using var reader = cmd.ExecuteReader();
        var acts = new List<(uint Id, string Type)>();
        while (reader.Read())
            acts.Add((Convert.ToUInt32(reader["act_detail_id"]), reader["act_detail_type"]?.ToString() ?? string.Empty));
        reader.Close();

        foreach (var act in acts)
        {
            var text = DecodeQuestObjective(connection, act.Type, act.Id);
            if (!string.IsNullOrWhiteSpace(text) && !results.Contains(text, StringComparer.OrdinalIgnoreCase))
                results.Add(text);
            if (results.Count >= 20)
                break;
        }
        return results;
    }

    private static string DecodeQuestObjective(SqliteConnection connection, string type, uint id)
    {
        try
        {
            switch (type)
            {
                case "QuestActObjMonsterHunt":
                {
                    var row = ReadQuestActRow(connection, "quest_act_obj_monster_hunts", id, "npc_id", "count");
                    if (row == null) return null;
                    var npcId = Convert.ToUInt32(row[0]);
                    return $"Defeat {GetNpcDisplayName(npcId)} ×{Convert.ToInt32(row[1])}";
                }
                case "QuestActObjTalk":
                {
                    var row = ReadQuestActRow(connection, "quest_act_obj_talks", id, "npc_id");
                    return row == null ? null : $"Talk to {GetNpcDisplayName(Convert.ToUInt32(row[0]))}";
                }
                case "QuestActObjZoneNpcTalk":
                {
                    var row = ReadQuestActRow(connection, "quest_act_obj_zone_npc_talks", id, "npc_id");
                    return row == null ? null : $"Talk to {GetNpcDisplayName(Convert.ToUInt32(row[0]))}";
                }
                case "QuestActObjItemGather":
                {
                    var row = ReadQuestActRow(connection, "quest_act_obj_item_gathers", id, "item_id", "count");
                    if (row == null) return null;
                    return $"Collect {GetItemDisplayName(Convert.ToUInt32(row[0]))} ×{Convert.ToInt32(row[1])}";
                }
                case "QuestActObjItemUse":
                {
                    var row = ReadQuestActRow(connection, "quest_act_obj_item_uses", id, "item_id", "count");
                    if (row == null) return null;
                    return $"Use {GetItemDisplayName(Convert.ToUInt32(row[0]))} ×{Convert.ToInt32(row[1])}";
                }
                case "QuestActObjCraft":
                {
                    var row = ReadQuestActRow(connection, "quest_act_obj_crafts", id, "craft_id", "count");
                    if (row == null) return null;
                    return $"Craft recipe {row[0]} ×{Convert.ToInt32(row[1])}";
                }
                case "QuestActObjLevel":
                {
                    var row = ReadQuestActRow(connection, "quest_act_obj_levels", id, "level");
                    return row == null ? null : $"Reach level {row[0]}";
                }
                case "QuestActObjCompleteQuest":
                {
                    var row = ReadQuestActRow(connection, "quest_act_obj_complete_quests", id, "quest_id");
                    if (row == null) return null;
                    var otherId = Convert.ToUInt32(row[0]);
                    return $"Complete quest {GetQuestDisplayName(connection, otherId)} [{otherId}]";
                }
                case "QuestActObjZoneMonsterHunt":
                {
                    var row = ReadQuestActRow(connection, "quest_act_obj_zone_monster_hunts", id, "zone_id", "count");
                    if (row == null) return null;
                    return $"Defeat enemies in {ResolveZoneName(Convert.ToUInt32(row[0]))} ×{Convert.ToInt32(row[1])}";
                }
                default:
                    return null;
            }
        }
        catch
        {
            return null;
        }
    }

    private static List<string> LoadQuestRewards(SqliteConnection connection, uint questId, byte level)
    {
        var results = new List<string>();

        // AAEmu adds level-based EXP/copper rewards at the Reward step.
        if (level > 0)
        {
            try
            {
                using var baseCmd = connection.CreateCommand();
                baseCmd.CommandText = "SELECT exp, copper FROM quest_supplies WHERE level = @level LIMIT 1";
                baseCmd.Parameters.AddWithValue("@level", level);
                using var baseReader = baseCmd.ExecuteReader();
                if (baseReader.Read())
                {
                    var exp = Convert.ToInt32(baseReader["exp"] is DBNull ? 0 : baseReader["exp"]);
                    var copper = Convert.ToInt32(baseReader["copper"] is DBNull ? 0 : baseReader["copper"]);
                    if (exp > 0) results.Add($"{exp:N0} EXP (base level reward)");
                    if (copper > 0) results.Add($"{FormatCopper(copper)} (base level reward)");
                }
            }
            catch { }
        }

        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
SELECT a.act_detail_id, a.act_detail_type
FROM quest_components c
JOIN quest_acts a ON a.quest_component_id = c.id
WHERE c.quest_context_id = @qid AND c.component_kind_id = 8
ORDER BY c.id, a.id";
        cmd.Parameters.AddWithValue("@qid", questId);
        using var reader = cmd.ExecuteReader();
        var acts = new List<(uint Id, string Type)>();
        while (reader.Read())
            acts.Add((Convert.ToUInt32(reader["act_detail_id"]), reader["act_detail_type"]?.ToString() ?? string.Empty));
        reader.Close();

        foreach (var act in acts)
        {
            var reward = DecodeQuestReward(connection, act.Type, act.Id);
            if (!string.IsNullOrWhiteSpace(reward) && !results.Contains(reward, StringComparer.OrdinalIgnoreCase))
                results.Add(reward);
            if (results.Count >= 20)
                break;
        }
        return results;
    }

    private static string DecodeQuestReward(SqliteConnection connection, string type, uint id)
    {
        try
        {
            switch (type)
            {
                case "QuestActSupplyItem":
                {
                    var row = ReadQuestActRow(connection, "quest_act_supply_items", id, "item_id", "count");
                    if (row == null) return null;
                    return $"{GetItemDisplayName(Convert.ToUInt32(row[0]))} ×{Convert.ToInt32(row[1])}";
                }
                case "QuestActSupplySelectiveItem":
                {
                    var row = ReadQuestActRow(connection, "quest_act_supply_selective_items", id, "item_id", "count");
                    if (row == null) return null;
                    return $"Choose: {GetItemDisplayName(Convert.ToUInt32(row[0]))} ×{Convert.ToInt32(row[1])}";
                }
                case "QuestActSupplyCopper":
                {
                    var row = ReadQuestActRow(connection, "quest_act_supply_coppers", id, "amount");
                    return row == null ? null : FormatCopper(Convert.ToInt32(row[0]));
                }
                case "QuestActSupplyExp":
                {
                    var row = ReadQuestActRow(connection, "quest_act_supply_exps", id, "exp");
                    return row == null ? null : $"{Convert.ToInt32(row[0]):N0} EXP";
                }
                case "QuestActSupplyHonorPoint":
                {
                    var row = ReadQuestActRow(connection, "quest_act_supply_honor_points", id, "point");
                    return row == null ? null : $"{Convert.ToInt32(row[0]):N0} Honor Points";
                }
                case "QuestActSupplyLivingPoint":
                {
                    var row = ReadQuestActRow(connection, "quest_act_supply_living_points", id, "point");
                    return row == null ? null : $"{Convert.ToInt32(row[0]):N0} Vocation Badges";
                }
                case "QuestActSupplyLp":
                {
                    var row = ReadQuestActRow(connection, "quest_act_supply_lps", id, "lp");
                    return row == null ? null : $"{Convert.ToInt32(row[0]):N0} Labor";
                }
                case "QuestActSupplyAppellation":
                {
                    var row = ReadQuestActRow(connection, "quest_act_supply_appellations", id, "appellation_id");
                    return row == null ? null : $"Title/Appellation ID {row[0]}";
                }
                default:
                    return null;
            }
        }
        catch
        {
            return null;
        }
    }

    private static object[] ReadQuestActRow(SqliteConnection connection, string table, uint id, params string[] columns)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {string.Join(", ", columns)} FROM {table} WHERE id = @id LIMIT 1";
        cmd.Parameters.AddWithValue("@id", id);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;
        var values = new object[columns.Length];
        for (var i = 0; i < columns.Length; i++)
            values[i] = reader.IsDBNull(i) ? 0 : reader.GetValue(i);
        return values;
    }

    private static string GetNpcDisplayName(uint npcId)
    {
        if (npcId == 0)
            return "Unknown NPC";
        var npc = NpcManager.Instance.GetTemplate(npcId);
        if (npc == null)
            return $"NPC {npcId}";
        return LocalizationManager.Instance.Get("npcs", "name", npc.Id, npc.Name) ?? npc.Name ?? $"NPC {npcId}";
    }

    private static string GetItemDisplayName(uint itemId)
    {
        if (itemId == 0)
            return "Unknown item";
        var item = ItemManager.Instance.GetTemplate(itemId);
        if (item == null)
            return $"Item {itemId}";
        return LocalizationManager.Instance.Get("items", "name", item.Id, item.Name) ?? item.Name ?? $"Item {itemId}";
    }

    private static string GetQuestDisplayName(SqliteConnection connection, uint questId)
    {
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT name FROM quest_contexts WHERE id = @id LIMIT 1";
            cmd.Parameters.AddWithValue("@id", questId);
            var raw = cmd.ExecuteScalar()?.ToString() ?? $"Quest {questId}";
            return LocalizationManager.Instance.Get("quest_contexts", "name", questId, raw) ?? raw;
        }
        catch
        {
            return $"Quest {questId}";
        }
    }

    private static string FormatCopper(int totalCopper)
    {
        if (totalCopper <= 0)
            return "0 Copper";
        var gold = totalCopper / 10000;
        var silver = (totalCopper % 10000) / 100;
        var copper = totalCopper % 100;
        var parts = new List<string>();
        if (gold > 0) parts.Add($"{gold:N0} Gold");
        if (silver > 0) parts.Add($"{silver} Silver");
        if (copper > 0 || parts.Count == 0) parts.Add($"{copper} Copper");
        return string.Join(" ", parts);
    }

    private static void HandleSkillLookupRequest(JsonElement command)
    {
        var requestId = GetString(command, "requestId");
        if (string.IsNullOrWhiteSpace(requestId))
            return;

        var query = GetString(command, "query").Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            _ = Instance?.PostEventAsync(new
            {
                type = "skill_lookup_snapshot",
                requestId,
                ok = false,
                error = "Skill query is empty."
            });
            return;
        }

        TryGetUInt(command, "accountId", out var accountId);
        TryGetUInt(command, "characterId", out var characterId);
        var requestedCharacterName = GetString(command, "characterName").Trim();

        try
        {
            Logger.Info($"Discord skill lookup request received: '{query}' ({requestId})");
            var dbPath = FindCompactDbPath();
            if (string.IsNullOrWhiteSpace(dbPath))
                throw new FileNotFoundException("compact.sqlite3 could not be found in the AAEmu data directories.");

            using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
            connection.Open();
            var ids = new List<uint>();

            if (uint.TryParse(query, out var skillId) && skillId > 0)
            {
                using var check = connection.CreateCommand();
                check.CommandText = "SELECT 1 FROM skills WHERE id = @id LIMIT 1";
                check.Parameters.AddWithValue("@id", skillId);
                if (check.ExecuteScalar() != null)
                    ids.Add(skillId);
            }
            else
            {
                var started = DateTime.UtcNow;
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT id, name FROM skills WHERE name IS NOT NULL ORDER BY id";
                using var reader = cmd.ExecuteReader();
                while (reader.Read() && ids.Count < 10)
                {
                    var id = Convert.ToUInt32(reader["id"]);
                    var rawName = reader["name"]?.ToString() ?? string.Empty;
                    var localizedName = LocalizationManager.Instance.Get("skills", "name", id, rawName) ?? rawName;
                    if (rawName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        localizedName.Contains(query, StringComparison.OrdinalIgnoreCase))
                        ids.Add(id);
                    if ((DateTime.UtcNow - started).TotalSeconds >= 6)
                        break;
                }
            }

            var detailed = ids.Count == 1;
            var skills = ids.Select(id => LoadSkillLookupInfo(connection, id, detailed, accountId, characterId, requestedCharacterName)).Where(x => x != null).ToList();

            _ = Instance?.PostEventAsync(new
            {
                type = "skill_lookup_snapshot",
                requestId,
                ok = true,
                query,
                skills
            });
            Logger.Info($"Discord skill lookup completed: '{query}' -> {skills.Count} result(s) ({requestId})");
        }
        catch (Exception ex)
        {
            Logger.Warn($"Discord skill lookup failed for '{query}': {ex.Message}");
            _ = Instance?.PostEventAsync(new
            {
                type = "skill_lookup_snapshot",
                requestId,
                ok = false,
                error = "AAEmu could not load skill data."
            });
        }
    }

    private static object LoadSkillLookupInfo(SqliteConnection connection, uint skillId, bool includeDetails, uint accountId, uint characterId, string requestedCharacterName)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
SELECT id, name, desc, category_id, ability_id, mana_cost, mana_level_md, cooldown_time, casting_time,
       channeling_time, ability_level, min_range, max_range, auto_learn, need_learn,
       skill_points, consume_lp, crime_point
FROM skills
WHERE id = @id
LIMIT 1";
        cmd.Parameters.AddWithValue("@id", skillId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;

        var rawName = reader["name"]?.ToString() ?? string.Empty;
        var rawDesc = reader["desc"]?.ToString() ?? string.Empty;
        var name = LocalizationManager.Instance.Get("skills", "name", skillId, rawName) ?? rawName;
        var desc = LocalizationManager.Instance.Get("skills", "desc", skillId, rawDesc) ?? rawDesc;
        var template = SkillManager.Instance.GetSkillTemplate(skillId);

        var reagents = includeDetails ? LoadSkillItemLinks(connection, "skill_reagents", skillId) : [];
        var products = includeDetails ? LoadSkillItemLinks(connection, "skill_products", skillId) : [];

        var abilityId = Convert.ToInt32(reader["ability_id"] is DBNull ? 0 : reader["ability_id"]);
        var categoryId = Convert.ToInt32(reader["category_id"] is DBNull ? 0 : reader["category_id"]);
        var abilityName = template?.AbilityId.ToString() ?? (abilityId > 0 ? $"Ability {abilityId}" : "Common/Other");
        var categoryName = template?.Category.ToString() ?? $"Category {categoryId}";
        var targetType = template?.TargetType.ToString() ?? "Unknown";
        var staticManaCost = Convert.ToInt32(reader["mana_cost"] is DBNull ? 0 : reader["mana_cost"]);
        var manaLevelMd = Convert.ToDouble(reader["mana_level_md"] is DBNull ? 0d : reader["mana_level_md"]);
        var manaInfo = ResolveCharacterSkillMana(template, accountId, characterId, requestedCharacterName, staticManaCost, manaLevelMd);

        return new
        {
            id = skillId,
            name,
            description = CleanQuestText(desc),
            categoryId,
            categoryName,
            abilityId,
            abilityName,
            manaCost = staticManaCost,
            manaLevelMd,
            characterManaCost = manaInfo.ManaCost,
            characterAbilityLevel = manaInfo.AbilityLevel,
            characterName = manaInfo.CharacterName,
            manaCostSource = manaInfo.Source,
            cooldownTime = Convert.ToInt32(reader["cooldown_time"] is DBNull ? 0 : reader["cooldown_time"]),
            castingTime = Convert.ToInt32(reader["casting_time"] is DBNull ? 0 : reader["casting_time"]),
            channelingTime = Convert.ToInt32(reader["channeling_time"] is DBNull ? 0 : reader["channeling_time"]),
            abilityLevel = Convert.ToInt32(reader["ability_level"] is DBNull ? 0 : reader["ability_level"]),
            minRange = Convert.ToInt32(reader["min_range"] is DBNull ? 0 : reader["min_range"]),
            maxRange = Convert.ToInt32(reader["max_range"] is DBNull ? 0 : reader["max_range"]),
            autoLearn = ReadSqliteBool(reader["auto_learn"]),
            needLearn = ReadSqliteBool(reader["need_learn"]),
            skillPoints = Convert.ToInt32(reader["skill_points"] is DBNull ? 0 : reader["skill_points"]),
            consumeLaborPower = Convert.ToInt32(reader["consume_lp"] is DBNull ? 0 : reader["consume_lp"]),
            crimePoint = Convert.ToInt32(reader["crime_point"] is DBNull ? 0 : reader["crime_point"]),
            targetType,
            reagents,
            products
        };
    }

    private static (int? ManaCost, int? AbilityLevel, string CharacterName, string Source) ResolveCharacterSkillMana(
        AAEmu.Game.Models.Game.Skills.Templates.SkillTemplate template,
        uint accountId,
        uint characterId,
        string requestedCharacterName,
        int staticManaCost,
        double manaLevelMd)
    {
        if (template == null || characterId == 0 || accountId == 0)
            return (null, null, requestedCharacterName ?? string.Empty, string.Empty);

        try
        {
            var onlineCharacter = WorldManager.Instance.GetCharacterById(characterId);
            if (onlineCharacter != null && onlineCharacter.IsOnline && onlineCharacter.AccountId == accountId)
            {
                var liveSkill = new Skill(template, onlineCharacter);
                return (
                    liveSkill.ManaCost(onlineCharacter),
                    onlineCharacter.GetAbLevel(template.AbilityId),
                    onlineCharacter.Name,
                    "live"
                );
            }
        }
        catch (Exception ex)
        {
            Logger.Debug($"Live skill mana lookup failed for character {characterId}: {ex.Message}");
        }

        try
        {
            using var db = MySQL.CreateConnection();
            using var cmd = db.CreateCommand();
            cmd.CommandText = @"
SELECT c.name, a.exp
FROM characters c
LEFT JOIN abilities a ON a.owner = c.id AND a.id = @ability_id
WHERE c.id = @character_id
  AND c.account_id = @account_id
  AND c.deleted = 0
LIMIT 1";
            cmd.Parameters.AddWithValue("@ability_id", (byte)template.AbilityId);
            cmd.Parameters.AddWithValue("@character_id", characterId);
            cmd.Parameters.AddWithValue("@account_id", accountId);
            cmd.Prepare();
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
                return (null, null, requestedCharacterName ?? string.Empty, string.Empty);

            var characterName = reader.GetString("name");
            var abilityExp = reader.IsDBNull(reader.GetOrdinal("exp")) ? 0 : reader.GetInt32("exp");
            var abilityLevel = ExperienceManager.Instance.GetLevelFromExp(abilityExp, out _);
            if (template.AbilityId == AbilityType.General)
            {
                // General skills use character level rather than a skillset level.
                reader.Close();
                using var levelCmd = db.CreateCommand();
                levelCmd.CommandText = "SELECT level FROM characters WHERE id = @character_id AND account_id = @account_id LIMIT 1";
                levelCmd.Parameters.AddWithValue("@character_id", characterId);
                levelCmd.Parameters.AddWithValue("@account_id", accountId);
                abilityLevel = Convert.ToByte(levelCmd.ExecuteScalar() ?? 1);
            }

            var baseCost = ((abilityLevel - 1) * 1.6 + 8) * 3 / 3.65;
            var calculated = (int)(baseCost * manaLevelMd + staticManaCost);
            return (calculated, abilityLevel, characterName, "saved");
        }
        catch (Exception ex)
        {
            Logger.Debug($"Saved skill mana lookup failed for character {characterId}: {ex.Message}");
            return (null, null, requestedCharacterName ?? string.Empty, string.Empty);
        }
    }

    private static List<object> LoadSkillItemLinks(SqliteConnection connection, string table, uint skillId)
    {
        var results = new List<object>();
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"SELECT item_id, amount FROM {table} WHERE skill_id = @id ORDER BY id LIMIT 20";
            cmd.Parameters.AddWithValue("@id", skillId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var itemId = Convert.ToUInt32(reader["item_id"]);
                results.Add(new
                {
                    itemId,
                    itemName = GetItemDisplayName(itemId),
                    amount = Convert.ToInt32(reader["amount"] is DBNull ? 0 : reader["amount"])
                });
            }
        }
        catch { }
        return results;
    }

    private static void HandleVocationLookupRequest(JsonElement command)
    {
        var requestId = GetString(command, "requestId");
        if (string.IsNullOrWhiteSpace(requestId))
            return;

        if (!TryGetUInt(command, "accountId", out var accountId) || accountId == 0 ||
            !TryGetUInt(command, "characterId", out var characterId) || characterId == 0)
        {
            _ = Instance?.PostEventAsync(new
            {
                type = "vocation_lookup_snapshot",
                requestId,
                ok = false,
                error = "A verified linked character is required."
            });
            return;
        }

        try
        {
            string characterName;
            int savedVocationPoints;
            var savedProficiencies = new List<(uint Id, int Point, byte Step)>();

            using (var gameConnection = MySQL.CreateConnection())
            {
                using (var characterCommand = gameConnection.CreateCommand())
                {
                    characterCommand.CommandText = @"
SELECT name, vocation_point
FROM characters
WHERE id = @character_id
  AND account_id = @account_id
  AND deleted = 0
LIMIT 1";
                    characterCommand.Parameters.AddWithValue("@character_id", characterId);
                    characterCommand.Parameters.AddWithValue("@account_id", accountId);
                    characterCommand.Prepare();
                    using var reader = characterCommand.ExecuteReader();
                    if (!reader.Read())
                        throw new InvalidOperationException("The selected character does not belong to the linked account.");
                    characterName = reader.GetString("name");
                    savedVocationPoints = reader.GetInt32("vocation_point");
                }

                using var proficiencyCommand = gameConnection.CreateCommand();
                proficiencyCommand.CommandText = @"
SELECT id, point, step
FROM actabilities
WHERE owner = @character_id
ORDER BY id";
                proficiencyCommand.Parameters.AddWithValue("@character_id", characterId);
                proficiencyCommand.Prepare();
                using var proficiencyReader = proficiencyCommand.ExecuteReader();
                while (proficiencyReader.Read())
                {
                    var id = proficiencyReader.GetUInt32("id");
                    if (!IsVocationActabilityId(id))
                        continue;
                    savedProficiencies.Add((id, proficiencyReader.GetInt32("point"), proficiencyReader.GetByte("step")));
                }
            }

            var onlineCharacter = WorldManager.Instance.GetCharacterById(characterId);
            var isOnline = onlineCharacter != null && onlineCharacter.IsOnline && onlineCharacter.AccountId == accountId;
            var vocationPoints = isOnline ? onlineCharacter.VocationPoint : savedVocationPoints;
            var source = isOnline
                ? onlineCharacter.Actability.Actabilities.Values
                    .Where(value => IsVocationActabilityId(value.Id))
                    .Select(value => (value.Id, value.Point, value.Step))
                    .ToList()
                : savedProficiencies;

            var dbPath = FindCompactDbPath();
            if (string.IsNullOrWhiteSpace(dbPath))
                throw new FileNotFoundException("compact.sqlite3 could not be found in the AAEmu data directories.");

            using var compactConnection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
            compactConnection.Open();
            var proficiencies = source
                .Select(value =>
                {
                    var rank = LoadVocationRank(compactConnection, value.Step);
                    return new
                    {
                        id = value.Id,
                        name = LoadActabilityName(compactConnection, value.Id),
                        points = value.Point,
                        step = value.Step,
                        rankName = rank.Name,
                        rankLimit = rank.Limit
                    };
                })
                .OrderByDescending(value => value.points)
                .ThenBy(value => value.name)
                .ToList();

            _ = Instance?.PostEventAsync(new
            {
                type = "vocation_lookup_snapshot",
                requestId,
                ok = true,
                accountId,
                characterId,
                characterName,
                characterOnline = isOnline,
                vocationPoints,
                proficiencies
            });
        }
        catch (Exception ex)
        {
            Logger.Warn($"Discord vocation lookup failed for account {accountId}, character {characterId}: {ex.Message}");
            _ = Instance?.PostEventAsync(new
            {
                type = "vocation_lookup_snapshot",
                requestId,
                ok = false,
                error = "AAEmu could not load vocation data for that linked character."
            });
        }
    }

    private static bool IsVocationActabilityId(uint id)
    {
        return (id >= 1 && id <= 18) || id == 20 || id == 31 || id == 33 || id == 34;
    }

    private static void HandleHonorLookupRequest(JsonElement command)
    {
        var requestId = GetString(command, "requestId");
        if (string.IsNullOrWhiteSpace(requestId))
            return;

        if (!TryGetUInt(command, "accountId", out var accountId) || accountId == 0 ||
            !TryGetUInt(command, "characterId", out var characterId) || characterId == 0)
        {
            _ = Instance?.PostEventAsync(new
            {
                type = "honor_lookup_snapshot",
                requestId,
                ok = false,
                error = "A verified linked character is required."
            });
            return;
        }

        try
        {
            string characterName;
            int savedHonorPoints;
            uint savedPvpHonor;
            uint savedHostileFactionKills;

            using (var gameConnection = MySQL.CreateConnection())
            using (var characterCommand = gameConnection.CreateCommand())
            {
                characterCommand.CommandText = @"
SELECT name, honor_point, pvp_honor, hostile_faction_kills
FROM characters
WHERE id = @character_id
  AND account_id = @account_id
  AND deleted = 0
LIMIT 1";
                characterCommand.Parameters.AddWithValue("@character_id", characterId);
                characterCommand.Parameters.AddWithValue("@account_id", accountId);
                characterCommand.Prepare();
                using var reader = characterCommand.ExecuteReader();
                if (!reader.Read())
                    throw new InvalidOperationException("The selected character does not belong to the linked account.");
                characterName = reader.GetString("name");
                savedHonorPoints = reader.GetInt32("honor_point");
                savedPvpHonor = reader.GetUInt32("pvp_honor");
                savedHostileFactionKills = reader.GetUInt32("hostile_faction_kills");
            }

            var onlineCharacter = WorldManager.Instance.GetCharacterById(characterId);
            var isOnline = onlineCharacter != null && onlineCharacter.IsOnline && onlineCharacter.AccountId == accountId;

            _ = Instance?.PostEventAsync(new
            {
                type = "honor_lookup_snapshot",
                requestId,
                ok = true,
                accountId,
                characterId,
                characterName,
                characterOnline = isOnline,
                honorPoints = isOnline ? onlineCharacter.HonorPoint : savedHonorPoints,
                pvpHonor = isOnline ? onlineCharacter.HonorGainedInCombat : savedPvpHonor,
                hostileFactionKills = isOnline ? onlineCharacter.HostileFactionKills : savedHostileFactionKills
            });
        }
        catch (Exception ex)
        {
            Logger.Warn($"Discord honor lookup failed for account {accountId}, character {characterId}: {ex.Message}");
            _ = Instance?.PostEventAsync(new
            {
                type = "honor_lookup_snapshot",
                requestId,
                ok = false,
                error = "AAEmu could not load honor data for that linked character."
            });
        }
    }

    private static (string Name, int Limit) LoadVocationRank(SqliteConnection connection, byte step)
    {
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
SELECT id, name, up_limit
FROM expert_limits
ORDER BY up_limit ASC
LIMIT 1 OFFSET @step";
            cmd.Parameters.AddWithValue("@step", step);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
                return ($"Rank {step}", 0);
            var id = Convert.ToUInt32(reader["id"]);
            var rawName = reader["name"]?.ToString() ?? $"Rank {step}";
            var name = LocalizationManager.Instance.Get("expert_limits", "name", id, rawName) ?? rawName;
            var limit = Convert.ToInt32(reader["up_limit"] is DBNull ? 0 : reader["up_limit"]);
            return (name, limit);
        }
        catch
        {
            return ($"Rank {step}", 0);
        }
    }

    private static void HandleTitleLookupRequest(JsonElement command)
    {
        var requestId = GetString(command, "requestId");
        if (string.IsNullOrWhiteSpace(requestId))
            return;

        var query = GetString(command, "query").Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            _ = Instance?.PostEventAsync(new
            {
                type = "title_lookup_snapshot",
                requestId,
                ok = false,
                error = "Title query is empty."
            });
            return;
        }

        TryGetUInt(command, "accountId", out var accountId);
        TryGetUInt(command, "characterId", out var characterId);
        var requestedCharacterName = GetString(command, "characterName").Trim();

        try
        {
            Logger.Info($"Discord title lookup request received: '{query}' ({requestId})");
            var dbPath = FindCompactDbPath();
            if (string.IsNullOrWhiteSpace(dbPath))
                throw new FileNotFoundException("compact.sqlite3 could not be found in the AAEmu data directories.");

            using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
            connection.Open();
            var ids = new List<uint>();

            if (uint.TryParse(query, out var titleId) && titleId > 0)
            {
                using var check = connection.CreateCommand();
                check.CommandText = "SELECT 1 FROM appellations WHERE id = @id LIMIT 1";
                check.Parameters.AddWithValue("@id", titleId);
                if (check.ExecuteScalar() != null)
                    ids.Add(titleId);
            }
            else
            {
                var started = DateTime.UtcNow;
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT id, name FROM appellations WHERE name IS NOT NULL ORDER BY id";
                using var reader = cmd.ExecuteReader();
                while (reader.Read() && ids.Count < 10)
                {
                    var id = Convert.ToUInt32(reader["id"]);
                    var rawName = reader["name"]?.ToString() ?? string.Empty;
                    var localizedName = LocalizationManager.Instance.Get("appellations", "name", id, rawName) ?? rawName;
                    if (rawName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        localizedName.Contains(query, StringComparison.OrdinalIgnoreCase))
                        ids.Add(id);

                    if ((DateTime.UtcNow - started).TotalSeconds >= 6)
                        break;
                }
            }

            var detailed = ids.Count == 1;
            var titles = ids
                .Select(id => LoadTitleLookupInfo(connection, id, detailed, accountId, characterId, requestedCharacterName))
                .Where(x => x != null)
                .ToList();

            _ = Instance?.PostEventAsync(new
            {
                type = "title_lookup_snapshot",
                requestId,
                ok = true,
                query,
                titles
            });
            Logger.Info($"Discord title lookup completed: '{query}' -> {titles.Count} result(s) ({requestId})");
        }
        catch (Exception ex)
        {
            Logger.Warn($"Discord title lookup failed for '{query}': {ex.Message}");
            _ = Instance?.PostEventAsync(new
            {
                type = "title_lookup_snapshot",
                requestId,
                ok = false,
                error = "AAEmu could not load title data."
            });
        }
    }

    private static object LoadTitleLookupInfo(
        SqliteConnection connection,
        uint titleId,
        bool includeCharacterStatus,
        uint accountId,
        uint characterId,
        string requestedCharacterName)
    {
        string rawName;
        uint buffId;

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT id, name, buff_id FROM appellations WHERE id = @id LIMIT 1";
            cmd.Parameters.AddWithValue("@id", titleId);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
                return null;

            rawName = reader["name"]?.ToString() ?? string.Empty;
            buffId = Convert.ToUInt32(reader["buff_id"] is DBNull ? 0 : reader["buff_id"]);
        }

        var name = LocalizationManager.Instance.Get("appellations", "name", titleId, rawName) ?? rawName;
        var characterStatusKnown = false;
        var characterOwned = false;
        var characterActive = false;
        var characterName = requestedCharacterName ?? string.Empty;

        if (includeCharacterStatus && accountId > 0 && characterId > 0)
        {
            try
            {
                using var db = MySQL.CreateConnection();

                using (var characterCmd = db.CreateCommand())
                {
                    characterCmd.CommandText = @"
SELECT name
FROM characters
WHERE id = @character_id
  AND account_id = @account_id
  AND deleted = 0
LIMIT 1";
                    characterCmd.Parameters.AddWithValue("@character_id", characterId);
                    characterCmd.Parameters.AddWithValue("@account_id", accountId);
                    var dbName = characterCmd.ExecuteScalar()?.ToString();
                    if (!string.IsNullOrWhiteSpace(dbName))
                    {
                        characterName = dbName;
                        characterStatusKnown = true;
                    }
                }

                if (characterStatusKnown)
                {
                    using var ownedCmd = db.CreateCommand();
                    ownedCmd.CommandText = @"
SELECT active
FROM appellations
WHERE owner = @owner
  AND id = @id
LIMIT 1";
                    ownedCmd.Parameters.AddWithValue("@owner", characterId);
                    ownedCmd.Parameters.AddWithValue("@id", titleId);
                    var activeValue = ownedCmd.ExecuteScalar();
                    if (activeValue != null && activeValue != DBNull.Value)
                    {
                        characterOwned = true;
                        characterActive = Convert.ToInt32(activeValue) != 0;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"Title ownership lookup failed for character {characterId}, title {titleId}: {ex.Message}");
                characterStatusKnown = false;
            }
        }

        return new
        {
            id = titleId,
            name,
            buffId,
            characterStatusKnown,
            characterName,
            characterOwned,
            characterActive
        };
    }

    private static void HandleRecipeLookupRequest(JsonElement command)
    {
        var requestId = GetString(command, "requestId");
        if (string.IsNullOrWhiteSpace(requestId))
            return;

        var query = GetString(command, "query").Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            _ = Instance?.PostEventAsync(new
            {
                type = "recipe_lookup_snapshot",
                requestId,
                ok = false,
                error = "Recipe query is empty."
            });
            return;
        }

        try
        {
            Logger.Info($"Discord recipe lookup request received: '{query}' ({requestId})");
            var dbPath = FindCompactDbPath();
            if (string.IsNullOrWhiteSpace(dbPath))
                throw new FileNotFoundException("compact.sqlite3 could not be found in the AAEmu data directories.");

            using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
            connection.Open();

            var ids = new List<uint>();
            if (uint.TryParse(query, out var recipeId) && recipeId > 0)
            {
                using var check = connection.CreateCommand();
                check.CommandText = "SELECT 1 FROM crafts WHERE id = @id LIMIT 1";
                check.Parameters.AddWithValue("@id", recipeId);
                if (check.ExecuteScalar() != null)
                    ids.Add(recipeId);
            }
            else
            {
                var started = DateTime.UtcNow;
                var candidates = new List<(uint Id, string Title)>();
                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = "SELECT id, title FROM crafts ORDER BY id";
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        candidates.Add((Convert.ToUInt32(reader["id"]), reader["title"]?.ToString() ?? string.Empty));
                    }
                }

                foreach (var candidate in candidates)
                {
                    var localizedTitle = LocalizationManager.Instance.Get("crafts", "title", candidate.Id, candidate.Title) ?? candidate.Title;
                    var matched = candidate.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                                  localizedTitle.Contains(query, StringComparison.OrdinalIgnoreCase);

                    if (!matched)
                    {
                        using var productCmd = connection.CreateCommand();
                        productCmd.CommandText = @"
SELECT i.id, i.name
FROM craft_products p
JOIN items i ON i.id = p.item_id
WHERE p.craft_id = @craft_id";
                        productCmd.Parameters.AddWithValue("@craft_id", candidate.Id);
                        using var productReader = productCmd.ExecuteReader();
                        while (productReader.Read())
                        {
                            var itemId = Convert.ToUInt32(productReader["id"]);
                            var rawName = productReader["name"]?.ToString() ?? string.Empty;
                            var itemName = LocalizationManager.Instance.Get("items", "name", itemId, rawName) ?? rawName;
                            if (rawName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                                itemName.Contains(query, StringComparison.OrdinalIgnoreCase))
                            {
                                matched = true;
                                break;
                            }
                        }
                    }

                    if (matched)
                        ids.Add(candidate.Id);

                    if (ids.Count >= 10 || (DateTime.UtcNow - started).TotalSeconds >= 8)
                        break;
                }
            }

            var detailed = ids.Count == 1;
            var recipes = ids.Select(id => LoadRecipeLookupInfo(connection, id, detailed)).Where(x => x != null).ToList();

            _ = Instance?.PostEventAsync(new
            {
                type = "recipe_lookup_snapshot",
                requestId,
                ok = true,
                query,
                recipes
            });
            Logger.Info($"Discord recipe lookup completed: '{query}' -> {recipes.Count} result(s) ({requestId})");
        }
        catch (Exception ex)
        {
            Logger.Warn($"Discord recipe lookup failed for '{query}': {ex.Message}");
            _ = Instance?.PostEventAsync(new
            {
                type = "recipe_lookup_snapshot",
                requestId,
                ok = false,
                error = "AAEmu could not load crafting data."
            });
        }
    }

    private static object LoadRecipeLookupInfo(SqliteConnection connection, uint recipeId, bool includeDetails)
    {
        string rawTitle;
        string rawDesc;
        int castDelay;
        uint toolId;
        uint skillId;
        uint wiId;
        uint milestoneId;
        uint reqDoodadId;
        bool needBind;
        uint acId;
        int actabilityLimit;
        bool showUpperCrafts;
        int recommendLevel;
        int visibleOrder;

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = @"
SELECT id, title, cast_delay, tool_id, skill_id, wi_id, desc, milestone_id,
       req_doodad_id, need_bind, ac_id, actability_limit, show_upper_crafts,
       recommend_level, visible_order
FROM crafts
WHERE id = @id
LIMIT 1";
            cmd.Parameters.AddWithValue("@id", recipeId);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
                return null;

            rawTitle = reader["title"]?.ToString() ?? string.Empty;
            rawDesc = reader["desc"]?.ToString() ?? string.Empty;
            castDelay = Convert.ToInt32(reader["cast_delay"] is DBNull ? 0 : reader["cast_delay"]);
            toolId = Convert.ToUInt32(reader["tool_id"] is DBNull ? 0 : reader["tool_id"]);
            skillId = Convert.ToUInt32(reader["skill_id"] is DBNull ? 0 : reader["skill_id"]);
            wiId = Convert.ToUInt32(reader["wi_id"] is DBNull ? 0 : reader["wi_id"]);
            milestoneId = Convert.ToUInt32(reader["milestone_id"] is DBNull ? 0 : reader["milestone_id"]);
            reqDoodadId = Convert.ToUInt32(reader["req_doodad_id"] is DBNull ? 0 : reader["req_doodad_id"]);
            needBind = ReadSqliteBool(reader["need_bind"]);
            acId = Convert.ToUInt32(reader["ac_id"] is DBNull ? 0 : reader["ac_id"]);
            actabilityLimit = Convert.ToInt32(reader["actability_limit"] is DBNull ? 0 : reader["actability_limit"]);
            showUpperCrafts = ReadSqliteBool(reader["show_upper_crafts"]);
            recommendLevel = Convert.ToInt32(reader["recommend_level"] is DBNull ? 0 : reader["recommend_level"]);
            visibleOrder = Convert.ToInt32(reader["visible_order"] is DBNull ? 0 : reader["visible_order"]);
        }

        var title = LocalizationManager.Instance.Get("crafts", "title", recipeId, rawTitle) ?? rawTitle;
        var desc = LocalizationManager.Instance.Get("crafts", "desc", recipeId, rawDesc) ?? rawDesc;
        var actabilityGroupId = acId > 0 ? CharacterManager.Instance.GetActabilityIdByCategoryId(acId) : 0;
        if (actabilityGroupId == 0 && acId > 0)
            actabilityGroupId = ResolveActabilityGroupId(connection, acId);
        var actabilityName = actabilityGroupId > 0 ? LoadActabilityName(connection, actabilityGroupId) : string.Empty;
        var workstationName = reqDoodadId > 0 ? LoadDoodadName(connection, reqDoodadId) : string.Empty;
        var materials = includeDetails ? LoadRecipeMaterials(connection, recipeId) : [];
        var products = LoadRecipeProducts(connection, recipeId, includeDetails ? 30 : 3);
        var isPack = IsPackRecipe(connection, recipeId);

        return new
        {
            id = recipeId,
            title,
            description = CleanQuestText(desc),
            castDelay,
            toolId,
            skillId,
            wiId,
            milestoneId,
            reqDoodadId,
            workstationName,
            needBind,
            actabilityCategoryId = acId,
            actabilityId = actabilityGroupId,
            actabilityName,
            actabilityLimit,
            showUpperCrafts,
            recommendLevel,
            visibleOrder,
            isPack,
            materials,
            products
        };
    }

    private static List<object> LoadRecipeMaterials(SqliteConnection connection, uint recipeId)
    {
        var results = new List<object>();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
SELECT item_id, amount, main_grade, require_grade
FROM craft_materials
WHERE craft_id = @id
ORDER BY id
LIMIT 40";
        cmd.Parameters.AddWithValue("@id", recipeId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var itemId = Convert.ToUInt32(reader["item_id"]);
            results.Add(new
            {
                itemId,
                itemName = GetItemDisplayName(itemId),
                amount = Convert.ToInt32(reader["amount"] is DBNull ? 1 : reader["amount"]),
                mainGrade = ReadSqliteBool(reader["main_grade"]),
                requireGrade = Convert.ToInt32(reader["require_grade"] is DBNull ? 0 : reader["require_grade"])
            });
        }
        return results;
    }

    private static List<object> LoadRecipeProducts(SqliteConnection connection, uint recipeId, int limit)
    {
        var results = new List<object>();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
SELECT item_id, amount, rate, show_lower_crafts, use_grade, item_grade_id
FROM craft_products
WHERE craft_id = @id
ORDER BY id
LIMIT @limit";
        cmd.Parameters.AddWithValue("@id", recipeId);
        cmd.Parameters.AddWithValue("@limit", limit);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var itemId = Convert.ToUInt32(reader["item_id"]);
            results.Add(new
            {
                itemId,
                itemName = GetItemDisplayName(itemId),
                amount = Convert.ToInt32(reader["amount"] is DBNull ? 1 : reader["amount"]),
                rate = Convert.ToInt32(reader["rate"] is DBNull ? 0 : reader["rate"]),
                showLowerCrafts = ReadSqliteBool(reader["show_lower_crafts"]),
                useGrade = ReadSqliteBool(reader["use_grade"]),
                itemGradeId = Convert.ToUInt32(reader["item_grade_id"] is DBNull ? 0 : reader["item_grade_id"])
            });
        }
        return results;
    }

    private static string LoadActabilityName(SqliteConnection connection, uint actabilityId)
    {
        var officialName = OfficialActabilityName(actabilityId);
        if (!string.IsNullOrWhiteSpace(officialName))
            return officialName;

        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT name FROM actability_groups WHERE id = @id LIMIT 1";
            cmd.Parameters.AddWithValue("@id", actabilityId);
            var raw = cmd.ExecuteScalar()?.ToString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(raw))
                return $"Proficiency {actabilityId}";
            return LocalizationManager.Instance.Get("actability_groups", "name", actabilityId, raw) ?? raw;
        }
        catch
        {
            return $"Proficiency {actabilityId}";
        }
    }

    private static uint ResolveActabilityGroupId(SqliteConnection connection, uint categoryId)
    {
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT group_id FROM actability_categories WHERE id = @id LIMIT 1";
            cmd.Parameters.AddWithValue("@id", categoryId);
            var value = cmd.ExecuteScalar();
            return value == null || value is DBNull ? 0 : Convert.ToUInt32(value);
        }
        catch
        {
            return 0;
        }
    }

    private static string OfficialActabilityName(uint id)
    {
        return id switch
        {
            1 => "Alchemy",
            2 => "Construction",
            3 => "Cooking",
            4 => "Handicrafts",
            5 => "Husbandry",
            6 => "Farming",
            7 => "Fishing",
            8 => "Logging",
            9 => "Gathering",
            10 => "Machining",
            11 => "Metalwork",
            12 => "Printing",
            13 => "Mining",
            14 => "Masonry",
            15 => "Tailoring",
            16 => "Leatherwork",
            17 => "Weaponry",
            18 => "Carpentry",
            19 => "Quest",
            20 => "Larceny",
            21 => "Nuian Language",
            22 => "Elven Language",
            23 => "Dwarven Language",
            24 => "Faerie Language",
            25 => "Harani Language",
            26 => "Firran Language",
            27 => "Warborn Language",
            28 => "Returned Language",
            29 => "Nuia Continent Dialect",
            30 => "Haranya Continent Dialect",
            31 => "Commerce",
            32 => "Commemorative Event",
            33 => "Artistry",
            34 => "Exploration",
            _ => string.Empty
        };
    }

    private static string LoadDoodadName(SqliteConnection connection, uint doodadId)
    {
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT name FROM doodad_almighties WHERE id = @id LIMIT 1";
            cmd.Parameters.AddWithValue("@id", doodadId);
            var raw = cmd.ExecuteScalar()?.ToString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(raw))
                return string.Empty;
            return LocalizationManager.Instance.Get("doodad_almighties", "name", doodadId, raw) ?? raw;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool IsPackRecipe(SqliteConnection connection, uint recipeId)
    {
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM craft_pack_crafts WHERE craft_id = @id LIMIT 1";
            cmd.Parameters.AddWithValue("@id", recipeId);
            return cmd.ExecuteScalar() != null;
        }
        catch
        {
            return false;
        }
    }

    private static void HandleAchievementLookupRequest(JsonElement command)
    {
        var requestId = GetString(command, "requestId");
        if (string.IsNullOrWhiteSpace(requestId))
            return;

        var query = GetString(command, "query").Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            _ = Instance?.PostEventAsync(new
            {
                type = "achievement_lookup_snapshot",
                requestId,
                ok = false,
                error = "Achievement query is empty."
            });
            return;
        }

        try
        {
            Logger.Info($"Discord achievement lookup request received: '{query}' ({requestId})");
            var dbPath = FindCompactDbPath();
            if (string.IsNullOrWhiteSpace(dbPath))
                throw new FileNotFoundException("compact.sqlite3 could not be found in the AAEmu data directories.");

            using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
            connection.Open();
            var ids = new List<uint>();

            if (uint.TryParse(query, out var achievementId) && achievementId > 0)
            {
                using var check = connection.CreateCommand();
                check.CommandText = "SELECT 1 FROM achievements WHERE id = @id LIMIT 1";
                check.Parameters.AddWithValue("@id", achievementId);
                if (check.ExecuteScalar() != null)
                    ids.Add(achievementId);
            }
            else
            {
                var started = DateTime.UtcNow;
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT id, name FROM achievements WHERE name IS NOT NULL ORDER BY id";
                using var reader = cmd.ExecuteReader();
                while (reader.Read() && ids.Count < 10)
                {
                    var id = Convert.ToUInt32(reader["id"]);
                    var rawName = reader["name"]?.ToString() ?? string.Empty;
                    var localizedName = LocalizationManager.Instance.Get("achievements", "name", id, rawName) ?? rawName;
                    if (rawName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        localizedName.Contains(query, StringComparison.OrdinalIgnoreCase))
                        ids.Add(id);

                    if ((DateTime.UtcNow - started).TotalSeconds >= 6)
                        break;
                }
            }

            var detailed = ids.Count == 1;
            var achievements = ids
                .Select(id => LoadAchievementLookupInfo(connection, id, detailed))
                .Where(x => x != null)
                .ToList();

            _ = Instance?.PostEventAsync(new
            {
                type = "achievement_lookup_snapshot",
                requestId,
                ok = true,
                query,
                achievements
            });
            Logger.Info($"Discord achievement lookup completed: '{query}' -> {achievements.Count} result(s) ({requestId})");
        }
        catch (Exception ex)
        {
            Logger.Warn($"Discord achievement lookup failed for '{query}': {ex.Message}");
            _ = Instance?.PostEventAsync(new
            {
                type = "achievement_lookup_snapshot",
                requestId,
                ok = false,
                error = "AAEmu could not load achievement data."
            });
        }
    }

    private static object LoadAchievementLookupInfo(SqliteConnection connection, uint achievementId, bool includeDetails)
    {
        string rawName;
        string rawSummary;
        string rawDescription;
        uint categoryId;
        uint subCategoryId;
        uint parentAchievementId;
        bool isActive;
        bool isHidden;
        uint completeNum;
        uint rewardItemId;

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = @"
SELECT id, name, summary, description, category_id, sub_category_id,
       parent_achievement_id, is_active, is_hidden, complete_num, item_id
FROM achievements
WHERE id = @id
LIMIT 1";
            cmd.Parameters.AddWithValue("@id", achievementId);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
                return null;

            rawName = reader["name"]?.ToString() ?? string.Empty;
            rawSummary = reader["summary"]?.ToString() ?? string.Empty;
            rawDescription = reader["description"]?.ToString() ?? string.Empty;
            categoryId = Convert.ToUInt32(reader["category_id"] is DBNull ? 0 : reader["category_id"]);
            subCategoryId = Convert.ToUInt32(reader["sub_category_id"] is DBNull ? 0 : reader["sub_category_id"]);
            parentAchievementId = Convert.ToUInt32(reader["parent_achievement_id"] is DBNull ? 0 : reader["parent_achievement_id"]);
            isActive = ReadSqliteBool(reader["is_active"]);
            isHidden = ReadSqliteBool(reader["is_hidden"]);
            completeNum = Convert.ToUInt32(reader["complete_num"] is DBNull ? 0 : reader["complete_num"]);
            rewardItemId = Convert.ToUInt32(reader["item_id"] is DBNull ? 0 : reader["item_id"]);
        }

        var name = LocalizationManager.Instance.Get("achievements", "name", achievementId, rawName) ?? rawName;
        var summary = LocalizationManager.Instance.Get("achievements", "summary", achievementId, rawSummary) ?? rawSummary;
        var description = LocalizationManager.Instance.Get("achievements", "description", achievementId, rawDescription) ?? rawDescription;
        var parentAchievementName = parentAchievementId > 0 ? GetAchievementDisplayName(connection, parentAchievementId) : string.Empty;
        var rewardItemName = rewardItemId > 0 ? GetItemDisplayName(rewardItemId) : string.Empty;
        var objectives = includeDetails ? LoadAchievementObjectives(connection, achievementId) : [];
        var prerequisites = includeDetails ? LoadAchievementPrerequisites(connection, achievementId) : [];

        return new
        {
            id = achievementId,
            name,
            summary = CleanQuestText(summary),
            description = CleanQuestText(description),
            categoryId,
            categoryName = AchievementCategoryName(categoryId),
            subCategoryId,
            subCategoryName = AchievementSubCategoryName(categoryId, subCategoryId),
            parentAchievementId,
            parentAchievementName,
            isActive,
            isHidden,
            completeNum,
            rewardItemId,
            rewardItemName,
            objectives,
            prerequisites
        };
    }

    private static string AchievementCategoryName(uint categoryId) => categoryId switch
    {
        1 => "Normal",
        2 => "Combat",
        3 => "Life",
        4 => "Adventure",
        5 => "Society",
        6 => "Level",
        7 => "Season",
        8 => "Event",
        _ => categoryId > 0 ? $"Category {categoryId}" : "Unknown"
    };

    private static string AchievementSubCategoryName(uint categoryId, uint subCategoryId)
    {
        if (subCategoryId == 0)
            return "General";

        return categoryId switch
        {
            1 => subCategoryId switch { 1 => "Character", 2 => "Pet", 3 => "Collect", 4 => "Etc", _ => $"Subcategory {subCategoryId}" },
            2 => subCategoryId switch { 1 => "PvP", 2 => "Siege", 3 => "Battle", 4 => "Get", _ => $"Subcategory {subCategoryId}" },
            3 => subCategoryId switch { 1 => "Make", 2 => "Animal Husbandry", 3 => "Trade", 4 => "Furniture", 5 => "Goal", _ => $"Subcategory {subCategoryId}" },
            4 => subCategoryId switch { 1 => "Quest", 2 => "Instance", 3 => "Get", _ => $"Subcategory {subCategoryId}" },
            5 => subCategoryId switch { 1 => "Crime", 2 => "Relation", 3 => "NPC", _ => $"Subcategory {subCategoryId}" },
            6 => subCategoryId switch
            {
                1 => "Level 1-5", 2 => "Level 6-10", 3 => "Level 11-15", 4 => "Level 16-20",
                5 => "Level 21-25", 6 => "Level 26-30", 7 => "Level 31-35", 8 => "Level 36-40",
                9 => "Level 41-45", 10 => "Level 46-50", 11 => "Level 51-55", _ => $"Subcategory {subCategoryId}"
            },
            7 => subCategoryId switch { 1 => "Spring", 2 => "Summer", 3 => "Autumn", 4 => "Winter", _ => $"Subcategory {subCategoryId}" },
            8 => subCategoryId == 1 ? "Event" : $"Subcategory {subCategoryId}",
            _ => $"Subcategory {subCategoryId}"
        };
    }

    private static List<object> LoadAchievementObjectives(SqliteConnection connection, uint achievementId)
    {
        var results = new List<object>();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
SELECT ao.id, ao.record_id, cr.kind_id, cr.value1, cr.value2
FROM achievement_objectives ao
LEFT JOIN char_records cr ON cr.id = ao.record_id
WHERE ao.achievement_id = @id
ORDER BY ao.id
LIMIT 20";
        cmd.Parameters.AddWithValue("@id", achievementId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var recordId = Convert.ToUInt32(reader["record_id"] is DBNull ? 0 : reader["record_id"]);
            var kindId = Convert.ToUInt32(reader["kind_id"] is DBNull ? 0 : reader["kind_id"]);
            var value1 = Convert.ToInt64(reader["value1"] is DBNull ? 0 : reader["value1"]);
            var value2 = Convert.ToInt64(reader["value2"] is DBNull ? 0 : reader["value2"]);
            results.Add(new
            {
                recordId,
                kindId,
                text = FormatAchievementObjective(connection, kindId, value1, value2)
            });
        }
        return results;
    }

    private static string FormatAchievementObjective(SqliteConnection connection, uint kindId, long value1, long value2)
    {
        var kindName = kindId switch
        {
            1 => "PvP kills (enemy)", 2 => "PvP kills (honor war)", 3 => "PvP kills (siege)", 4 => "PvP kills (ally)",
            9 => "Complete achievement", 10 => "Character level", 11 => "Pet level", 12 => "Play time",
            13 => "Win zone battle", 14 => "Own gold", 15 => "Spend gold", 17 => "Gain vocation points",
            18 => "Gain proficiency", 19 => "Gain honor points", 20 => "Spend labor", 21 => "Skillset level",
            25 => "Kill NPC", 27 => "Complete quest category", 28 => "Complete quest type",
            29 => "Get item type", 30 => "Craft item type", 31 => "Get item", 32 => "Craft item",
            33 => "Get loot pack", 34 => "Get loot item", 35 => "Use item", 38 => "Build housing",
            39 => "Auction purchase", 40 => "Auction sale", 41 => "Use skill", 42 => "Gain jury points",
            43 => "Report crime", 44 => "Judgement", 45 => "Win duel", 46 => "Kill wanted player",
            52 => "Join party", 53 => "Join raid", 54 => "Join family", 55 => "Join guild", 56 => "Join nation",
            61 => "Gear score", 62 => "Become hero", 63 => "Hero grade", 64 => "Gain leadership",
            71 => "Battlefield wins", 72 => "Battlefield losses", 73 => "Battlefield draws", 74 => "Battlefield kills",
            75 => "Battlefield deaths", 76 => "Battlefield assists",
            _ => kindId > 0 ? $"Record type {kindId}" : "Objective"
        };

        if (kindId == 9 && value1 > 0)
            return $"{kindName}: {GetAchievementDisplayName(connection, (uint)value1)} [{value1}]";
        if (kindId is 10 or 11 or 12 or 14 or 15 or 17 or 19 or 20 or 42 or 61 or 64 or 71 or 72 or 73 or 74 or 75 or 76)
            return value1 > 0 ? $"{kindName}: {value1:N0}" : kindName;

        var values = new List<string>();
        if (value1 != 0) values.Add($"Value 1: {value1}");
        if (value2 != 0 && value2 != -1) values.Add($"Value 2: {value2}");
        return values.Count > 0 ? $"{kindName} ({string.Join(", ", values)})" : kindName;
    }

    private static List<object> LoadAchievementPrerequisites(SqliteConnection connection, uint achievementId)
    {
        var results = new List<object>();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
SELECT completed_achievement_id
FROM pre_completed_achievements
WHERE my_achievement_id = @id
ORDER BY id
LIMIT 20";
        cmd.Parameters.AddWithValue("@id", achievementId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var id = Convert.ToUInt32(reader["completed_achievement_id"] is DBNull ? 0 : reader["completed_achievement_id"]);
            if (id == 0)
                continue;
            results.Add(new { id, name = GetAchievementDisplayName(connection, id) });
        }
        return results;
    }

    private static string GetAchievementDisplayName(SqliteConnection connection, uint achievementId)
    {
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT name FROM achievements WHERE id = @id LIMIT 1";
            cmd.Parameters.AddWithValue("@id", achievementId);
            var raw = cmd.ExecuteScalar()?.ToString() ?? $"Achievement {achievementId}";
            return LocalizationManager.Instance.Get("achievements", "name", achievementId, raw) ?? raw;
        }
        catch
        {
            return $"Achievement {achievementId}";
        }
    }

    private static void HandleAnnouncement(JsonElement command)
    {
        var message = GetMessage(command);
        if (string.IsNullOrWhiteSpace(message))
            return;

        var sentBy = GetString(command, "discordDisplayName");
        if (string.IsNullOrWhiteSpace(sentBy))
            sentBy = "Discord Administrator";

        // Same AAEmu notice packet used by the built-in /announce command, but invoked
        // directly by the localhost bridge. No dummy/admin character needs to be online.
        WorldManager.Instance.BroadcastPacketToServer(
            new SCNoticeMessagePacket(3, Color.Lime, 10000, message)
        );

        var playerCount = WorldManager.Instance.GetAllCharacters().Count(c => c != null && c.IsOnline);

        _ = Instance?.PostEventAsync(new
        {
            type = "announcement_sent",
            message,
            sentBy,
            playerCount
        });
    }

    private static void HandleMaintenanceControl(JsonElement command)
    {
        var requestId = GetString(command, "requestId");
        var action = GetString(command, "action").ToLowerInvariant();
        var requestedMode = GetString(command, "mode").ToLowerInvariant();
        var requestedBy = GetString(command, "discordDisplayName");
        var reason = GetString(command, "reason");
        var targetExecutable = GetString(command, "targetExecutable");
        if (string.IsNullOrWhiteSpace(targetExecutable))
            targetExecutable = "AAEmu.Game.exe";

        if (string.IsNullOrWhiteSpace(requestedBy))
            requestedBy = "Discord Administrator";

        if (reason.Length > 200)
            reason = reason[..200];

        if (requestedMode == "restart" && targetExecutable.Equals("AAEmu.Login.exe", StringComparison.OrdinalIgnoreCase))
        {
            HandleLoginRestartControl(command, requestId, action, reason, requestedBy);
            return;
        }

        if (!targetExecutable.Equals("AAEmu.Game.exe", StringComparison.OrdinalIgnoreCase))
        {
            PostMaintenanceError(requestId, action, requestedMode, "Unsupported restart target.", false);
            return;
        }

        var restartSupported = ShutdownTask.CanSelfRestart(out _);

        if (action == "status")
        {
            PostMaintenanceState(requestId, true, "status", SaveManager.Instance.ShutdownTask, restartSupported);
            return;
        }

        if (action == "cancel")
        {
            var existing = SaveManager.Instance.ShutdownTask;
            if (existing == null)
            {
                PostMaintenanceError(
                    requestId,
                    "cancel",
                    requestedMode,
                    "No server shutdown/restart is currently scheduled.",
                    restartSupported);
                return;
            }

            var oldMode = existing.RestartAfterShutdown ? "restart" : "maintenance";
            var oldReason = existing.Reason;
            var oldRequestedBy = existing.RequestedBy;
            existing.Cancel();
            SaveManager.Instance.ShutdownTask = null;

            WorldManager.Instance.BroadcastPacketToServer(new SCNoticeMessagePacket(
                3,
                Color.Aqua,
                10000,
                "The scheduled server shutdown/restart has been cancelled!"));

            _ = Instance?.PostEventAsync(new
            {
                type = "maintenance_state",
                requestId,
                ok = true,
                action = "cancel",
                state = "cancelled",
                mode = oldMode,
                reason = oldReason,
                requestedBy = oldRequestedBy,
                restartSupported
            });
            return;
        }

        var isRestart = action == "restart" || requestedMode == "restart";
        if (action != "start" && action != "restart")
        {
            PostMaintenanceError(requestId, action, requestedMode, "Unknown maintenance action.", restartSupported);
            return;
        }

        if (SaveManager.Instance.ShutdownTask != null)
        {
            PostMaintenanceError(
                requestId,
                action,
                requestedMode,
                "A server shutdown/restart is already scheduled. Check status or cancel it first.",
                restartSupported);
            return;
        }

        lock (LoginRestartLock)
        {
            if (_loginRestartCts != null)
            {
                PostMaintenanceError(
                    requestId,
                    action,
                    requestedMode,
                    "An AAEmu.Login restart is already scheduled. Cancel it first.",
                    restartSupported);
                return;
            }
        }

        var minutes = 0;
        if (command.TryGetProperty("minutes", out var minutesElement) && minutesElement.ValueKind == JsonValueKind.Number)
            minutesElement.TryGetInt32(out minutes);

        var maxMinutes = isRestart ? 120 : 1440;
        if (minutes < 1 || minutes > maxMinutes)
        {
            PostMaintenanceError(
                requestId,
                action,
                requestedMode,
                $"Minutes must be between 1 and {maxMinutes}.",
                restartSupported);
            return;
        }

        if (isRestart && !restartSupported)
        {
            ShutdownTask.CanSelfRestart(out var restartDetail);
            PostMaintenanceError(
                requestId,
                action,
                "restart",
                $"Automatic restart is not available: {restartDetail}",
                false);
            return;
        }

        if (!SaveManager.Instance.DoSave())
        {
            PostMaintenanceError(
                requestId,
                action,
                isRestart ? "restart" : "maintenance",
                "AAEmu could not save the game database, so the shutdown/restart was not scheduled.",
                restartSupported);
            return;
        }

        var shutdownTime = DateTime.UtcNow.AddMinutes(minutes).AddSeconds(5);
        var mode = isRestart ? "restart" : "maintenance";
        var task = new ShutdownTask(shutdownTime, -1, isRestart, reason, requestedBy);
        SaveManager.Instance.ShutdownTask = task;
        TaskManager.Instance.Schedule(task, null, TimeSpan.FromSeconds(1));

        WorldManager.Instance.BroadcastPacketToServer(new SCNoticeMessagePacket(
            3,
            Color.Red,
            10000,
            isRestart
                ? $"Server restart scheduled in {minutes} minute{(minutes == 1 ? string.Empty : "s")}. {reason}".Trim()
                : $"Server maintenance shutdown scheduled in {minutes} minute{(minutes == 1 ? string.Empty : "s")}. {reason}".Trim()));

        _ = Instance?.PostEventAsync(new
        {
            type = "maintenance_state",
            requestId,
            ok = true,
            action,
            state = "scheduled",
            mode,
            scheduledFor = IsoUtc(shutdownTime),
            reason,
            requestedBy,
            targetExecutable = "AAEmu.Game.exe",
            restartSupported
        });
    }

    private static void HandleLoginRestartControl(
        JsonElement command,
        string requestId,
        string action,
        string reason,
        string requestedBy)
    {
        var supported = CanRestartLogin(out var supportDetail);

        if (action == "status")
        {
            lock (LoginRestartLock)
            {
                _ = Instance?.PostEventAsync(new
                {
                    type = "maintenance_state",
                    requestId,
                    ok = true,
                    action,
                    state = _loginRestartCts == null ? "none" : "scheduled",
                    mode = "restart",
                    scheduledFor = _loginRestartCts == null ? string.Empty : IsoUtc(_loginRestartAt),
                    reason = _loginRestartReason,
                    requestedBy = _loginRestartRequestedBy,
                    targetExecutable = "AAEmu.Login.exe",
                    restartSupported = supported
                });
            }
            return;
        }

        if (action == "cancel")
        {
            lock (LoginRestartLock)
            {
                if (_loginRestartCts == null)
                {
                    PostMaintenanceError(requestId, action, "restart", "No AAEmu.Login restart is scheduled.", supported);
                    return;
                }

                _loginRestartCts.Cancel();
                _loginRestartCts.Dispose();
                _loginRestartCts = null;
                var oldReason = _loginRestartReason;
                var oldRequestedBy = _loginRestartRequestedBy;
                _loginRestartReason = string.Empty;
                _loginRestartRequestedBy = string.Empty;
                _ = Instance?.PostEventAsync(new
                {
                    type = "maintenance_state",
                    requestId,
                    ok = true,
                    action,
                    state = "cancelled",
                    mode = "restart",
                    reason = oldReason,
                    requestedBy = oldRequestedBy,
                    targetExecutable = "AAEmu.Login.exe",
                    restartSupported = supported
                });
            }
            return;
        }

        if (action != "restart")
        {
            PostMaintenanceError(requestId, action, "restart", "Unknown Login restart action.", supported);
            return;
        }

        if (!supported)
        {
            PostMaintenanceError(requestId, action, "restart", $"AAEmu.Login restart is not available: {supportDetail}", false);
            return;
        }

        if (SaveManager.Instance.ShutdownTask != null)
        {
            PostMaintenanceError(requestId, action, "restart", "An AAEmu.Game shutdown/restart is already scheduled.", true);
            return;
        }

        var minutes = 0;
        if (command.TryGetProperty("minutes", out var minutesElement) && minutesElement.ValueKind == JsonValueKind.Number)
            minutesElement.TryGetInt32(out minutes);
        if (minutes < 1 || minutes > 120)
        {
            PostMaintenanceError(requestId, action, "restart", "Minutes must be between 1 and 120.", true);
            return;
        }

        CancellationTokenSource restartCts;
        DateTime restartAt;
        lock (LoginRestartLock)
        {
            if (_loginRestartCts != null)
            {
                PostMaintenanceError(requestId, action, "restart", "An AAEmu.Login restart is already scheduled.", true);
                return;
            }
            restartCts = new CancellationTokenSource();
            _loginRestartCts = restartCts;
            restartAt = DateTime.UtcNow.AddMinutes(minutes).AddSeconds(5);
            _loginRestartAt = restartAt;
            _loginRestartReason = reason;
            _loginRestartRequestedBy = requestedBy;
        }

        WorldManager.Instance.BroadcastPacketToServer(new SCNoticeMessagePacket(
            3,
            Color.Aqua,
            10000,
            $"Login server restart scheduled in {minutes} minute{(minutes == 1 ? string.Empty : "s")}. {reason}".Trim()));

        _ = RunLoginRestartAsync(restartAt, reason, requestedBy, restartCts);
        _ = Instance?.PostEventAsync(new
        {
            type = "maintenance_state",
            requestId,
            ok = true,
            action,
            state = "scheduled",
            mode = "restart",
            scheduledFor = IsoUtc(restartAt),
            reason,
            requestedBy,
            targetExecutable = "AAEmu.Login.exe",
            restartSupported = true
        });
    }

    private static bool CanRestartLogin(out string detail)
    {
        if (TryFindLoginProcess(out var process, out var executablePath, out detail))
        {
            process.Dispose();
            detail = executablePath;
            return true;
        }
        return false;
    }

    private static bool TryFindLoginProcess(out Process process, out string executablePath, out string detail)
    {
        process = null;
        executablePath = string.Empty;
        detail = "AAEmu.Login.exe is not running as a discoverable standalone process.";
        try
        {
            foreach (var candidate in Process.GetProcessesByName("AAEmu.Login"))
            {
                try
                {
                    var path = candidate.MainModule?.FileName ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(path) && File.Exists(path) &&
                        Path.GetFileName(path).Equals("AAEmu.Login.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        process = candidate;
                        executablePath = path;
                        detail = string.Empty;
                        return true;
                    }
                    candidate.Dispose();
                }
                catch
                {
                    candidate.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            detail = ex.Message;
        }
        return false;
    }

    private static async Task RunLoginRestartAsync(
        DateTime restartAt,
        string reason,
        string requestedBy,
        CancellationTokenSource restartCts)
    {
        string executablePath = string.Empty;
        try
        {
            var delay = restartAt - DateTime.UtcNow;
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, restartCts.Token);

            if (!TryFindLoginProcess(out var process, out executablePath, out var detail))
                throw new InvalidOperationException(detail);

            using (process)
            {
                var workingDirectory = Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory;
                if (!process.CloseMainWindow() || !process.WaitForExit(5000))
                {
                    process.Kill(true);
                    process.WaitForExit(10000);
                }

                using var started = Process.Start(new ProcessStartInfo
                {
                    FileName = executablePath,
                    WorkingDirectory = workingDirectory,
                    UseShellExecute = false
                });
                if (started == null)
                    throw new InvalidOperationException("The replacement AAEmu.Login process did not start.");
                await Task.Delay(TimeSpan.FromSeconds(2));
                if (started.HasExited)
                    throw new InvalidOperationException($"The replacement AAEmu.Login process exited immediately with code {started.ExitCode}.");
            }

            _ = Instance?.PostEventAsync(new
            {
                type = "login_restart_result",
                ok = true,
                reason,
                requestedBy,
                executablePath
            });
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Logger.Error($"AAEmu.Login restart failed: {ex.Message}");
            _ = Instance?.PostEventAsync(new
            {
                type = "login_restart_result",
                ok = false,
                reason,
                requestedBy,
                executablePath,
                error = ex.Message
            });
        }
        finally
        {
            lock (LoginRestartLock)
            {
                if (ReferenceEquals(_loginRestartCts, restartCts))
                {
                    _loginRestartCts.Dispose();
                    _loginRestartCts = null;
                    _loginRestartReason = string.Empty;
                    _loginRestartRequestedBy = string.Empty;
                }
            }
        }
    }

    private static void PostMaintenanceState(
        string requestId,
        bool ok,
        string action,
        ShutdownTask task,
        bool restartSupported,
        string stateOverride = null)
    {
        var state = stateOverride ?? (task == null ? "none" : "scheduled");
        _ = Instance?.PostEventAsync(new
        {
            type = "maintenance_state",
            requestId,
            ok,
            action,
            state,
            mode = task == null ? string.Empty : (task.RestartAfterShutdown ? "restart" : "maintenance"),
            scheduledFor = task == null ? string.Empty : IsoUtc(task.ShutdownTime),
            reason = task?.Reason ?? string.Empty,
            requestedBy = task?.RequestedBy ?? string.Empty,
            targetExecutable = "AAEmu.Game.exe",
            restartSupported
        });
    }

    private static void PostMaintenanceError(
        string requestId,
        string action,
        string mode,
        string error,
        bool restartSupported)
    {
        _ = Instance?.PostEventAsync(new
        {
            type = "maintenance_state",
            requestId,
            ok = false,
            action,
            state = "error",
            mode,
            error,
            targetExecutable = "AAEmu.Game.exe",
            restartSupported
        });
    }

    private static void HandleTeamChat(JsonElement command)
    {
        if (!TryGetUInt(command, "teamId", out var teamId) || teamId == 0)
            return;

        if (!TryGetUInt(command, "accountId", out var accountId) || accountId == 0)
            return;

        var message = GetMessage(command);
        if (string.IsNullOrWhiteSpace(message))
            return;

        var team = TeamManager.Instance.GetActiveTeam(teamId);
        if (team == null)
            return;

        // Temporary Party/Raid chat remains online-only. AAEmu is authoritative:
        // the linked AA account must have an ONLINE character in this exact team.
        var character = team.Members
            .Where(m => m?.Character != null)
            .Select(m => m.Character)
            .FirstOrDefault(c => c.AccountId == accountId && c.IsOnline);

        if (character == null)
            return;

        var chat = team.IsParty
            ? ChatManager.Instance.GetPartyChat(team, character)
            : ChatManager.Instance.GetRaidChat(team);

        chat.SendMessage(character, $"[Discord] {message}");
    }

    private static void HandleLocalChat(JsonElement command)
    {
        if (!TryGetUInt(command, "accountId", out var accountId) || accountId == 0)
            return;

        if (!TryGetUInt(command, "characterId", out var characterId) || characterId == 0)
            return;

        var message = GetMessage(command);
        if (string.IsNullOrWhiteSpace(message))
            return;

        var requestedSide = NormalizeSide(GetString(command, "side"));

        // Local/Say needs a real online character because it is proximity based.
        var character = WorldManager.Instance.GetCharacterById(characterId);
        if (character == null || !character.IsOnline || character.AccountId != accountId)
            return;

        var characterMotherFactionId =
            character.Faction?.MotherId
            ?? FactionsEnum.Invalid;

        if (characterMotherFactionId == FactionsEnum.Invalid)
            characterMotherFactionId = character.Faction?.Id ?? FactionsEnum.Invalid;

        if (!string.IsNullOrWhiteSpace(requestedSide) &&
            !string.Equals(
                FactionSide(characterMotherFactionId),
                requestedSide,
                StringComparison.OrdinalIgnoreCase))
            return;

        character.BroadcastPacket(
            new SCChatMessagePacket(
                ChatType.White,
                character,
                $"[Discord] {message}",
                0,
                0
            ),
            true
        );
    }

    private static void HandlePublicChat(JsonElement command)
    {
        if (!TryGetUInt(command, "accountId", out var accountId) || accountId == 0)
            return;

        _ = TryGetUInt(command, "characterId", out var preferredCharacterId);

        var message = GetMessage(command);
        if (string.IsNullOrWhiteSpace(message))
            return;

        var requestedType = GetString(command, "chatType").ToLowerInvariant();
        var requestedSide = NormalizeSide(GetString(command, "side"));
        var chatType = requestedType switch
        {
            "trade" => ChatType.Trade,
            "groupfind" => ChatType.GroupFind,
            "group_find" => ChatType.GroupFind,
            "needparty" => ChatType.GroupFind,
            "need_party" => ChatType.GroupFind,
            "lfg" => ChatType.GroupFind,
            _ => (ChatType?)null
        };

        if (chatType == null)
        {
            Logger.Warn($"Discord public chat rejected: unsupported chat type '{requestedType}'");
            return;
        }

        if (!TryGetPublicChatIdentity(
                accountId,
                preferredCharacterId,
                requestedSide,
                out var characterName,
                out var factionId,
                out var zoneId))
        {
            Logger.Warn(
                $"Discord public chat identity check failed for account {accountId}, type '{requestedType}'"
            );
            return;
        }

        var chat = ChatManager.Instance.GetZoneChat(zoneId);
        chat.SendPacket(
            new SCChatMessagePacket(
                chatType.Value,
                $"[Discord] {characterName}",
                message,
                factionId
            )
        );
    }

    private static void HandleNationChat(JsonElement command)
    {
        if (!TryGetUInt(command, "accountId", out var accountId) || accountId == 0)
            return;

        _ = TryGetUInt(command, "characterId", out var preferredCharacterId);

        var message = GetMessage(command);
        if (string.IsNullOrWhiteSpace(message))
            return;

        var requestedSide = NormalizeSide(GetString(command, "side"));
        if (!string.Equals(requestedSide, "Nuia", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(requestedSide, "Haranya", StringComparison.OrdinalIgnoreCase))
        {
            Logger.Warn($"Discord nation chat rejected: invalid/missing side '{requestedSide}'");
            return;
        }

        if (!TryGetNationChatIdentity(
                accountId,
                preferredCharacterId,
                requestedSide,
                out var characterName,
                out var factionId,
                out var race))
        {
            Logger.Warn(
                $"Discord nation chat identity check failed for account {accountId}, side '{requestedSide}'"
            );
            return;
        }

        var chat = ChatManager.Instance.GetNationChat(race);
        chat.SendPacket(
            new SCChatMessagePacket(
                ChatType.Region,
                $"[Discord] {characterName}",
                message,
                factionId
            )
        );
    }

    private static void HandlePersistentChat(JsonElement command, string kind)
    {
        if (!TryGetUInt(command, "accountId", out var accountId) || accountId == 0)
            return;

        _ = TryGetUInt(command, "characterId", out var preferredCharacterId);

        var message = GetMessage(command);
        if (string.IsNullOrWhiteSpace(message))
            return;

        // Faction is special: Discord channels are keyed by the localized faction
        // name. Resolve the authoritative faction id + mother faction from AAEmu's
        // database instead of trusting a cached numeric id from links.json.
        if (kind == "faction")
        {
            var requestedSide = NormalizeSide(GetString(command, "side"));

            if (!string.IsNullOrWhiteSpace(requestedSide))
            {
                if (!TryGetSideFactionPersistentChatIdentity(
                        accountId,
                        preferredCharacterId,
                        requestedSide,
                        out var characterName,
                        out var characterFactionId,
                        out var motherFactionId))
                {
                    Logger.Warn(
                        $"Discord faction chat membership check failed for account {accountId}, side '{requestedSide}'"
                    );
                    return;
                }

                var sideChat = ChatManager.Instance.GetFactionChat(motherFactionId);
                sideChat.SendPacket(
                    new SCChatMessagePacket(
                        ChatType.Ally,
                        $"[Discord] {characterName}",
                        message,
                        characterFactionId
                    )
                );
                return;
            }

            // Backward compatibility with v11/v12 faction-name commands.
            var requestedFactionName = GetString(command, "factionName");
            if (string.IsNullOrWhiteSpace(requestedFactionName))
            {
                Logger.Warn("Discord faction chat rejected: missing side/factionName");
                return;
            }

            if (!TryGetFactionPersistentChatIdentity(
                    accountId,
                    preferredCharacterId,
                    requestedFactionName,
                    out var legacyCharacterName,
                    out var legacyCharacterFactionId,
                    out var legacyMotherFactionId))
            {
                Logger.Warn(
                    $"Discord faction chat membership check failed for account {accountId}, faction '{requestedFactionName}'"
                );
                return;
            }

            var legacyChat = ChatManager.Instance.GetFactionChat(legacyMotherFactionId);
            legacyChat.SendPacket(
                new SCChatMessagePacket(
                    ChatType.Ally,
                    $"[Discord] {legacyCharacterName}",
                    message,
                    legacyCharacterFactionId
                )
            );
            return;
        }

        var groupProperty = kind switch
        {
            "guild" => "guildId",
            "family" => "familyId",
            _ => string.Empty
        };

        if (string.IsNullOrWhiteSpace(groupProperty) ||
            !TryGetUInt(command, groupProperty, out var groupId) ||
            groupId == 0)
            return;

        // Persistent channels are intentionally usable while the Discord user is away
        // from the game. Validate membership against AAEmu's game database instead of
        // requiring their character to be online.
        if (!TryGetPersistentChatIdentity(
                accountId,
                preferredCharacterId,
                kind,
                groupId,
                out var persistentCharacterName,
                out var persistentCharacterFactionId))
            return;

        var senderName = $"[Discord] {persistentCharacterName}";

        switch (kind)
        {
            case "guild":
            {
                var expedition =
                    ExpeditionManager.Instance.GetExpedition((FactionsEnum)groupId);

                if (expedition == null)
                    return;

                var chat = ChatManager.Instance.GetGuildChat(expedition);
                chat.SendPacket(
                    new SCChatMessagePacket(
                        ChatType.Clan,
                        senderName,
                        message,
                        persistentCharacterFactionId
                    )
                );
                break;
            }

            case "family":
            {
                var chat = ChatManager.Instance.GetFamilyChat(groupId);
                chat.SendPacket(
                    new SCChatMessagePacket(
                        ChatType.Family,
                        senderName,
                        message,
                        persistentCharacterFactionId
                    )
                );
                break;
            }
        }
    }

    private static string GetMessage(JsonElement command)
    {
        if (!command.TryGetProperty("message", out var messageElement))
            return string.Empty;

        var message = (messageElement.GetString() ?? string.Empty).Trim();

        if (message.Length > 300)
            message = message[..300];

        return message;
    }

    private static string GetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String)
            return string.Empty;

        return (property.GetString() ?? string.Empty).Trim();
    }

    private static bool TryGetNationChatIdentity(
        uint accountId,
        uint preferredCharacterId,
        string requestedSide,
        out string characterName,
        out uint factionId,
        out Race race)
    {
        characterName = string.Empty;
        factionId = 0;
        race = Race.None;

        try
        {
            using var connection = MySQL.CreateConnection();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT id, name, race, faction_id
                FROM characters
                WHERE account_id = @account_id
                  AND deleted = 0
                ORDER BY CASE WHEN id = @character_id THEN 0 ELSE 1 END, id
                """;

            command.Parameters.AddWithValue("@account_id", accountId);
            command.Parameters.AddWithValue("@character_id", preferredCharacterId);
            command.Prepare();

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var rowRace = (Race)reader.GetByte("race");
                var rowSide = NationSide(rowRace);

                if (!string.Equals(
                        rowSide,
                        requestedSide,
                        StringComparison.OrdinalIgnoreCase))
                    continue;

                characterName = reader.GetString("name");
                factionId = reader.GetUInt32("faction_id");
                race = rowRace;

                return !string.IsNullOrWhiteSpace(characterName) && race != Race.None;
            }

            return false;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Discord nation chat identity check failed: {ex.Message}");
            return false;
        }
    }

    private static bool TryGetPublicChatIdentity(
        uint accountId,
        uint preferredCharacterId,
        string requestedSide,
        out string characterName,
        out uint factionId,
        out uint zoneId)
    {
        characterName = string.Empty;
        factionId = 0;
        zoneId = 0;

        try
        {
            using var connection = MySQL.CreateConnection();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT id, name, faction_id, zone_id
                FROM characters
                WHERE account_id = @account_id
                  AND deleted = 0
                ORDER BY CASE WHEN id = @character_id THEN 0 ELSE 1 END, id
                """;

            command.Parameters.AddWithValue("@account_id", accountId);
            command.Parameters.AddWithValue("@character_id", preferredCharacterId);
            command.Prepare();

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var rowFactionId = reader.GetUInt32("faction_id");

                if (!string.IsNullOrWhiteSpace(requestedSide))
                {
                    var faction = FactionManager.Instance.GetFaction((FactionsEnum)rowFactionId);
                    var rowMotherFactionId =
                        faction?.MotherId
                        ?? FactionsEnum.Invalid;

                    if (rowMotherFactionId == FactionsEnum.Invalid)
                        rowMotherFactionId = faction?.Id ?? FactionsEnum.Invalid;

                    var rowSide = FactionSide(rowMotherFactionId);

                    if (!string.Equals(
                            rowSide,
                            requestedSide,
                            StringComparison.OrdinalIgnoreCase))
                        continue;
                }

                characterName = reader.GetString("name");
                factionId = rowFactionId;
                zoneId = reader.GetUInt32("zone_id");

                if (!string.IsNullOrWhiteSpace(characterName) && zoneId > 0)
                    return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Discord public chat identity check failed: {ex.Message}");
            return false;
        }
    }

    private static bool TryGetSideFactionPersistentChatIdentity(
        uint accountId,
        uint preferredCharacterId,
        string requestedSide,
        out string characterName,
        out uint factionId,
        out FactionsEnum motherFactionId)
    {
        characterName = string.Empty;
        factionId = 0;
        motherFactionId = FactionsEnum.Invalid;

        try
        {
            using var connection = MySQL.CreateConnection();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT id, name, faction_id
                FROM characters
                WHERE account_id = @account_id
                  AND deleted = 0
                ORDER BY CASE WHEN id = @character_id THEN 0 ELSE 1 END, id
                """;

            command.Parameters.AddWithValue("@account_id", accountId);
            command.Parameters.AddWithValue("@character_id", preferredCharacterId);
            command.Prepare();

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var rowFactionId = reader.GetUInt32("faction_id");
                var faction = FactionManager.Instance.GetFaction((FactionsEnum)rowFactionId);
                if (faction == null)
                    continue;

                var rowMotherFactionId =
                    faction.MotherId != FactionsEnum.Invalid
                        ? faction.MotherId
                        : faction.Id;

                if (!string.Equals(
                        FactionSide(rowMotherFactionId),
                        requestedSide,
                        StringComparison.OrdinalIgnoreCase))
                    continue;

                characterName = reader.GetString("name");
                factionId = rowFactionId;
                motherFactionId = rowMotherFactionId;

                return !string.IsNullOrWhiteSpace(characterName) &&
                       motherFactionId != FactionsEnum.Invalid;
            }

            return false;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Discord side faction chat membership check failed: {ex.Message}");
            return false;
        }
    }

    private static bool TryGetFactionPersistentChatIdentity(
        uint accountId,
        uint preferredCharacterId,
        string requestedFactionName,
        out string characterName,
        out uint factionId,
        out FactionsEnum motherFactionId)
    {
        characterName = string.Empty;
        factionId = 0;
        motherFactionId = FactionsEnum.Invalid;

        try
        {
            using var connection = MySQL.CreateConnection();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT id, name, faction_id
                FROM characters
                WHERE account_id = @account_id
                  AND deleted = 0
                ORDER BY CASE WHEN id = @character_id THEN 0 ELSE 1 END, id
                """;

            command.Parameters.AddWithValue("@account_id", accountId);
            command.Parameters.AddWithValue("@character_id", preferredCharacterId);
            command.Prepare();

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var rowFactionId = reader.GetUInt32("faction_id");
                var faction = FactionManager.Instance.GetFaction((FactionsEnum)rowFactionId);
                if (faction == null)
                    continue;

                if (!string.Equals(
                        faction.Name?.Trim(),
                        requestedFactionName.Trim(),
                        StringComparison.OrdinalIgnoreCase))
                    continue;

                characterName = reader.GetString("name");
                factionId = rowFactionId;
                motherFactionId = faction.MotherId != FactionsEnum.Invalid
                    ? faction.MotherId
                    : faction.Id;

                return !string.IsNullOrWhiteSpace(characterName) &&
                       motherFactionId != FactionsEnum.Invalid;
            }

            return false;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Discord faction chat membership check failed: {ex.Message}");
            return false;
        }
    }

    private static bool TryGetPersistentChatIdentity(
        uint accountId,
        uint preferredCharacterId,
        string kind,
        uint groupId,
        out string characterName,
        out uint factionId)
    {
        characterName = string.Empty;
        factionId = 0;

        var membershipColumn = kind switch
        {
            "guild" => "expedition_id",
            "family" => "family",
            "faction" => "faction_id",
            _ => string.Empty
        };

        if (string.IsNullOrWhiteSpace(membershipColumn))
            return false;

        try
        {
            using var connection = MySQL.CreateConnection();
            using var command = connection.CreateCommand();

            // membershipColumn is selected only from the fixed whitelist above.
            command.CommandText =
                $"""
                SELECT id, name, faction_id
                FROM characters
                WHERE account_id = @account_id
                  AND {membershipColumn} = @group_id
                  AND deleted = 0
                ORDER BY CASE WHEN id = @character_id THEN 0 ELSE 1 END, id
                LIMIT 1
                """;

            command.Parameters.AddWithValue("@account_id", accountId);
            command.Parameters.AddWithValue("@group_id", groupId);
            command.Parameters.AddWithValue("@character_id", preferredCharacterId);
            command.Prepare();

            using var reader = command.ExecuteReader();
            if (!reader.Read())
                return false;

            characterName = reader.GetString("name");
            factionId = reader.GetUInt32("faction_id");
            return !string.IsNullOrWhiteSpace(characterName);
        }
        catch (Exception ex)
        {
            Logger.Warn($"Discord persistent chat membership check failed: {ex.Message}");
            return false;
        }
    }

    private static string NationSide(Race race)
    {
        if (race == Race.None)
            return string.Empty;

        // Keep this identical to ChatManager.GetNationChat(): races 1-4 share the
        // Nuia birth-continent channel and races 5-8 share Haranya. This remains
        // correct even if the character later changes allegiance (for example Pirate).
        var raceGroup = ((byte)race - 1) & 0xFC;
        var nuiaGroup = ((byte)Race.Nuian - 1) & 0xFC;
        var haranyaGroup = ((byte)Race.Hariharan - 1) & 0xFC;

        if (raceGroup == nuiaGroup)
            return "Nuia";
        if (raceGroup == haranyaGroup)
            return "Haranya";

        return string.Empty;
    }

    private static string NormalizeSide(string side)
    {
        var value = (side ?? string.Empty).Trim().ToLowerInvariant();

        return value switch
        {
            "nuia" => "Nuia",
            "haranya" => "Haranya",
            "haranyan" => "Haranya",
            "pirate" => "Pirate",
            "other" => "Other",
            _ => string.Empty
        };
    }

    private static string FactionSide(FactionsEnum motherFactionId)
    {
        return motherFactionId switch
        {
            FactionsEnum.NuiaAlliance => "Nuia",
            FactionsEnum.HaranyaAlliance => "Haranya",
            FactionsEnum.Pirate => "Pirate",
            _ => "Other"
        };
    }

    private static bool TryGetUInt(JsonElement element, string propertyName, out uint value)
    {
        value = 0;

        if (!element.TryGetProperty(propertyName, out var property))
            return false;

        if (property.ValueKind == JsonValueKind.Number && property.TryGetUInt32(out value))
            return true;

        if (property.ValueKind == JsonValueKind.String &&
            uint.TryParse(property.GetString(), out value))
            return true;

        return false;
    }

    private async Task PostAsync(string path, object payload)
    {
        if (string.IsNullOrWhiteSpace(_secret))
            return;

        try
        {
            using var request =
                new HttpRequestMessage(HttpMethod.Post, $"{_bridgeUrl}{path}");

            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", _secret);

            request.Content =
                new StringContent(
                    JsonSerializer.Serialize(payload),
                    Encoding.UTF8,
                    "application/json"
                );

            using var response = await Http.SendAsync(request);

            if (!response.IsSuccessStatusCode)
            {
                Logger.Warn(
                    $"Discord bridge returned HTTP {(int)response.StatusCode}"
                );
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"Discord bridge unavailable: {ex.Message}");
        }
    }

    private static NLog.Logger Logger { get; } =
        NLog.LogManager.GetCurrentClassLogger();
}
