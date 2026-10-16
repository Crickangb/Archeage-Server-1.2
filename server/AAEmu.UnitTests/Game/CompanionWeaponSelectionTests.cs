using AAEmu.Game.Bots.Equipment;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Static;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.UnitTests.Game;

public class CompanionWeaponSelectionTests
{
    private static SkillTemplate Offensive(uint id, AbilityType ability) => new()
    { Id = id, AbilityId = ability, Show = true, NeedLearn = true, TargetType = SkillTargetType.Hostile };

    private static Item Weapon(ulong id, int category, EquipmentItemSlotType hands, int level = 1) => new()
    {
        Id = id, Template = new WeaponTemplate
        { CategoryId = category, Level = level, HoldableTemplate = new Holdable { SlotTypeId = (uint)hands } }
    };

    [Test]
    public async Task LearnedMagicOverridesStartingFightAndChoosesOwnedStaff()
    {
        var profile = BotWeaponProfile.Resolve([Offensive(1, AbilityType.Magic)], [], AbilityType.Fight, "melee-dps");
        var sword = Weapon(1, 127, EquipmentItemSlotType.TwoHanded, 20);
        var staff = Weapon(2, 131, EquipmentItemSlotType.TwoHanded);
        var result = BotArchetypeManager.Instance.PickBestWeaponConfiguration(new Character(null) { Level = 30 },
            profile, [sword, staff], true);
        await Assert.That(profile.PrimaryStat).IsEqualTo(UnitAttribute.Int);
        await Assert.That(result.Mainhand).IsEqualTo(staff);
        await Assert.That(result.Offhand).IsNull();
    }

    [Test]
    public async Task LearnedSkillPriorityWinsOverMajorityAndIgnoresStaleIds()
    {
        var profile = BotWeaponProfile.Resolve([Offensive(1, AbilityType.Fight), Offensive(2, AbilityType.Fight),
            Offensive(3, AbilityType.Magic)], [999, 3, 1], AbilityType.Fight, "melee-dps");
        await Assert.That(profile.StartingAbility).IsEqualTo(AbilityType.Magic);
    }

    [Test]
    public async Task HealerRoleWithLearnedHealingSelectsClubAndShield()
    {
        var profile = BotWeaponProfile.Resolve([Offensive(1, AbilityType.Magic),
            new SkillTemplate { Id = 2, AbilityId = AbilityType.Love, Show = true, NeedLearn = true }],
            [1], AbilityType.Magic, "healer");
        var club = Weapon(1, 74, EquipmentItemSlotType.OneHanded);
        var shield = Weapon(2, 79, EquipmentItemSlotType.Shield);
        var result = BotArchetypeManager.Instance.PickBestWeaponConfiguration(new Character(null) { Level = 30 },
            profile, [club, shield], true);
        await Assert.That(result.Mainhand).IsEqualTo(club);
        await Assert.That(result.Offhand).IsEqualTo(shield);
    }

    [Test]
    public async Task MageCanUseScepterAndNeverPairsShieldWithStaff()
    {
        var profile = BotWeaponProfile.Resolve([Offensive(1, AbilityType.Magic)], [], AbilityType.Magic, "melee-dps");
        var scepter = Weapon(1, 75, EquipmentItemSlotType.OneHanded);
        var shield = Weapon(2, 79, EquipmentItemSlotType.Shield, 100);
        var result = BotArchetypeManager.Instance.PickBestWeaponConfiguration(new Character(null) { Level = 30 },
            profile, [scepter, shield], true);
        await Assert.That(result.Mainhand).IsEqualTo(scepter);
        await Assert.That(result.Offhand).IsNull();
    }

    [Test]
    public async Task EqualWeaponsKeepCurrentAndTooHighLevelIsRejected()
    {
        var profile = BotWeaponProfile.Resolve([Offensive(1, AbilityType.Magic)], [], AbilityType.Magic, "melee-dps");
        var current = Weapon(1, 131, EquipmentItemSlotType.TwoHanded);
        var same = Weapon(2, 131, EquipmentItemSlotType.TwoHanded);
        var unusable = Weapon(3, 131, EquipmentItemSlotType.TwoHanded, 100);
        unusable.Template.LevelRequirement = 40;
        var result = BotArchetypeManager.Instance.PickBestWeaponConfiguration(new Character(null) { Level = 30 },
            profile, [current, same, unusable], true);
        await Assert.That(result.Mainhand).IsEqualTo(current);
    }

    [Test]
    public async Task UnsupportedSkillsKeepExistingLoadout()
    {
        await Assert.That(BotWeaponProfile.Resolve([Offensive(1, AbilityType.Vocation)], [],
            AbilityType.Vocation, "melee-dps")).IsNull();
    }
    [Test]
    public async Task LearnedHealingWithoutAttackUsesHealingGearDespiteStartingFight()
    {
        var profile = BotWeaponProfile.Resolve([
            new SkillTemplate { Id = 1, AbilityId = AbilityType.Love, Show = true, NeedLearn = true }],
            [], AbilityType.Fight, "melee-dps");
        await Assert.That(profile.StartingAbility).IsEqualTo(AbilityType.Love);
        await Assert.That(profile.UsesShield).IsTrue();
    }

    [Test]
    public async Task OneHandedDefenseBuildPairsShieldInsteadOfTwoWeapons()
    {
        var profile = BotWeaponProfile.Resolve([Offensive(1, AbilityType.Fight),
            new SkillTemplate { Id = 2, AbilityId = AbilityType.Adamant, Show = true, NeedLearn = true }],
            [], AbilityType.Fight, "tank");
        var sword = Weapon(1, 70, EquipmentItemSlotType.OneHanded);
        var second = Weapon(2, 70, EquipmentItemSlotType.OneHanded);
        var shield = Weapon(3, 79, EquipmentItemSlotType.Shield);
        var result = BotArchetypeManager.Instance.PickBestWeaponConfiguration(new Character(null) { Level = 30 },
            profile, [sword, second, shield], true);
        await Assert.That(result.Mainhand).IsEqualTo(sword);
        await Assert.That(result.Offhand).IsEqualTo(shield);
    }
    [Test]
    public async Task PriorityDefenseAttackSelectsShieldBuildOverMagic()
    {
        var profile = BotWeaponProfile.Resolve([Offensive(1, AbilityType.Magic), Offensive(2, AbilityType.Adamant)],
            [2, 1], AbilityType.Magic, "melee-dps");
        await Assert.That(profile.StartingAbility).IsEqualTo(AbilityType.Fight);
        await Assert.That(profile.UsesShield).IsTrue();
    }
}
