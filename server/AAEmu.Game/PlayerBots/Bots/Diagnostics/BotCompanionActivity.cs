using System.Numerics;

namespace AAEmu.Game.Bots.Diagnostics;

public sealed record BotCompanionActivityEntry(DateTime TimestampUtc, string Event, string Detail,
    uint ResourceId, float X, float Y, float Z);

// Read-only diagnostics. Bounded memory, no disk access on the world tick.
public sealed class BotCompanionActivityBuffer
{
    private readonly object _sync = new();
    private readonly Dictionary<uint, Queue<BotCompanionActivityEntry>> _entries = new();
    public void Record(uint botId, string kind, string detail, Vector3 position, uint resourceId = 0)
    {
        lock (_sync)
        {
            if (!_entries.TryGetValue(botId, out var queue))
            {
                if (_entries.Count >= 256)
                    _entries.Remove(_entries.MinBy(pair => pair.Value.Last().TimestampUtc).Key);
                _entries[botId] = queue = new();
            }
            queue.Enqueue(new(DateTime.UtcNow, kind, detail?.Length > 2048 ? detail[..2048] : detail ?? "",
                resourceId, position.X, position.Y, position.Z));
            while (queue.Count > 200) queue.Dequeue();
        }
    }
    public BotCompanionActivityEntry[] Recent(uint botId, int take = 200)
    {
        lock (_sync)
            return _entries.TryGetValue(botId, out var queue)
                ? queue.Reverse().Take(Math.Clamp(take, 1, 200)).ToArray() : [];
    }
}

public static class BotCompanionActivity
{
    private static readonly BotCompanionActivityBuffer Buffer = new();
    public static void Record(uint botId, string kind, string detail, Vector3 position, uint resourceId = 0)
    {
        Buffer.Record(botId, kind, detail, position, resourceId);
    }
    public static BotCompanionActivityEntry[] Recent(uint botId, int take = 200) => Buffer.Recent(botId, take);
}
