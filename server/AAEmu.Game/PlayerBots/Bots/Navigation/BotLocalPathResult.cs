using System.Numerics;

namespace AAEmu.Game.Bots.Navigation;

internal enum BotLocalPathStatus
{
    Missing,
    Partial,
    Complete
}

/// <summary>
/// Classifies the native path's actual endpoint. Complete means the returned
/// path reaches the goal tolerance; it is not proof of movement or interaction.
/// </summary>
internal sealed record BotLocalPathResult(
    BotLocalPathStatus Status,
    IReadOnlyList<Vector3> Waypoints,
    Vector3? ActualEndpoint)
{
    internal const float EndpointTolerance = 0.35f;

    internal static BotLocalPathResult Classify(IReadOnlyList<Vector3> path, Vector3 goal)
    {
        // Never remove invalid points and silently join the surrounding segments.
        if (!IsFinite(goal) || path == null || path.Count == 0 || path.Any(point => !IsFinite(point)))
            return new(BotLocalPathStatus.Missing, [], null);

        var points = Array.AsReadOnly(path.ToArray());
        var endpoint = points[^1];
        return new(
            Vector3.Distance(endpoint, goal) <= EndpointTolerance
                ? BotLocalPathStatus.Complete
                : BotLocalPathStatus.Partial,
            points,
            endpoint);
    }

    private static bool IsFinite(Vector3 point) =>
        float.IsFinite(point.X) && float.IsFinite(point.Y) && float.IsFinite(point.Z);
}
