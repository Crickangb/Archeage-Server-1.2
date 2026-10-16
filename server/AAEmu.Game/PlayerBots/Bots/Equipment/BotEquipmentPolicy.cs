#if !PLAYERBOTS_AAEMU_3_0
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Skills;

namespace AAEmu.Game.Bots.Equipment;

/// <summary>Shared 1.2 leveling rules for rewards, wrapped gear, and equipped gear.</summary>
internal static class BotEquipmentPolicy
{
    internal static BotArchetypeDefinition ForGear(BotArchetypeDefinition definition)
    {
        if (definition == null || (int)definition.StartingAbility is not (1 or 6 or 7 or 10)) return definition;
        return new BotArchetypeDefinition
        {
            Name = definition.Name,
            StartingAbility = definition.StartingAbility,
            PrimaryStat = definition.PrimaryStat,
            ArmorType = (int)definition.StartingAbility is 7 or 10 ? 1 : 2,
            WeaponPriority = (int)definition.StartingAbility switch
            {
                1 => ["Greatsword", "Nodachi"],
                6 => ["Bow", "Sword"],
                10 => ["Club"],
                _ => ["Staff"]
            },
            UsesShield = (int)definition.StartingAbility == 10
        };
    }

    internal static bool Usable(ItemTemplate item, int level) => item != null &&
        level >= item.LevelRequirement && (item.LevelLimit <= 0 || level <= item.LevelLimit);

    // Rank is a hard role preference, before item level. Unrelated consumables are neutral.
    internal static int Rank(AbilityType role, ItemTemplate item)
    {
        if (item == null || (int)role is not (1 or 6 or 7 or 10)) return 0;
        if (item is ArmorTemplate armor)
            return armor.KindTemplate?.TypeId == ((int)role is 7 or 10 ? 1 : 2) ? 200 : -1;
        if (item is AccessoryTemplate) return 50;
        if (item is not WeaponTemplate weapon) return 0;
        var category = BotArchetypeManager.WeaponCategoryName(weapon.CategoryId);
        var slot = (EquipmentItemSlotType?)weapon.HoldableTemplate?.SlotTypeId;
        return (int)role switch
        {
            1 when category is "Greatsword" or "Nodachi" && slot == EquipmentItemSlotType.TwoHanded => 300,
            6 when category == "Bow" => 300,
            6 when category == "Sword" && slot is EquipmentItemSlotType.OneHanded or EquipmentItemSlotType.Mainhand or EquipmentItemSlotType.Offhand => 100,
            7 when category == "Staff" && slot == EquipmentItemSlotType.TwoHanded => 300,
            10 when category == "Club" && slot is EquipmentItemSlotType.OneHanded or EquipmentItemSlotType.Mainhand => 300,
            10 when slot == EquipmentItemSlotType.Shield => 100,
            _ when category is "Lute" or "Flute" => 25,
            _ => -1
        };
    }

    internal static int SelectReward(IEnumerable<(int Index, int Rank, int Level)> options, int fallback) =>
        options.Where(option => option.Index >= 0)
            .OrderByDescending(option => option.Rank).ThenByDescending(option => option.Level)
            .ThenBy(option => option.Index).Select(option => option.Index).DefaultIfEmpty(fallback).First();
}
#endif
