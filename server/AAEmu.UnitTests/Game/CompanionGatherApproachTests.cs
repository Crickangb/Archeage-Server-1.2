using System.Numerics;
using AAEmu.Game.Bots.Gathering;
using AAEmu.Game.Bots.Navigation;
using AAEmu.Game.Bots.Questing;

namespace AAEmu.UnitTests.Game;

public class CompanionGatherApproachTests
{
    [Test]
    public async Task RaisedVein_UsesStandingPointWithinNativeRange()
    {
        var start=new Vector3(0,0,0);var resource=new Vector3(10,0,1.5f);
        BotTravelRoute GroundRoute(Vector3 point) => point.Z==0
            ? new("test",[start,point],0,"ground") : new("unavailable",[],0,"object_above_ground");
        var route=BotGatherApproach.Plan(start,resource,3,(_,_)=>0,GroundRoute);
        await Assert.That(route!=null).IsTrue();
        await Assert.That(route.Waypoints[^1].Z).IsEqualTo(0f);
        await Assert.That(BotQuestInteractions.InRange(route.Waypoints[^1],resource,3)).IsTrue();
        await Assert.That(route.Waypoints[^1].X<resource.X).IsTrue();
    }

    [Test]
    public async Task BlockedNearestApproach_TriesAnotherSideWithoutAcceptingPartialRoute()
    {
        var start=new Vector3(0,0,0);var resource=new Vector3(10,0,0);
        BotTravelRoute Route(Vector3 point) => point.X>10
            ? new("test",[point],0,"reachable_other_side") : new("test",[start],0,"partial_out_of_range");
        var route=BotGatherApproach.Plan(start,resource,3,(_,_)=>0,Route);
        await Assert.That(route!=null).IsTrue();
        await Assert.That(route.Waypoints[^1].X>10).IsTrue();
    }

    [Test]
    public async Task UnreachableOrDifferentFloor_DoesNotInventRoute()
    {
        var calls=0;
        var route=BotGatherApproach.Plan(Vector3.Zero,new(10,0,20),3,(_,_)=>0,point=>
            { calls++; return new("unavailable",[],0,"no_route"); });
        await Assert.That(route==null).IsTrue();
        await Assert.That(calls).IsEqualTo(1);
        await Assert.That(BotGatherApproach.Plan(Vector3.Zero,new(10,0,0),float.NaN,(_,_)=>0,_=>throw new Exception())==null).IsTrue();
    }

    [Test]
    public async Task NativeRaisedSurface_RemainsAvailableWhenTerrainIsBelowResource()
    {
        var resource=new Vector3(10,0,20);
        var route=BotGatherApproach.Plan(Vector3.Zero,resource,3,(_,_)=>0,
            point=>new("bai-surface",[point],0,"supported_upper_surface") { NativeSurface=true });
        await Assert.That(route.NativeSurface).IsTrue();
        await Assert.That(route.Waypoints[^1]).IsEqualTo(resource);
    }
    [Test]
    public async Task DryRoute_RejectsWaterBetweenDryWaypointsAtExecutedTerrainHeight()
    {
        var start=new Vector3(0,0,10);
        var route=new BotTravelRoute("bai+terminal-direct",[new Vector3(775,0,10)],0,"long_route");
        float Ground(float x,float y)=>x>=300 && x<=305 ? -1 : 10;
        bool Water(Vector3 p)=>p.Z<0;
        await Assert.That(BotGatherApproach.DryRoute(start,route,Ground,Water)).IsFalse();
        // A supported raised native bridge keeps its own dry deck height.
        var bridge=route with { NativeSurface=true };
        await Assert.That(BotGatherApproach.DryRoute(start,bridge,Ground,Water)).IsTrue();
    }

    [Test]
    public async Task WetFirstRoute_IsSkippedForDryAlternative()
    {
        var start=Vector3.Zero;var resource=new Vector3(10,0,0);
        var route=BotGatherApproach.Plan(start,resource,3,(_,_)=>0,
            point=>new("test",[point],0,"route"),candidate=>candidate.Waypoints[^1].Y>1);
        await Assert.That(route!=null).IsTrue();
        await Assert.That(route.Waypoints[^1].Y>1).IsTrue();
    }
}
