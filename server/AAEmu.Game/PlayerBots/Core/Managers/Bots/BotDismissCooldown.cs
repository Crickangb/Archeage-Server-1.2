using System.Collections.Concurrent;

namespace AAEmu.Game.Core.Managers.Bots;

public sealed class BotDismissCooldown(Func<DateTime> utcNow)
{
    private readonly ConcurrentDictionary<uint, DateTime> _until = new();
    public void Start(uint characterId, int seconds) => _until[characterId] = utcNow().AddSeconds(seconds);
    public int RemainingSeconds(uint characterId)
    {
        if (!_until.TryGetValue(characterId, out var until)) return 0;
        var remaining = Math.Max(0, (int)Math.Ceiling((until - utcNow()).TotalSeconds));
        return remaining;
    }
}
