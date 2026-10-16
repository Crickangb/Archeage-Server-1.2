using System.Numerics;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Static;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Bots.Body;

/// <summary>
/// Chooses an offensive action from the character's live learned-skill set.
/// This path has no archetype rotation table and no fixed skill IDs.
/// </summary>
public static class LearnedSkillDecision
{
    internal static readonly TimeSpan ZeroCooldownReuseDelay = TimeSpan.FromSeconds(2);
    internal const int ManaReservePercent = 25;

    public static SkillTemplate Select(
        Character bot,
        Unit target,
        BotCombatState state,
        DateTime now,
        BotConfig config = null)
    {
        if (bot?.Skills?.Skills == null || target == null || state == null)
            return null;

        var reserve = Math.Max(0, bot.MaxMp * ManaReservePercent / 100);
        var distance = bot.Transform == null || target.Transform == null
            ? 0f : Vector3.Distance(bot.Transform.World.Position, target.Transform.World.Position);
        return Pick(bot.Skills.Skills.Values.Select(learned => learned?.Template), template =>
            IsOffensive(template) && (template.Id != 11918 || distance >= 5f) && template.ManaCost <= Math.Max(0, bot.Mp - reserve) &&
            (template.CooldownTime != 0 || state.LastSkillTime == DateTime.MinValue || now - state.LastSkillTime >= ZeroCooldownReuseDelay) &&
            BotSkillGate.Check(bot, template, target, distance, now, config).IsAllowed,
            BotSkillPriorities.Current.Get(bot.Id), CompanionRoleRegistry.Get(bot.Id), distance);
    }

    internal static SkillTemplate Pick(IEnumerable<SkillTemplate> skills, Func<SkillTemplate, bool> usable,
        uint[] priorities, string role, float distance)
    {
        SkillTemplate selected = null;
        foreach (var template in skills)
        {
            if (template == null || !usable(template)) continue;
            if (selected == null || CompareWithPriority(priorities, role, distance, template, selected) < 0)
                selected = template;
        }
        return selected;
    }

    internal static bool IsOffensive(SkillTemplate template) =>
        template != null &&
        template.Show &&
        template.NeedLearn &&
        (template.TargetType == SkillTargetType.Hostile || template.TargetRelation == SkillTargetRelation.Hostile ||
         template.Id == 10534); // Antithesis damages enemies and heals allies; the normal relation gate still applies.

    internal static int CompareWithPriority(uint[] priorities, string role, float distance, SkillTemplate left, SkillTemplate right)
    {
        var rank = BotSkillPriorities.Rank(priorities, left.Id).CompareTo(BotSkillPriorities.Rank(priorities, right.Id));
        return rank != 0 ? rank : CompareForRole(role, distance, left, right);
    }

    private static int CompareForRole(string role, float distance, SkillTemplate left, SkillTemplate right)
    {
        if (string.Equals(role, "melee-dps", StringComparison.OrdinalIgnoreCase))
        {
            var leftPriority = MeleeDpsPriority(left, distance);
            var rightPriority = MeleeDpsPriority(right, distance);
            var roleComparison = leftPriority.CompareTo(rightPriority);
            if (roleComparison != 0)
                return roleComparison;
        }

        return Compare(left, right);
    }

    private static int MeleeDpsPriority(SkillTemplate skill, float distance)
    {
        // Battlerage Charge is the preferred gap closer when its normal AAEmu
        // range/cooldown gates permit it. Triple Slash is the preferred melee
        // filler once the companion has closed on the target.
        if (skill.Id == 11918) // Charge
            return distance > 4f ? 0 : 3;
        if (skill.Id == 18132) // Triple Slash (learned 1.2 entry)
            return distance <= 4f ? 0 : 2;
        return 1;
    }

    private static int Compare(SkillTemplate left, SkillTemplate right)
    {
        // Prefer a ready cooldown ability to filler, then conserve mana and keep
        // the choice deterministic without encoding a cast sequence.
        var comparison = (left.CooldownTime > 0 ? 0 : 1).CompareTo(right.CooldownTime > 0 ? 0 : 1);
        if (comparison != 0)
            return comparison;
        comparison = left.ManaCost.CompareTo(right.ManaCost);
        return comparison != 0 ? comparison : left.Id.CompareTo(right.Id);
    }
}
