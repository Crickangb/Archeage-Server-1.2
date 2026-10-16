using System.Text.Json;
using AAEmu.Game.Bots.Host;
using AAEmu.Game.Bots.Remote;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Bots;

namespace AAEmu.Game.Core.Managers.Bots;

public sealed record BotJobSetting(string Job, bool CombatWasActive = false, string? ForcedState = null);

/// <summary>Persistent bot jobs. A farmer retains its mover but never runs the companion brain.</summary>
public static class BotJobRegistry
{
    private static readonly object Sync = new();
    private static readonly string JobsPath = Path.Combine(AppContext.BaseDirectory, "Configurations", "PlayerBotsJobs.json");
    private static Dictionary<uint, BotJobSetting> Jobs = Load();
    public static BotJobSetting Get(uint id)
    {
        lock (Sync) return Jobs.TryGetValue(id, out var setting) ? setting : new("companion");
    }
    public static bool IsFarmer(uint id) => Get(id).Job == "farmer";
    public static void Set(uint id, BotJobSetting setting)
    {
        if (id == 0 || setting.Job is not ("farmer" or "companion")) throw new ArgumentException("Invalid bot job.");
        lock (Sync)
        {
            var next = new Dictionary<uint, BotJobSetting>(Jobs) { [id] = setting };
            Directory.CreateDirectory(Path.GetDirectoryName(JobsPath)!);
            File.WriteAllText(JobsPath + ".tmp", JsonSerializer.Serialize(next, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(JobsPath + ".tmp", JobsPath, true);
            Jobs = next;
        }
    }
    private static Dictionary<uint, BotJobSetting> Load()
    {
        if (!File.Exists(JobsPath)) return new();
        var loaded = JsonSerializer.Deserialize<Dictionary<uint, BotJobSetting>>(File.ReadAllText(JobsPath))
            ?? throw new InvalidDataException("PlayerBotsJobs.json is empty or invalid.");
        if (loaded.Any(x => x.Key == 0 || x.Value == null || x.Value.Job is not ("farmer" or "companion")))
            throw new InvalidDataException("PlayerBotsJobs.json contains an invalid bot job. Restore this file before starting bots.");
        return loaded;
    }
    public static void SuppressCombat(BotRuntime runtime)
    {
        var bot = runtime.Bot;
        var state = runtime.CombatState;
        state.IsActive = false;
        state.Target = null;
        state.LostTarget = null;
        state.LastKnownTargetPosition = null;
        state.IsSearching = false;
        state.InDuel = false;
        state.DuelOpponent = null;
        state.DuelRequestPending = false;
        state.SetForcedState(runtime.MovementState.FollowTarget == null ? BotCombatStateType.Idle : BotCombatStateType.Following);
        state.TransitionTo(runtime.MovementState.FollowTarget == null ? BotCombatStateType.Idle : BotCombatStateType.Following);
        bot.CurrentTarget = null;
    }
    public static void StepFarmer(BotRuntime runtime)
    {
        SuppressCombat(runtime);
        var bot = runtime.Bot;
        var threatened = bot.IsInBattle || bot.IsInAggroListOf.Values.Any(x => x is AAEmu.Game.Models.Game.Units.Unit unit && !unit.IsDead);
        if (!threatened || bot.IsDead) return;
        bot.InterruptSkills();
        if (!PlayerBotsRemoteService.TryGetCompanionOwner(bot.Id, out var ownerId)) { BotManager.Instance.StopImmediately(bot); return; }
        var owner = WorldManager.Instance.GetCharacterById(ownerId);
        if (owner?.IsOnline != true || owner.IsDead || owner.Transform == null || bot.Transform == null ||
            owner.ParentWorld == null || !ReferenceEquals(owner.ParentWorld, bot.ParentWorld) ||
            owner.Transform.InstanceId != bot.Transform.InstanceId || owner.Buffs.CheckBuffTag((uint)AAEmu.Game.Models.Game.Skills.BuffConstants.TagPrisoner))
        { BotManager.Instance.StopImmediately(bot); return; }
        if (!ReferenceEquals(runtime.MovementState.FollowTarget, owner)) BotManager.Instance.SetFollowTarget(bot, owner, 2f);
    }
}
