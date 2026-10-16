using System.Diagnostics;

using AAEmu.Commons.Utils;
using AAEmu.Commons.Utils.DB;
using AAEmu.Commons.Utils.Updater;
using AAEmu.Game.Bots.Ops;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.Stream;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Core.Network.Login;
using AAEmu.Game.Core.Network.Stream;
using AAEmu.Game.GameData.Framework;
using AAEmu.Game.IO;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Utils.Scripts;

using Microsoft.Extensions.Hosting;

using NLog;

namespace AAEmu.Game;

public sealed class GameService : IHostedService, IDisposable
{
    private enum ActivityDirectorLifecycleState
    {
        Ready,
        Starting,
        Running,
        Rejected,
        Stopping,
        Stopped
    }

    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();
    private static TimeProvider s_timeProvider = TimeProvider.System;
    public static DateTime StartTime { get; private set; } = DateTime.UtcNow;
    public static TimeSpan TimeSinceStart => s_timeProvider.GetUtcNow().UtcDateTime.Subtract(StartTime);

    private readonly ManagerOrchestrator _orchestrator;
    private readonly IBotManager _botManager;
    private readonly ITaskManager? _taskManager;
    private readonly BotConfig _botConfig;
    // Lock order is service lifecycle, scheduler, then Director execution/state.
    // A Director tick never acquires this service lock, so shutdown may wait for
    // a running tick without creating a lock cycle.
    private readonly object _activityDirectorLifecycleLock = new();
    private readonly Func<BotActivityDirectorTask> _activityDirectorFactory;
    private readonly Func<BotActivityDirectorTask, bool> _tryStartActivityDirector;
    private readonly Action<BotActivityDirectorTask> _stopActivityDirector;
    private readonly Action _beforeActivityDirectorStartLock;
    private readonly Action _beforeBotSubsystemStopLock;
    private BotActivityDirectorTask _activityDirector;
    private ActivityDirectorLifecycleState _activityDirectorState;
    private bool _activityDirectorScheduled;
    private bool _botManagerStopped;

    public GameService(IServiceProvider serviceProvider, ManagerOrchestrator orchestrator, TimeProvider timeProvider)
        : this(serviceProvider, orchestrator, timeProvider, null, null, null)
    {
    }

    internal GameService(
        IServiceProvider serviceProvider,
        ManagerOrchestrator orchestrator,
        TimeProvider timeProvider,
        Func<BotActivityDirectorTask> activityDirectorFactory,
        Func<BotActivityDirectorTask, bool> tryStartActivityDirector,
        Action<BotActivityDirectorTask> stopActivityDirector,
        Action beforeActivityDirectorStartLock = null,
        Action beforeBotSubsystemStopLock = null)
    {
        SingletonContainer.ServiceProvider = serviceProvider;
        _orchestrator = orchestrator;
        _botManager = serviceProvider.GetService(typeof(IBotManager)) as IBotManager ?? BotManager.Instance;
        _taskManager = serviceProvider.GetService(typeof(ITaskManager)) as ITaskManager;
        _botConfig = serviceProvider.GetService(typeof(BotConfig)) as BotConfig ?? BotConfig.Instance;
        _activityDirectorFactory = activityDirectorFactory ??
                                   (() => new BotActivityDirectorTask(_botConfig, _botManager, timeProvider));
        _tryStartActivityDirector = tryStartActivityDirector ?? (director => director.TryStart());
        _stopActivityDirector = stopActivityDirector ?? (director => director.Stop());
        _beforeActivityDirectorStartLock = beforeActivityDirectorStartLock;
        _beforeBotSubsystemStopLock = beforeBotSubsystemStopLock;
        s_timeProvider = timeProvider;
        StartTime = timeProvider.GetUtcNow().UtcDateTime;
    }

