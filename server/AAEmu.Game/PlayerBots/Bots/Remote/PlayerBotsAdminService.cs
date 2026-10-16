#nullable enable
using System.Net;
using System.Text.Json;
using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Bots.Host;
using AAEmu.Game.Bots.Gathering;
using AAEmu.Game.Bots.Crafting;
using AAEmu.Game.Bots.Population.Identity;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items;

namespace AAEmu.Game.Bots.Remote;

public sealed partial class PlayerBotsRemoteService
{
    private IReadOnlyList<uint> CleanupArchivedFactoryRoster()
    {
        var options = BotIdentityFactoryOptions.FromEnvironment();
        var authority = (IBotIdentityAuthority)CharacterManager.Instance;
        var roster = new JsonBotRosterStore(options.RosterPath, authority.CharacterExists);
        using var connection = MySQL.CreateConnection();
        return roster.RemoveArchivedCharacters(id => {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT deleted FROM characters WHERE id=@id LIMIT 1";
            command.Parameters.AddWithValue("@id", id);
            var value = command.ExecuteScalar();
            return value != null && value != DBNull.Value && Convert.ToInt32(value) != 0;
        });
    }

    private object ReadCompanionCreationOptions()
    {
        var builds = BotArchetypeManager.Instance.GetArchetypeNames().Select(name => {
            var supported = BotArchetypeManager.Instance.TryResolveCreationPlan(name, ExperienceManager.Instance.MaxPlayerLevel, out var plan);
            return new { name, supported, abilities = supported ? new[] { plan.Ability1.ToString(), plan.Ability2.ToString(), plan.Ability3.ToString() } : Array.Empty<string>() };
        }).Where(build => build.supported).ToArray();
        var appearances = new List<AdminAppearance>();
        foreach (var race in Enum.GetValues<Race>().Where(IsSupportedCompanionRace))
            foreach (var gender in Enum.GetValues<Gender>())
                try { if (CharacterManager.Instance.GetTemplate(race, gender) != null) appearances.Add(new AdminAppearance(race.ToString(), gender.ToString())); }
                catch (KeyNotFoundException) { }
        return new { builds, appearances, customSkillsets = true, creationRosterRevision = "2026-10-09-roster-preflight-1", level = 1 };
    }

    private readonly object _adminRosterLock = new();
    private readonly string _adminKnownPath = Path.Combine(AppContext.BaseDirectory, "Configurations", "PlayerBotsKnown.json");
    private sealed record AdminBot(uint id, string name, bool active, byte level, int experience,
        int hp, int? maxHp, int mp, int? maxMp, uint zoneId, uint? ownerId, string? ownerName,
        bool ownerOnline, string job, string role, string task, string dataSource, uint accountId, int cooldownRemainingSeconds);
    private sealed record BotPoolAccount(uint id, string username);
    private sealed record BotPoolConfig(int capacity, BotPoolAccount[] accounts);
    private readonly string _adminPoolPath = Path.Combine(AppContext.BaseDirectory, "Configurations", "PlayerBotsAccounts.json");
    private sealed record AdminAppearance(string race, string gender);
    private sealed record AdminCommand(string action, uint botId, uint ownerId, string? job,
        bool expectedActive, uint expectedOwnerId, string? expectedJob,
        string? name, string? race, string? gender, string? archetype, int level, uint accountId, string? username, int capacity, string? reason, string? operationId, string? snapshotHash);

    public static bool IsSupportedCompanionRace(Race race) =>
        race is Race.Nuian or Race.Elf or Race.Hariharan or Race.Ferre;

    private static string? ReadCompanionBuild(uint characterId) =>
        new JsonBotRosterStore(BotIdentityFactoryOptions.FromEnvironment().RosterPath, _ => true)
            .Read().Entries.FirstOrDefault(entry => entry.Identity.CharacterId == characterId)?.Profile;

    private uint ServerBotAccount() => uint.TryParse(Environment.GetEnvironmentVariable("AAEMU_PLAYERBOTS_ACCOUNT_ID"), out var id) ? id : 0;

