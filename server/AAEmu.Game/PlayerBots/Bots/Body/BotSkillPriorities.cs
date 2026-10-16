using System.Text.Json;

namespace AAEmu.Game.Bots.Body;

/// <summary>Persistent ranking; eligibility and cooldown checks remain with the combat skill gate.</summary>
internal sealed class BotSkillPriorities
{
    internal static readonly BotSkillPriorities Current = new(Path.Combine(AppContext.BaseDirectory, "Data", "PlayerBotsSkillPriorities.json"));
    private readonly string _path;
    private readonly object _sync = new();
    private Dictionary<uint, uint[]> _orders;

    internal BotSkillPriorities(string path)
    {
        _path = path;
        _orders = File.Exists(path)
            ? JsonSerializer.Deserialize<Dictionary<uint, uint[]>>(File.ReadAllText(path)) ?? new()
            : new();
    }

    internal uint[] Get(uint botId)
    {
        lock (_sync) return _orders.TryGetValue(botId, out var ids) ? (uint[])ids.Clone() : [];
    }

    internal void Set(uint botId, uint[] ids)
    {
        if (ids == null || ids.Length > 96 || ids.Any(id => id == 0) || ids.Distinct().Count() != ids.Length)
            throw new ArgumentException("Choose distinct learned skill IDs.");
        lock (_sync)
        {
            var next = new Dictionary<uint, uint[]>(_orders);
            if (ids.Length == 0) next.Remove(botId); else next[botId] = (uint[])ids.Clone();
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(next, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, _path, true);
            _orders = next;
        }
    }

    internal static int Rank(uint[] ids, uint skillId)
    {
        var index = Array.IndexOf(ids, skillId);
        return index < 0 ? int.MaxValue : index;
    }
}
