using System.Numerics;
using AAEmu.Game.Bots.Gathering;
using AAEmu.Game.Bots.Navigation;

namespace AAEmu.UnitTests.Game;

public class CompanionGatherDetourTests
{
    private static BotTravelRoute Route(Vector3 goal)=>new("test",[goal],0,"original");
    private static bool Clear(Vector3 start,BotTravelRoute route,BotGatherThreat threat)
    {
        foreach(var point in route.Waypoints)
        {
            if(BotGatherController.SegmentDistance(threat.Position,start,point)<=threat.Clearance)return false;
            start=point;
        }
        return true;
    }
    [Test]
    public async Task HostileInMiddle_DetoursAndRetainsGatheringDestination()
    {
        var threat=new BotGatherThreat(new(50,0,0),15);var goal=new Vector3(100,0,0);
        var result=BotGatherDetour.Plan(Vector3.Zero,Route(goal),[threat],(_,_)=>0,_=>false);
        await Assert.That(result!=null).IsTrue();
        await Assert.That(result.Waypoints[^1]).IsEqualTo(goal);
        await Assert.That(Clear(Vector3.Zero,result,threat with {Clearance=20})).IsTrue();
    }
    [Test]
    public async Task MovingHostile_NewRemainingRouteAvoidsBothCreatures()
    {
        var threats=new[]{new BotGatherThreat(new(50,0,0),15),new BotGatherThreat(new(50,25,0),15)};
        var start=new Vector3(20,0,0);
        var result=BotGatherDetour.Plan(start,Route(new(100,0,0)),threats,(_,_)=>0,_=>false);
        await Assert.That(result!=null).IsTrue();
        await Assert.That(threats.All(t=>Clear(start,result,t))).IsTrue();
    }
    [Test]
    public async Task NarrowDryCorridor_FallsBackToFifteenMetres()
    {
        var threat=new BotGatherThreat(new(50,0,0),15);
        var result=BotGatherDetour.Plan(Vector3.Zero,Route(new(100,0,0)),[threat],(_,_)=>0,p=>Math.Abs(p.Y)>19);
        await Assert.That(result!=null).IsTrue();
        await Assert.That(Clear(Vector3.Zero,result,threat)).IsTrue();
        await Assert.That(result.Detail.Contains("clearance=15")).IsTrue();
    }
    [Test]
    public async Task NoDryDetour_RejectsWaterAndCliff()
    {
        var threat=new BotGatherThreat(new(50,0,0),15);
        var wet=BotGatherDetour.Plan(Vector3.Zero,Route(new(100,0,0)),[threat],(_,_)=>0,p=>Math.Abs(p.Y)>1);
        await Assert.That(wet==null).IsTrue();
        var cliff=BotGatherDetour.Plan(Vector3.Zero,Route(new(100,0,0)),[threat],(_,y)=>Math.Abs(y)>1?100:0,_=>false);
        await Assert.That(cliff==null).IsTrue();
    }
    [Test]
    public async Task UnsafeStartOrEndpoint_CannotInventSafeDetour()
    {
        var nearStart=BotGatherDetour.Plan(Vector3.Zero,Route(new(100,0,0)),[new(new(5,0,0),15)],(_,_)=>0,_=>false);
        var nearEnd=BotGatherDetour.Plan(Vector3.Zero,Route(new(100,0,0)),[new(new(100,0,0),15)],(_,_)=>0,_=>false);
        await Assert.That(nearStart==null).IsTrue();
        await Assert.That(nearEnd==null).IsTrue();
    }
    [Test]
    public async Task NativeDeck_PreservedWithoutFlatteningToTerrain()
    {
        var deck=Route(new(100,0,20)) with {NativeSurface=true};var start=new Vector3(0,0,20);
        await Assert.That(ReferenceEquals(BotGatherDetour.Plan(start,deck,[],(_,_)=>-10,_=>false),deck)).IsTrue();
        await Assert.That(BotGatherDetour.Plan(start,deck,[new(new(50,0,20),15)],(_,_)=>-10,_=>false)==null).IsTrue();
    }

    [Test]
    public async Task OverlappingHostiles_ExtendsExitPastEntireGroup()
    {
        var threats=new[]{new BotGatherThreat(new(50,0,0),15),new BotGatherThreat(new(75,0,0),15)};
        var result=BotGatherDetour.Plan(Vector3.Zero,Route(new(130,0,0)),threats,(_,_)=>0,_=>false);
        await Assert.That(result!=null).IsTrue();
        await Assert.That(threats.All(t=>Clear(Vector3.Zero,result,t with {Clearance=20}))).IsTrue();
        await Assert.That(result.Waypoints[^1]).IsEqualTo(new Vector3(130,0,0));
    }
    [Test]
    public async Task LiveScan_LooksSixtyMetresPastShortWaypoints()
    {
        var waypoints=Enumerable.Range(1,10).Select(i=>new Vector3(i*10,0,0));
        var points=BotGatherDetour.LookAhead(Vector3.Zero,waypoints,60);
        await Assert.That(points[^1]).IsEqualTo(new Vector3(60,0,0));
        var threat=new Vector3(55,10,0);
        await Assert.That(points.Zip(points.Skip(1)).Any(p=>BotGatherController.SegmentDistance(threat,p.First,p.Second)<20)).IsTrue();
        await Assert.That(BotGatherController.SegmentDistance(threat,Vector3.Zero,new(10,0,0))>20).IsTrue();
    }
    [Test]
    public async Task NativeGroundRoute_CanUseVerifiedTerrainDetour()
    {
        var route=new BotTravelRoute("bai-surface",Enumerable.Range(1,10).Select(i=>new Vector3(i*10,0,0)).ToArray(),0,"ground_edges") {NativeSurface=true};
        var threat=new BotGatherThreat(new(50,0,0),15);
        var result=BotGatherDetour.Plan(Vector3.Zero,route,[threat],(_,_)=>0,_=>false);
        await Assert.That(result!=null).IsTrue();
        await Assert.That(result.NativeSurface).IsFalse();
        await Assert.That(Clear(Vector3.Zero,result,threat with {Clearance=20})).IsTrue();
    }
}
