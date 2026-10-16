using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Items.Templates;

namespace AAEmu.Game.Bots.Equipment;

internal static class BotEquipmentHands
{
    internal static bool BlocksOffhand(Inventory inventory, EquipmentItemSlot target) =>
        target == EquipmentItemSlot.Offhand &&
        inventory?.Equipment?.GetItemBySlot((int)EquipmentItemSlot.Mainhand)?.Template is WeaponTemplate main &&
        main.HoldableTemplate?.SlotTypeId == (uint)EquipmentItemSlotType.TwoHanded;

    internal static bool RepairConflictingOffhand(Inventory inventory)
    {
        var main = inventory?.Equipment?.GetItemBySlot((int)EquipmentItemSlot.Mainhand);
        var off = inventory?.Equipment?.GetItemBySlot((int)EquipmentItemSlot.Offhand);
        if (main?.Template is not WeaponTemplate weapon ||
            weapon.HoldableTemplate?.SlotTypeId != (uint)EquipmentItemSlotType.TwoHanded || off == null)
            return false;
        // Keep both items. Never discard an offhand to repair a previously invalid loadout.
        return inventory.Bag != null && inventory.Bag.FreeSlotCount > 0 &&
            inventory.Bag.AddOrMoveExistingItem(ItemTaskType.Invalid, off);
    }
}
