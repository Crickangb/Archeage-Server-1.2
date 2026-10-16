#if !PLAYERBOTS_AAEMU_3_0
using System.Numerics;
using System.Runtime.CompilerServices;
using AAEmu.Game.Models.Game.Char;
using NLog;

namespace AAEmu.Game.Bots.Navigation;

// Bounded, in-memory evidence of continuous grounded owner movement. Never
// connect across a context change, transport, jump, disconnect or teleport.
internal static class BotOwnerTrail
{
    private sealed class Trail
    {
        internal readonly List<Vector3> Points = [];
        internal DateTime Updated;
        internal uint World;
        internal uint Instance;
        internal byte ActorFlags;
        internal string Capture = "new";
        internal DateTime NextDiagnostic;
    }

    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private static readonly ConditionalWeakTable<Character, Trail> Trails = new();

    internal static void Record(Character owner, bool grounded, DateTime now, byte actorFlags = 0)
    {
        var trail = Trails.GetOrCreateValue(owner);
        lock (trail)
        {
            var position = owner.Transform.World.Position;
            trail.ActorFlags = actorFlags;
            trail.Capture = !grounded ? "not_grounded" : owner.IsDead ? "dead" :
                owner.IsRiding || owner.Transform.Parent != null || owner.Transform.StickyParent != null ? "attached" :
                owner.ParentWorld == null ? "no_world" : owner.ParentWorld.IsWater(position) ? "water" :
                !float.IsFinite(position.X + position.Y + position.Z) ? "nonfinite" : "grounded";
            var valid = grounded && !owner.IsDead && !owner.IsRiding &&
                owner.Transform.Parent == null && owner.Transform.StickyParent == null &&
                owner.ParentWorld != null && !owner.ParentWorld.IsWater(position) &&
                float.IsFinite(position.X + position.Y + position.Z);
            if (!valid)
            {
                trail.Points.Clear();
                trail.Updated = now;
                LogCapture(owner,trail,now);
                return;
            }
            if (trail.Points.Count > 0)
            {
                var previous = trail.Points[^1];
                var elapsed = (now - trail.Updated).TotalSeconds;
                var displacement = Vector3.Distance(previous,position);
                // Idle movement packets can be several seconds apart. Keep an
                // unchanged endpoint, but never connect movement across a gap.
                var gap = elapsed > 2 && (displacement >= .1f || elapsed > 120);
                if (trail.World != owner.Transform.WorldId || trail.Instance != owner.Transform.InstanceId ||
                    elapsed < 0 || gap || displacement > 3 ||
                    Math.Abs(previous.Z - position.Z) > 1 ||
                    displacement > Math.Max(.25,elapsed * 12))
                {
                    trail.Points.Clear();
                    trail.Capture = "continuity_break";
                }
            }
            trail.World = owner.Transform.WorldId;
            trail.Instance = owner.Transform.InstanceId;
            trail.Updated = now;
            if (trail.Points.Count == 0 || Vector3.Distance(trail.Points[^1],position) >= .1f)
                trail.Points.Add(position);
            if (trail.Points.Count > 4096)
                trail.Points.RemoveRange(0,trail.Points.Count - 4096);
            LogCapture(owner,trail,now);
        }
    }

    private static void LogCapture(Character owner, Trail trail, DateTime now)
    {
        if (now < trail.NextDiagnostic) return;
        trail.NextDiagnostic = now.AddSeconds(10);
        Logger.Info($"OWNER id={owner.Id} ev=owner_trail_capture capture={trail.Capture} actor_flags={trail.ActorFlags} points={trail.Points.Count} pos={owner.Transform.World.Position}");
    }

