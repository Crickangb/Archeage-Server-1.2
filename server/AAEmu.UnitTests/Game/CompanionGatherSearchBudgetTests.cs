using System.Numerics;
using AAEmu.Game.Bots.Gathering;
using AAEmu.Game.Bots.Navigation;
using AAEmu.Game.Models.CryEngine.Entities;
using AAEmu.Game.Models.CryEngine.Readers;

namespace AAEmu.UnitTests.Game;

public class CompanionGatherSearchBudgetTests
{
    [Test]
    public async Task UnreachableCandidatesShareDeadlineAndStopBeforeAll34Searches()
    {
        var elapsed = TimeSpan.Zero;
        var budget = new BotGatherSearchBudget(elapsed: () => elapsed);
        var attempts = 0;
        var result = budget.Run(() => BotGatherApproach.Plan(Vector3.Zero, new Vector3(100,0,0), 4,
            (_,_) => 0, point => { attempts++; elapsed += TimeSpan.FromMilliseconds(600); return null; },
            checkBudget: budget.Check), out var timedOut);
        await Assert.That(timedOut).IsTrue();
        await Assert.That(result).IsNull();
        await Assert.That(attempts).IsEqualTo(4);
    }

    [Test]
    public async Task RouteFinishingAfterDeadlineIsNotReturnedForExecution()
    {
        var elapsed = TimeSpan.Zero;
        var budget = new BotGatherSearchBudget(elapsed: () => elapsed);
        var route = new BotTravelRoute("test", [Vector3.One], 0, "late");
        var result = budget.Run(() => { elapsed = TimeSpan.FromSeconds(3); return route; }, out var timedOut);
        await Assert.That(result).IsNull();
        await Assert.That(timedOut).IsTrue();
    }

    [Test]
    public async Task DryAlternativeWithinBudgetStillSucceeds()
    {
        var elapsed = TimeSpan.Zero;
        var budget = new BotGatherSearchBudget(elapsed: () => elapsed);
        var attempts = 0;
        var result = budget.Run(() => BotGatherApproach.Plan(Vector3.Zero, new Vector3(10,0,0), 3,
            (_,_) => 0, point => { attempts++; elapsed += TimeSpan.FromMilliseconds(10); return new("test", [point], 0, "ok"); },
            route => attempts > 1, budget.Check), out var timedOut);
        await Assert.That(timedOut).IsFalse();
        await Assert.That(result).IsNotNull();
        await Assert.That(attempts).IsEqualTo(2);
    }

    [Test]
    public async Task ExpiryDuringNativeGraphScanPropagatesToGatheringBudget()
    {
        var graph = new NetMissionReader(Stream.Null, 0);
        var start = new NodeDescriptor(graph) { Id = 1, Pos = Vector3.Zero, Type = 4 };
        var goal = new NodeDescriptor(graph) { Id = 2, Pos = new Vector3(4,0,0), Type = 4 };
        graph.NodeDescriptorList[start.Id] = start;
        graph.NodeDescriptorList[goal.Id] = goal;
        graph.LinkDescriptorList.Add(new LinkDescriptor(graph) { SourceNode=1, TargetNode=2,
            SourceNodeDescriptor=start, TargetNodeDescriptor=goal, MaxPassRadius=320 });
        var checks = 0;
        var budget = new BotGatherSearchBudget(elapsed: () => TimeSpan.FromMilliseconds(++checks * 600));
        var result = budget.Run(() => BotBaiRoute.PlanConnected(start, start.Pos, goal.Pos, (a,b) => a==b,
            false, out _, checkBudget: budget.Check), out var timedOut);
        await Assert.That(timedOut).IsTrue();
        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task GroundValidationDoesNotSwallowSearchExpiry()
    {
        var budget = new BotGatherSearchBudget(elapsed: () => TimeSpan.FromSeconds(3));
        var cancelled = false;
        try { BotTravelRoutePlanner.IsGroundCompatiblePoint(Vector3.Zero, (x,y) => { budget.Check(); return 0; }); }
        catch (OperationCanceledException) { cancelled = true; }
        await Assert.That(cancelled).IsTrue();
    }
}
