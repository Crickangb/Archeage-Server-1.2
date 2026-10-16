using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.StaticValues;
using AAEmu.Game.Services;
using AAEmu.Game.Utils.Scripts;

namespace AAEmu.Game.Scripts.Commands;

/// <summary>
/// Player-side Discord account linking command.
/// Usage: /discord ABCD-1234
///
/// One Discord link represents the whole AA account.
/// All non-deleted characters on the same AA account are sent to Node
/// so /account can show every character without linking each one separately.
/// </summary>
public class Discord : ICommand
{
    public string[] CommandNames { get; set; } = ["discord"];

    public void OnLoad()
    {
        CommandNames = ["discord"];
        CommandManager.Instance.Register(CommandNames, this);
    }

    public string GetCommandLineHelp() => "<link-code>";

    public string GetCommandHelpText()
        => "Links this AAEmu account to Discord and includes all characters on the account.";

    public void Execute(
        Character character,
        string[] args,
        IMessageOutput messageOutput)
    {
        if (args.Length != 1)
        {
            CommandManager.SendNormalText(
                this,
                messageOutput,
                "Usage: /discord <code>"
            );
            return;
        }

        var guildId =
            character.Expedition != null
                ? (uint)character.Expedition.Id
                : 0u;

        var guildName =
            character.Expedition?.Name
            ?? string.Empty;

        var characters = LoadAccountCharacters(character.AccountId);

        var currentMotherFactionId =
            character.Faction?.MotherId
            ?? FactionsEnum.Invalid;

        if (currentMotherFactionId == FactionsEnum.Invalid)
            currentMotherFactionId = character.Faction?.Id ?? FactionsEnum.Invalid;

        var currentSide = FactionSide(currentMotherFactionId);

        _ = DiscordBotService.Instance?.CompleteLinkAsync(
            args[0],
            character.AccountId,
            character.Id,
            character.Name,
            character.Faction?.Name
                ?? character.FactionName
                ?? "Unknown",
            (uint)(character.Faction?.Id ?? FactionsEnum.Invalid),
            (uint)currentMotherFactionId,
            currentSide,
            guildId,
            guildName,
            character.Family,
            character.Level,
            characters
        );

        CommandManager.SendNormalText(
            this,
            messageOutput,
            $"Discord linking request sent for account {character.AccountId} with {characters.Count} character(s)."
        );
    }

    /// <summary>
    /// Loads every currently valid character belonging to the AA account.
    /// This uses AAEmu's own configured game database connection;
    /// Node does not need direct MySQL access.
    /// </summary>
    private static List<object> LoadAccountCharacters(uint accountId)
    {
        var result = new List<object>();

        using var connection = MySQL.CreateConnection();
        using var command = connection.CreateCommand();

        command.CommandText =
            """
            SELECT
                c.id,
                c.name,
                c.level,
                c.faction_id,
                c.faction_name,
                c.expedition_id,
                c.family,
                c.delete_time,
                e.name AS guild_name
            FROM characters c
            LEFT JOIN expeditions e ON e.id = c.expedition_id
            WHERE c.account_id = @account_id
              AND c.deleted = 0
            ORDER BY c.id
            """;

        command.Parameters.AddWithValue("@account_id", accountId);
        command.Prepare();

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            var deleteTime = reader.GetDateTime("delete_time");

            // Same behavior as AAEmu's character selection list:
            // hide a character once its scheduled deletion time has passed.
            if (deleteTime > DateTime.MinValue && deleteTime < DateTime.UtcNow)
                continue;

            var factionId = reader.GetUInt32("faction_id");

            var databaseFactionName =
                reader.IsDBNull(reader.GetOrdinal("faction_name"))
                    ? string.Empty
                    : reader.GetString("faction_name");

            var systemFaction =
                FactionManager.Instance
                    .GetFaction((FactionsEnum)factionId);

            var factionName = systemFaction?.Name;
            var motherFactionId =
                systemFaction?.MotherId
                ?? FactionsEnum.Invalid;

            if (motherFactionId == FactionsEnum.Invalid)
                motherFactionId = systemFaction?.Id ?? FactionsEnum.Invalid;

            var side = FactionSide(motherFactionId);

            if (string.IsNullOrWhiteSpace(factionName))
                factionName = databaseFactionName;

            if (string.IsNullOrWhiteSpace(factionName))
                factionName = $"Faction {factionId}";

            var guildId = reader.GetUInt32("expedition_id");

            var guildName =
                reader.IsDBNull(reader.GetOrdinal("guild_name"))
                    ? string.Empty
                    : reader.GetString("guild_name");

            result.Add(new
            {
                characterId = reader.GetUInt32("id"),
                characterName = reader.GetString("name"),
                level = reader.GetByte("level"),
                factionId,
                faction = factionName,
                motherFactionId = (uint)motherFactionId,
                side,
                guildId,
                guildName,
                familyId = reader.GetUInt32("family")
            });
        }

        return result;
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

}