    internal static bool TryRecoveryLanding(Character bot, Character owner, DateTime now, out Vector3 position)
    {
        position = default;
        if (owner?.ParentWorld == null || !ReferenceEquals(bot.ParentWorld,owner.ParentWorld) ||
            bot.Transform.InstanceId != owner.Transform.InstanceId || owner.IsDead || owner.IsRiding ||
            owner.Transform.Parent != null || owner.Transform.StickyParent != null ||
            !Trails.TryGetValue(owner,out var trail)) return false;
        lock (trail)
        {
            var age = (now - trail.Updated).TotalSeconds;
            var current = owner.Transform.World.Position;
            if (trail.Capture != "grounded" || trail.Points.Count < 2 || age < -.25 || age > 15 ||
                trail.World != owner.Transform.WorldId || trail.Instance != owner.Transform.InstanceId ||
                Vector3.Distance(current,trail.Points[^1]) > .1f || owner.ParentWorld.IsWater(current) ||
                !float.IsFinite(current.X + current.Y + current.Z)) return false;
            // Use the occupied player surface, including raised stairs. Offsets
            // could land in a wall or below the deck, so use this exact point.
            position = current;
            return true;
        }
    }

    internal static string Describe(Character bot, Character owner, DateTime now)
    {
        if (owner == null) return "reason=no_owner";
        if (!Trails.TryGetValue(owner,out var trail)) return "reason=no_capture";
        lock (trail)
        {
            var age = (now - trail.Updated).TotalSeconds;
            var nearest = trail.Points.Count == 0 ? float.PositiveInfinity :
                trail.Points.Min(point => Vector3.Distance(bot.Transform.World.Position,point));
            var endDistance = trail.Points.Count == 0 ? float.PositiveInfinity :
                Vector3.Distance(owner.Transform.World.Position,trail.Points[^1]);
            var reason = !ReferenceEquals(bot.ParentWorld,owner.ParentWorld) ||
                bot.Transform.InstanceId != owner.Transform.InstanceId ||
                trail.World != owner.Transform.WorldId || trail.Instance != owner.Transform.InstanceId ? "different_context" :
                owner.IsRiding || owner.Transform.Parent != null || owner.Transform.StickyParent != null ? "attached" :
                trail.Points.Count < 2 ? "insufficient_points" : age < -.25 ? "clock_skew" : age > 120 ? "expired" :
                endDistance > .3f ? "owner_off_trail" : nearest > 3 ? "too_far_to_join" :
                Vector3.Distance(bot.Transform.World.Position,trail.Points[^1]) < 2 ? "already_near_end" :
                "no_safe_join_or_no_remaining_path";
            return FormattableString.Invariant($"reason={reason} capture={trail.Capture} actor_flags={trail.ActorFlags} points={trail.Points.Count} age_s={age:F1} nearest_m={nearest:F2} owner_end_m={endDistance:F2}");
        }
    }

    internal static BotTravelRoute Plan(Character bot, Character owner, DateTime now,
        Func<Vector3,Vector3,bool> connector = null)
    {
        if (owner == null || !Trails.TryGetValue(owner,out var trail) ||
            !ReferenceEquals(bot.ParentWorld,owner.ParentWorld) ||
            bot.Transform.InstanceId != owner.Transform.InstanceId ||
            owner.Transform.Parent != null || owner.Transform.StickyParent != null || owner.IsRiding)
            return null;
        lock (trail)
        {
            if (trail.Points.Count < 2 || now - trail.Updated > TimeSpan.FromMinutes(2) ||
                // A packet can update the trail after the caller captured now.
                // Allow one movement tick of overlap under this lock.
                trail.Updated - now > TimeSpan.FromMilliseconds(250) || trail.World != owner.Transform.WorldId ||
                trail.Instance != owner.Transform.InstanceId ||
                Vector3.Distance(owner.Transform.World.Position,trail.Points[^1]) > .3f)
                return null;
            var position = bot.Transform.World.Position;
            var nearest = -1;
            var distance = 3f;
            for (var i=0;i<trail.Points.Count;i++)
            {
                var candidate = Vector3.Distance(position,trail.Points[i]);
                if (candidate <= distance && (candidate <= .6f || connector?.Invoke(position,trail.Points[i]) == true))
                    { distance=candidate; nearest=i; }
            }
            // Join directly only at the same observed surface, or through a
            // separately checked ground connector. Never join across floors.
            if (nearest < 0 || nearest == trail.Points.Count - 1 ||
                Vector3.Distance(position,trail.Points[^1]) < 2)
                return null;
            List<Vector3> points = [position];
            points.AddRange(trail.Points.Skip(nearest));
            return new BotTravelRoute("owner-footsteps",points,0,"continuous_grounded_owner_trail")
                { NativeSurface=true };
        }
    }
}
#endif
