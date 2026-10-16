using AAEmu.Game.Bots.Gathering;
using System.Numerics;

namespace AAEmu.UnitTests.Game;

public class CompanionGatherThreatTests
{
    [Test]
    public async Task PassiveAttackableCreature_DoesNotBlockGathering()
    {
        await Assert.That(BotGatherThreatPolicy.Clearance(true,false,false,2)).IsEqualTo(0f);
        await Assert.That(BotGatherThreatPolicy.Clearance(false,true,false,2)).IsEqualTo(0f);
    }

    [Test]
    public async Task NeutralCreature_AttackingFarmerStillBlocksGathering()
    {
        await Assert.That(BotGatherThreatPolicy.Clearance(false,false,true,2)).IsEqualTo(20f);
    }

    [Test]
    public async Task AggressiveCreature_UsesItsDetectionRangeAndMargin()
    {
        await Assert.That(BotGatherThreatPolicy.Clearance(true,true,false,1)).IsEqualTo(15f);
        await Assert.That(BotGatherThreatPolicy.Clearance(true,true,false,4)).IsEqualTo(42f);
    }

    [Test]
    public async Task RouteScan_ChecksAlongSegmentRatherThanOnlyEndpoints()
    {
        await Assert.That(BotGatherController.SegmentDistance(new(50,10,0),Vector3.Zero,new(100,0,0))).IsEqualTo(10f);
        await Assert.That(BotGatherController.SegmentDistance(new(150,0,0),Vector3.Zero,new(100,0,0))).IsEqualTo(50f);
    }
}
