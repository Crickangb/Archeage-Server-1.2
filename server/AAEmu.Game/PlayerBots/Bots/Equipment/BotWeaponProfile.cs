#if !PLAYERBOTS_AAEMU_3_0
using AAEmu.Game.Bots.Body;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Bots.Equipment;

internal static class BotWeaponProfile
{
    internal static BotArchetypeDefinition Resolve(Character bot) => Resolve(
        bot.Skills.Skills.Values.Select(skill => skill.Template), BotSkillPriorities.Current.Get(bot.Id),
        bot.Ability1, CompanionRoleRegistry.Get(bot.Id));

    internal static BotArchetypeDefinition Resolve(IEnumerable<SkillTemplate> skills, uint[] priorities,
        AbilityType fallback, string role)
    {
        var learned = skills.Where(skill => skill != null && skill.Show && skill.NeedLearn).ToArray();
        var combat = learned.Where(LearnedSkillDecision.IsOffensive)
            .Where(skill => Family(skill.AbilityId) != AbilityType.None).ToArray();
        // Healing/support skills still inform gear when there is no learned attack yet.
        var choices = combat.Length > 0 ? combat : learned.Where(skill => Family(skill.AbilityId) != AbilityType.None).ToArray();
        var ranked = choices.OrderBy(skill => BotSkillPriorities.Rank(priorities, skill.Id)).FirstOrDefault();
        var ability = ranked != null && BotSkillPriorities.Rank(priorities, ranked.Id) != int.MaxValue
            ? Family(ranked.AbilityId)
            : choices.GroupBy(skill => Family(skill.AbilityId)).OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key == Family(fallback) ? 0 : 1).ThenBy(group => (int)group.Key)
                .Select(group => group.Key).DefaultIfEmpty(Family(fallback)).First();
        if (role == "healer" && learned.Any(skill => skill.AbilityId == AbilityType.Love))
            ability = AbilityType.Love;
        if (ability == AbilityType.None) return null;
        var shield = ability == AbilityType.Love || ability == AbilityType.Fight &&
            (role == "tank" || learned.Any(skill => skill.AbilityId == AbilityType.Adamant));
        return new BotArchetypeDefinition
        {
            Name = "Learned weapons", StartingAbility = ability, UsesShield = shield,
            PrimaryStat = ability switch
            {
                AbilityType.Magic => UnitAttribute.Int, AbilityType.Love => UnitAttribute.Spi,
                AbilityType.Wild => UnitAttribute.Dex, _ => UnitAttribute.Str
            },
            WeaponPriority = ability switch
            {
                AbilityType.Magic => ["Staff", "Scepter"],
                AbilityType.Love => ["Club", "Greatclub"],
                AbilityType.Wild => ["Sword", "Dagger", "Katana"],
                _ when shield => ["Sword", "Axe", "Club", "Katana", "Shortspear"],
                _ => ["Greatsword", "Nodachi", "Greataxe", "Longspear", "Sword", "Axe", "Katana", "Dagger"]
            }
        };
    }

    // These are gear preferences, not invented skill prerequisites.
    private static AbilityType Family(AbilityType ability) => ability switch
    {
        AbilityType.Fight or AbilityType.Adamant => AbilityType.Fight,
        AbilityType.Wild => AbilityType.Wild,
        AbilityType.Magic or AbilityType.Death or AbilityType.Illusion => AbilityType.Magic,
        AbilityType.Love => AbilityType.Love,
        _ => AbilityType.None
    };
}
#endif
