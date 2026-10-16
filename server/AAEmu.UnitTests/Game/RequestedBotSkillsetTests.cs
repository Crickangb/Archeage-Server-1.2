using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Core.Managers.Bots;

namespace AAEmu.UnitTests.Game;

public class RequestedBotSkillsetTests
{
    [Test]
    public async Task RequestedTrees_ReconstructWithoutConfigurationAndUnlockThroughProgression()
    {
        var manager = new BotArchetypeManager();
        foreach (var level in new byte[] { 1, 5, 10 })
        {
            await Assert.That(manager.TryResolveCreationPlan("Requested-8-6-10", level, out var plan)).IsTrue();
            await Assert.That(plan.Ability1).IsEqualTo(AbilityType.Vocation);
            await Assert.That(plan.Ability2).IsEqualTo(level >= 5 ? AbilityType.Wild : AbilityType.None);
            await Assert.That(plan.Ability3).IsEqualTo(level >= 10 ? AbilityType.Love : AbilityType.None);
        }
        await Assert.That(manager.GetArchetypeNames().Contains("Requested-8-6-10")).IsFalse();
    }

    [Test]
    public async Task InvalidRequestedTrees_AreRejected()
    {
        foreach (var name in new[] { "Requested-1-1-10", "Requested-0-6-10", "Requested-3-6-10", "Requested-8-6-11", "Requested-08-6-10", "Requested-8-6", "Requested-8-6-10-extra" })
            await Assert.That(RequestedBotSkillset.Resolve(name) == null).IsTrue();
    }
}
