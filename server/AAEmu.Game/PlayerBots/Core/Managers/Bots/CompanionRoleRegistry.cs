using System.Collections.Concurrent;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Runtime companion-role view shared by the remote control surface and combat decisions.
/// Persistence remains owned by PlayerBotsRemoteService (PlayerBotsRoles.json).
/// </summary>
public static class CompanionRoleRegistry
{
    public const string DefaultRole = "melee-dps";
    private static readonly ConcurrentDictionary<uint, string> Roles = new();

    public static string Get(uint characterId) =>
        Roles.TryGetValue(characterId, out var role) && !string.IsNullOrWhiteSpace(role)
            ? role
            : DefaultRole;

    public static void Set(uint characterId, string role)
    {
        if (string.IsNullOrWhiteSpace(role))
            Roles.TryRemove(characterId, out _);
        else
            Roles[characterId] = role.Trim().ToLowerInvariant();
    }
}
