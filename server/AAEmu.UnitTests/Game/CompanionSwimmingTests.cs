using System.Numerics;
using AAEmu.Game.Bots.Navigation;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.Models;
using AAEmu.Game.Models.StaticValues;

namespace AAEmu.UnitTests.Game;

public class CompanionSwimmingTests
{
    [Test]
    public async Task GroundPackets_AreUprightAndGroundedWhileSwimmingRemainsUngrounded()
    {
        var bot=new Character(new UnitCustomModelParams());
        bot.Transform.Local.SetRotationDegree(180,45,90);
        var clock=new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var broadcaster=new BotMovementBroadcaster(bot,clock);
        AAEmu.Game.Models.Game.Units.Movements.UnitMoveType packet=null;
        broadcaster.MoveTypeSink=move=>packet=move;
        clock.Advance(TimeSpan.FromMilliseconds(100));
        broadcaster.SendStop(new(10,10,10),false);
        await Assert.That(packet.ActorFlags).IsEqualTo((byte)MoveTypeActorFlags.StandingOnSolid);
        await Assert.That(packet.RotationX).IsEqualTo((sbyte)0);
        await Assert.That(packet.RotationY).IsEqualTo((sbyte)0);
        await Assert.That(packet.DeltaMovement.All(x=>x==0)).IsTrue();
        await Assert.That(bot.Transform.Local.Rotation.X).IsEqualTo(0f);
        await Assert.That(bot.Transform.Local.Rotation.Y).IsEqualTo(0f);
        var yaw=packet.RotationZ;
        clock.Advance(TimeSpan.FromMilliseconds(100));
        broadcaster.SendMove(new(11,10,10),new(1,0,0),false);
        await Assert.That(packet.ActorFlags).IsEqualTo((byte)MoveTypeActorFlags.StandingOnSolid);
        await Assert.That(packet.RotationX).IsEqualTo((sbyte)0);
        await Assert.That(packet.RotationY).IsEqualTo((sbyte)0);
        await Assert.That(packet.RotationZ).IsEqualTo(yaw);
        clock.Advance(TimeSpan.FromMilliseconds(100));
        broadcaster.SendSwim(new(11,10,9.5f),Vector3.Zero,false);
        await Assert.That(packet.ActorFlags).IsEqualTo((byte)0);
        await Assert.That(packet.Stance).IsEqualTo(GameStanceType.Swim);
    }

    [Test]
    public async Task Swim_StaysAtSurfaceInsteadOfDeepRiverbed()
    {
        var current=new Vector3(0,0,9.5f);
        var result=BotSwimFollow.Step(current,new(20,0,9.5f),3,.1f,(_,_)=>-30,_=>10,p=>p.Z<=10,out var next,out var wet);
        await Assert.That(result&&wet).IsTrue();
        await Assert.That(next.X).IsEqualTo(.3f);
        await Assert.That(next.Z).IsEqualTo(9.5f);
    }

    [Test]
    public async Task WaterEntryAndGentleExit_AreAllowedButCliffAndBridgeAreNot()
    {
        var entered=BotSwimFollow.Step(new(0,0,10),new(20,0,9.5f),3,.1f,(_,_)=>0,_=>10,p=>p.Z<=10,out var next,out var wet);
        await Assert.That(entered&&wet).IsTrue();
        var exited=BotSwimFollow.Step(next,new(20,0,10.1f),3,.1f,(_,_)=>10.1f,_=>10,p=>p.Z<=10,out var shore,out wet);
        await Assert.That(exited&&!wet).IsTrue();
        await Assert.That(shore.Z).IsEqualTo(10.1f);
        await Assert.That(BotSwimFollow.Step(new(0,0,16),new(20,0,9.5f),3,.1f,(_,_)=>0,_=>10,p=>p.Z<=10,out _,out _)).IsFalse();
        await Assert.That(BotSwimFollow.Step(next,new(20,0,16),3,.1f,(_,_)=>16,_=>10,p=>p.Z<=10,out _,out _)).IsFalse();
    }

    [Test]
    public async Task SwimPacket_UsesSwimStanceAndStopsWithoutWalkingInput()
    {
        var bot=new Character(new UnitCustomModelParams());
        var clock=new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var broadcaster=new BotMovementBroadcaster(bot,clock);
        clock.Advance(TimeSpan.FromMilliseconds(100));
        AAEmu.Game.Models.Game.Units.Movements.UnitMoveType packet=null;
        broadcaster.MoveTypeSink=move=>packet=(AAEmu.Game.Models.Game.Units.Movements.UnitMoveType)move;
        broadcaster.SendSwim(new(0,0,9.5f),new(3,0,0),false);
        await Assert.That(packet.Stance).IsEqualTo(GameStanceType.Swim);
        await Assert.That(packet.VelX).IsEqualTo((short)3000);
        clock.Advance(TimeSpan.FromMilliseconds(100));
        broadcaster.SendSwim(new(0,0,9.5f),Vector3.Zero,false);
        await Assert.That(packet.Stance).IsEqualTo(GameStanceType.Swim);
        await Assert.That(packet.ActorFlags).IsEqualTo((byte)0);
        await Assert.That(packet.DeltaMovement.All(x=>x==0)).IsTrue();
    }
}
