using System.Numerics;
using AAEmu.Game.Bots.Navigation;
using AAEmu.Game.Models.CryEngine.Entities;
using AAEmu.Game.Models.CryEngine.Readers;

namespace AAEmu.UnitTests.Game;

public class CompanionBridgeNavigationTests
{
    private static (NodeDescriptor,NodeDescriptor) Graph(bool connected=true)
    {
        var graph=new NetMissionReader(Stream.Null,0);
        var points=new[]{new Vector3(0,0,10),new Vector3(0,4,12),new Vector3(4,4,12),new Vector3(4,8,10)};
        var nodes=points.Select((p,i)=>new NodeDescriptor(graph){Id=i+1,Pos=p,Type=4}).ToArray();
        foreach(var n in nodes)graph.NodeDescriptorList[n.Id]=n;
        for(var i=0;i<(connected?3:2);i++)graph.LinkDescriptorList.Add(new LinkDescriptor(graph){SourceNode=(uint)nodes[i].Id,TargetNode=(uint)nodes[i+1].Id,SourceNodeDescriptor=nodes[i],TargetNodeDescriptor=nodes[i+1],MaxPassRadius=320});
        return (nodes[0],nodes[^1]);
    }

    [Test]
    public async Task MidDeckConnector_FollowsItsNativeEdgeAndRejectsOtherFloors()
    {
        var (start,goal)=Graph();
        var origin=new Vector3(0,2,11);
        var route=BotBaiRoute.PlanCandidates([start],origin,goal.Pos,(a,b)=>false,false,out var failure);
        await Assert.That(failure).IsNull();
        await Assert.That(route.Waypoints[0]).IsEqualTo(origin);
        await Assert.That(route.Waypoints[^1]).IsEqualTo(goal.Pos);
        await Assert.That(BotBaiRoute.EdgeConnector(start,new Vector3(0,2,7))).IsFalse();
        await Assert.That(BotBaiRoute.EdgeConnector(start,new Vector3(2,2,11))).IsFalse();
        start.NetMission.LinkDescriptorList[0].MaxPassRadius=0;
        await Assert.That(BotBaiRoute.EdgeConnector(start,origin)).IsFalse();
    }

    [Test]
    public async Task NativeRoute_PreservesRaisedDeckAndObstacleCorners()
    {
        var (start,goal)=Graph();var path=BotBaiRoute.Search(start,goal);
        await Assert.That(path.Count).IsEqualTo(4);
        await Assert.That(path[1]).IsEqualTo(new Vector3(0,4,12));
        await Assert.That(path[2]).IsEqualTo(new Vector3(4,4,12));
    }

    [Test]
    public async Task DisconnectedOrExhaustedSearch_DoesNotAppendFakeGoal()
    {
        var (start,goal)=Graph(false);
        await Assert.That(BotBaiRoute.Search(start,goal).Count).IsEqualTo(0);
        (start,goal)=Graph();
        await Assert.That(BotBaiRoute.Search(start,goal,1).Count).IsEqualTo(0);
    }

    [Test]
    public async Task BlockedNativeLink_CannotBeWalked()
    {
        var (start,goal)=Graph();start.NetMission.LinkDescriptorList[1].MaxPassRadius=0;
        await Assert.That(BotBaiRoute.Search(start,goal).Count).IsEqualTo(0);
    }

    [Test]
    public async Task SurfaceHeight_KeepsDeckInsteadOfRiverbedAndRejectsOffRoute()
    {
        var surface=new BotRouteSurface([new(0,0,149),new(0,10,149),new(0,0,144),new(0,10,144)]);
        await Assert.That(surface.Height(0,5,149)).IsEqualTo(149f);
        await Assert.That(surface.Height(0,5,144)).IsEqualTo(144f);
        await Assert.That(float.IsNaN(surface.Height(3,5,149))).IsTrue();
    }
    [Test]
    public async Task ChunkBoundary_ChoosesReachableEndpointInsteadOfNearestDisconnectedNode()
    {
        var (start, _) = Graph();
        var disconnected = new NodeDescriptor(start.NetMission) { Id=99, Type=4, Pos=new(4,9,10) };
        start.NetMission.NodeDescriptorList[99]=disconnected;
        var goal=new Vector3(4,10,10);
        var route=BotBaiRoute.PlanConnected(start,start.Pos,goal,(a,b)=>a==b,true,out var failure);
        await Assert.That(failure).IsNull();
        await Assert.That(route.Waypoints[^1]).IsEqualTo(new Vector3(4,8,10));
        await Assert.That(route.Waypoints.Contains(disconnected.Pos)).IsFalse();
        await Assert.That(route.Waypoints.Contains(goal)).IsFalse();
    }

