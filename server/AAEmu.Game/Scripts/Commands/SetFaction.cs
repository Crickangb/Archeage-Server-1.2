using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.StaticValues;
using AAEmu.Game.Services;
using AAEmu.Game.Utils.Scripts;

namespace AAEmu.Game.Scripts.Commands;

public class SetFaction : ICommand
{
    public string[] CommandNames { get; set; } = ["setfaction", "set_faction"];

    public void OnLoad()
    {
        CommandManager.Instance.Register(CommandNames, this);
    }

    public string GetCommandLineHelp()
    {
        return "<faction name or id>";
    }

    public string GetCommandHelpText()
    {
        return "Gets or sets selected target's faction";
    }

    public void Execute(Character character, string[] args, IMessageOutput messageOutput)
    {
        if (character.CurrentTarget is not Unit targetUnit)
        {
            CommandManager.SendDefaultHelpText(this, messageOutput);
            return;
        }

        if (args.Length == 0)
        {
            CommandManager.SendErrorText(this, messageOutput,
                $"Current target is from faction: {targetUnit.Faction?.Name} ({targetUnit.Faction?.Id})");
            // CommandManager.SendDefaultHelpText(this, messageOutput);
            return;
        }

        if (!Enum.TryParse<FactionsEnum>(args[0], true, out var faction))
        {
            faction = FactionsEnum.Invalid;
        }

        if (faction == FactionsEnum.Invalid)
        {
            CommandManager.SendErrorText(this, messageOutput, $"Invalid faction name or number {args[0]}");
            return;
        }

        if (FactionManager.Instance.GetFaction(faction) == null)
        {
            CommandManager.SendErrorText(this, messageOutput, $"{args[0]} is not a valid faction");
            return;
        }

        var oldFactionId = targetUnit.Faction?.Id ?? FactionsEnum.Invalid;
        var oldFactionName = targetUnit.Faction?.Name ?? string.Empty;

        // v20.1: /setfaction must act on the selected target, matching the command help.
        // v20 accidentally called character.SetFaction(...), which changed the GM instead.
        targetUnit.SetFaction(faction);

        var newFactionId = targetUnit.Faction?.Id ?? FactionsEnum.Invalid;
        var newFactionName = targetUnit.Faction?.Name ?? faction.ToString();
        var motherFactionId = targetUnit.Faction?.MotherId ?? FactionsEnum.Invalid;
        if (motherFactionId == FactionsEnum.Invalid)
            motherFactionId = newFactionId;

        var targetCharacter = targetUnit as Character;

        if (oldFactionId != newFactionId)
        {
            _ = DiscordBotService.Instance?.PostEventAsync(new
            {
                type = "faction_changed",
                accountId = targetCharacter?.AccountId,
                characterId = targetUnit.Id,
                characterName = targetUnit.Name,
                targetType = targetCharacter != null ? "character" : "unit",
                factionId = (uint)newFactionId,
                faction = newFactionName,
                motherFactionId = (uint)motherFactionId,
                oldFactionId = (uint)oldFactionId,
                oldFaction = oldFactionName,
                newFactionId = (uint)newFactionId,
                newFaction = newFactionName,
                guildId = targetCharacter?.Expedition?.Id,
                guildName = targetCharacter?.Expedition?.Name ?? string.Empty,
                familyId = targetCharacter?.Family,
                changedBy = character.Name,
                source = "admin_setfaction"
            });
        }

        CommandManager.SendNormalText(this, messageOutput, $"Faction set to: {faction} ({(uint)faction})");
    }
}
