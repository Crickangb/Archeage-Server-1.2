using System.Numerics;
using AAEmu.Game.Bots.Navigation;
using AAEmu.Game.Bots.Questing;

namespace AAEmu.Game.Bots.Gathering;

internal static class BotGatherApproach
{
    internal static bool DryRoute(Vector3 start, BotTravelRoute route,
        Func<float,float,float> ground, Func<Vector3,bool> water)
    {
        if (route?.Waypoints.Count is not > 0) return false;
        var previous = start;
        foreach (var endpoint in route.Waypoints)
        {
            var length = Vector3.Distance(previous,endpoint);
            if (!float.IsFinite(length) || length > BotGatherController.MaximumTravel) return false;
            var samples = Math.Max(1,(int)Math.Ceiling(length));
            for (var i=0;i<=samples;i++)
            {
                var point = Vector3.Lerp(previous,endpoint,(float)i/samples);
                // Generic routes are executed at terrain height. A high line
                // between two dry waypoints must not hide water below it.
                if (!route.NativeSurface) point.Z = ground(point.X,point.Y);
                if (!float.IsFinite(point.X+point.Y+point.Z) || water(point)) return false;
            }
            previous = endpoint;
        }
        return true;
    }

    internal static BotTravelRoute Plan(Vector3 start, Vector3 resource, float range,
        Func<float,float,float> ground, Func<Vector3,BotTravelRoute> plan,
        Func<BotTravelRoute,bool> supported = null, Action checkBudget = null)
    {
        if (!float.IsFinite(range) || range <= 0 ||
            !float.IsFinite(start.X + start.Y + start.Z + resource.X + resource.Y + resource.Z)) return null;
        checkBudget?.Invoke();
        var candidates = new List<Vector3>();
        void Add(float x,float y)
        {
            checkBudget?.Invoke();
            var point = new Vector3(x,y,ground(x,y));
            // Leave a small range margin so movement's final rounding cannot
            // stop just outside the native action's three-dimensional range.
            if (BotQuestInteractions.InRange(point,resource,Math.Max(0,range-.1f)) &&
                !candidates.Any(other => Vector3.Distance(other,point) < .1f)) candidates.Add(point);
        }
        Add(resource.X,resource.Y);
        var bearing = MathF.Atan2(start.Y-resource.Y,start.X-resource.X);
        foreach (var fraction in new[] { .85f,.5f })
            for (var i=0;i<16;i++)
            {
                var angle = bearing + i*MathF.PI/8;
                Add(resource.X + MathF.Cos(angle)*range*fraction,
                    resource.Y + MathF.Sin(angle)*range*fraction);
            }
        foreach (var point in candidates.OrderBy(point => Vector3.Distance(start,point)))
        {
            checkBudget?.Invoke();
            var route = plan(point);
            checkBudget?.Invoke();
            if (route?.Waypoints.Count > 0 && BotQuestInteractions.InRange(route.Waypoints[^1],resource,range) &&
                (supported?.Invoke(route) ?? true))
                return route;
        }
        // Raised native surfaces may have no terrain-height candidate. Retain
        // the existing native route option; never invent a height or teleport.
        checkBudget?.Invoke();
        var native = plan(resource);
        checkBudget?.Invoke();
        return native?.Waypoints.Count > 0 && BotQuestInteractions.InRange(native.Waypoints[^1],resource,range) && (supported?.Invoke(native) ?? true)
            ? native : null;
    }
}
