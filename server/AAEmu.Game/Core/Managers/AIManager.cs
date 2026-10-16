using AAEmu.Commons.Utils;
using AAEmu.Game.Models.Game.AI.v2.Framework;

using NLog;

namespace AAEmu.Game.Core.Managers;

public class AIManager : Singleton<AIManager>, IAIManager
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();
    private bool _initialized = false;

    private List<NpcAi> _npcAis;
    private object _aiLock;
    private readonly object _tickLock = new();

    public void Initialize()
    {
        if (_initialized)
            return;

        _npcAis = [];
        _aiLock = new object();
        TickManager.Instance.OnTick.Subscribe(Tick, TimeSpan.FromMilliseconds(100), true);

        _initialized = true;
    }

    public void AddAi(NpcAi ai)
    {
        lock (_aiLock)
        {
            _npcAis.Add(ai);
        }
    }

    public void Tick(TimeSpan delta)
    {
        // Spawning holds a spawner lock while AddAi acquires _aiLock. An AI
        // callback can despawn through that same spawner; never run it under
        // the registry lock. Keep callbacks serialized using a separate lock.
        lock (_tickLock)
        {
            NpcAi[] snapshot;
            lock (_aiLock)
                snapshot = _npcAis.ToArray();
            foreach (var npcai in snapshot)
            {
                try
                {
                    if (npcai.Owner != null)
                        npcai.Tick(delta);
                }
                catch (Exception e)
                {
                    Logger.Error(e);
                }
            }
        }
    }

    public void Stop()
    {
        Logger.Debug($"Stopping AIManager");
        TickManager.Instance.OnTick.UnSubscribe(Tick);
    }
}