    private BotPoolConfig ReadBotPool()
    {
        if (!File.Exists(_adminPoolPath)) return new BotPoolConfig(4, ServerBotAccount() == 0 ? [] : [new BotPoolAccount(ServerBotAccount(), $"Account #{ServerBotAccount()}")]);
        var config = JsonSerializer.Deserialize<BotPoolConfig>(File.ReadAllText(_adminPoolPath)) ?? throw new InvalidDataException("Bot account pool is invalid.");
        if (config.capacity is < 1 or > 100 || config.accounts is null || config.accounts.Length > 20 || config.accounts.Any(account => account.id == 0 || account.id > int.MaxValue) || config.accounts.Select(account => account.id).Distinct().Count() != config.accounts.Length)
            throw new InvalidDataException("Bot account pool is invalid.");
        return config;
    }

    private void SaveBotPool(BotPoolConfig pool)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_adminPoolPath)!);
        File.WriteAllText(_adminPoolPath + ".tmp", JsonSerializer.Serialize(pool));
        File.Move(_adminPoolPath + ".tmp", _adminPoolPath, true);
    }

    private int BotAccountCount(uint accountId)
    {
        using var connection = MySQL.CreateConnection(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM characters WHERE account_id=@id AND deleted=0";
        command.Parameters.AddWithValue("@id", accountId);return Convert.ToInt32(command.ExecuteScalar());
    }

    private void RememberAdminBot(uint id)
    {
        lock (_adminRosterLock)
        {
            var known = PlayerBotsAdminRegistry.Read(_adminKnownPath);
            if (!known.Add(id)) return;
            PlayerBotsAdminRegistry.Save(_adminKnownPath, known);
        }
    }

    private AdminBot[] ReadAdminRoster()
    {
        lock (_adminRosterLock)
        {
            var active = _botManager.GetAllBots().ToDictionary(bot => bot.Id);
            var known = PlayerBotsAdminRegistry.Read(_adminKnownPath);
            known.UnionWith(active.Keys);
            Dictionary<uint, uint> owners;
            lock (_ownersLock) owners = new(_owners);
            known.UnionWith(owners.Keys);
            lock (_rolesLock) known.UnionWith(_roles.Keys);
            var options = BotIdentityFactoryOptions.FromEnvironment();
            known.UnionWith(new JsonBotRosterStore(options.RosterPath, _ => true).Read().Entries.Select(entry => entry.Identity.CharacterId));
            using var connection = MySQL.CreateConnection();
            using var command = connection.CreateCommand();
            var pool = ReadBotPool();
            var accountParameters = pool.accounts.Select((account, i) => { var parameter = $"@account{i}"; command.Parameters.AddWithValue(parameter, account.id); return parameter; }).ToArray();
            var parameters = known.Select((id, i) =>
            {
                var parameter = $"@id{i}"; command.Parameters.AddWithValue(parameter, id); return parameter;
            }).ToArray();
            command.CommandText = "SELECT id,name,level,experience,hp,mp,zone_id,account_id FROM characters WHERE deleted=0 AND " +
                (accountParameters.Length != 0 ? $"(account_id IN ({string.Join(',', accountParameters)})" : "(1=0") + (parameters.Length > 0 ? $" OR id IN ({string.Join(',', parameters)})" : "") + ") ORDER BY id";
            var bots = new List<AdminBot>();
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    var id = reader.GetUInt32("id");
                    var live = active.GetValueOrDefault(id);
                    var ownerId = owners.GetValueOrDefault(id);
                    var runtime = BotHost.Instance.GetRuntime(id);
                    bots.Add(new AdminBot(id, live?.Name ?? reader.GetString("name"), live != null,
                        live?.Level ?? reader.GetByte("level"), live?.Experience ?? reader.GetInt32("experience"),
                        live?.Hp ?? reader.GetInt32("hp"), live?.MaxHp,
                        live?.Mp ?? reader.GetInt32("mp"), live?.MaxMp,
                        live?.Transform?.ZoneId ?? reader.GetUInt32("zone_id"), ownerId == 0 ? null : ownerId,
                        ResolveCharacterName(ownerId), WorldManager.Instance.GetCharacterById(ownerId)?.IsOnline == true,
                        BotJobRegistry.Get(id).Job, CompanionRoleRegistry.Get(id),
                        live == null ? "Offline" : runtime != null && BotCraftController.IsActive(runtime) ? "Crafting" :
                        runtime != null && BotGatherController.IsActive(runtime) ? "Gathering" : runtime?.CombatState.CurrentState.ToString() ?? "Unavailable",
                        live == null ? "saved" : "live", reader.GetUInt32("account_id"), _botManager.GetDismissCooldownSeconds(id)));
                }
            }
            known.UnionWith(bots.Select(bot => bot.id));
            PlayerBotsAdminRegistry.Save(_adminKnownPath, known);
            return bots.ToArray();
        }
    }

    private object ReadSavedAdminDetails(uint id)
    {
        using var connection = MySQL.CreateConnection();
        using var command = connection.CreateCommand();
        command.Parameters.AddWithValue("@id", id);
        command.CommandText = "SELECT race,gender,ability1,ability2,ability3,updated_at FROM characters WHERE id=@id AND deleted=0";
        object character;
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) throw new InvalidDataException("Bot character is unavailable.");
            character = new { race = ((Race)reader.GetByte("race")).ToString(), gender = ((Gender)reader.GetByte("gender")).ToString(),
                ability1 = ((AAEmu.Game.Models.Game.Skills.AbilityType)reader.GetByte("ability1")).ToString(),
                ability2 = ((AAEmu.Game.Models.Game.Skills.AbilityType)reader.GetByte("ability2")).ToString(),
                ability3 = ((AAEmu.Game.Models.Game.Skills.AbilityType)reader.GetByte("ability3")).ToString(),
                savedAt = reader.GetValue(reader.GetOrdinal("updated_at")).ToString() };
        }
        command.CommandText = "SELECT id,level,type FROM skills WHERE owner=@id ORDER BY type,id";
        var skills = new List<object>(); var passives = new List<object>();
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                var skillId = reader.GetUInt32("id"); var passive = reader.GetString("type") == "Buff";
                var entry = new { id = skillId, level = reader.GetByte("level"),
                    name = LocalizationManager.Instance.Get(passive ? "buffs" : "skills", "name", skillId, $"{(passive ? "Passive" : "Skill")} {skillId}") };
                (passive ? passives : skills).Add(entry);
            }
        command.CommandText = "SELECT id,template_id,slot_type,slot,count,grade FROM items WHERE owner=@id AND slot_type IN (1,2) ORDER BY slot_type,slot";
        var equipment = new List<object>(); var inventory = new List<object>();
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                var templateId = reader.GetUInt32("template_id");
                var entry = new { id = reader.GetUInt64("id").ToString(), templateId, slot = reader.GetInt32("slot"),
                    count = reader.GetInt32("count"), grade = reader.IsDBNull(reader.GetOrdinal("grade")) ? (byte?)null : reader.GetByte("grade"),
                    name = LocalizationManager.Instance.Get("items", "name", templateId, $"Item {templateId}") };
                (reader.GetInt32("slot_type") == (int)SlotType.Equipment ? equipment : inventory).Add(entry);
            }
        return new { character, skills = new { skills, passives }, equipment = new { items = equipment }, inventory = new { items = inventory } };
    }

    private async Task HandleAdminAsync(HttpListenerContext context, string path)
    {
        if (context.Request.Headers["X-PlayerBots-Account-Id"] != null ||
            !uint.TryParse(context.Request.Headers["X-PlayerBots-Admin-Account-Id"], out var accountId) || accountId == 0)
        { await Reply(context, 403, new { message = "Administrator identity required." }); return; }
        var gateHeld = false;
        try
        {
            // Recheck authority at the Game boundary, including immediately before mutations.
            if (JsonSerializer.SerializeToElement(ReadManagerAccount(accountId)).GetProperty("isAdmin").GetBoolean() != true)
            { await Reply(context, 403, new { message = "Administrator access required." }); return; }
            if (context.Request.HttpMethod == "POST")
            {
                if (context.Request.ContentLength64 > 4096)
                { await Reply(context, 400, new { message = "Admin request is too large." }); return; }
                await _itemMutationGate.WaitAsync(_cts.Token); gateHeld = true;
                if (!JsonSerializer.SerializeToElement(ReadManagerAccount(accountId)).GetProperty("isAdmin").GetBoolean())
                { await Reply(context, 403, new { message = "Administrator access changed." }); return; }
            }
            var bots = ReadAdminRoster();
            if (context.Request.HttpMethod == "GET" && path == "/api/v1/manager/admin/roster")
            {
                var pool = ReadBotPool();
                var poolAccounts = pool.accounts.Select(account => new { account.id, account.username, count = BotAccountCount(account.id), exists = AccountManager.Instance.TryGetExistingAccountDetails(account.id, out _), online = AccountManager.Instance.Contains(account.id) }).ToArray();
                var botAccountId = pool.accounts.FirstOrDefault()?.id ?? 0;
                var exists = botAccountId != 0 && AccountManager.Instance.TryGetExistingAccountDetails(botAccountId, out _);
                var online = botAccountId != 0 && AccountManager.Instance.Contains(botAccountId);
                var factoryRoster = new JsonBotRosterStore(BotIdentityFactoryOptions.FromEnvironment().RosterPath, _ => true).Read();
                var builds = BotArchetypeManager.Instance.GetArchetypeNames().Select(name => {
                    var supported = BotArchetypeManager.Instance.TryResolveCreationPlan(name, ExperienceManager.Instance.MaxPlayerLevel, out var plan);
                    return new { name, supported, abilities = supported ? new[] { plan.Ability1.ToString(), plan.Ability2.ToString(), plan.Ability3.ToString() } : Array.Empty<string>() };
                }).Where(build => build.supported).ToArray();
                var appearances = new List<AdminAppearance>();
                foreach (var race in Enum.GetValues<Race>().Where(IsSupportedCompanionRace))
                    foreach (var gender in Enum.GetValues<Gender>())
                        try { var template = CharacterManager.Instance.GetTemplate(race, gender); if (template != null) appearances.Add(new AdminAppearance(race.ToString(), gender.ToString())); }
                        catch (KeyNotFoundException) { }
                await Reply(context, 200, new { bots, createdBots = factoryRoster.Entries.Where(entry => bots.Any(bot => bot.id == entry.Identity.CharacterId)).Select(entry => new { id = entry.Identity.CharacterId, build = entry.Profile }),
                    accounts = poolAccounts, capacity = pool.capacity, offlineSupported = true, scope = "registered", capabilities = new {
                    botAccount = new { id = botAccountId, exists, online, status = botAccountId == 0 ? "Not configured" : !exists ? "Game account missing" : online ? "Account logged in; log out before creation" : "Ready" },
                    builds, appearances, customSkillsets = true, creationRosterRevision = "2026-10-09-roster-preflight-1",
                    accountPool = true, create = poolAccounts.Any(account => account.exists && !account.online && account.count < pool.capacity) && builds.Length != 0 && appearances.Count != 0 && _botManager is BotManager,
                    creationNote = "Create uses the existing server-owned identity factory and starts the bot at its race spawn. Assign an owner separately.",
                    maxLevel = ExperienceManager.Instance.MaxPlayerLevel,
                    archetypes = builds.Select(build => build.name).ToArray(),
                    races = appearances.Select(pair => pair.race).Distinct().ToArray(), genders = Enum.GetNames<Gender>() } }); return;
            }
            if (context.Request.HttpMethod == "GET" && path == "/api/v1/manager/admin/owners")
            {
                var search = (context.Request.QueryString["search"] ?? "").Trim();
                if (search.Length > 64) { await Reply(context, 400, new { message = "Search is too long." }); return; }
                using var connection = MySQL.CreateConnection(); using var command = connection.CreateCommand();
                var ownerFilter = context.Request.QueryString["ownerId"];
                if (ownerFilter != null && (!uint.TryParse(ownerFilter, out var parsedOwner) || parsedOwner == 0))
                { await Reply(context, 400, new { message = "Invalid owner." }); return; }
                _ = uint.TryParse(ownerFilter, out var ownerId);
                command.CommandText = "SELECT id,name,account_id FROM characters WHERE deleted=0 AND account_id<>0 AND account_id NOT IN (" + string.Join(',', ReadBotPool().accounts.Select(account => account.id).DefaultIfEmpty(0u)) + ") AND (@owner=0 OR id=@owner) AND LOCATE(@search,name)>0 ORDER BY name LIMIT 100";
                command.Parameters.AddWithValue("@owner", ownerId);
                command.Parameters.AddWithValue("@account", ServerBotAccount()); command.Parameters.AddWithValue("@search", search);
                var owners = new List<object>(); var known = bots.Select(bot => bot.id).ToHashSet();
                using (var reader = command.ExecuteReader())
                    while (reader.Read())
                    { var id = reader.GetUInt32("id"); if (!known.Contains(id)) owners.Add(new { id, name = reader.GetString("name"), accountId = reader.GetUInt32("account_id"), online = WorldManager.Instance.GetCharacterById(id)?.IsOnline == true }); }
                await Reply(context, 200, new { owners }); return;
            }
            if (context.Request.HttpMethod == "GET" && path == "/api/v1/manager/admin/details")
            {
                if (!uint.TryParse(context.Request.QueryString["botId"], out var id) || !bots.Any(bot => bot.id == id))
                { await Reply(context, 404, new { message = "Registered bot not found." }); return; }
                var bot = bots.Single(bot => bot.id == id);
                var saved = ReadSavedAdminDetails(id);
                await Reply(context, 200, new { bot, saved, snapshotHash = DeletionHash(saved) }); return;
            }
            if (context.Request.HttpMethod == "POST" && path == "/api/v1/manager/admin/action")
            {
                var request = await Body<AdminCommand>(context);
                if (request == null) { await Reply(context, 400, new { message = "Invalid admin request." }); return; }
                if (request.action == "pool-add")
                {
                    if (request.accountId == 0 || request.accountId > int.MaxValue || request.accountId == accountId || string.IsNullOrWhiteSpace(request.username) || request.username.Length > 32 || request.capacity is < 1 or > 100)
                    { await Reply(context, 400, new { message = "Choose an existing dedicated account and a capacity of 1–100." }); return; }
                    var pool = ReadBotPool();
                    if (pool.accounts.Length >= 20 && !pool.accounts.Any(account => account.id == request.accountId))
                    { await Reply(context, 409, new { message = "Bot account pool is limited to 20 accounts." }); return; }
                    if (AccountManager.Instance.Contains(request.accountId))
                    { await Reply(context, 409, new { message = "Log out of this account before adding it to the bot pool." }); return; }
                    using (var connection = MySQL.CreateConnection()) using (var command = connection.CreateCommand())
                    {
                        command.CommandText = "INSERT INTO accounts (account_id,access_level) VALUES (@id,0) ON DUPLICATE KEY UPDATE account_id=account_id";
                        command.Parameters.AddWithValue("@id", request.accountId);command.ExecuteNonQuery();
                    }
                    if (!AccountManager.Instance.TryGetExistingAccountDetails(request.accountId, out var accountDetails) || accountDetails.AccessLevel >= 100)
                    { await Reply(context, 409, new { message = "Use a dedicated account without administrator access." }); return; }
                    SaveBotPool(new BotPoolConfig(request.capacity, pool.accounts.Where(account => account.id != request.accountId).Append(new BotPoolAccount(request.accountId, request.username)).ToArray()));
                    await Reply(context, 200, new { ok = true, message = "Bot account pool updated." }); return;
                }
                if (request.action == "create")
                {
                    if (request.level != 1)
                    { await Reply(context, 400, new { message = "New companions must start at level 1." }); return; }
                    if (!Enum.TryParse<Race>(request.race, true, out var requestedRace) || !IsSupportedCompanionRace(requestedRace))
                    { await Reply(context, 400, new { message = "Choose a supported 1.2 client race: Nuian, Elf, Hariharan or Ferre." }); return; }
#if !PLAYERBOTS_AAEMU_3_0
                    if (request.accountId == 0 || !ReadBotPool().accounts.Any(account => account.id == request.accountId))
                    { await Reply(context, 409, new { message = "Configured bot account changed. Review creation again." }); return; }
                    if (_botManager is not BotManager manager || !Enum.TryParse<Race>(request.race, true, out var race) ||
                        !Enum.TryParse<Gender>(request.gender, true, out var gender))
                    { await Reply(context, 400, new { message = "Choose a supported race and gender." }); return; }
                    if (BotAccountCount(request.accountId) >= ReadBotPool().capacity)
                    { await Reply(context, 409, new { message = "Bot account filled while reviewing. Refresh before retrying." }); return; }
                    var options = BotIdentityFactoryOptions.FromEnvironment() with { ServerOwnedAccountId = request.accountId };
                    var authority = (IBotIdentityAuthority)CharacterManager.Instance;
                    try
                    {
                        var archived = CleanupArchivedFactoryRoster();
                        if (archived.Count != 0) Logger.Info("Removed archived bot identities from factory roster: {0}", string.Join(",", archived));
                    }
                    catch (Exception exception) when (exception is InvalidDataException or IOException)
                    {
                        Logger.Error(exception, "Factory roster validation failed before character creation");
                        await Reply(context, 409, new { message = $"Factory roster check failed before creating a character: {exception.Message}. Check Data/PlayerBots/bot-roster.json or the configured roster path." }); return;
                    }
                    var factory = new BotIdentityFactory(options, authority, BotArchetypeManager.Instance,
                        new JsonBotRosterStore(options.RosterPath, authority.CharacterExists), manager.SpawnBot);
                    var result = factory.CreateAndAdmit(new BotIdentityCreationRequest(request.name ?? "", race, gender,
                        request.archetype ?? "", 1, BotIdentityPlacement.RaceSpawn));
                    if (!result.Success) { await Reply(context, 409, new { message = $"Creation failed: {result.Status} ({result.Reason})." + (result.CharacterId != 0 ? $" Inspect character #{result.CharacterId} before retrying." : ""), characterId = result.CharacterId }); return; }
                    ReadAdminRoster();
                    await Reply(context, 200, new { ok = true, botId = result.CharacterId, message = "Bot created and started. Assign an owner before personal-manager use." }); return;
#else
                    await Reply(context, 409, new { message = "Identity creation is unavailable on this host." }); return;
#endif
                }
                var bot = bots.FirstOrDefault(bot => bot.id == request.botId);
                if (bot == null) { await Reply(context, 404, new { message = "Registered bot not found." }); return; }
                if (bot.active != request.expectedActive || (bot.ownerId ?? 0) != request.expectedOwnerId || bot.job != request.expectedJob)
                { await Reply(context, 409, new { message = "Bot state changed. Review the action again." }); return; }
                if (request.action == "delete")
                {
                    try { var result = DeleteAdminBot(bot, request, accountId); await Reply(context, 200, result); }
                    catch (InvalidOperationException exception) { await Reply(context, 409, new { message = exception.Message }); }
                    return;
                }
                switch (request.action)
                {
                    case "assign":
                    case "unassign":
                        if (bot.active) { await Reply(context, 409, new { message = "Dismiss the bot before changing its owner." }); return; }
                        if (request.action == "assign")
                        {
                            using var connection = MySQL.CreateConnection(); using var command = connection.CreateCommand();
                            command.CommandText = "SELECT account_id FROM characters WHERE id=@id AND deleted=0"; command.Parameters.AddWithValue("@id", request.ownerId);
                            var ownerAccount = Convert.ToUInt32(command.ExecuteScalar() ?? 0);
                            if (ownerAccount == 0 || ReadBotPool().accounts.Any(account => account.id == ownerAccount) || bots.Any(item => item.id == request.ownerId))
                            { await Reply(context, 400, new { message = "Select an existing human owner character." }); return; }
                        }
                        var becameActive = false;
                        lock (BotAccountQuota.Sync)
                        {
                            becameActive = _botManager.GetBot(bot.id) != null;
                            if (!becameActive) lock (_ownersLock)
                            {
                                var previous = new Dictionary<uint, uint>(_owners);
                                try { if (request.action == "assign") _owners[bot.id] = request.ownerId; else _owners.Remove(bot.id); SaveOwners(); }
                                catch { _owners = previous; throw; }
                            }
                        }
                        if (becameActive) { await Reply(context, 409, new { message = "Bot started while reviewing. Dismiss it before changing owner." }); return; }
                        break;
                    case "mode":
                        if (bot.active || request.job is not ("farmer" or "companion"))
                        { await Reply(context, 409, new { message = "Dismiss the bot and select Combat or Farmer before changing mode." }); return; }
                        var modeBotActive = false;
                        lock (BotAccountQuota.Sync)
                        {
                            modeBotActive = _botManager.GetBot(bot.id) != null;
                            if (!modeBotActive) BotJobRegistry.Set(bot.id, new BotJobSetting(request.job));
                        }
                        if (modeBotActive) { await Reply(context, 409, new { message = "Bot started while reviewing. Dismiss it before changing mode." }); return; }
                        break;
                    case "spawn":
                        if (bot.ownerId == null || BotAccountQuota.AccountForBot(bot.id) == 0)
                        { await Reply(context, 409, new { message = "Assign a valid owner before starting this bot." }); return; }
                        var spawn = _botManager.SpawnBot(bot.id, out _);
                        if (spawn != SpawnResult.Ok) { await Reply(context, 409, new { message = spawn == SpawnResult.DismissCooldown ? $"Dismiss cooldown: wait {_botManager.GetDismissCooldownSeconds(bot.id)} seconds before starting this bot." : $"Start failed: {spawn}." }); return; }
                        break;
                    case "despawn":
                        if (!_botManager.DismissBot(bot.id)) { await Reply(context, 409, new { message = "Bot is already offline." }); return; }
                        break;
                    case "stop":
                        var live = _botManager.GetBot(bot.id);
                        if (live == null) { await Reply(context, 409, new { message = "Bot is offline." }); return; }
                        _botManager.StopImmediately(live); break;
                    default: await Reply(context, 400, new { message = "Unsupported admin action." }); return;
                }
                await Reply(context, 200, new { ok = true, botId = bot.id, message = $"{request.action} completed." }); return;
            }
            await Reply(context, 404, new { message = "Admin endpoint not found." });
        }
        catch (Exception exception)
        {
            Logger.Error(exception, "PlayerBots admin request failed");
            await Reply(context, 503, new { message = "Admin service unavailable. Check the Game server log; refresh before retrying a change." });
        }
        finally { if (gateHeld) _itemMutationGate.Release(); }
    }
}

