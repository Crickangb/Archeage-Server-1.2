using System.Numerics;
using AAEmu.Game.Bots.Navigation;

namespace AAEmu.Game.Bots.Gathering;

internal readonly record struct BotGatherThreat(Vector3 Position, float Clearance);

/// <summary>Bounded ground detours around live gathering threats.</summary>
internal static class BotGatherDetour
{
    internal static BotTravelRoute Plan(Vector3 start, BotTravelRoute route,
        IReadOnlyList<BotGatherThreat> threats, Func<float, float, float> ground, Func<Vector3, bool> water)
    {
        if (route?.Waypoints.Count is not > 0) return null;
        // Prefer 20m; retry with the 15m minimum where a narrow passage requires it.
        return TryPlan(start, route, threats, ground, water, true) ??
            TryPlan(start, route, threats, ground, water, false);
    }

    internal static IReadOnlyList<Vector3> LookAhead(Vector3 start, IEnumerable<Vector3> route, float distance)
    {
        var points = new List<Vector3> { start };
        foreach (var endpoint in route)
        {
            var length = Vector3.Distance(start, endpoint);
            if (!float.IsFinite(length)) break;
            if (length > distance)
            {
                points.Add(Vector3.Lerp(start, endpoint, distance / length));
                break;
            }
            points.Add(endpoint);
            distance -= length;
            if (distance <= 0) break;
            start = endpoint;
        }
        return points;
    }

    private static float Radius(BotGatherThreat threat, bool preferred)
        => Math.Max(preferred ? 20f : 15f, threat.Clearance);

    private static BotTravelRoute TryPlan(Vector3 start, BotTravelRoute route,
        IReadOnlyList<BotGatherThreat> threats, Func<float, float, float> ground,
        Func<Vector3, bool> water, bool preferred)
    {
        bool Clear(Vector3 a, Vector3 b) => threats.All(t =>
            BotGatherController.SegmentDistance(t.Position, a, b) > Radius(t, preferred));
        var previous = start;
        var blocked = false;
        foreach (var point in route.Waypoints)
        {
            if (!Clear(previous, point)) { blocked = true; break; }
            previous = point;
        }
        if (!blocked) return route;
        // Do not flatten supported bridge/stair decks to terrain while avoiding enemies.
        if (!Clear(start, start)) return null;
        if (route.NativeSurface)
        {
            // A native route that is already on terrain may use ground avoidance.
            // Raised decks must retain their surface and cannot use this fallback.
            var from = start;
            foreach (var endpoint in route.Waypoints)
            {
                if (!BotTravelRoutePlanner.IsSafeNearbyConnector(from, endpoint, ground)) return null;
                from = endpoint;
            }
            route = route with { NativeSurface = false };
        }
        var points = new List<Vector3> { start };
        previous = start;
        foreach (var endpoint in route.Waypoints)
        {
            var length = Vector3.Distance(previous, endpoint);
            if (!float.IsFinite(length) || length > BotGatherController.MaximumTravel) return null;
            var count = Math.Max(1, (int)Math.Ceiling(length / 10));
            for (var i = 1; i <= count; i++)
            {
                var point = Vector3.Lerp(previous, endpoint, (float)i / count);
                point.Z = ground(point.X, point.Y);
                points.Add(point);
            }
            previous = endpoint;
            if (points.Count > 4096) return null;
        }
        var destination = route.Waypoints[^1];
        if (Math.Abs(destination.Z - points[^1].Z) > 1) return null;
        points[^1] = destination;
        for (var repairs = 0; repairs < 16; repairs++)
        {
            var index = -1;
            for (var i = 0; i + 1 < points.Count; i++)
                if (!Clear(points[i], points[i + 1])) { index = i; break; }
            if (index < 0)
                return route with { Waypoints = points.Skip(1).ToArray(),
                    Detail = route.Detail + $" hostile_detour clearance={(preferred ? 20 : 15)}" };
            var hit = threats.First(t => BotGatherController.SegmentDistance(t.Position,
                points[index], points[index + 1]) <= Radius(t, preferred));
            var begin = index;
            while (begin > 0 && threats.Any(t => Vector3.Distance(points[begin], t.Position) < Radius(t, preferred) + 3)) begin--;
            var end = index + 1;
            while (end < points.Count - 1 && threats.Any(t => Vector3.Distance(points[end], t.Position) < Radius(t, preferred) + 3)) end++;
            if (!Clear(points[begin], points[begin]) || !Clear(points[end], points[end])) return null;
            var nearby = threats.Where(t => BotGatherController.SegmentDistance(t.Position, points[begin], points[end]) < Radius(t, preferred) + 35)
                .OrderBy(t => Vector3.Distance(t.Position, hit.Position)).Take(12).ToArray();
            var nodes = new List<Vector3> { points[begin], points[end] };
            // Retain safe anchors along the original route so a long cluster
            // exit can be joined through bounded 30m ground connectors.
            nodes.AddRange(points.Skip(begin + 1).Take(end - begin - 1).Where(p => Clear(p, p)));
            foreach (var threat in nearby)
                for (var i = 0; i < 24; i++)
                {
                    var angle = i * MathF.PI / 12;
                    var radius = Radius(threat, preferred) + 2;
                    var x = threat.Position.X + radius * MathF.Cos(angle);
                    var y = threat.Position.Y + radius * MathF.Sin(angle);
                    var point = new Vector3(x, y, ground(x, y));
                    if (float.IsFinite(point.Z) && !water(point) && Clear(point, point)) nodes.Add(point);
                }
            if (nodes.Count > 512) return null;
            bool Safe(Vector3 a, Vector3 b)
            {
                if (!BotTravelRoutePlanner.IsSafeNearbyConnector(a, b, ground) || !Clear(a, b)) return false;
                var samples = Math.Max(1, (int)Math.Ceiling(Vector3.Distance(a, b)));
                for (var i = 0; i <= samples; i++)
                {
                    var point = Vector3.Lerp(a, b, (float)i / samples);
                    point.Z = ground(point.X, point.Y);
                    if (!float.IsFinite(point.Z) || water(point)) return false;
                }
                return true;
            }
            var distances = Enumerable.Repeat(float.PositiveInfinity, nodes.Count).ToArray();
            var parents = Enumerable.Repeat(-1, nodes.Count).ToArray();
            var queue = new PriorityQueue<int, float>();
            distances[0] = 0; queue.Enqueue(0, 0);
            while (queue.TryDequeue(out var current, out var cost))
            {
                if (cost > distances[current]) continue;
                if (current == 1) break;
                for (var next = 1; next < nodes.Count; next++)
                {
                    var nextCost = cost + Vector3.Distance(nodes[current], nodes[next]);
                    if (nextCost >= distances[next] || !Safe(nodes[current], nodes[next])) continue;
                    distances[next] = nextCost; parents[next] = current; queue.Enqueue(next, nextCost);
                }
            }
            if (parents[1] < 0) return null;
            var detour = new List<Vector3>();
            for (var i = 1; i != 0; i = parents[i]) detour.Add(nodes[i]);
            detour.Reverse();
            points.RemoveRange(begin + 1, end - begin);
            points.InsertRange(begin + 1, detour);
        }
        return null;
    }
}