    internal static void LogSelectedGameDatabaseSchema(string database)
    {
        Logger.Info($"Selected Game database schema: {database}");
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        Logger.Info("Starting daemon: AAEmu.Game");
        LogSelectedGameDatabaseSchema(AppConfiguration.Instance.Connections.MySQLProvider.Database);

        // Check for updates
        using (var connection = MySQL.CreateConnection())
        {
            if (!MySqlDatabaseUpdater.Run(connection, "aaemu_game", AppConfiguration.Instance.Connections.MySQLProvider.Database,
                    AppConfiguration.Instance.Connections.AutoApplyUpdates))
            {
                Logger.Fatal("Failed to update database!");
                Logger.Fatal("Press Ctrl+C to quit");
                return;
            }
        }

        ClientFileManager.Initialize();
        if (ClientFileManager.Sources.Count == 0)
        {
            Logger.Fatal($"Failed up load client files! ({string.Join(", ", AppConfiguration.Instance.ClientData.Sources)})");
            Logger.Fatal("Press Ctrl+C to quit");
            return;
        }

        var stopWatch = new Stopwatch();
        stopWatch.Start();

        // --- Auto-Attack system: ensure compact.sqlite3 has the 6 weapon-anim columns ---
        // The shipped ArcheAge compact.sqlite3 already contains these. This is a defensive
        // check for stripped/custom DBs and is a no-op if the columns are already present.
        Utils.DB.HoldablesSchemaCheck.EnsureColumns();

        // --- ID managers ---
        // All ID managers implement ILoadable and are handled by the orchestrator in Stage 2.
        // SkillTlIdManager.Instance.Initialize(); // static class, not migrated
        FormulaManager.Instance.Load();
        ItemManager.Instance.Load();
        ItemManager.Instance.LoadUserItems();
        BotArchetypeManager.Instance.EnsureSchema();

        // --- Stage 2: Orchestrated parallel Load() ---
        // Managers implementing ILoadable are sorted by constructor dep graph and run in parallel batches.
        await _orchestrator.RunLoadAsync();

        // --- Stage 3: Post-load special steps ---
        GameDataManager.Instance.PostLoadGameData();
        CashShopManager.Instance.EnabledShop();

        // --- Scripts ---
        if (AppConfiguration.Instance.Scripts.LoadStrategy == ScriptsConfig.LoadStrategyType.Compilation)
        {
            ScriptCompiler.Compile();
        }
        else
        {
            // (Preferred for debugging)
            // Use reflection to load scripts
            ScriptReflector.Reflect();
        }

        TimeManager.Instance.Start();
        TaskManager.Instance.Start();

        // --- Stage 4: Orchestrated parallel Initialize() ---
        await _orchestrator.RunInitializeAsync();

        // --- Stage 5: World creation + network ---
        // Start main_world and other static instances
        WorldManager.Instance.CreateStaticInstances();
        WorldManager.Instance.Initialize();

        CharacterManager.Instance.CheckForDeletedCharacters();
        CharacterManager.Instance.StartOnlineTracking();

        GameNetwork.Instance.Start();
        StreamNetwork.Instance.Start();
        LoginNetwork.Instance.Start();

        stopWatch.Stop();
        CompleteStartup(stopWatch.Elapsed);
    }

    internal void CompleteStartup(TimeSpan elapsed)
    {
        Logger.Info($"Server started! Took {elapsed}");
        StartActivityDirector();
    }

    internal void StartActivityDirector()
    {
        _beforeActivityDirectorStartLock?.Invoke();
        lock (_activityDirectorLifecycleLock)
        {
            if (_activityDirectorState != ActivityDirectorLifecycleState.Ready)
                return;

            _activityDirectorState = ActivityDirectorLifecycleState.Starting;
            var director = _activityDirectorFactory();
            _activityDirector = director;
            if (!_tryStartActivityDirector(director))
            {
                _activityDirector = null;
                _activityDirectorState = ActivityDirectorLifecycleState.Rejected;
                return;
            }

            var scheduler = _taskManager ?? TaskManager.Instance;
            if (scheduler.Schedule(director, director.InitialDelay, director.ReconciliationInterval, -1))
            {
                _activityDirectorScheduled = true;
                _activityDirectorState = ActivityDirectorLifecycleState.Running;
                return;
            }

            _stopActivityDirector(director);
            _activityDirector = null;
            _activityDirectorState = ActivityDirectorLifecycleState.Rejected;
            Logger.Error("BOT director ev=start result=schedule_rejected");
        }
    }

    internal void StopBotSubsystem()
    {
        _beforeBotSubsystemStopLock?.Invoke();
        lock (_activityDirectorLifecycleLock)
        {
            StopActivityDirectorLocked();
            if (_botManagerStopped)
                return;

            _botManagerStopped = true;
            _botManager.Stop();
        }
    }

    internal void StopActivityDirector()
    {
        lock (_activityDirectorLifecycleLock)
            StopActivityDirectorLocked();
    }

    private void StopActivityDirectorLocked()
    {
        if (_activityDirectorState is ActivityDirectorLifecycleState.Stopping or ActivityDirectorLifecycleState.Stopped)
            return;

        _activityDirectorState = ActivityDirectorLifecycleState.Stopping;
        var director = _activityDirector;
        if (director != null && _activityDirectorScheduled)
            (_taskManager ?? TaskManager.Instance).Cancel(director);
        if (director != null)
            _stopActivityDirector(director);

        _activityDirector = null;
        _activityDirectorScheduled = false;
        _activityDirectorState = ActivityDirectorLifecycleState.Stopped;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Logger.Info("Stopping daemon...");

        StopBotSubsystem();
        await SaveManager.Instance.StopAsync();

        // SpawnManager.Instance.Stop(); Moved to World Instance
        TaskManager.Instance.Stop();
        GameNetwork.Instance.Stop();
        StreamNetwork.Instance.Stop();
        LoginNetwork.Instance.Stop();

        /*
        HousingManager.Instance.Save();
        MailManager.Instance.Save();
        ItemManager.Instance.Save();
        */
        AIManager.Instance.Stop();
        WorldManager.Instance.Stop();

        TickManager.Instance.Stop();
        TimeManager.Instance.Stop();

        ClientFileManager.ClearSources();
    }

    public void Dispose()
    {
        Logger.Info("Disposing...");

        LogManager.Flush();
    }
}