/// <summary>Metadata only: retains observed bot IDs without creating character/account rows.</summary>
public static class PlayerBotsAdminRegistry
{
    // These runtime documents have their own readers and are not host settings.
    // In particular, the known-ID registry is a top-level JSON array.
    public static bool IsHostConfigurationFile(string path) =>
        !string.Equals(Path.GetFileName(path), "PlayerBotsKnown.json", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(Path.GetFileName(path), "PlayerBotsAccounts.json", StringComparison.OrdinalIgnoreCase);

    public static HashSet<uint> Read(string path)
    {
        if (!File.Exists(path)) return [];
        var known = JsonSerializer.Deserialize<HashSet<uint>>(File.ReadAllText(path))
            ?? throw new InvalidDataException("Known bot registry is invalid.");
        if (known.Contains(0)) throw new InvalidDataException("Known bot registry contains a zero ID.");
        return known;
    }

    // Caller serializes updates with the remote service's roster lock.
    public static void Save(string path, IEnumerable<uint> identities)
    {
        var known = identities.Distinct().Order().ToArray();
        if (known.Contains(0u)) throw new InvalidDataException("Cannot register a zero bot ID.");
        var serialized = JsonSerializer.Serialize(known);
        if (File.Exists(path) && File.ReadAllText(path) == serialized) return;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path + ".tmp", serialized);
        File.Move(path + ".tmp", path, true);
    }
}
