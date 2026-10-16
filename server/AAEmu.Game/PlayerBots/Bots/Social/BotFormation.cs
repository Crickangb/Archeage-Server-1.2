using System.Numerics;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;

namespace AAEmu.Game.Bots.Social;

public static class BotFormation
{
    public static Vector3 FollowPositionFor(Character bot, Character leader, BotMovementState movement,
        IEnumerable<Character> companions)
    {
        // Ordinary companions can stand anywhere nearby with enough personal space.
        // Only catch-up/crowding needs a destination; explicit column orders retain their layout.
        if (bot?.Transform != null && leader?.Transform != null && movement != null &&
            movement.FormationColumns == 0 && movement.FormationSlot >= 0)
        {
            var current = bot.Transform.World.Position;
            var owner = leader.Transform.World.Position;
            var distance = Vector2.Distance(new(current.X, current.Y), new(owner.X, owner.Y));
            if (distance >= 1.5f && distance <= MathF.Max(2f, movement.FollowDistance) + 2f &&
                MathF.Abs(current.Z - owner.Z) <= 2f &&
                !companions.Any(other => other.Id != bot.Id && !other.IsDead && other.Transform != null &&
                    ReferenceEquals(other.ParentWorld, bot.ParentWorld) &&
                    other.Transform.InstanceId == bot.Transform.InstanceId &&
                    Vector3.Distance(current, other.Transform.World.Position) < 1.5f))
                return current;
        }
        return PositionFor(leader, movement);
    }

    public static Vector3 PositionFor(Character leader, BotMovementState movement)
    {
        if (leader?.Transform == null || movement?.FormationSlot < 0)
            return leader?.Transform?.World.Position ?? Vector3.Zero;

        return movement.FormationColumns > 0
            ? SpreadPositionFor(
                leader,
                movement.FormationSlot,
                movement.FormationMemberCount,
                movement.FollowDistance,
                movement.FormationColumns,
                movement.FormationSpacing)
            : PositionFor(leader, movement.FormationSlot, movement.FollowDistance);
    }

    public static Vector3 PositionFor(Character leader, int slot, float spacing)
    {
        if (leader?.Transform == null || slot < 0)
            return leader?.Transform?.World.Position ?? Vector3.Zero;

        spacing = MathF.Max(2f, spacing);
        var angle = StableSignedVariation(leader.Id, 0, 0xA511E9B3u) * MathF.PI + slot * 2.39996323f;
        var radius = spacing * (1f + 0.15f * StableSignedVariation(leader.Id, slot, 0x63D83595u));
        // World-relative offsets do not shuffle companions when an idle owner turns.
        return leader.Transform.World.Position + new Vector3(MathF.Cos(angle) * radius, MathF.Sin(angle) * radius, 0f);
    }

    // Party-follow targets are recalculated every movement tick. A stable hash gives
    // each slot organic-looking variance without random sampling or target jitter.
    private static float StableSignedVariation(uint leaderId, int slot, uint salt)
    {
        var value = unchecked(leaderId * 0x9E3779B9u + ((uint)slot + 1u) * 0x85EBCA6Bu + salt);
        value ^= value >> 16;
        value = unchecked(value * 0x7FEB352Du);
        value ^= value >> 15;
        value = unchecked(value * 0x846CA68Bu);
        value ^= value >> 16;
        return (value & 0x00FFFFFFu) / 8388607.5f - 1f;
    }

    public static Vector3 SpreadPositionFor(
        Character leader,
        int slot,
        int memberCount,
        float baseDistance,
        int columns,
        float spacing)
    {
        if (leader?.Transform == null || slot < 0 || memberCount <= 0 || slot >= memberCount)
            return leader?.Transform?.World.Position ?? Vector3.Zero;

        baseDistance = MathF.Max(0.5f, baseDistance);
        spacing = MathF.Max(0.5f, spacing);
        columns = Math.Clamp(columns, 1, memberCount);

        var row = slot / columns;
        var column = slot % columns;
        var membersBeforeRow = row * columns;
        var membersInRow = Math.Min(columns, memberCount - membersBeforeRow);
        var centeredColumn = column - (membersInRow - 1) / 2f;

        var yaw = leader.Transform.World.Rotation.Z * MathF.PI / 180f;
        var forward = new Vector3(MathF.Cos(yaw), MathF.Sin(yaw), 0f);
        var right = new Vector3(-forward.Y, forward.X, 0f);
        var behind = baseDistance + row * spacing;
        var lateral = centeredColumn * spacing;
        return leader.Transform.World.Position - forward * behind + right * lateral;
    }
}
