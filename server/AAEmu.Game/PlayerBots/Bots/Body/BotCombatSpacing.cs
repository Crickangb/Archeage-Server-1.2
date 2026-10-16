using System.Numerics;

namespace AAEmu.Game.Bots.Body;

/// <summary>Deterministic attack positions for companions sharing a hostile target.</summary>
public static class BotCombatSpacing
{
    public static Vector3 RangedPosition(Vector3 current, Vector3 target, float range)
    {
        var offset = new Vector2(current.X - target.X, current.Y - target.Y);
        var direction = offset.LengthSquared() < 0.0001f ? Vector2.UnitX : Vector2.Normalize(offset);
        return new Vector3(target.X + direction.X * range, target.Y + direction.Y * range, current.Z);
    }

    public static Vector3 Position(Vector3 target, uint targetId, int slot, int count, float range)
    {
        count = Math.Max(1, count);
        slot = Math.Clamp(slot, 0, count - 1);
        var radius = MathF.Max(0.1f, range * 0.8f);
        var angle = (targetId % 360) * MathF.PI / 180f + 2f * MathF.PI * slot / count;
        return new Vector3(target.X + MathF.Cos(angle) * radius, target.Y + MathF.Sin(angle) * radius, target.Z);
    }
}
