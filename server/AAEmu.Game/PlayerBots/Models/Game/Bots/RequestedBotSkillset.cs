using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Models.Game.Bots;

public static class RequestedBotSkillset
{
    // Stable names reconstruct the requested progression after a server restart.
    // These definitions are deliberately excluded from random population presets.
    public static BotArchetypeDefinition Resolve(string name)
    {
        var parts = name?.Split('-');
        if (parts?.Length != 4 || parts[0] != "Requested") return null;
        var abilities = new List<AbilityType>();
        foreach (var part in parts.Skip(1))
        {
            if (!byte.TryParse(part, out var id) || id < 1 || id > 10 || part != id.ToString()) return null;
            abilities.Add((AbilityType)id);
        }
        if (abilities.Distinct().Count() != 3 || !new byte[] { 1, 7, 6, 10, 5, 8 }.Contains((byte)abilities[0])) return null;
        var first = (byte)abilities[0];
        return new BotArchetypeDefinition
        {
            Name = name, StartingAbility = abilities[0], RequiredAbilities = abilities,
            LevelToUnlockSecond = 5, LevelToUnlockThird = 10,
            PrimaryStat = first == 10 ? UnitAttribute.Spi : first is 7 or 5 ? UnitAttribute.Int : first is 6 or 8 ? UnitAttribute.Dex : UnitAttribute.Str,
            ArmorType = first is 7 or 5 or 10 ? 1 : first is 6 or 8 ? 2 : 3,
            WeaponPriority = new List<string> { first == 10 ? "Club" : first is 7 or 5 ? "Staff" : first == 6 ? "Bow" : first == 8 ? "Dagger" : "Sword" },
            UsesShield = first is 1 or 10,
            Weight = 0
        };
    }
}