    [Test]
    public async Task AdjacentChunk_IsTriedWhenNearestStartGraphCannotReachGoal()
    {
        var (first, _) = Graph(false);
        var (second, _) = Graph();
        var goal=new Vector3(4,8,10);
        var route=BotBaiRoute.PlanCandidates([first,second],first.Pos,goal,(a,b)=>a==b,false,out var failure);
        await Assert.That(failure).IsNull();
        await Assert.That(route.Waypoints[^1]).IsEqualTo(goal);
        await Assert.That(route.Waypoints.Contains(new Vector3(4,4,12))).IsTrue();
    }

    [Test]
    public async Task NoConnectedProgress_DoesNotInventRouteOrAppendUnreachableOwner()
    {
        var (start, _) = Graph(false);
        var goal=new Vector3(4,10,10);
        var route=BotBaiRoute.PlanConnected(start,start.Pos,goal,(a,b)=>a==b,false,out var failure);
        await Assert.That(route).IsNull();
        await Assert.That(failure.StartsWith("no_reachable_endpoint")).IsTrue();
    }

    [Test]
    public async Task WaterInsideEdge_ChoosesRaisedBridgeInsteadOfDryEndpointsAcrossRiver()
    {
        var graph=new NetMissionReader(Stream.Null,0);
        var points=new[]{new Vector3(0,0,10),new Vector3(10,0,10),new Vector3(0,5,12),new Vector3(10,5,12)};
        var nodes=points.Select((p,i)=>new NodeDescriptor(graph){Id=i+1,Pos=p,Type=4}).ToArray();
        foreach(var n in nodes)graph.NodeDescriptorList[n.Id]=n;
        foreach(var (a,b) in new[]{(0,1),(0,2),(2,3),(3,1)})
            graph.LinkDescriptorList.Add(new LinkDescriptor(graph){SourceNode=(uint)nodes[a].Id,TargetNode=(uint)nodes[b].Id,
                SourceNodeDescriptor=nodes[a],TargetNodeDescriptor=nodes[b],MaxPassRadius=320,MaxWaterDepth=0});
        bool Water(Vector3 p)=>p.X>3f&&p.X<7f&&p.Z<11f;
        var route=BotBaiRoute.PlanConnected(nodes[0],points[0],points[1],(a,b)=>a==b,false,out var failure,isWater:Water);
        await Assert.That(failure).IsNull();
        await Assert.That(route.Waypoints.Contains(points[2])).IsTrue();
        await Assert.That(route.Waypoints.Contains(points[3])).IsTrue();
        for(var i=1;i<route.Waypoints.Count;i++)
            await Assert.That(BotBaiRoute.DrySegment(route.Waypoints[i-1],route.Waypoints[i],Water)).IsTrue();
    }

    [Test]
    public async Task WetStartOrTerminalConnector_CannotBypassWaterWithShortSnap()
    {
        var (start,goal)=Graph();
        var origin=new Vector3(-1,0,10);
        bool Water(Vector3 p)=>p.X<0f;
        var route=BotBaiRoute.PlanCandidates([start],origin,goal.Pos,(a,b)=>true,true,out _,Water);
        await Assert.That(route).IsNull();
        route=BotBaiRoute.PlanConnected(start,start.Pos,new Vector3(-1,0,10),(a,b)=>true,false,out _,isWater:Water);
        await Assert.That(route).IsNull();
    }

}
