using System.Collections.Concurrent;

using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Bots.Diagnostics;

public sealed record BotCombatTelemetryEntry(DateTime TimestampUtc, uint BotId, uint SkillId, uint TargetObjId, string TargetName, float Distance, string Result, string Detail);

public static class BotCombatTelemetry
{
    private const int MaxEntriesPerBot = 50;
    private static readonly ConcurrentDictionary<uint, ConcurrentQueue<BotCombatTelemetryEntry>> Entries = new();
    private static readonly ConcurrentDictionary<string, DateTime> LastDiagnostic = new();

    public static void Record(uint botId, uint skillId, Unit target, float distance, string result, string detail = null)
    {
        var queue = Entries.GetOrAdd(botId, static _ => new ConcurrentQueue<BotCombatTelemetryEntry>());
        queue.Enqueue(new BotCombatTelemetryEntry(DateTime.UtcNow, botId, skillId, target?.ObjId ?? 0, target?.Name ?? string.Empty, distance, result ?? string.Empty, detail ?? string.Empty));
        while (queue.Count > MaxEntriesPerBot && queue.TryDequeue(out _)) { }
    }

    public static void RecordDiagnostic(uint botId, Unit target, float distance, string reason, int throttleSeconds = 3)
    {
        var now = DateTime.UtcNow;
        var key = $"{botId}:{reason}";
        if (LastDiagnostic.TryGetValue(key, out var last) && (now - last).TotalSeconds < Math.Max(1, throttleSeconds))
            return;

        LastDiagnostic[key] = now;
        var queue = Entries.GetOrAdd(botId, static _ => new ConcurrentQueue<BotCombatTelemetryEntry>());
        queue.Enqueue(new BotCombatTelemetryEntry(now, botId, 0, target?.ObjId ?? 0, target?.Name ?? string.Empty,
            distance, "WAIT", reason ?? "unknown"));
        while (queue.Count > MaxEntriesPerBot && queue.TryDequeue(out _)) { }
    }

    public static BotCombatTelemetryEntry[] Recent(uint botId, int take = 25)
    {
        if (!Entries.TryGetValue(botId, out var queue)) return [];
        return queue.Reverse().Take(Math.Clamp(take, 1, MaxEntriesPerBot)).ToArray();
    }
}
