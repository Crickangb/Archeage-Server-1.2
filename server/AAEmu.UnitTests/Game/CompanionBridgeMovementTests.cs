using System.Numerics;
using System.Collections.Concurrent;
using System.Reflection;
using AAEmu.Game.Bots.Navigation;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Tasks.Bots;
using Microsoft.Extensions.Time.Testing;
using TUnit.Mocks;

namespace AAEmu.UnitTests.Game;

public class CompanionBridgeMovementTests
{
    private sealed class OfflineCharacter() : Character(new UnitCustomModelParams())
    {
        public override void SetPosition(float x, float y, float z, float rotationX, float rotationY, float rotationZ)
        {
            Transform.Local.SetPosition(x, y, z);
        }
    }

    private static (Character Bot, BotMovementState State, BotMovementTask Task, FakeTimeProvider Clock) Mover()
    {
        var bot = new OfflineCharacter { Id = 99, Hp = 100 };
        typeof(GameObject).GetField("_disabledSetPosition", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(bot, true);
        typeof(GameObject).GetField("_parentWorld", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(bot,
            new WorldInstance(new WorldTemplate { OceanLevel = -10 }, 0, true, 0));
        // This fixture executes movement without visibility/anti-cheat services.
        bot.Transform.GetType().GetField("_skipCheckTime", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(bot.Transform, float.MaxValue);
        bot.Transform.World.Position = new Vector3(100, 100, 10);
        var state = new BotMovementState();
        var clock = new FakeTimeProvider();
        var task = new BotMovementTask(bot, state, Mock.Of<IBotMovementBroadcaster>().Object,
            _ => 5f, (_, _) => 0f, new BotConfig { JumpEnabled = true, AmbientJumpEnabled = true }, clock);
        return (bot, state, task, clock);
    }

    [Test]
    public async Task OwnerFootsteps_ClimbStairsAboveTerrainAndKeepUpperLanding()
    {
        var (bot,state,task,clock)=Mover();
        var owner=new OfflineCharacter { Id=100,Hp=100 };
        typeof(GameObject).GetField("_parentWorld",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(owner,bot.ParentWorld);
        Vector3[] points=[new(100,100,10),new(100,100.5f,10.5f),new(100,101,11),
            new(100,101.5f,11.5f),new(100,102,12),new(100,102.5f,12.5f),new(100,103,13)];
        foreach(var point in points)
        {
            clock.Advance(TimeSpan.FromMilliseconds(100));
            owner.Transform.World.Position=point;
            BotOwnerTrail.Record(owner,true,clock.GetUtcNow().UtcDateTime);
        }
        state.FollowTarget=owner;
        task.UpdateFollowDestination(points[^1],true);
        await Assert.That(state.TravelMode).IsEqualTo("owner-footsteps");
        for(var i=0;i<100;i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(100));task.Step();
            await Assert.That(bot.Transform.World.Position.Z>=9.99f).IsTrue();
            await Assert.That(state.IsFalling||state.IsJumping).IsFalse();
        }
        await Assert.That(Vector3.Distance(bot.Transform.World.Position,points[^1])<.02f).IsTrue();
        await Assert.That(state.TravelDestination.HasValue).IsFalse();
        await Assert.That(bot.Transform.World.Position.Z).IsEqualTo(13f);
    }

    [Test]
    public async Task OwnerFootsteps_DoNotJoinDifferentFloorOrCrossTeleportJumpAndDisconnect()
    {
        var (bot,_,_,clock)=Mover();
        var owner=new OfflineCharacter { Id=100,Hp=100 };
        typeof(GameObject).GetField("_parentWorld",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(owner,bot.ParentWorld);
        void Sample(Vector3 point,bool grounded=true)
        {
            clock.Advance(TimeSpan.FromMilliseconds(100));
            owner.Transform.World.Position=point;
            BotOwnerTrail.Record(owner,grounded,clock.GetUtcNow().UtcDateTime);
        }
        Sample(new(100,100,10));Sample(new(101,100,10));Sample(new(102,100,10));Sample(new(103,100,10));
        await Assert.That(BotOwnerTrail.Plan(bot,owner,clock.GetUtcNow().UtcDateTime)!=null).IsTrue();
        bot.Transform.World.Position=new(100,102,10);
        await Assert.That(BotOwnerTrail.Plan(bot,owner,clock.GetUtcNow().UtcDateTime)==null).IsTrue();
        await Assert.That(BotOwnerTrail.Plan(bot,owner,clock.GetUtcNow().UtcDateTime,(_,_)=>true)!=null).IsTrue();
        bot.Transform.World.Position=new(100,100,10);
        clock.Advance(TimeSpan.FromSeconds(10)); // Stationary owner keeps a usable trail.
        await Assert.That(BotOwnerTrail.Plan(bot,owner,clock.GetUtcNow().UtcDateTime)!=null).IsTrue();
        bot.Transform.World.Position=new(100,100,0);
        await Assert.That(BotOwnerTrail.Plan(bot,owner,clock.GetUtcNow().UtcDateTime)==null).IsTrue();
        bot.Transform.World.Position=new(100,100,10);
        Sample(new(200,100,10)); // Teleport ends the old segment.
        await Assert.That(BotOwnerTrail.Plan(bot,owner,clock.GetUtcNow().UtcDateTime)==null).IsTrue();
        Sample(new(100,100,10));Sample(new(101,100,10));Sample(new(102,100,10));
        Sample(new(102,100,11),false); // Airborne packet clears the segment.
        await Assert.That(BotOwnerTrail.Plan(bot,owner,clock.GetUtcNow().UtcDateTime)==null).IsTrue();
        Sample(new(100,100,10));Sample(new(101,100,10));Sample(new(102,100,10));
        clock.Advance(TimeSpan.FromSeconds(3));Sample(new(103,100,10));
        await Assert.That(BotOwnerTrail.Plan(bot,owner,clock.GetUtcNow().UtcDateTime)==null).IsTrue();
    }

    [Test]
    public async Task OwnerFootsteps_KeepTrailOnStationaryUpdateAndConcurrentCapture()
    {
        var (bot,_,_,clock)=Mover();
        var owner=new OfflineCharacter { Id=101,Hp=100 };
        typeof(GameObject).GetField("_parentWorld",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(owner,bot.ParentWorld);
        for(var i=0;i<4;i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(100));
            owner.Transform.World.Position=new(100+i,100,10);
            BotOwnerTrail.Record(owner,true,clock.GetUtcNow().UtcDateTime);
        }
        // The route request took its timestamp just before the latest packet.
        await Assert.That(BotOwnerTrail.Plan(bot,owner,clock.GetUtcNow().UtcDateTime.AddMilliseconds(-100))!=null).IsTrue();
        await Assert.That(BotOwnerTrail.Plan(bot,owner,clock.GetUtcNow().UtcDateTime.AddSeconds(-1))==null).IsTrue();
        // A delayed stationary packet must not erase the already walked stairs.
        clock.Advance(TimeSpan.FromSeconds(10));
        BotOwnerTrail.Record(owner,true,clock.GetUtcNow().UtcDateTime);
        await Assert.That(BotOwnerTrail.Plan(bot,owner,clock.GetUtcNow().UtcDateTime)!=null).IsTrue();
        // Unobserved movement after a gap still cannot bridge two segments.
        clock.Advance(TimeSpan.FromSeconds(3));
        owner.Transform.World.Position=new(104,100,10);
        BotOwnerTrail.Record(owner,true,clock.GetUtcNow().UtcDateTime);
        await Assert.That(BotOwnerTrail.Plan(bot,owner,clock.GetUtcNow().UtcDateTime)==null).IsTrue();
    }

    private sealed class RecoveryBroadcaster(Character bot) : IBotMovementBroadcaster
    {
        internal int Teleports;
        public void SendTeleport(Vector3 p,bool battle) { Teleports++; bot.Transform.World.Position=p; }
        public void SendMove(Vector3 p,Vector3 v,bool battle) { }
        public void SendStop(Vector3 p,bool battle) { }
        public void SendFall(Vector3 p,float velocity,bool battle) { }
        public void SendJump(Vector3 p,Vector3 v,bool battle) { }
        public void SendFaceTarget(Vector3 p,float rotation,bool battle) { }
        public void SendRelaxedStance(Vector3 p) { }
    }

    [Test]
    public async Task StalledFollow_RecallsToObservedUpperLandingAndPreservesHeight()
    {
        var (bot,state,_,clock)=Mover();
        var owner=new OfflineCharacter { Id=102,Hp=100 };
        typeof(GameObject).GetField("_parentWorld",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(owner,bot.ParentWorld);
        owner.Transform.World.Position=new(100,100,20);
        BotOwnerTrail.Record(owner,true,clock.GetUtcNow().UtcDateTime);
        clock.Advance(TimeSpan.FromMilliseconds(100));
        owner.Transform.World.Position=new(101,100,20);
        BotOwnerTrail.Record(owner,true,clock.GetUtcNow().UtcDateTime);
        state.FollowTarget=owner;
        var broadcaster=new RecoveryBroadcaster(bot);
        var task=new BotMovementTask(bot,state,broadcaster,_=>5f,(_,_)=>0f,new BotConfig(),clock);
        task.UpdateFollowDestination(owner.Transform.World.Position,true);
        await Assert.That(broadcaster.Teleports).IsEqualTo(0);
        clock.Advance(TimeSpan.FromSeconds(11));
        task.UpdateFollowDestination(owner.Transform.World.Position,true);
        await Assert.That(broadcaster.Teleports).IsEqualTo(1);
        await Assert.That(bot.Transform.World.Position).IsEqualTo(owner.Transform.World.Position);
        await Assert.That(state.FollowTarget).IsEqualTo(owner);
        await Assert.That(state.Destination.HasValue).IsFalse();
        await Assert.That(state.OccupiedNativeSurface.Height(101,100,20)).IsEqualTo(20f);
        task.UpdateFollowDestination(null,false);
        await Assert.That(broadcaster.Teleports).IsEqualTo(1);
        state.FollowTarget=null;
        clock.Advance(TimeSpan.FromMilliseconds(100));task.Step();
        await Assert.That(bot.Transform.World.Position.Z).IsEqualTo(20f);
        await Assert.That(state.IsFalling).IsFalse();
    }

    [Test]
    public async Task FollowRecovery_WaitsForOwnerAndBotProgressAndHonorsCooldown()
    {
        var watch=new BotFollowRecovery();
        var start=DateTime.UtcNow;
        var bot=new Vector3(0,0,10);var owner=new Vector3(10,0,20);
        await Assert.That(watch.Observe(bot,owner,start,true,10)).IsFalse();
        await Assert.That(watch.Observe(bot+Vector3.UnitX,owner,start.AddSeconds(9),true,10)).IsFalse();
        await Assert.That(watch.Observe(bot+Vector3.UnitX,owner,start.AddSeconds(18),true,10)).IsFalse();
        await Assert.That(watch.Observe(bot+Vector3.UnitX,owner+Vector3.UnitX,start.AddSeconds(19),true,10)).IsFalse();
        await Assert.That(watch.Observe(bot+Vector3.UnitX,owner+Vector3.UnitX,start.AddSeconds(21),true,10)).IsTrue();
        await Assert.That(watch.Observe(bot,owner,start.AddSeconds(22),true,10)).IsFalse();
        await Assert.That(watch.Observe(bot,owner,start.AddSeconds(40),true,10)).IsFalse();
        await Assert.That(watch.Observe(bot,owner,start.AddSeconds(51),true,10)).IsTrue();
        await Assert.That(watch.Observe(bot,owner,start.AddSeconds(52),false,10)).IsFalse();
        await Assert.That(watch.Observe(bot,owner,start.AddSeconds(100),true,10)).IsFalse();
    }

    [Test]
    public async Task RecoveryLanding_RejectsUnobservedAirborneWaterAndDifferentInstance()
    {
        var (bot,_,_,clock)=Mover();
        var owner=new OfflineCharacter { Id=103,Hp=100 };
        typeof(GameObject).GetField("_parentWorld",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(owner,bot.ParentWorld);
        var now=clock.GetUtcNow().UtcDateTime;
        await Assert.That(BotOwnerTrail.TryRecoveryLanding(bot,owner,now,out _)).IsFalse();
        owner.Transform.World.Position=new(100,100,20);
        BotOwnerTrail.Record(owner,true,now);
        clock.Advance(TimeSpan.FromMilliseconds(100));now=clock.GetUtcNow().UtcDateTime;
        owner.Transform.World.Position=new(101,100,20);BotOwnerTrail.Record(owner,true,now);
        await Assert.That(BotOwnerTrail.TryRecoveryLanding(bot,owner,now,out _)).IsTrue();
        await Assert.That(BotOwnerTrail.TryRecoveryLanding(bot,owner,now.AddSeconds(16),out _)).IsFalse();
        bot.Transform.GetType().GetField("_instanceId",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(bot.Transform,(uint)1);
        await Assert.That(BotOwnerTrail.TryRecoveryLanding(bot,owner,now,out _)).IsFalse();
        bot.Transform.GetType().GetField("_instanceId",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(bot.Transform,(uint)0);
        owner.IsRiding=true;
        await Assert.That(BotOwnerTrail.TryRecoveryLanding(bot,owner,now,out _)).IsFalse();
        owner.IsRiding=false;
        BotOwnerTrail.Record(owner,false,now);
        await Assert.That(BotOwnerTrail.TryRecoveryLanding(bot,owner,now,out _)).IsFalse();
        owner.Transform.World.Position=new(100,100,-20);BotOwnerTrail.Record(owner,true,now);
        await Assert.That(BotOwnerTrail.TryRecoveryLanding(bot,owner,now,out _)).IsFalse();
    }

    [Test]
    public async Task TightNativeTurns_ExecuteEveryVertexWithoutLosingSurface()
    {
        var (bot,state,task,clock)=Mover();
        Vector3[] points=[new(100,100,10),new(100,104,10),new(100.4f,104,10),
            new(100.4f,103.6f,10),new(104,103.6f,11),new(104,110,10)];
        var visited=new bool[points.Length];
        BotManager.ApplyTravelRoute(bot,state,points[^1],true,BotTravelIntent.PartyRendezvous,
            BotMovementOwner.External,new BotTravelRoute("bai-surface",points,0,"test") { NativeSurface=true });
        for(var tick=0;tick<200;tick++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(100));task.Step();
            var position=bot.Transform.World.Position;
            for(var i=0;i<points.Length;i++)
                visited[i] |= Vector3.Distance(position,points[i])<=.02f;
            await Assert.That(float.IsFinite(position.X+position.Y+position.Z)).IsTrue();
            await Assert.That(position.Z>=9.99f).IsTrue();
        }
        // The initial vertex is advanced immediately; every later corner and
        // final destination must actually be occupied, not just skipped nearby.
        await Assert.That(visited.Skip(1).All(value=>value)).IsTrue();
        await Assert.That(state.TravelDestination.HasValue).IsFalse();
    }

    [Test]
    public async Task NativeCorner_DoesNotAdvanceBeforeReachingItsSurfaceVertex()
    {
        var (bot, state, task, _) = Mover();
        var corner = new Vector3(100,105,10);
        BotManager.ApplyTravelRoute(bot, state, new(110,105,10), true,
            BotTravelIntent.PartyRendezvous, BotMovementOwner.External,
            new BotTravelRoute("bai-surface", [new(100,100,10),corner,new(110,105,10)],0,"test") { NativeSurface=true });
        state.Destination=corner;
        state.TravelWaypoints.Clear();
        state.TravelWaypoints.Enqueue(new(110,105,10));
        typeof(BotMovementTask).GetMethod("AdvanceReachedTravelWaypoints", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(task, [new Vector3(100,104.4f,10)]);
        await Assert.That(state.Destination).IsEqualTo((Vector3?)corner);
    }

    [Test]
    public async Task SwimmingFollow_ExecutesAndFloatsWhenOwnerStops()
    {
        var (bot,state,task,clock)=Mover();
        bot.ParentWorld.Template.OceanLevel=10;
        bot.ParentWorld.Water.OceanLevel=10;
        bot.Transform.World.Position=new Vector3(100,100,9.5f);
        var owner=new OfflineCharacter { Id=100,Hp=100 };
        typeof(GameObject).GetField("_parentWorld",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(owner,bot.ParentWorld);
        owner.Transform.World.Position=new Vector3(110,100,9.5f);
        state.FollowTarget=owner;
        for(var i=0;i<100;i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(100));task.Step();
            await Assert.That(bot.Transform.World.Position.Z).IsEqualTo(9.5f);
            await Assert.That(state.IsFalling||state.IsJumping).IsFalse();
        }
        await Assert.That(state.IsSwimming).IsTrue();
        await Assert.That(bot.Transform.World.Position.X>107.5f).IsTrue();
        await Assert.That(BotSwimFollow.CanRecall(owner)).IsFalse();
        await Assert.That(BotSwimFollow.CanRecall(bot)).IsFalse();
    }

    [Test]
    public async Task RaisedRoute_ExecutesCornersAndRetainsDeckWhenStopped()
    {
        var (bot, state, task, clock) = Mover();
        var points = new List<Vector3> { new(100,100,10), new(100,105,12), new(105,105,12), new(105,110,10) };
        var route = new BotTravelRoute("bai-surface", points, 0, "test") { NativeSurface = true };
        BotManager.ApplyTravelRoute(bot, state, points[^1], true, BotTravelIntent.PartyRendezvous, BotMovementOwner.External, route);
        var visitedCorner = false;
        for (var i = 0; i < 150; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(100));
            task.Step();
            var position = bot.Transform.World.Position;
            if (Vector3.Distance(position, points[1]) < 1f) visitedCorner = true;
            await Assert.That(position.Z >= 9.9f).IsTrue();
            await Assert.That(state.IsJumping || state.IsFalling).IsFalse();
        }
        await Assert.That(visitedCorner).IsTrue();
        await Assert.That(Vector3.Distance(bot.Transform.World.Position, points[^1]) < .8f).IsTrue();
        await Assert.That(state.TravelDestination.HasValue).IsFalse();
    }

    [Test]
    public async Task UnchangedFollowGoal_DoesNotRestartAnActiveRouteAfterOneSecond()
    {
        var (bot, state, task, clock) = Mover();
        var goal = new Vector3(105,110,10);
        BotManager.ApplyTravelRoute(bot, state, goal, true, BotTravelIntent.PartyRendezvous, BotMovementOwner.External,
            new BotTravelRoute("bai-surface", [new(100,100,10),new(100,105,10),goal], 0, "test") { NativeSurface = true });
        typeof(BotMovementTask).GetField("_followRouteGoal", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(task, goal);
        clock.Advance(TimeSpan.FromSeconds(3));
        var surface = state.NativeRouteSurface;
        var destination = state.Destination;
        var waypoints = state.TravelWaypoints.ToArray();
        task.UpdateFollowDestination(goal, true);
        await Assert.That(ReferenceEquals(state.NativeRouteSurface, surface)).IsTrue();
        await Assert.That(state.Destination).IsEqualTo(destination);
        await Assert.That(state.TravelWaypoints.SequenceEqual(waypoints)).IsTrue();
    }

    [Test]
    public async Task AirborneFollow_DoesNotReplaceWalkableSurfaceWithAirborneConnector()
    {
        var (bot, state, task, _) = Mover();
        state.NativeRouteSurface = new BotRouteSurface([new(100,100,10), new(100,105,10)]);
        state.Destination = new Vector3(100,105,10);
        state.IsJumping = true;
        var surface = state.NativeRouteSurface;
        task.UpdateFollowDestination(new Vector3(110,110,10), true);
        await Assert.That(ReferenceEquals(state.NativeRouteSurface, surface)).IsTrue();
        await Assert.That(state.Destination).IsEqualTo((Vector3?)new Vector3(100,105,10));
    }
    [Test]
    public async Task DueAmbientJump_IsSuppressedOnNativeSurfaceButStillWorksOnLand()
    {
        var (bot, state, task, clock) = Mover();
        state.FollowTarget = bot;
        state.Destination = new Vector3(100,105,10);
        state.NativeRouteSurface = new BotRouteSurface([new(100,100,10),new(100,105,10)]);
        state.NextAmbientJumpAt = clock.GetUtcNow().UtcDateTime.AddSeconds(-1);
        var jump = typeof(BotMovementTask).GetMethod("TryStartJump", BindingFlags.NonPublic | BindingFlags.Instance)!;
        jump.Invoke(task, [clock.GetUtcNow().UtcDateTime, bot.Transform.World.Position, 10f, state.Destination]);
        await Assert.That(state.IsJumping).IsFalse();
        state.NativeRouteSurface = null;
        state.NextAmbientJumpAt = clock.GetUtcNow().UtcDateTime.AddSeconds(-1);
        jump.Invoke(task, [clock.GetUtcNow().UtcDateTime, bot.Transform.World.Position, 10f, state.Destination]);
        await Assert.That(state.IsJumping).IsTrue();
    }

    [Test]
    public async Task WaypointAboveRiver_CannotBeCompletedFromTheRiverbed()
    {
        var (bot, state, task, _) = Mover();
        state.NativeRouteSurface = new BotRouteSurface([new(100,100,10),new(100,105,10)]);
        state.TravelDestination = state.Destination = new Vector3(100,100,10);
        typeof(BotMovementTask).GetMethod("AdvanceReachedTravelWaypoints", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(task, [new Vector3(100,100,0)]);
        await Assert.That(state.Destination).IsEqualTo((Vector3?)new Vector3(100,100,10));
    }

    [Test]
    public async Task OwnerRefreshEveryThreeTicks_DoesNotEraseRouteDuringBridgeCrossing()
    {
        var (bot, state, task, clock) = Mover();
        var owner = new OfflineCharacter { Id=5, Hp=100 };
        owner.Transform.World.Position=new Vector3(105,110,10);
        var manager=new BotManager(_=>null,onlineLookup:_=>null,
            botCombatManager:Mock.Of<IBotCombatManager>().Object,
            botArchetypeManager:Mock.Of<IBotArchetypeManager>().Object);
        var states=(ConcurrentDictionary<uint,BotMovementState>)typeof(BotManager)
            .GetField("_botStates",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(manager)!;
        states[bot.Id]=state;
        manager.SetFollowTarget(bot,owner,2f);
        // Use the direct owner goal to isolate route execution from peer spacing.
        state.FormationSlot=-1;
        var points=new List<Vector3>{new(100,100,10),new(100,105,12),new(105,105,12),owner.Transform.World.Position};
        BotManager.ApplyTravelRoute(bot,state,points[^1],true,BotTravelIntent.PartyRendezvous,BotMovementOwner.External,
            new BotTravelRoute("bai-surface",points,0,"test"){NativeSurface=true});
        typeof(BotMovementTask).GetField("_followRouteGoal",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(task,points[^1]);
        var surface=state.NativeRouteSurface;
        var arrived=false;
        for(var tick=0;tick<150;tick++)
        {
            if(tick%3==0)
            {
                var destination=state.Destination;
                var speed=state.TravelSpeed;
                manager.SetFollowTarget(bot,owner,2f);
                await Assert.That(ReferenceEquals(state.NativeRouteSurface,surface)).IsTrue();
                await Assert.That(state.Destination).IsEqualTo(destination);
                await Assert.That(state.TravelSpeed).IsEqualTo(speed);
            }
            clock.Advance(TimeSpan.FromMilliseconds(100));
            task.Step();
            await Assert.That(bot.Transform.World.Position.Z>=9.9f).IsTrue();
            if(!state.TravelDestination.HasValue){arrived=true;break;}
        }
        await Assert.That(arrived).IsTrue();
        await Assert.That(Vector3.Distance(bot.Transform.World.Position,owner.Transform.World.Position)<2.1f).IsTrue();
        // Explicitly following someone else must still replace the old route.
        BotManager.ApplyTravelRoute(bot,state,points[^1],true,BotTravelIntent.PartyRendezvous,BotMovementOwner.External,
            new BotTravelRoute("bai-surface",points,0,"test"){NativeSurface=true});
        var differentOwner=new OfflineCharacter { Id=6, Hp=100 };
        manager.SetFollowTarget(bot,differentOwner,2f);
        await Assert.That(state.NativeRouteSurface).IsNull();
        await Assert.That(state.Destination).IsNull();
        await Assert.That(ReferenceEquals(state.FollowTarget,differentOwner)).IsTrue();
    }

    [Test]
    public async Task OwnerMovesWhileBotOnRaisedCrossing_KeepsCrossingUntilItsEndpoint()
    {
        var (bot,state,task,_) = Mover();
        var goal=new Vector3(105,110,10);
        BotManager.ApplyTravelRoute(bot,state,goal,true,BotTravelIntent.PartyRendezvous,BotMovementOwner.External,
            new BotTravelRoute("bai-surface",[new(100,100,10),new(100,105,12),goal],0,"test"){NativeSurface=true});
        typeof(BotMovementTask).GetField("_followRouteGoal",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(task,goal);
        var surface=state.NativeRouteSurface;
        var destination=state.Destination;
        task.UpdateFollowDestination(new Vector3(120,120,10),true);
        await Assert.That(ReferenceEquals(state.NativeRouteSurface,surface)).IsTrue();
        await Assert.That(state.Destination).IsEqualTo(destination);
        await Assert.That(state.TravelDestination).IsEqualTo((Vector3?)goal);
        state.NativeRouteSurface=null;state.TravelDestination=null;state.Destination=null;
        // After arrival, ordinary following may adopt the owner's newer goal.
        task.UpdateFollowDestination(new Vector3(120,120,10),true);
        await Assert.That(state.Destination).IsEqualTo((Vector3?)new Vector3(120,120,10));
    }

}
