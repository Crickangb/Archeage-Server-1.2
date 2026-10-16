using System.Numerics;
using AAEmu.Game.Bots.Body;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Bots.Equipment;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Templates;

namespace AAEmu.UnitTests.Game;

public class CompanionCombatTests
{
    [Test]
    public async Task VitalismGear_PrefersHealingClubAndShieldOverSword()
    {
        var ability = (AbilityType)10;
        var club = new WeaponTemplate { CategoryId = 74, HoldableTemplate = new Holdable { SlotTypeId = (uint)EquipmentItemSlotType.OneHanded } };
        var sword = new WeaponTemplate { CategoryId = 70, HoldableTemplate = new Holdable { SlotTypeId = (uint)EquipmentItemSlotType.OneHanded } };
        var shield = new WeaponTemplate { CategoryId = 79, HoldableTemplate = new Holdable { SlotTypeId = (uint)EquipmentItemSlotType.Shield } };
        await Assert.That(BotEquipmentPolicy.Rank(ability, club) > BotEquipmentPolicy.Rank(ability, sword)).IsTrue();
        await Assert.That(BotEquipmentPolicy.Rank(ability, sword) < 0).IsTrue();
        await Assert.That(BotEquipmentPolicy.Rank(ability, shield) > 0).IsTrue();
    }

    [Test]
    public async Task ThreeAttackPositions_StaySeparateAndWithinAttackRange()
    {
        var target = new Vector3(10, 20, 30);
        for (uint targetId = 1; targetId < 360; targetId += 20)
        {
            var positions = Enumerable.Range(0, 3).Select(slot => BotCombatSpacing.Position(target, targetId, slot, 3, 2f)).ToArray();
            foreach (var position in positions)
                await Assert.That(Vector3.Distance(position, target) < 2f).IsTrue();
            for (var a = 0; a < 3; a++)
                for (var b = a + 1; b < 3; b++)
                    await Assert.That(Vector3.Distance(positions[a], positions[b]) > 1f).IsTrue();
        }
    }

    [Test]
    public async Task CasterPosition_RetreatsFromMeleeAndApproachesFromDistance()
    {
        var target = new Vector3(10, 20, 30);
        foreach (var current in new[] { target, target + Vector3.UnitX, target + Vector3.UnitX * 40f })
        {
            var desired = BotCombatSpacing.RangedPosition(current, target, 12f);
            await Assert.That(Vector3.Distance(desired, target)).IsEqualTo(12f);
        }
    }

    [Test]
    public async Task DualPurposeAntithesis_IsEligibleButHiddenAndUnrelatedFriendlySkillsAreNot()
    {
        var skill = new SkillTemplate { Id = 10534, Show = true, NeedLearn = true, TargetType = SkillTargetType.Friendly };
        await Assert.That(LearnedSkillDecision.IsOffensive(skill)).IsTrue();
        skill.Show = false;
        await Assert.That(LearnedSkillDecision.IsOffensive(skill)).IsFalse();
        skill.Show = true; skill.Id = 10547;
        await Assert.That(LearnedSkillDecision.IsOffensive(skill)).IsFalse();
    }
}
