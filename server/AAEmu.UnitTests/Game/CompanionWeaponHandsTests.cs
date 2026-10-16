using AAEmu.Game.Bots.Equipment;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.Items.Templates;

namespace AAEmu.UnitTests.Game;

public class CompanionWeaponHandsTests
{
    private static (Inventory Inventory, ItemContainer Bag, ItemContainer Equipment, Item Staff, Item Shield) Setup(int bagSize = 5)
    {
        var owner = new Character(null) { Id = 900001, Hp = 100 };
        // Container ownership callbacks/quest tracking are outside this slot-move fixture.
        var bag = new ItemContainer(0, SlotType.Inventory, false, null) { ContainerSize = bagSize };
        var equipment = new EquipmentContainer(0, SlotType.Equipment, false, null);
        var staff = new Item { Id = 900001, Count = 1, OwnerId = owner.Id,
            Template = new WeaponTemplate { MaxCount = 1, HoldableTemplate = new Holdable { SlotTypeId = (uint)EquipmentItemSlotType.TwoHanded } } };
        var shield = new Item { Id = 900002, Count = 1, OwnerId = owner.Id,
            Template = new WeaponTemplate { MaxCount = 1, HoldableTemplate = new Holdable { SlotTypeId = (uint)EquipmentItemSlotType.Shield } } };
        var inventory = new Inventory(owner, bag, equipment, id => id == staff.Id ? staff : id == shield.Id ? shield : null);
        return (inventory, bag, equipment, staff, shield);
    }

    private static void Place(ItemContainer container, Item item, int slot)
    {
        item.SlotType = container.ContainerType; item.Slot = slot; item._holdingContainer = container;
        container.Items.Add(item); container.UpdateFreeSlotCount();
    }

    [Test]
    public async Task EquipStaffIntoEmptyMainhand_ReturnsShieldToBag()
    {
        var f = Setup();
        Place(f.Bag, f.Staff, 0); Place(f.Equipment, f.Shield, (int)EquipmentItemSlot.Offhand);
        await Assert.That(f.Inventory.SplitOrMoveItemEx(ItemTaskType.Invalid, f.Bag, f.Equipment, f.Staff.Id, SlotType.Inventory, 0,
            0, SlotType.Equipment, (byte)EquipmentItemSlot.Mainhand)).IsTrue();
        await Assert.That(f.Equipment.GetItemBySlot((int)EquipmentItemSlot.Mainhand)).IsEqualTo(f.Staff);
        await Assert.That(f.Equipment.GetItemBySlot((int)EquipmentItemSlot.Offhand)).IsNull();
        await Assert.That(f.Bag.Items.Contains(f.Shield)).IsTrue();
        await Assert.That(f.Bag.Items.Count + f.Equipment.Items.Count).IsEqualTo(2);
    }

    [Test]
    public async Task EquipShieldIntoEmptyOffhand_ReturnsTwoHandedStaffToBag()
    {
        var f = Setup();
        Place(f.Equipment, f.Staff, (int)EquipmentItemSlot.Mainhand); Place(f.Bag, f.Shield, 0);
        await Assert.That(f.Inventory.SplitOrMoveItemEx(ItemTaskType.Invalid, f.Bag, f.Equipment, f.Shield.Id, SlotType.Inventory, 0,
            0, SlotType.Equipment, (byte)EquipmentItemSlot.Offhand)).IsTrue();
        await Assert.That(f.Equipment.GetItemBySlot((int)EquipmentItemSlot.Mainhand)).IsNull();
        await Assert.That(f.Equipment.GetItemBySlot((int)EquipmentItemSlot.Offhand)).IsEqualTo(f.Shield);
        await Assert.That(f.Bag.Items.Contains(f.Staff)).IsTrue();
    }

    [Test]
    public async Task FullBag_RejectsConflictingEquipWithoutLosingEitherItem()
    {
        var f = Setup(1);
        Place(f.Bag, f.Staff, 0); Place(f.Equipment, f.Shield, (int)EquipmentItemSlot.Offhand);
        await Assert.That(f.Inventory.SplitOrMoveItemEx(ItemTaskType.Invalid, f.Bag, f.Equipment, f.Staff.Id, SlotType.Inventory, 0,
            0, SlotType.Equipment, (byte)EquipmentItemSlot.Mainhand)).IsFalse();
        await Assert.That(f.Bag.GetItemBySlot(0)).IsEqualTo(f.Staff);
        await Assert.That(f.Equipment.GetItemBySlot((int)EquipmentItemSlot.Offhand)).IsEqualTo(f.Shield);
    }

    [Test]
    public async Task ExistingInvalidLoadout_RepairsOnceAndKeepsShield()
    {
        var f = Setup();
        Place(f.Equipment, f.Staff, (int)EquipmentItemSlot.Mainhand);
        Place(f.Equipment, f.Shield, (int)EquipmentItemSlot.Offhand);
        await Assert.That(BotEquipmentHands.RepairConflictingOffhand(f.Inventory)).IsTrue();
        await Assert.That(f.Bag.Items.Contains(f.Shield)).IsTrue();
        await Assert.That(f.Equipment.GetItemBySlot((int)EquipmentItemSlot.Mainhand)).IsEqualTo(f.Staff);
        await Assert.That(BotEquipmentHands.RepairConflictingOffhand(f.Inventory)).IsFalse();
    }
    [Test]
    public async Task DashboardOffhandGuardKeepsStaffAndAllowsMainhandReplacement()
    {
        var f = Setup();
        Place(f.Equipment, f.Staff, (int)EquipmentItemSlot.Mainhand);
        Place(f.Bag, f.Shield, 0);
        await Assert.That(BotEquipmentHands.BlocksOffhand(f.Inventory, EquipmentItemSlot.Offhand)).IsTrue();
        await Assert.That(BotEquipmentHands.BlocksOffhand(f.Inventory, EquipmentItemSlot.Mainhand)).IsFalse();
        await Assert.That(f.Equipment.GetItemBySlot((int)EquipmentItemSlot.Mainhand)).IsEqualTo(f.Staff);
        await Assert.That(f.Bag.Items.Contains(f.Shield)).IsTrue();
    }

}
