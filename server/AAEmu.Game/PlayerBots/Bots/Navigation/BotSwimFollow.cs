#if !PLAYERBOTS_AAEMU_3_0
using System.Numerics;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Bots.Navigation;

internal static class BotSwimFollow
{
    internal static bool OnBoat(Character owner)
    {
        for(var parent=owner.Transform.Parent ?? owner.Transform.StickyParent; parent!=null; parent=parent.Parent)
            if(parent.GameObject is Slave ship && ship.Template.IsABoat())return true;
        return false;
    }

    internal static bool CanRecall(Character owner)
    {
        if(owner?.ParentWorld==null || owner.Transform.Parent!=null || owner.Transform.StickyParent!=null)return false;
        var p=owner.Transform.World.Position;
        return !owner.ParentWorld.IsWater(p) && Math.Abs(p.Z-owner.ParentWorld.GetHeight(p.X,p.Y))<=.75f;
    }

    internal static bool Step(Vector3 current, Vector3 target, float speed, float dt,
        Func<float,float,float> ground, Func<Vector3,float> surface, Func<Vector3,bool> water,
        out Vector3 next, out bool swimming)
    {
        next=current;swimming=water(current);
        if(!float.IsFinite(current.X+current.Y+current.Z+target.X+target.Y+target.Z)||dt<=0)return false;
        var delta=new Vector2(target.X-current.X,target.Y-current.Y);
        var length=delta.Length();
        var distance=Math.Min(Math.Max(0,speed)*dt,Math.Max(0,length-2f));
        var candidate=current;
        if(length>.0001f){candidate.X+=delta.X/length*distance;candidate.Y+=delta.Y/length*distance;}
        var floor=ground(candidate.X,candidate.Y);
        var level=surface(candidate);
        if(!float.IsFinite(floor)||!float.IsFinite(level))return false;
        var wet=floor<level && water(new(candidate.X,candidate.Y,level-.5f));
        var height=wet?level-.5f:floor;
        // Do not turn a bridge, cliff, dock wall or steep bank into a swim shortcut.
        if(Math.Abs(height-current.Z)>.75f)return false;
        candidate.Z=height;
        next=candidate;swimming=wet;
        return true;
    }
}
#endif
