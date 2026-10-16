using System.Numerics;

namespace AAEmu.Game.Bots.Navigation;

internal sealed class BotRouteSurface(IReadOnlyList<Vector3> points)
{
    internal float Height(float x,float y,float referenceZ)
    {
        var best=float.PositiveInfinity;var bestVertical=float.PositiveInfinity;var height=float.NaN;
        for(var i=1;i<points.Count;i++)
        {
            var a=points[i-1];var b=points[i];var delta=new Vector2(b.X-a.X,b.Y-a.Y);
            var t=delta.LengthSquared()<.0001f?0:Math.Clamp(Vector2.Dot(new(x-a.X,y-a.Y),delta)/delta.LengthSquared(),0,1);
            var p=Vector3.Lerp(a,b,t);var distance=new Vector2(x-p.X,y-p.Y).Length();
            // Only the occupied narrow graph corridor is a supported surface.
            if(distance>.8f)continue;
            var vertical=Math.Abs(p.Z-referenceZ);
            // The nearest walked edge supplies the height. Adding the height
            // difference to lateral distance can pin an ascending bot to the
            // lower endpoint instead of letting it climb the edge underfoot.
            // Use height only to disambiguate overlapping floors.
            if(distance<best-.001f || (Math.Abs(distance-best)<=.001f && vertical<bestVertical))
            { best=distance;bestVertical=vertical;height=p.Z; }
        }
        return height;
    }
}
