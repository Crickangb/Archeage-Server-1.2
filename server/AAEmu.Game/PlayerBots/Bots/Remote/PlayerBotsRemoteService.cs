using AAEmu.Game.Bots.Gathering;
using AAEmu.Game.Bots.Crafting;
using AAEmu.Game.Bots.Questing;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Bots.Host;
using AAEmu.Game.Bots.Diagnostics;
using AAEmu.Game.Bots.Kernel;
using AAEmu.Game.Bots.Content.Rotations;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Core.Managers.World;
using Microsoft.Extensions.Hosting;
using NLog;

namespace AAEmu.Game.Bots.Remote;



public sealed partial class PlayerBotsRemoteService(IBotManager botManager) : IHostedService, IDisposable

{

    private readonly System.Collections.Concurrent.ConcurrentDictionary<uint, RespecPending> _respecPending = new();

    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private readonly IBotManager _botManager = botManager;

    private static PlayerBotsRemoteService? _instance;

    private readonly CancellationTokenSource _cts = new();

    private HttpListener? _listener;

    private Task? _loop;

    private Task? _companionLoop;

    private readonly Dictionary<uint, DateTime> _ownerOfflineSince = new();

    private readonly Dictionary<uint, DateTime> _ownerAfkSince = new();

    private readonly HashSet<uint> _afkSuppressed = new();

    private static readonly TimeSpan CompanionDisconnectGrace = TimeSpan.FromMinutes(5);

    private readonly TimeSpan _companionAfkDefense = TimeSpan.FromMinutes(IntEnv("AAEMU_PLAYERBOTS_COMPANION_AFK_DEFENSE_MINUTES", 15, 1, 1440));

    private readonly bool _enabled = BoolEnv("AAEMU_PLAYERBOTS_REMOTE_ENABLED", false);

    private readonly string _prefix = Environment.GetEnvironmentVariable("AAEMU_PLAYERBOTS_REMOTE_PREFIX") ?? "http://127.0.0.1:17840/";

    private readonly string _apiKey = Environment.GetEnvironmentVariable("AAEMU_PLAYERBOTS_REMOTE_API_KEY") ?? string.Empty;

    private readonly object _ownersLock = new();

    private Dictionary<uint, uint> _owners = new();

    private readonly System.Collections.Concurrent.ConcurrentDictionary<uint,GatherPending> _gatherPending=new();
    private sealed record GatherPending(object Bot,uint OwnerId,BotInteractionPlan Plan,string Token,DateTime Expires);
    private sealed record TransferRequest(uint ownerId,ulong itemId,int quantity,bool confirm,string token);
    private sealed record GatherRequest(uint ownerId,bool confirm,string token);
    private sealed record CraftRequest(uint ownerId,uint recipeId,uint stationId,bool confirm,string token,int quantity=1);
    private sealed record RecallRequest(uint ownerId);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<uint, JobPending> _jobPending = new();
    private sealed record JobPending(object Bot, uint OwnerId, string From, string To, string Token, DateTime Expires);
    private sealed record JobRequest(string job, uint ownerId, bool confirm, string token);

    private readonly object _rolesLock = new();

    private Dictionary<uint, string> _roles = new();

    private readonly string _ownersPath = Path.Combine(AppContext.BaseDirectory, "Configurations", "PlayerBotsOwners.json");

    private readonly string _rolesPath = Path.Combine(AppContext.BaseDirectory, "Configurations", "PlayerBotsRoles.json");

    private readonly HashSet<string> _allowed = (Environment.GetEnvironmentVariable("AAEMU_PLAYERBOTS_REMOTE_ALLOWED_IPS") ?? "127.0.0.1")

        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);



    public Task StartAsync(CancellationToken cancellationToken)

    {

        if (!_enabled) { Logger.Info("PlayerBots remote controller is disabled"); return Task.CompletedTask; }

        if (_apiKey.Length < 32) throw new InvalidOperationException("AAEMU_PLAYERBOTS_REMOTE_API_KEY must contain at least 32 characters.");

        LoadOwners();

        LoadRoles();

        _instance = this;

        _listener = new HttpListener(); _listener.Prefixes.Add(_prefix); _listener.Start();

        _loop = Task.Run(ListenLoopAsync, CancellationToken.None);

        _companionLoop = Task.Run(CompanionLoopAsync, CancellationToken.None);

        Logger.Info("PlayerBots remote controller listening on {0}", _prefix);

        return Task.CompletedTask;

    }



    public async Task StopAsync(CancellationToken cancellationToken)

    {

        _cts.Cancel();

        try { _listener?.Stop(); } catch { }

        if (_loop != null) try { await _loop.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken); } catch { }

        if (_companionLoop != null) try { await _companionLoop.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken); } catch { }

    }



    private async Task ListenLoopAsync()

    {

        while (!_cts.IsCancellationRequested && _listener?.IsListening == true)

        {

            HttpListenerContext ctx;

            try { ctx = await _listener.GetContextAsync(); }

            catch when (_cts.IsCancellationRequested || _listener?.IsListening != true) { break; }

            _ = Task.Run(() => HandleAsync(ctx));

        }

    }



    private readonly SemaphoreSlim _itemMutationGate=new(1,1);

    private async Task HandleAsync(HttpListenerContext c)

    {
        var itemGateHeld=false;

        try

        {

            if (!Authorized(c)) { await Reply(c, 403, new { message = "forbidden" }); return; }

            var path = c.Request.Url?.AbsolutePath.TrimEnd('/') ?? "";
            if (path.StartsWith("/api/v1/manager/admin/", StringComparison.Ordinal))
            {
                await HandleAdminAsync(c, path);
                return;
            }
            var itemAction=path.Split('/').LastOrDefault();
            if(path.StartsWith("/api/v1/bots/",StringComparison.Ordinal) &&
                (c.Request.Headers["X-PlayerBots-Account-Id"] != null ||
                 itemAction == "owner" && c.Request.HttpMethod is "POST" or "DELETE" ||
                 itemAction=="transfer" || c.Request.HttpMethod=="POST" && itemAction is "equip" or "unequip" or "owner" or "despawn" or "craft"))
            {
                // Prevent another admin session changing gear, ownership or spawn state during transfer.
                await _itemMutationGate.WaitAsync(_cts.Token);itemGateHeld=true;
            }

            uint? managerAccountId = null;
            if (c.Request.Headers["X-PlayerBots-Account-Id"] is { } accountHeader)
            {
                if (!uint.TryParse(accountHeader, out var scopedAccount) || scopedAccount == 0)
                {
                    await Reply(c, 403, new { message = "Companion access denied." });
                    return;
                }
                managerAccountId = scopedAccount;
                var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length >= 4 && segments[0] == "api" && segments[1] == "v1" && segments[2] == "bots" &&
                    (!uint.TryParse(segments[3], out var scopedBot) || BotAccountQuota.AccountForBot(scopedBot) != scopedAccount))
                {
                    await Reply(c, 403, new { message = "Companion access denied." });
                    return;
                }
                if (segments.Length >= 5 && segments[4] == "owner" && c.Request.HttpMethod != "GET")
                {
                    await Reply(c, 403, new { message = "Owner changes require administrator access." });
                    return;
                }
            }


            if (c.Request.HttpMethod == "GET" && path == "/api/v1/health") { await Reply(c, 200, new { service = "AAEmu PlayerBots Remote", version = "0.17.8.6", creationRosterRevision = "2026-10-09-roster-preflight-1", utc = DateTime.UtcNow }); return; }

            if (c.Request.HttpMethod == "GET" && path == "/api/v1/manager/creation-options")
            {
                await Reply(c, 200, ReadCompanionCreationOptions()); return;
            }

            if (c.Request.HttpMethod == "GET" && path.StartsWith("/api/v1/manager/accounts/", StringComparison.Ordinal))
            {
                var accountPart = path["/api/v1/manager/accounts/".Length..];
                if (!uint.TryParse(accountPart, out var accountId) || accountId == 0)
                {
                    await Reply(c, 400, new { message = "Invalid account." });
                    return;
                }
                try
                {
                    await Reply(c, 200, ReadManagerAccount(accountId));
                }
                catch (Exception)
                {
                    await Reply(c, 503, new { message = "Account access is temporarily unavailable." });
                }
                return;
            }

            if (c.Request.HttpMethod == "GET" && path == "/api/v1/bots")

            {

                var bots = botManager.GetAllBots().Where(b => managerAccountId == null || BotAccountQuota.AccountForBot(b.Id) == managerAccountId)
                    .Select(b => new { id=b.Id, objId=b.ObjId, name=b.Name, level=b.Level, zoneId=b.Transform.ZoneId, hp=b.Hp, maxHp=b.MaxHp, mp=b.Mp, maxMp=b.MaxMp, x=b.Transform.World.Position.X, y=b.Transform.World.Position.Y, z=b.Transform.World.Position.Z }).ToArray();

                await Reply(c, 200, new { count=bots.Length, bots }); return;

            }

            if (c.Request.HttpMethod == "GET" && path == "/api/v1/rotations")

            {

                var manager = BotRotationManager.Instance;

                if (manager.Rotations.Count == 0) manager.Load();

                await Reply(c,200,new{count=manager.Rotations.Count,rotations=manager.Rotations.Keys.Order(StringComparer.OrdinalIgnoreCase).ToArray()}); return;

            }

            if (c.Request.HttpMethod == "GET" && path == "/api/v1/strategies")

            {

                await Reply(c,200,new{count=BotContentRegistry.StrategyNames.Count,strategies=BotContentRegistry.StrategyNames.Order(StringComparer.OrdinalIgnoreCase).ToArray()}); return;

            }

            var s = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

            if (s.Length < 5 || s[0] != "api" || s[1] != "v1" || s[2] != "bots" || !uint.TryParse(s[3], out var id)) { await Reply(c,404,new{message="not found"}); return; }

            var action=s[4];

            if(c.Request.HttpMethod=="GET" && action=="quota")
            {
                var account=BotAccountQuota.AccountForBot(id);
                int used;lock(BotAccountQuota.Sync)used=BotAccountQuota.Used(botManager,account);
                await Reply(c,200,new{ok=true,limit=BotAccountQuota.Limit,used,accountId=account,assigned=account!=0,cooldownRemainingSeconds=botManager.GetDismissCooldownSeconds(id)});return;
            }
            if (c.Request.HttpMethod == "POST" && action == "spawn")
            {
                if(BotAccountQuota.AccountForBot(id)==0){await Reply(c,409,new{message="Assign this bot to a valid human owner before spawning through the dashboard."});return;}
                var spawned=botManager.SpawnBot(id,out var b);
                if(spawned==SpawnResult.Ok)RememberAdminBot(id);
                await Reply(c,spawned==SpawnResult.Ok?200:409,spawned==SpawnResult.Ok?new{ok=true,id=b.Id,name=b.Name}:new{message=spawned==SpawnResult.AccountLimitReached?"Account limit reached: use at most 3 active bots, shared between Combat and Farmer jobs.":spawned==SpawnResult.DismissCooldown?$"Dismiss cooldown: wait {botManager.GetDismissCooldownSeconds(id)} seconds before spawning this companion.":$"Spawn failed: {spawned}"});return;
            }

            if (c.Request.HttpMethod == "POST" && action == "despawn") { if(botManager.GetBot(id)!=null)RememberAdminBot(id);var ok=botManager.DismissBot(id); await Reply(c,ok?200:404,new{ok,message=ok?"despawned":"bot not active"}); return; }

            if (action == "role")

            {

                if (c.Request.HttpMethod == "GET")

                {

                    string role;

                    lock (_rolesLock) role = _roles.TryGetValue(id, out var savedRole) ? savedRole : CompanionRoleRegistry.Get(id);

                    await Reply(c, 200, new { ok=true, botId=id, role, available=new[]{"melee-dps","ranged-dps","tank","healer","support"} }); return;

                }

                if (c.Request.HttpMethod == "POST")

                {

                    var r = await Body<RoleRequest>(c);

                    var role = (r?.role ?? "").Trim().ToLowerInvariant();

                    var available = new[]{"melee-dps","ranged-dps","tank","healer","support"};

                    if (!available.Contains(role, StringComparer.OrdinalIgnoreCase))

                    { await Reply(c, 400, new { message="invalid role", available }); return; }

                    lock (_rolesLock) { _roles[id]=role; CompanionRoleRegistry.Set(id, role); SaveRoles(); }

                    await Reply(c, 200, new { ok=true, botId=id, role }); return;

                }

            }

            if(action=="owner"&&c.Request.HttpMethod is "POST" or "DELETE")
            {
                var ownerRuntime=BotHost.Instance.GetRuntime(id);
                if(ownerRuntime!=null)lock(ownerRuntime.SyncRoot)BotCraftController.Stop(ownerRuntime,"Owner assignment changed; crafting cancelled.");
            }
            if (action == "owner")

            {

                if (c.Request.HttpMethod == "GET")

                {

                    uint ownerId; lock(_ownersLock) _owners.TryGetValue(id,out ownerId);

                    var owner=ownerId==0?null:WorldManager.Instance.GetCharacterById(ownerId);

                    await Reply(c,200,new{ok=true,botId=id,assigned=ownerId!=0,ownerId=(uint?) (ownerId==0?null:ownerId),ownerName=owner?.Name ?? ResolveCharacterName(ownerId),ownerOnline=owner!=null});return;

                }

                if (c.Request.HttpMethod == "POST")

                {

                    var r=await Body<OwnerRequest>(c); if(r==null||r.ownerId==0){await Reply(c,400,new{message="ownerId is required"});return;}

                    var ownerAccount=NameManager.Instance.GetCharacterAccount(r.ownerId);
                    if(ownerAccount==0){await Reply(c,400,new{message="Owner character does not belong to a known account."});return;}
                    var limitReached=false;
                    lock(BotAccountQuota.Sync)
                    {
                        limitReached=botManager.GetBot(id)!=null && BotAccountQuota.Used(botManager,ownerAccount,id)>=BotAccountQuota.Limit;
                        if(!limitReached)lock(_ownersLock){_owners[id]=r.ownerId;SaveOwners();}
                    }
                    if(limitReached){await Reply(c,409,new{message="The owner account already has 3 active bots. Dismiss one before assigning another."});return;}

                    var owner=WorldManager.Instance.GetCharacterById(r.ownerId);

                    await Reply(c,200,new{ok=true,botId=id,assigned=true,ownerId=r.ownerId,ownerName=owner?.Name ?? ResolveCharacterName(r.ownerId),ownerOnline=owner!=null});return;

                }

                if (c.Request.HttpMethod == "DELETE")

                {

                    bool removed; lock(_ownersLock) { removed=_owners.Remove(id); if(removed) SaveOwners(); }

                    await Reply(c,200,new{ok=true,botId=id,assigned=false,removed});return;

                }

            }

            if (action == "job" && c.Request.HttpMethod == "GET" && string.IsNullOrEmpty(c.Request.QueryString["job"]))
            {
                var saved = BotJobRegistry.Get(id);
                await Reply(c,200,new { ok=true,botId=id,job=saved.Job,combatAllowed=saved.Job=="companion",gatheringImplemented=true,craftingImplemented=true,offlineMiningEnabled=BotConfig.Instance.OfflineMiningEnabled }); return;
            }
            var bot=botManager.GetBot(id); if(bot==null){await Reply(c,404,new{message="bot not active"});return;}
            if (action == "skill-priority")
            {
                var skills = bot.Skills.Skills.Values.Where(x => AAEmu.Game.Bots.Body.LearnedSkillDecision.IsOffensive(x.Template)).ToArray();
                if (c.Request.HttpMethod == "GET")
                {
                    await Reply(c, 200, new { ok=true, botId=id, skillIds=AAEmu.Game.Bots.Body.BotSkillPriorities.Current.Get(id),
                        skills=skills.Select(x => new { id=x.Id, name=LocalizationManager.Instance.Get("skills", "name", x.Id, $"Skill {x.Id}") }) }); return;
                }
                if (c.Request.HttpMethod == "POST")
                {
                    var request = await Body<SkillPriorityRequest>(c);
                    var ids = request?.skillIds;
                    if (ids == null || ids.Length > 96 || ids.Distinct().Count() != ids.Length || ids.Any(skillId => !skills.Any(x => x.Id == skillId)))
                    { await Reply(c, 400, new { message="Choose distinct learned combat skills. Refresh after learning or resetting skills." }); return; }
                    AAEmu.Game.Bots.Body.BotSkillPriorities.Current.Set(id, ids);
                    await Reply(c, 200, new { ok=true, botId=id, skillIds=ids }); return;
                }
            }
            if(c.Request.HttpMethod=="POST" && action=="recall-owner")
            {
                var request=await Body<RecallRequest>(c);
                var runtime=BotHost.Instance.GetRuntime(id);
                if(request==null || runtime==null){await Reply(c,409,new{message="A spawned companion and assigned owner are required."});return;}
                object result;int status=200;
                lock(runtime.SyncRoot)
                {
                    uint actualOwner;lock(_ownersLock)_owners.TryGetValue(id,out actualOwner);
                    var owner=WorldManager.Instance.GetCharacterById(request.ownerId);
                    if(runtime.Retired || !ReferenceEquals(runtime.Bot,bot) || !ReferenceEquals(botManager.GetBot(id),bot) || bot.IsDead)
                    {status=409;result=new{message="A living spawned companion is required."};}
                    else if(actualOwner==0 || actualOwner!=request.ownerId || owner?.IsOnline!=true || owner.IsDead)
                    {status=409;result=new{message="The assigned human owner must be online and alive."};}
                    else if(owner.Buffs.CheckBuffTag((uint)BuffConstants.TagPrisoner) || runtime.CombatState.InDuel)
                    {status=409;result=new{message="Recall is unavailable during a duel or while the owner is imprisoned."};}
                    else if(bot.Transform==null || owner.Transform==null || bot.ParentWorld==null || !ReferenceEquals(bot.ParentWorld,owner.ParentWorld) || bot.Transform.InstanceId!=owner.Transform.InstanceId)
                    {status=409;result=new{message="Recall requires the companion and owner in the same world instance. Dismiss and spawn to recover across instances."};}
                    else if(BotDrivers.For(bot).Owns(bot))
                    {status=409;result=new{message="Detach the client-controlled actor before server recall."};}
                    else
                    {
                        var position=owner.Transform.World.Position;
                        if(!float.IsFinite(position.X)||!float.IsFinite(position.Y)||!float.IsFinite(position.Z))
                        {status=409;result=new{message="The owner position is unavailable."};}
                        else
                        {
                            BotCraftController.Stop(runtime,"Recall cancelled crafting.");
                            BotGatherController.Stop(runtime,"Recall to Owner cancelled gathering.");
                            _gatherPending.TryRemove(id,out _);
                            bot.InterruptSkills();botManager.StopImmediately(bot);
                            botManager.MoveBotTo(bot,position.X,position.Y,position.Z);
                            var recallDistance=bot.GetDistanceTo(owner,true);
                            if(!float.IsFinite(recallDistance) || recallDistance>3f)
                            {status=409;result=new{message="Native recall did not move the companion. Refresh before retrying."};}
                            else
                            {
                                var combat=runtime.CombatState;
                                combat.Target=null;combat.LostTarget=null;combat.LastKnownTargetPosition=null;combat.IsSearching=false;bot.CurrentTarget=null;
                                botManager.SetFollowTarget(bot,owner,2f);
                                combat.SetForcedState(BotCombatStateType.Following);combat.TransitionTo(BotCombatStateType.Following);
                                if(BotJobRegistry.IsFarmer(id))BotJobRegistry.SuppressCombat(runtime);
                                result=new{ok=true,botId=id,name=bot.Name,message="Companion recalled to the assigned owner and following restored."};
                            }
                        }
                    }
                }
                await Reply(c,status,result);return;
            }
            if(c.Request.HttpMethod=="POST" && action is "stop" or "move" or "follow" or "unfollow" or "state")
            {
                var gatheringRuntime=BotHost.Instance.GetRuntime(id);
                if(gatheringRuntime!=null)lock(gatheringRuntime.SyncRoot){BotCraftController.Stop(gatheringRuntime,"A movement command cancelled crafting.");BotGatherController.Stop(gatheringRuntime,"A movement command cancelled gathering.");}
            }
            if(action is "craft" or "craft-stop")
            {
                var runtime=BotHost.Instance.GetRuntime(id);
                if(runtime==null){await Reply(c,409,new{message="Bot runtime is not active."});return;}
                var request=c.Request.HttpMethod=="POST"?await Body<CraftRequest>(c):null;
                object result;int status=200;
                lock(runtime.SyncRoot)
                {
                    uint.TryParse(c.Request.QueryString["ownerId"],out var ownerId);
                    uint.TryParse(c.Request.QueryString["recipeId"],out var recipeId);
                    uint.TryParse(c.Request.QueryString["stationId"],out var stationId);
                    var quantity=string.IsNullOrEmpty(c.Request.QueryString["quantity"])?1:int.TryParse(c.Request.QueryString["quantity"],out var parsedQuantity)?parsedQuantity:0;
                    if(runtime.Retired||!ReferenceEquals(runtime.Bot,bot)||!ReferenceEquals(botManager.GetBot(id),bot))
                    {status=409;result=new{message="Bot is no longer active."};}
                    else if(action=="craft"&&c.Request.HttpMethod=="GET"&&recipeId==0)result=BotCraftController.View(runtime,uint.TryParse(c.Request.QueryString["selectedRecipeId"],out var selectedRecipeId)?selectedRecipeId:BotCraftController.IronIngotRecipe);
                    else if(action=="craft"&&c.Request.HttpMethod=="GET")
                    {
                        var preview=BotCraftController.Preview(runtime,ownerId,recipeId,stationId,quantity,out var error);
                        if(error!=null){status=409;result=new{message=error};}else result=preview;
                    }
                    else if(c.Request.HttpMethod=="POST"&&request!=null)
                    {
                        var error=action=="craft-stop"?BotCraftController.Cancel(runtime,request.ownerId):
                            request.confirm?BotCraftController.Confirm(runtime,request.ownerId,request.recipeId,request.stationId,request.quantity,request.token):"Preview and confirm crafting first.";
                        if(error!=null){status=409;result=new{message=error};}else result=new{ok=true,message=action=="craft-stop"?"Crafting cancelled.":"Native crafting started. Check Crafting for the result."};
                    }
                    else{status=405;result=new{message="Use GET to preview and POST to confirm crafting."};}
                }
                await Reply(c,status,result);return;
            }
            if(action is "gather" or "gather-stop")
            {
                var runtime=BotHost.Instance.GetRuntime(id);
                if(runtime==null){await Reply(c,409,new{message="Bot runtime is not active."});return;}
                var request=c.Request.HttpMethod=="POST"?await Body<GatherRequest>(c):null;
                object result;int status=200;
                lock(runtime.SyncRoot)
                {
                    uint.TryParse(c.Request.QueryString["ownerId"],out var queryOwner);
                    var ownerId=request?.ownerId??queryOwner;
                    if(runtime.Retired || !ReferenceEquals(runtime.Bot,bot) || !ReferenceEquals(botManager.GetBot(id),bot))
                    {status=409;result=new{message="Bot is no longer active."};}
                    else if(c.Request.HttpMethod=="GET" && string.IsNullOrEmpty(c.Request.QueryString["resourceId"])){
                        int.TryParse(c.Request.QueryString["radius"],out var radius);uint.TryParse(c.Request.QueryString["zoneId"],out var zoneId);
                        result=BotGatherController.View(runtime,c.Request.QueryString["scan"]=="1",radius,c.Request.QueryString["kind"]??"all",zoneId,c.Request.QueryString["area"]=="zone");
                    }
                    else if(action=="gather-stop" && c.Request.HttpMethod=="POST")
                    {
                        if(!TryGetCompanionOwner(id,out var actualOwner)||actualOwner!=ownerId){status=409;result=new{message="The assigned owner is required."};}
                        else {BotGatherController.Stop(runtime);_gatherPending.TryRemove(id,out _);result=new{ok=true,message="Gathering cancelled."};}
                    }
                    else if(BotGatherController.Check(runtime,ownerId) is string error){status=409;result=new{message=error};}
                    else if(c.Request.HttpMethod=="GET")
                    {
                        uint.TryParse(c.Request.QueryString["resourceId"],out var resourceId);uint.TryParse(c.Request.QueryString["skillId"],out var skillId);
                        var plan=BotGatherController.Resolve(runtime,resourceId,skillId);
                        if(!plan.Available){status=409;result=new{message=plan.Reason};}
                        else
                        {
                            var pending=new GatherPending(bot,ownerId,plan,Convert.ToHexString(RandomNumberGenerator.GetBytes(24)),DateTime.UtcNow.AddMinutes(2));
                            _gatherPending[id]=pending;
                            var skill=SkillManager.Instance.GetSkillTemplate(plan.SkillId);
                            result=new{ok=true,name=bot.Name,resourceId,skillId,ownerId,resourceKind=BotGatherController.ResourceKind(skill),skillName=LocalizationManager.Instance.Get("skills","name",skillId,$"Gather {skillId}"),laborCost=skill.ConsumeLaborPower,availableLabor=bot.LaborPower,manaCost=skill.ManaCost,availableMana=bot.Mp,durationMs=plan.DurationMs,token=pending.Token,expiresUtc=pending.Expires,
                                message="Walk to this resource and perform one native gathering action. Hostiles near the route cancel the request. Following will stop. No automatic retry."};
                        }
                    }
                    else if(c.Request.HttpMethod=="POST" && request?.confirm==true && _gatherPending.TryRemove(id,out var pending) && ReferenceEquals(pending.Bot,bot) && pending.OwnerId==ownerId && pending.Expires>DateTime.UtcNow && pending.Token==request.token)
                    {
                        var fresh=BotGatherController.Resolve(runtime,pending.Plan.DoodadObjectId,pending.Plan.SkillId);
                        var confirmError=!fresh.Available||fresh.Phase!=pending.Plan.Phase||System.Numerics.Vector3.Distance(fresh.Position,pending.Plan.Position)>.5f?"Resource changed; preview again.":BotGatherController.Start(runtime,pending.Plan,ownerId);
                        if(confirmError!=null){status=409;result=new{message=confirmError};}else result=new{ok=true,message="Gathering attempt started. Check Farmer status for the outcome."};
                    }
                    else {status=409;result=new{message="A matching gathering preview is required."};}
                }
                await Reply(c,status,result);return;
            }
            if (action == "transfer")
            {
                var runtime=BotHost.Instance.GetRuntime(id);
                if(runtime==null || !ReferenceEquals(runtime.Bot,bot)){await Reply(c,409,new{message="Bot runtime is not active."});return;}
                var request=c.Request.HttpMethod=="POST"?await Body<TransferRequest>(c):null;
                uint.TryParse(c.Request.QueryString["ownerId"],out var queryOwner);
                ulong.TryParse(c.Request.QueryString["itemId"],out var queryItem);
                int.TryParse(c.Request.QueryString["quantity"],out var queryQuantity);
                if(c.Request.HttpMethod is not ("GET" or "POST")){await Reply(c,405,new{message="Use GET to preview and POST to confirm."});return;}
                if(c.Request.HttpMethod=="POST" && request?.confirm!=true){await Reply(c,409,new{message="A confirmed transfer preview is required."});return;}
                var result=BotItemTransfer.Handle(runtime,request?.ownerId??queryOwner,request?.itemId??queryItem,
                    request?.quantity??queryQuantity,c.Request.HttpMethod=="POST",request?.token);
                await Reply(c,result.Status,result.Data);return;
            }
            if (action == "job")
            {
                var runtime = BotHost.Instance.GetRuntime(id);
                if (!bot.IsBot || runtime == null) { await Reply(c,409,new{message="A spawned PlayerBot is required."}); return; }
                var request = c.Request.HttpMethod == "POST" ? await Body<JobRequest>(c) : null;
                var desired = (request?.job ?? c.Request.QueryString["job"] ?? "").Trim().ToLowerInvariant();
                uint.TryParse(c.Request.QueryString["ownerId"],out var queryOwner);
                var ownerId = request?.ownerId ?? queryOwner;
                object result; int status=200;
                lock (runtime.SyncRoot)
                {
                    uint actualOwner;
                    lock (_ownersLock) _owners.TryGetValue(id,out actualOwner);
                    var owner = WorldManager.Instance.GetCharacterById(ownerId);
                    if (runtime.Retired || !ReferenceEquals(runtime.Bot,bot) || !ReferenceEquals(botManager.GetBot(id),bot) || bot.IsDead)
                    { status=409;result=new{message="The bot must be spawned and alive. No job changed."}; }
                    else if (ownerId==0 || actualOwner!=ownerId || owner?.IsOnline!=true)
                    { status=409;result=new{message="The assigned human owner must be online. No job changed."}; }
                    else if (desired is not ("farmer" or "companion"))
                    { status=400;result=new{message="Choose farmer or companion."}; }
                    else if (runtime.CombatState.InDuel)
                    { status=409;result=new{message="End the duel before changing jobs."}; }
                    else
                    {
                        var current = BotJobRegistry.Get(id);
                        if (c.Request.HttpMethod=="GET")
                        {
                            var preview=new JobPending(bot,ownerId,current.Job,desired,Convert.ToHexString(RandomNumberGenerator.GetBytes(24)),DateTime.UtcNow.AddMinutes(2));
                            _jobPending[id]=preview;
                            result=new{ok=true,previewOnly=true,botId=id,name=bot.Name,fromJob=current.Job,job=desired,ownerId,token=preview.Token,expiresUtc=preview.Expires,
                                message=desired=="farmer"?"Combat automation will stop. Ore mining is available. Basic material processing is available at matching public workbenches when permitted by the administrator; the owner must be online.":"The previous combat setting will be restored."};
                        }
                        else if(c.Request.HttpMethod=="POST" && request?.confirm==true && _jobPending.TryRemove(id,out var pending) &&
                            pending.Expires>DateTime.UtcNow && ReferenceEquals(pending.Bot,bot) && pending.OwnerId==ownerId &&
                            pending.From==current.Job && pending.To==desired && pending.Token==request.token)
                        {
                            if(desired!=current.Job)
                            {
                                var setting=desired=="farmer"?new BotJobSetting(desired,runtime.CombatState.IsActive,runtime.CombatState.ForcedState?.ToString()):new BotJobSetting(desired);
                                BotJobRegistry.Set(id,setting);
                                BotCraftController.Stop(runtime,"Bot job changed; crafting cancelled.");
                                BotGatherController.Stop(runtime,"Bot job changed.");
                                bot.InterruptSkills();bot.CurrentTarget=null;
                                runtime.Blackboard.InvalidateAll();
                                if(desired=="farmer") BotJobRegistry.SuppressCombat(runtime);
                                else
                                {
                                    runtime.CombatState.SetForcedState(null);
                                    runtime.CombatState.IsActive=current.CombatWasActive;
                                    if(Enum.TryParse<BotCombatStateType>(current.ForcedState,out var previous))runtime.CombatState.SetForcedState(previous);
                                    BotCombatManager.Instance.StartListening(bot);
                                }
                            }
                            result=new{ok=true,botId=id,name=bot.Name,job=desired,combatAllowed=desired=="companion",message="Job saved. Ore mining is available. Basic material processing is available at matching public workbenches when permitted by the administrator; the owner must be online."};
                        }
                        else { status=409;result=new{message="A matching unexpired job preview is required. No job changed."}; }
                    }
                }
                await Reply(c,status,result);return;
            }


            if(c.Request.HttpMethod=="GET"&&action=="skillcatalog")
            {
                var runtime = BotHost.Instance.GetRuntime(bot.Id);
                if (!bot.IsBot || runtime == null) { await Reply(c,409,new{message="Bot has no active runtime."}); return; }
                object catalog;
                lock (runtime.SyncRoot)
                {
                    if (runtime.Retired || !ReferenceEquals(runtime.Bot, bot) || !ReferenceEquals(botManager.GetBot(id), bot) || bot.Skills == null)
                    { catalog = new { ok=false, message="Bot is no longer active. Refresh after spawning." }; }
                    else
                    {
                        var abilities = new[]{bot.Ability1,bot.Ability2,bot.Ability3}.Where(a=>a>AbilityType.General&&a<AbilityType.None).Distinct().ToArray();
                        var used = bot.Skills.Skills.Values.Sum(x=>x.Template?.SkillPoints ?? 0) + bot.Skills.PassiveBuffs.Count;
                        var free = Math.Max(0,ExperienceManager.Instance.GetSkillPointsForLevel(bot.Level)-used);
                        catalog = new { ok=true, botId=bot.Id, trees=abilities.Select(a=>new {
                            ability=a.ToString(), abilityLevel=bot.GetAbLevel(a),
                            skills=SkillManager.Instance.GetPlayerSkillTemplates(a).OrderBy(x=>x.AbilityLevel).ThenBy(x=>x.Id).Select(x=>new {
                                id=x.Id, type="active", name=LocalizationManager.Instance.Get("skills","name",x.Id,$"Skill {x.Id}"),
                                requiredLevel=x.AbilityLevel, requiredPoints=0, cost=x.SkillPoints,
                                learned=bot.Skills.Skills.ContainsKey(x.Id),
                                available=bot.GetAbLevel(a)>=x.AbilityLevel && free>=x.SkillPoints && !bot.Skills.Skills.ContainsKey(x.Id)
                            }).ToArray(),
                            passives=SkillManager.Instance.GetPlayerPassiveTemplates(a).OrderBy(x=>x.ReqPoints).ThenBy(x=>x.Id).Select(x=>new {
                                id=x.Id, type="passive", name=LocalizationManager.Instance.Get("buffs","name",x.Id,$"Passive {x.Id}"),
                                requiredLevel=(int)x.Level, requiredPoints=x.ReqPoints, cost=1,
                                learned=bot.Skills.PassiveBuffs.ContainsKey(x.Id),
                                available=bot.GetAbLevel(a)>=x.Level && free>=1 && !bot.Skills.PassiveBuffs.ContainsKey(x.Id) &&
                                    bot.Skills.Skills.Values.Where(s=>s.Template?.AbilityId==a).Sum(s=>s.Template?.SkillPoints ?? 0)+bot.Skills.PassiveBuffs.Values.Count(p=>p.Template?.AbilityId==a)>=x.ReqPoints &&
                                    SkillManager.Instance.GetBuffTemplate(x.BuffId)!=null
                            }).ToArray()
                        }).ToArray() };
                    }
                }
                await Reply(c,200,catalog); return;
            }

            if (action == "learn" && s.Length == 5)
            {
                if (c.Request.HttpMethod == "GET")
                {
                    uint.TryParse(c.Request.QueryString["ownerId"], out var expectedOwnerId);
                    uint.TryParse(c.Request.QueryString["skillId"], out var skillId);
                    var result = Learn(bot, c.Request.QueryString["type"] ?? "active", skillId, expectedOwnerId, false, null);
                    await Reply(c, result.Status, result.Body); return;
                }
                if (c.Request.HttpMethod == "POST")
                {
                    var request = await Body<LearnRequest>(c);
                    if (request == null || !request.confirm || string.IsNullOrEmpty(request.token))
                    { await Reply(c, 400, new { message = "Confirmation and a fresh learning preview token are required. No skills changed." }); return; }
                    var result = Learn(bot, request.type, request.skillId, request.ownerId, true, request.token);
                    await Reply(c, result.Status, result.Body); return;
                }
                await Reply(c, 405, new { message = "Use GET to preview or POST to confirm learning." }); return;
            }

            if (action == "respec" && s.Length == 5)
            {
                if (c.Request.HttpMethod == "GET")
                {
                    uint.TryParse(c.Request.QueryString["ownerId"], out var expectedOwnerId);
                    var result = Respec(bot, c.Request.QueryString["scope"] ?? "all", expectedOwnerId, false, null);
                    await Reply(c, result.Status, result.Body); return;
                }
                if (c.Request.HttpMethod == "POST")
                {
                    var request = await Body<RespecRequest>(c);
                    if (request == null || !request.confirm || string.IsNullOrEmpty(request.token))
                    { await Reply(c, 400, new { message = "Confirmation and a fresh preview token are required. No skills changed." }); return; }
                    var result = Respec(bot, request.scope, request.ownerId, true, request.token);
                    await Reply(c, result.Status, result.Body); return;
                }
                await Reply(c, 405, new { message = "Use GET to preview or POST to confirm respec." }); return;
            }

            if(c.Request.HttpMethod=="POST"&&action=="stop"){botManager.StopImmediately(bot);await Reply(c,200,new{ok=true});return;}

            if(c.Request.HttpMethod=="GET"&&action=="activitylog")
            {
                var entries=BotCompanionActivity.Recent(bot.Id).Select(e=>new
                {
                    timestampUtc=e.TimestampUtc, eventType=e.Event, detail=e.Detail,
                    resourceId=e.ResourceId, x=e.X, y=e.Y, z=e.Z
                }).ToArray();
                await Reply(c,200,new{ok=true,botId=bot.Id,name=bot.Name,retention="Latest 200 events; cleared on server restart",count=entries.Length,entries});return;
            }

            if(c.Request.HttpMethod=="GET"&&action=="combatlog")

            {

                var take = int.TryParse(c.Request.QueryString["take"], out var requested) ? Math.Clamp(requested, 1, 50) : 25;

                var entries = BotCombatTelemetry.Recent(bot.Id, take).Select(x => new

                {

                    timestampUtc=x.TimestampUtc, skillId=x.SkillId,

                    skillName=x.SkillId==0 ? "AI WAIT" : LocalizationManager.Instance.Get("skills","name",x.SkillId,$"Skill {x.SkillId}"),

                    targetObjId=x.TargetObjId, targetName=x.TargetName, distance=Math.Round(x.Distance,1),

                    result=x.Result, detail=x.Detail

                }).ToArray();

                await Reply(c,200,new{ok=true,botId=bot.Id,name=bot.Name,count=entries.Length,entries});return;

            }

            if(c.Request.HttpMethod=="GET"&&action=="character")

            {

                var runtime=BotHost.Instance.GetRuntime(bot.Id);
                var xp=ExperienceManager.Instance;
                var capped=bot.Level>=xp.MaxPlayerLevel;
                var levelStart=xp.GetExpForLevel(bot.Level);
                var levelEnd=capped?levelStart:xp.GetExpForLevel((byte)(bot.Level+1));
                var required=Math.Max(0,levelEnd-levelStart);
                var earned=Math.Clamp(bot.Experience-levelStart,0,required);
                var xpProgress=new{current=earned,required,remaining=Math.Max(0,required-earned),percent=capped?100d:required==0?0d:100d*earned/required,maxLevel=capped,total=bot.Experience};

                await Reply(c,200,new{ok=true,experienceProgress=xpProgress,botId=bot.Id,objId=bot.ObjId,accountId=bot.AccountId,name=bot.Name,build=ReadCompanionBuild(bot.Id),level=bot.Level,experience=bot.Experience,race=bot.Race.ToString(),gender=bot.Gender.ToString(),faction=bot.FactionName,ability1=bot.Ability1.ToString(),ability2=bot.Ability2.ToString(),ability3=bot.Ability3.ToString(),hp=bot.Hp,maxHp=bot.MaxHp,mp=bot.Mp,maxMp=bot.MaxMp,labor=bot.LaborPower,money=bot.Money,money2=bot.Money2,honor=bot.HonorPoint,vocation=bot.VocationPoint,str=bot.Str,spi=bot.Spi,zoneId=bot.Transform?.ZoneId,x=bot.Transform?.World.Position.X,y=bot.Transform?.World.Position.Y,z=bot.Transform?.World.Position.Z,state=runtime?.CombatState.CurrentState.ToString(),forced=runtime?.CombatState.ForcedState?.ToString()});return;

            }

            if(c.Request.HttpMethod=="GET"&&action=="skills")

            {

                var learnedSkills = bot.Skills?.Skills?.Values.OrderBy(x => x.Id).ToArray() ?? [];

                var learnedPassives = bot.Skills?.PassiveBuffs?.Values.OrderBy(x => x.Id).ToArray() ?? [];

                var skills=learnedSkills.Select(x=>new{id=x.Id,level=x.Level,ability=x.Template?.AbilityId.ToString(),skillPoints=x.Template?.SkillPoints ?? 0,name=LocalizationManager.Instance.Get("skills","name",x.Id,$"Skill {x.Id}")}).ToArray();

                var passives=learnedPassives.Select(x=>new{id=x.Id,ability=x.Template?.AbilityId.ToString(),requiredTreePoints=x.Template?.ReqPoints ?? 0,name=LocalizationManager.Instance.Get("buffs","name",x.Id,$"Passive {x.Id}")}).ToArray();



                // AAEmu 1.2 grants a level-based pool shared by all three skill trees.

                // Active skills spend their template SkillPoints; learned passives cost one point.

                var totalSkillPoints = ExperienceManager.Instance.GetSkillPointsForLevel(bot.Level);

                var usedSkillPoints = learnedSkills.Sum(x => x.Template?.SkillPoints ?? 0) + learnedPassives.Length;

                var remainingSkillPoints = Math.Max(0, totalSkillPoints - usedSkillPoints);
                byte? nextSkillPointLevel=null;
                for(var next=(int)bot.Level+1;next<=ExperienceManager.Instance.MaxPlayerLevel;next++)
                    if(ExperienceManager.Instance.GetSkillPointsForLevel((byte)next)>totalSkillPoints){nextSkillPointLevel=(byte)next;break;}

                var activeAbilities = new[]{bot.Ability1,bot.Ability2,bot.Ability3}.Where(a=>a!=AbilityType.None).Distinct().ToArray();

                var trees = activeAbilities.Select(a => new

                {

                    ability=a.ToString(),

                    abilityLevel=bot.GetAbLevel(a),

                    usedSkillPoints=learnedSkills.Where(x=>x.Template?.AbilityId==a).Sum(x=>x.Template?.SkillPoints ?? 0) + learnedPassives.Count(x=>x.Template?.AbilityId==a),

                    learnedActive=learnedSkills.Count(x=>x.Template?.AbilityId==a),

                    learnedPassive=learnedPassives.Count(x=>x.Template?.AbilityId==a)

                }).ToArray();



                await Reply(c,200,new{ok=true,botId=bot.Id,name=bot.Name,level=bot.Level,skillsets=new[]{bot.Ability1.ToString(),bot.Ability2.ToString(),bot.Ability3.ToString()},progression=new{totalSkillPoints,usedSkillPoints,remainingSkillPoints,nextSkillPointLevel,trees},count=skills.Length,skills,passiveCount=passives.Length,passives});return;

            }

            if(c.Request.HttpMethod=="GET"&&action=="equipment")

            {

                var items=bot.Inventory?.Equipment?.Items?.Where(x=>x!=null&&x.Slot>=0).OrderBy(x=>x.Slot).Select(x=>ItemView(x, true)).ToArray() ?? [];

                await Reply(c,200,new{ok=true,botId=bot.Id,name=bot.Name,count=items.Length,items});return;

            }

            if(c.Request.HttpMethod=="GET"&&action=="inventory")

            {

                var bag=bot.Inventory?.Bag;

                var items=bag?.Items?.Where(x=>x!=null).OrderBy(x=>x.Slot).Select(x=>ItemView(x, false)).ToArray() ?? [];

                await Reply(c,200,new{ok=true,botId=bot.Id,name=bot.Name,used=items.Length,freeSlots=bag?.FreeSlotCount ?? 0,items});return;

            }

            if(c.Request.HttpMethod=="POST"&&action is "equip" or "unequip")
            {
                var equipmentRuntime=BotHost.Instance.GetRuntime(id);
                if(equipmentRuntime!=null&&BotCraftController.IsActive(equipmentRuntime)){await Reply(c,409,new{message="Stop crafting before changing equipment."});return;}
            }
            if(c.Request.HttpMethod=="POST"&&action=="equip")

            {

                var r=await Body<EquipRequest>(c);

                if(r==null||r.instanceId==0){await Reply(c,400,new{message="instanceId is required"});return;}

                var bag=bot.Inventory?.Bag; var equipment=bot.Inventory?.Equipment;

                if(bag==null||equipment==null){await Reply(c,409,new{message="inventory not available"});return;}

                var item=bag.GetItemByItemId(r.instanceId);

                if(item==null){await Reply(c,404,new{message="item is not in bot inventory"});return;}

                if(item is not EquipItem){await Reply(c,409,new{message="item is not equippable"});return;}

                var allowed=EquipmentContainer.GetAllowedGearSlots(item.Template);

                if(allowed.Count==0){await Reply(c,409,new{message="item has no valid equipment slot"});return;}

                EquipmentItemSlot target;

                if(r.slot.HasValue)

                {

                    if(r.slot.Value<0||r.slot.Value>byte.MaxValue||!Enum.IsDefined(typeof(EquipmentItemSlot),(byte)r.slot.Value)){await Reply(c,400,new{message="invalid equipment slot"});return;}

                    target=(EquipmentItemSlot)(byte)r.slot.Value;

                    if(!allowed.Contains(target)){await Reply(c,409,new{message="item cannot be equipped in requested slot",allowed=allowed.Select(x=>new{id=(int)x,name=x.ToString()}).ToArray()});return;}

                }

                else target=allowed.FirstOrDefault(x=>equipment.GetItemBySlot((int)x)==null,allowed[0]);

                if(AAEmu.Game.Bots.Equipment.BotEquipmentHands.BlocksOffhand(bot.Inventory,target))
                {await Reply(c,409,new{message="Unequip the two-handed weapon before equipping an offhand item."});return;}

                var targetItem=equipment.GetItemBySlot((int)target);

                bool ok;

                if(targetItem==null)

                    ok=bot.Inventory.SplitOrMoveItem(ItemTaskType.SwapItems,0,SlotType.Equipment,(byte)target,item.Id,SlotType.Inventory,(byte)item.Slot);

                else

                    ok=bot.Inventory.SplitOrMoveItem(ItemTaskType.SwapItems,item.Id,SlotType.Inventory,(byte)item.Slot,targetItem.Id,SlotType.Equipment,(byte)target);

                if(!ok){await Reply(c,409,new{message="equip failed"});return;}

                await Reply(c,200,new{ok=true,botId=bot.Id,name=bot.Name,slot=(int)target,slotName=target.ToString(),equipped=ItemView(item)});return;

            }

            if(c.Request.HttpMethod=="POST"&&action=="unequip")

            {

                var r=await Body<UnequipRequest>(c);

                if(r==null||r.slot<0||r.slot>byte.MaxValue||!Enum.IsDefined(typeof(EquipmentItemSlot),(byte)r.slot)){await Reply(c,400,new{message="valid equipment slot is required"});return;}

                var bag=bot.Inventory?.Bag; var equipment=bot.Inventory?.Equipment;

                if(bag==null||equipment==null){await Reply(c,409,new{message="inventory not available"});return;}

                var item=equipment.GetItemBySlot(r.slot);

                if(item==null){await Reply(c,404,new{message="equipment slot is empty"});return;}

                if(item is not EquipItem){await Reply(c,409,new{message="body/appearance parts cannot be unequipped remotely"});return;}

                var bagSlot=bag.GetUnusedSlot(-1);

                if(bagSlot<0){await Reply(c,409,new{message="inventory is full"});return;}

                var ok=bot.Inventory.SplitOrMoveItem(ItemTaskType.SwapItems,item.Id,SlotType.Equipment,(byte)item.Slot,0,SlotType.Inventory,(byte)bagSlot);

                if(!ok){await Reply(c,409,new{message="unequip failed"});return;}

                await Reply(c,200,new{ok=true,botId=bot.Id,name=bot.Name,fromSlot=r.slot,bagSlot,item=ItemView(item,false)});return;

            }

            if(c.Request.HttpMethod=="POST"&&action=="unfollow")

            {

                var runtime = BotHost.Instance.GetRuntime(bot.Id);

                if (runtime == null) { await Reply(c,409,new{message="bot runtime not active"}); return; }

                botManager.StopFollow(bot);

                runtime.MovementState.FormationSlot = -1;

                runtime.MovementState.FormationColumns = 0;

                runtime.MovementState.FormationMemberCount = 0;

                runtime.MovementState.FormationSpacing = 2.5f;

                runtime.CombatState.Target = null;

                bot.CurrentTarget = null;

                runtime.CombatState.SetForcedState(BotCombatStateType.Idle);

                runtime.CombatState.TransitionTo(BotCombatStateType.Idle);

                await Reply(c,200,new{ok=true}); return;

            }

            if(c.Request.HttpMethod=="POST"&&action=="move"){var r=await Body<MoveRequest>(c);if(r==null){await Reply(c,400,new{message="invalid body"});return;}botManager.SetBotDestination(bot,r.x,r.y,r.z,r.run);await Reply(c,200,new{ok=true});return;}

            if(c.Request.HttpMethod=="GET"&&action=="rotation")

            {

                var runtime=BotHost.Instance.GetRuntime(bot.Id); if(runtime==null){await Reply(c,409,new{message="bot runtime not active"});return;}

                var manager=BotRotationManager.Instance; if(manager.Rotations.Count==0) manager.Load();

                var rotationId=runtime.RotationOverrideId ?? runtime.AttachedRotationId;

                var rotation=manager.GetRotation(rotationId);

                await Reply(c,200,new{ok=true,botId=bot.Id,name=bot.Name,rotation=rotationId,overrideId=runtime.RotationOverrideId,attachedId=runtime.AttachedRotationId,archetype=rotation?.Archetype,fallback=rotation==null,available=manager.Rotations.Keys.Order(StringComparer.OrdinalIgnoreCase).ToArray()});return;

            }

            if(c.Request.HttpMethod=="POST"&&action=="rotation")

            {

                var r=await Body<RotationRequest>(c); if(r==null||string.IsNullOrWhiteSpace(r.rotationId)){await Reply(c,400,new{message="rotationId is required"});return;}

                var runtime=BotHost.Instance.GetRuntime(bot.Id); if(runtime==null){await Reply(c,409,new{message="bot runtime not active"});return;}

                var manager=BotRotationManager.Instance; if(manager.Rotations.Count==0) manager.Load();

                if (r.rotationId.Equals("auto", StringComparison.OrdinalIgnoreCase) ||

                    r.rotationId.Equals("clear", StringComparison.OrdinalIgnoreCase) ||

                    r.rotationId.Equals("default", StringComparison.OrdinalIgnoreCase) ||

                    r.rotationId.Equals("fallback", StringComparison.OrdinalIgnoreCase))

                {

                    var engine = runtime.Engines[(int)BotEngineKind.Combat];

                    engine?.RemoveStrategy("rotation");

                    runtime.RotationOverrideId = null;

                    runtime.AttachedRotationId = null;

                    runtime.AttachedRotationVersion = 0;

                    runtime.AttachedRotationArchetype = null;

                    await Reply(c,200,new{ok=true,botId=bot.Id,name=bot.Name,rotation=(string?)null,fallback=true,message="rotation override cleared; automatic learned-skill fallback restored"});return;

                }

                if(manager.GetRotation(r.rotationId)==null){await Reply(c,400,new{message=$"unknown rotation '{r.rotationId}'",available=manager.Rotations.Keys.Order(StringComparer.OrdinalIgnoreCase).ToArray()});return;}

                if(!manager.SetRotation(runtime,r.rotationId)){await Reply(c,409,new{message=$"could not attach rotation '{r.rotationId}'"});return;}

                await Reply(c,200,new{ok=true,botId=bot.Id,name=bot.Name,rotation=r.rotationId});return;

            }

            if(c.Request.HttpMethod=="GET"&&action=="strategy")

            {

                var kindText=c.Request.QueryString["engine"] ?? "co"; if(!TryEngineKind(kindText,out var kind)){await Reply(c,400,new{message="engine must be co, nc, or de"});return;}

                var engine=BotHost.Instance.GetRuntime(bot.Id)?.Engines[(int)kind]; if(engine==null){await Reply(c,409,new{message=$"bot has no {kind} engine"});return;}

                await Reply(c,200,new{ok=true,botId=bot.Id,name=bot.Name,engine=kind.ToString(),active=engine.ListStrategies(),available=BotContentRegistry.StrategyNames.Order(StringComparer.OrdinalIgnoreCase).ToArray()});return;

            }

            if(c.Request.HttpMethod=="POST"&&action=="strategy")

            {

                var r=await Body<StrategyRequest>(c); if(r==null||!TryEngineKind(r.engine,out var kind)){await Reply(c,400,new{message="engine must be co, nc, or de"});return;}

                if(r.operation is not ("+" or "-" or "~")){await Reply(c,400,new{message="operation must be +, -, or ~"});return;}

                var names=(r.names ?? []).Where(x=>!string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(); if(names.Length==0){await Reply(c,400,new{message="at least one strategy name is required"});return;}

                var unknown=names.Where(x=>!BotContentRegistry.StrategyNames.Contains(x,StringComparer.OrdinalIgnoreCase)).ToArray(); if(unknown.Length>0){await Reply(c,400,new{message="unknown strategy",unknown,available=BotContentRegistry.StrategyNames.Order(StringComparer.OrdinalIgnoreCase).ToArray()});return;}

                var engine=BotHost.Instance.GetRuntime(bot.Id)?.Engines[(int)kind]; if(engine==null){await Reply(c,409,new{message=$"bot has no {kind} engine"});return;}

                foreach(var name in names){if(r.operation=="+")engine.AddStrategy(name);else if(r.operation=="-")engine.RemoveStrategy(name);else engine.ToggleStrategy(name);}

                await Reply(c,200,new{ok=true,botId=bot.Id,name=bot.Name,engine=kind.ToString(),active=engine.ListStrategies()});return;

            }

            if(c.Request.HttpMethod=="GET"&&action=="state")

            {

                var runtime = BotHost.Instance.GetRuntime(bot.Id);

                if(runtime == null){await Reply(c,409,new{message="bot runtime not active"});return;}

                var cs = runtime.CombatState;

                var ms = runtime.MovementState;

                await Reply(c,200,new{ok=true,botId=bot.Id,name=bot.Name,state=cs.CurrentState.ToString(),forced=cs.ForcedState?.ToString(),active=cs.IsActive,target=cs.Target?.Name,followTarget=ms.FollowTarget?.Name,hp=bot.Hp,maxHp=bot.MaxHp,killCount=cs.KillCount,killGoal=cs.KillGoal});return;

            }

            if(c.Request.HttpMethod=="POST"&&action=="state")

            {
                if(BotJobRegistry.IsFarmer(bot.Id))
                {
                    var farmerState=await Body<StateRequest>(c);
                    if(farmerState?.state?.Trim().ToLowerInvariant()!="idle")
                    {await Reply(c,409,new{message="Farmer mode blocks combat states. Use Follow or Stay, or confirm the Companion job first."});return;}
                    botManager.StopFollow(bot);botManager.StopImmediately(bot);
                    var farmerRuntime=BotHost.Instance.GetRuntime(bot.Id);
                    if(farmerRuntime!=null)lock(farmerRuntime.SyncRoot)BotJobRegistry.SuppressCombat(farmerRuntime);
                    await Reply(c,200,new{ok=true,state="Idle",job="farmer"});return;
                }


                var r=await Body<StateRequest>(c);

                if(r==null||string.IsNullOrWhiteSpace(r.state)){await Reply(c,400,new{message="state is required"});return;}

                var runtime = BotHost.Instance.GetRuntime(bot.Id);

                if(runtime == null){await Reply(c,409,new{message="bot runtime not active"});return;}

                var cs=runtime.CombatState;

                var cmd=r.state.Trim().ToLowerInvariant();

                if(cmd=="free")

                {

                    cs.StopAtTargetHpPercent=null; cs.NonlethalFloorReached=null;

                    BotCombatManager.Instance.SetForcedState(bot,null);

                    if(cs.IsActive) BotCombatManager.Instance.StartListening(bot);

                    await Reply(c,200,new{ok=true,botId=bot.Id,name=bot.Name,state=cs.CurrentState.ToString(),forced=(string?)null});return;

                }

                BotCombatStateType? newState=cmd switch

                {

                    "idle"=>BotCombatStateType.Idle,

                    "grind" or "grinding"=>BotCombatStateType.Grinding,

                    "quest" or "questing"=>BotCombatStateType.Questing,

                    "roam" or "roaming"=>BotCombatStateType.Roaming,

                    "follow" or "following"=>BotCombatStateType.Following,

                    "rest" or "resting"=>BotCombatStateType.Resting,

                    _=>null

                };

                if(!newState.HasValue){await Reply(c,400,new{message="invalid state; use idle, grind, questing, roaming, following, resting, or free"});return;}

                if(r.killGoal.HasValue && (newState.Value!=BotCombatStateType.Grinding || r.killGoal.Value<=0)){await Reply(c,400,new{message="a positive kill goal is supported only with grind"});return;}

                cs.StopAtTargetHpPercent=null; cs.NonlethalFloorReached=null;

                if(r.killGoal.HasValue){cs.KillGoal=r.killGoal.Value;cs.KillCount=0;cs.IsActive=true;}

                cs.SetForcedState(newState.Value);

                if(newState.Value==BotCombatStateType.Idle)

                {

                    botManager.StopFollow(bot); cs.Target=null; bot.CurrentTarget=null; cs.TransitionTo(BotCombatStateType.Idle);

                }

                else BotCombatManager.Instance.StartListening(bot);

                await Reply(c,200,new{ok=true,botId=bot.Id,name=bot.Name,state=cs.CurrentState.ToString(),forced=cs.ForcedState?.ToString(),killGoal=r.killGoal});return;

            }

            if(c.Request.HttpMethod=="POST"&&action=="follow")

            {

                var r=await Body<FollowRequest>(c);

                if(r==null){await Reply(c,400,new{message="invalid body"});return;}

                var target=WorldManager.Instance.GetCharacterById(r.targetId);

                if(target?.Transform == null || target.ParentWorld == null){await Reply(c,404,new{message="target character not online"});return;}

                var runtime = BotHost.Instance.GetRuntime(bot.Id);

                if(runtime == null){await Reply(c,409,new{message="bot runtime not active"});return;}

                if(bot.Transform == null || bot.ParentWorld == null || !ReferenceEquals(bot.ParentWorld,target.ParentWorld) || bot.Transform.InstanceId != target.Transform.InstanceId)

                {await Reply(c,409,new{message="bot and target are not in the same world instance"});return;}

                var distance=Math.Clamp(r.distance,1f,20f);

                botManager.SetFollowTarget(bot,target,distance);

                runtime.CombatState.Target = null;

                bot.CurrentTarget = null;

                runtime.CombatState.SetForcedState(BotCombatStateType.Following);

                runtime.CombatState.TransitionTo(BotCombatStateType.Following);

                BotCombatManager.Instance.StartListening(bot);

                await Reply(c,200,new{ok=true,botId=bot.Id,targetId=target.Id,targetName=target.Name,distance});return;

            }

            await Reply(c,404,new{message="not found"});

        }

        catch(Exception ex){Logger.Error(ex,"PlayerBots remote request failed");try{await Reply(c,500,new{message="server error"});}catch{}}
        finally{if(itemGateHeld)_itemMutationGate.Release();}

    }





    private readonly System.Collections.Concurrent.ConcurrentDictionary<uint, LearnPending> _learnPending = new();
    private sealed record LearnRequest(string type, uint skillId, uint ownerId, bool confirm, string token);
    private sealed record LearnPending(AAEmu.Game.Models.Game.Char.Character Bot, uint OwnerId,
        string Type, uint SkillId, string Signature, string Token, DateTime ExpiresUtc);

    private RespecResult Learn(AAEmu.Game.Models.Game.Char.Character bot, string type, uint skillId,
        uint expectedOwnerId, bool confirm, string token)
    {
        RespecResult Reject(int status, string message) => new(status, new { ok = false, message });
        var runtime = BotHost.Instance.GetRuntime(bot.Id);
        if (!bot.IsBot || runtime == null)
            return Reject(409, "Learning requires a spawned PlayerBot with an active runtime. No skills changed.");
        type = (type ?? "").ToLowerInvariant();
        if (skillId == 0 || type is not ("active" or "passive"))
            return Reject(400, "Specify a positive skill ID and type active or passive. No skills changed.");

        lock (runtime.SyncRoot)
        lock (_ownersLock)
        {
            if (runtime.Retired || !ReferenceEquals(runtime.Bot, bot) ||
                !ReferenceEquals(botManager.GetBot(bot.Id), bot) || bot.ParentWorld == null || bot.Skills == null)
                return Reject(409, "Bot is no longer active. Spawn it and preview again. No skills changed.");
            if (!_owners.TryGetValue(bot.Id, out var ownerId) || ownerId == 0 || ownerId != expectedOwnerId)
                return Reject(403, "Companion ownership is missing or changed. Check owner assignment and preview again. No skills changed.");
            var owner = WorldManager.Instance.GetCharacterById(ownerId);
            if (owner == null || !owner.IsOnline || owner.IsBot)
                return Reject(409, "The assigned human owner must be online for learning. No skills changed.");
            if (bot.IsDead || bot.IsInBattle || bot.SkillTask != null || bot.AutoAttackTask != null || runtime.CombatState.Target != null)
                return Reject(409, "Bot must be alive, out of combat, and not casting or attacking. No skills changed.");

            var activeAbilities = new[] { bot.Ability1, bot.Ability2, bot.Ability3 }
                .Where(a => a > AbilityType.General && a < AbilityType.None).Distinct().ToArray();
            var learned = bot.Skills.Skills.Values.OrderBy(x => x.Id).ToArray();
            var passives = bot.Skills.PassiveBuffs.Values.OrderBy(x => x.Id).ToArray();
            if (learned.Any(x => x.Template == null) || passives.Any(x => x.Template == null))
                return Reject(409, "Current skill templates are unavailable. No skills changed.");
            var used = learned.Sum(x => x.Template.SkillPoints) + passives.Length;
            var total = ExperienceManager.Instance.GetSkillPointsForLevel(bot.Level);
            if (used > total)
                return Reject(409, "Learned skills exceed the level skill-point budget. Resolve the invalid build before learning. No skills changed.");

            AbilityType ability;
            int cost, requiredLevel, requiredPoints;
            string skillName;
            if (type == "active")
            {
                var template = SkillManager.Instance.GetSkillTemplate(skillId);
                if (template == null)
                    return Reject(404, $"Active skill {skillId} was not found. No skills changed.");
                // PlotOnly selects the skill's execution path; it does not mean
                // the player cannot learn the skill through CharacterSkills.
                if (!template.NeedLearn || !template.Show || template.SkillPoints <= 0)
                    return Reject(400, $"Skill {skillId} is not a visible, learnable, point-costing skill (NeedLearn={template.NeedLearn}, Show={template.Show}, SkillPoints={template.SkillPoints}). No skills changed.");
                ability = template.AbilityId; cost = template.SkillPoints;
                requiredLevel = template.AbilityLevel; requiredPoints = 0;
                skillName = LocalizationManager.Instance.Get("skills", "name", skillId, $"Skill {skillId}");
                if (bot.Skills.Skills.ContainsKey(skillId))
                    return Reject(409, $"{skillName} [{skillId}] is already learned. No points spent.");
            }
            else
            {
                var template = SkillManager.Instance.GetPassiveBuffTemplate(skillId);
                if (template == null)
                    return Reject(404, $"Passive skill {skillId} was not found. Use the passive ID, not its underlying buff ID. No skills changed.");
                if (SkillManager.Instance.GetBuffTemplate(template.BuffId) == null)
                    return Reject(409, "The passive's buff template is unavailable. No skills changed.");
                ability = template.AbilityId; cost = 1;
                requiredLevel = template.Level; requiredPoints = template.ReqPoints;
                skillName = LocalizationManager.Instance.Get("buffs", "name", skillId, $"Passive {skillId}");
                if (bot.Skills.PassiveBuffs.ContainsKey(skillId))
                    return Reject(409, $"{skillName} [{skillId}] is already learned. No points spent.");
            }
            if (!activeAbilities.Contains(ability))
                return Reject(400, "Skill must belong to one of the bot's three selected skillsets. No skills changed.");
            var abilityLevel = bot.GetAbLevel(ability);
            if (abilityLevel < requiredLevel)
                return Reject(409, $"Requires {ability} level {requiredLevel}; current level is {abilityLevel}. No skills changed.");
            var treePoints = learned.Where(x => x.Template.AbilityId == ability).Sum(x => x.Template.SkillPoints) +
                passives.Count(x => x.Template.AbilityId == ability);
            if (treePoints < requiredPoints)
                return Reject(409, $"Requires {requiredPoints} points already spent in {ability}; currently {treePoints}. No skills changed.");
            if (total - used < cost)
                return Reject(409, $"Not enough skill points: needs {cost}, available {total - used}. No skills changed.");

            var signature = $"{bot.Level}:{bot.Ability1}:{bot.Ability2}:{bot.Ability3}:{abilityLevel}:{cost}:{requiredLevel}:{requiredPoints}|" +
                string.Join(";", learned.Select(x => $"s:{x.Id}:{x.Level}:{x.Template.AbilityId}:{x.Template.SkillPoints}")) + "|" +
                string.Join(";", passives.Select(x => $"p:{x.Id}:{x.Template.AbilityId}"));
            if (!confirm)
            {
                var pending = new LearnPending(bot, ownerId, type, skillId, signature,
                    Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), DateTime.UtcNow.AddMinutes(2));
                _learnPending[bot.Id] = pending;
                return new(200, new { ok = true, previewOnly = true, botId = bot.Id, name = bot.Name,
                    type, skillId, skillName, ability = ability.ToString(), abilityLevel, requiredLevel, requiredPoints,
                    cost, usedSkillPoints = used, remainingSkillPoints = total - used, pointsAfterLearn = total - used - cost,
                    ownerId, token = pending.Token, expiresUtc = pending.ExpiresUtc });
            }
            if (!_learnPending.TryGetValue(bot.Id, out var previous) || previous.Token != token ||
                previous.ExpiresUtc <= DateTime.UtcNow || !ReferenceEquals(previous.Bot, bot) || previous.OwnerId != ownerId ||
                previous.Type != type || previous.SkillId != skillId || previous.Signature != signature)
                return Reject(409, "Confirmation is expired or bot, owner, skill, or build changed. Preview learning again. No skills changed.");
            _learnPending.TryRemove(bot.Id, out _);
            _respecPending.TryRemove(bot.Id, out _);
            try
            {
                if (type == "active") bot.Skills.AddSkill(skillId);
                else bot.Skills.AddBuff(skillId);
                var exists = type == "active" ? bot.Skills.Skills.ContainsKey(skillId) : bot.Skills.PassiveBuffs.ContainsKey(skillId);
                var afterUsed = bot.Skills.Skills.Values.Sum(x => x.Template.SkillPoints) + bot.Skills.PassiveBuffs.Count;
                if (!exists || afterUsed != used + cost)
                    throw new InvalidOperationException("Native skill learning did not match the preview.");
                return new(200, new { ok = true, previewOnly = false, botId = bot.Id, name = bot.Name,
                    type, skillId, skillName, ability = ability.ToString(), spentPoints = cost,
                    learnedLevel = type == "active" ? bot.Skills.Skills[skillId].Level : (byte?)null,
                    usedSkillPoints = afterUsed, remainingSkillPoints = total - afterUsed,
                    message = "Skill learned through CharacterSkills. It will persist through normal character saving." });
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Skill learning failed for bot {0}, skill {1}", bot.Id, skillId);
                return Reject(500, "Learning failed after starting; skills may have changed. Inspect skills and the server log before previewing again.");
            }
        }
    }


    private sealed record RespecRequest(string scope, uint ownerId, bool confirm, string token);
    private sealed record RespecPending(AAEmu.Game.Models.Game.Char.Character Bot, uint OwnerId,
        string Scope, string Signature, string Token, DateTime ExpiresUtc);
    private sealed record RespecResult(int Status, object Body);

    // The remote API key is administrative authority. ownerId binds the operation to
    // the currently assigned companion owner; it is not an alternative credential.
    private RespecResult Respec(AAEmu.Game.Models.Game.Char.Character bot, string scope,
        uint expectedOwnerId, bool confirm, string token)
    {
        RespecResult Reject(int status, string message) => new(status, new { ok = false, message });
        var runtime = BotHost.Instance.GetRuntime(bot.Id);
        if (!bot.IsBot || runtime == null)
            return Reject(409, "Respec requires a spawned PlayerBot with an active runtime. No skills changed.");

        lock (runtime.SyncRoot)
        lock (_ownersLock)
        {
            if (runtime.Retired || !ReferenceEquals(runtime.Bot, bot) ||
                !ReferenceEquals(botManager.GetBot(bot.Id), bot) || bot.ParentWorld == null || bot.Skills == null)
                return Reject(409, "Bot is no longer active. Spawn it and preview again. No skills changed.");
            if (!_owners.TryGetValue(bot.Id, out var ownerId) || ownerId == 0 || ownerId != expectedOwnerId)
                return Reject(403, "Companion ownership is missing or changed. Check owner assignment and preview again. No skills changed.");
            var owner = WorldManager.Instance.GetCharacterById(ownerId);
            if (owner == null || !owner.IsOnline || owner.IsBot)
                return Reject(409, "The assigned human owner must be online for respec. No skills changed.");
            if (bot.IsDead || bot.IsInBattle || bot.SkillTask != null || bot.AutoAttackTask != null ||
                runtime.CombatState.Target != null)
                return Reject(409, "Bot must be alive, out of combat, and not casting or attacking. No skills changed.");

            var activeAbilities = new[] { bot.Ability1, bot.Ability2, bot.Ability3 }
                .Where(a => a > AbilityType.General && a < AbilityType.None).Distinct().ToArray();
            var all = string.Equals(scope, "all", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(scope, "full", StringComparison.OrdinalIgnoreCase);
            var selected = activeAbilities.Where(a => string.Equals(a.ToString(), scope, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (all) selected = activeAbilities;
            if (selected.Length == 0)
                return Reject(400, "Unknown or inactive skillset. Use all, full, or an active skillset name. No skills changed.");
            var canonicalScope = all ? "all" : selected[0].ToString();
            var learned = bot.Skills.Skills.Values.OrderBy(x => x.Id).ToArray();
            var passives = bot.Skills.PassiveBuffs.Values.OrderBy(x => x.Id).ToArray();
            if (learned.Any(x => x.Template == null) || passives.Any(x => x.Template == null))
                return Reject(409, "Skill templates are unavailable. No skills changed.");
            var signature = $"{bot.Level}:{bot.Ability1}:{bot.Ability2}:{bot.Ability3}|" +
                string.Join(";", learned.Select(x => $"s:{x.Id}:{x.Level}:{x.Template.AbilityId}:{x.Template.SkillPoints}")) + "|" +
                string.Join(";", passives.Select(x => $"p:{x.Id}:{x.Template.AbilityId}"));
            var resetSkills = learned.Where(x => selected.Contains(x.Template.AbilityId)).ToArray();
            var resetPassives = passives.Where(x => selected.Contains(x.Template.AbilityId)).ToArray();
            var used = learned.Sum(x => x.Template.SkillPoints) + passives.Length;
            var total = ExperienceManager.Instance.GetSkillPointsForLevel(bot.Level);
            var refund = resetSkills.Sum(x => x.Template.SkillPoints) + resetPassives.Length;
            if (used > total)
                return Reject(409, "Learned skills exceed the level skill-point budget. Resolve the invalid build before respec. No skills changed.");

            if (!confirm)
            {
                var pending = new RespecPending(bot, ownerId, canonicalScope, signature,
                    Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), DateTime.UtcNow.AddMinutes(2));
                _respecPending[bot.Id] = pending;
                return new(200, new
                {
                    ok = true, previewOnly = true, botId = bot.Id, name = bot.Name, scope = canonicalScope,
                    ownerId, token = pending.Token, expiresUtc = pending.ExpiresUtc,
                    totalSkillPoints = total, currentlyUsedSkillPoints = used, currentlyRemainingSkillPoints = total - used,
                    refundablePoints = refund, pointsAfterReset = total - used + refund,
                    trees = selected.Select(a => new { ability = a.ToString(), abilityLevel = bot.GetAbLevel(a),
                        usedSkillPoints = resetSkills.Where(x => x.Template.AbilityId == a).Sum(x => x.Template.SkillPoints) + resetPassives.Count(x => x.Template.AbilityId == a),
                        learnedActive = resetSkills.Count(x => x.Template.AbilityId == a), learnedPassive = resetPassives.Count(x => x.Template.AbilityId == a) }).ToArray(),
                    activeSkills = resetSkills.Select(x => new { id = x.Id, level = x.Level, ability = x.Template.AbilityId.ToString(), name = LocalizationManager.Instance.Get("skills", "name", x.Id, $"Skill {x.Id}") }).ToArray(),
                    passiveSkills = resetPassives.Select(x => new { id = x.Id, ability = x.Template.AbilityId.ToString(), name = LocalizationManager.Instance.Get("buffs", "name", x.Id, $"Passive {x.Id}") }).ToArray()
                });
            }

            if (!_respecPending.TryGetValue(bot.Id, out var previous) || previous.Token != token ||
                previous.ExpiresUtc <= DateTime.UtcNow || !ReferenceEquals(previous.Bot, bot) ||
                previous.OwnerId != ownerId || previous.Scope != canonicalScope || previous.Signature != signature)
                return Reject(409, "Confirmation is expired or bot, owner, scope, or skills changed. Run respec preview again. No skills changed.");
            // Consume before mutation: repeated requests cannot execute the reset again.
            _respecPending.TryRemove(bot.Id, out _);
            try
            {
                foreach (var ability in selected)
                    bot.Skills.Reset(ability);
                var afterUsed = bot.Skills.Skills.Values.Sum(x => x.Template.SkillPoints) + bot.Skills.PassiveBuffs.Count;
                if (resetSkills.Any(x => bot.Skills.Skills.ContainsKey(x.Id)) ||
                    resetPassives.Any(x => bot.Skills.PassiveBuffs.ContainsKey(x.Id)) || afterUsed != used - refund)
                    throw new InvalidOperationException("Reset result did not match the preview.");
                return new(200, new { ok = true, previewOnly = false, botId = bot.Id, name = bot.Name,
                    scope = canonicalScope, removedActive = resetSkills.Length, removedPassive = resetPassives.Length,
                    refundedPoints = refund, remainingSkillPoints = total - afterUsed,
                    message = "Respec complete. Removals are tracked by CharacterSkills for normal character saving." });
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Respec failed for bot {0}, scope {1}", bot.Id, canonicalScope);
                return Reject(500, "Respec failed after starting; skills may have changed. Inspect skills and the server log before previewing again.");
            }
        }
    }


    public static bool TryGetCompanionOwner(uint botId, out uint ownerId)

    {

        ownerId = 0;

        var instance = _instance;

        if (instance == null) return false;

        lock (instance._ownersLock)

            return instance._owners.TryGetValue(botId, out ownerId) && ownerId != 0;

    }



    // v0.16.2: event-driven owner assist. Called only after AAEmu has accepted an

    // offensive player skill, so companions do not have to wait for the lifecycle

    // poll to notice that their owner entered battle.

    public static void NotifyOwnerOffensiveSkill(AAEmu.Game.Models.Game.Char.Character owner, Unit target)

    {

        var instance = _instance;

        if (instance == null || owner == null || target == null || target.IsDead ||

            owner.Id == target.Id || !owner.CanAttack(target) || owner.Transform == null || target.Transform == null ||

            owner.ParentWorld == null || !ReferenceEquals(owner.ParentWorld, target.ParentWorld) ||

            owner.Transform.InstanceId != target.Transform.InstanceId || owner.GetDistanceTo(target, true) > 40f)

            return;



        uint[] companionIds;

        lock (instance._ownersLock)

            companionIds = instance._owners.Where(x => x.Value == owner.Id).Select(x => x.Key).ToArray();



        foreach (var botId in companionIds)

        {

            if (BotJobRegistry.IsFarmer(botId)) continue;
            var bot = instance._botManager.GetBot(botId);

            var runtime = BotHost.Instance.GetRuntime(botId);

            if (bot == null || runtime == null || bot.IsDead || bot.Transform == null ||

                bot.ParentWorld == null || !ReferenceEquals(bot.ParentWorld, owner.ParentWorld) ||

                bot.Transform.InstanceId != owner.Transform.InstanceId || !bot.CanAttack(target))

                continue;



            // Preserve AFK anti-farming: an AFK owner may be defended, but their own

            // offensive casts must not become an unattended proactive assist trigger.

            lock (instance._ownersLock)

                if (instance._afkSuppressed.Contains(owner.Id) || instance._ownerAfkSince.ContainsKey(owner.Id))

                    continue;



            runtime.Social.ApplyAttack(target);

            BotCombatManager.Instance.StartListening(bot);

            Logger.Trace("Companion bot {0} immediate owner-assist target {1} from owner {2}", botId, target.ObjId, owner.Id);

        }

    }



    private async Task CompanionLoopAsync()

    {

        while (!_cts.IsCancellationRequested)

        {

            try

            {

                KeyValuePair<uint,uint>[] owners;

                lock (_ownersLock) owners = _owners.ToArray();



                foreach (var pair in owners)

                {

                    var botId = pair.Key;

                    var ownerId = pair.Value;

                    var owner = WorldManager.Instance.GetCharacterById(ownerId);

                    var ownerOnline = owner?.IsOnline == true;

                    var bot = botManager.GetBot(botId);



                    if (ownerOnline)

                    {

                        _ownerOfflineSince.Remove(botId);



                        // Prison is a hard companion boundary. Never let an owned bot

                        // path across the world toward a jailed owner or appear inside jail.

                        // AAEmu marks prisoners with TagPrisoner for the actual sentence,

                        // so this follows release rather than guessing with a timer.

                        if (owner!.Buffs.CheckBuffTag((uint)BuffConstants.TagPrisoner))

                        {

                            _ownerAfkSince.Remove(botId);

                            _afkSuppressed.Remove(botId);

                            if (bot != null)

                            {

                                botManager.StopImmediately(bot);

                                botManager.DespawnBot(botId);

                                Logger.Info("Companion bot {0} despawned while owner {1} is imprisoned", botId, ownerId);

                            }

                            continue;

                        }



                        // AAEmu 1.2 already exposes the client's real Away/AFK state as

                        // Character.IdleStatus (CSIdleStatusPacket). Do not infer AFK from

                        // ping traffic or movement timers.

                        if (owner!.IdleStatus)

                        {

                            if (!_ownerAfkSince.TryGetValue(botId, out var afkSince))

                            {

                                afkSince = DateTime.UtcNow;

                                _ownerAfkSince[botId] = afkSince;

                                Logger.Info("Companion bot {0} owner {1} entered AFK defence mode", botId, ownerId);

                            }



                            if (_afkSuppressed.Contains(botId))

                            {

                                if (bot != null)

                                    botManager.DespawnBot(botId);

                                continue;

                            }



                            if (bot == null && BotConfig.Instance.AutoSpawnEnabled)

                            {

                                bot = botManager.SpawnBot(botId);

                                if (bot != null)

                                    Logger.Info("Companion bot {0} auto-spawned for AFK owner {1} ({2})", botId, owner.Name, ownerId);

                            }



                            var afkExpired = DateTime.UtcNow - afkSince >= _companionAfkDefense;

                            if (bot != null && !afkExpired)

                            {

                                ApplyCompanionFollow(bot, owner, defensiveOnly: true);

                                TeamManager.Instance.EnsureCompanionInOwnerTeam(owner, bot);

                            }



                            // Defence mode is intentionally time-limited. Once the window

                            // expires, stop acquiring new threats, finish only the encounter

                            // already in progress, then despawn until the owner becomes active.

                            if (afkExpired && bot != null)

                            {

                                BeginSafeDisconnect(bot);

                                var runtime = BotHost.Instance.GetRuntime(botId);

                                var target = runtime?.CombatState.Target;

                                if (target == null || target.IsDead)

                                {

                                    botManager.StopImmediately(bot);

                                    if (botManager.DespawnBot(botId))

                                    {

                                        _afkSuppressed.Add(botId);

                                        Logger.Info("Companion bot {0} despawned after AFK defence window for owner {1}", botId, ownerId);

                                    }

                                }

                            }

                            continue;

                        }



                        // Owner became active again: clear the AFK lockout and restore the

                        // normal companion Follow + Assist behaviour.

                        _ownerAfkSince.Remove(botId);

                        _afkSuppressed.Remove(botId);

                        if (bot == null && BotConfig.Instance.AutoSpawnEnabled)

                        {

                            bot = botManager.SpawnBot(botId);

                            if (bot != null)

                                Logger.Info("Companion bot {0} auto-spawned for online owner {1} ({2})", botId, owner.Name, ownerId);

                        }



                        if (bot != null)

                        {

                            // Establish follow first. Party synchronization must never prevent

                            // the companion movement state from being assigned.

                            ApplyCompanionFollow(bot, owner, defensiveOnly: false);

                            TeamManager.Instance.EnsureCompanionInOwnerTeam(owner, bot);

                        }

                        continue;

                    }



                    _ownerAfkSince.Remove(botId);

                    _afkSuppressed.Remove(botId);

                    if (bot == null)

                    {

                        _ownerOfflineSince.Remove(botId);

                        continue;

                    }



                    // Farmers remain spawned for confirmed native mining while the owner is offline.
                    // Clear only stale following; preserve an active gathering destination/cast.
                    if (BotJobRegistry.IsFarmer(botId) && BotConfig.Instance.OfflineMiningEnabled)
                    {
                        _ownerOfflineSince.Remove(botId);
                        var farmerRuntime = BotHost.Instance.GetRuntime(botId);
                        if (farmerRuntime != null)
                        {
                            lock (farmerRuntime.SyncRoot)
                            {
                                if (!farmerRuntime.Retired && ReferenceEquals(farmerRuntime.Bot, bot) &&
                                    farmerRuntime.MovementState.FollowTarget != null)
                                    botManager.StopFollow(bot);
                            }
                        }
                        continue;
                    }

                    if (!_ownerOfflineSince.TryGetValue(botId, out var offlineSince))

                    {

                        offlineSince = DateTime.UtcNow;

                        _ownerOfflineSince[botId] = offlineSince;

                        BeginSafeDisconnect(bot);

                        Logger.Info("Companion bot {0} owner {1} offline; safe disconnect grace started", botId, ownerId);

                    }



                    BeginSafeDisconnect(bot);



                    if (DateTime.UtcNow - offlineSince < CompanionDisconnectGrace)

                        continue;



                    var offlineRuntime = BotHost.Instance.GetRuntime(botId);

                    var offlineTarget = offlineRuntime?.CombatState.Target;

                    if (offlineTarget != null && !offlineTarget.IsDead)

                        continue;



                    botManager.StopImmediately(bot);

                    if (botManager.DespawnBot(botId))

                    {

                        _ownerOfflineSince.Remove(botId);

                        Logger.Info("Companion bot {0} despawned after owner {1} disconnect grace", botId, ownerId);

                    }

                }

            }

            catch (Exception ex)

            {

                Logger.Error(ex, "PlayerBots companion lifecycle tick failed");

            }



            // Companion assist responsiveness: the old 2-second lifecycle cadence was also

            // the owner-target acquisition cadence, which made melee companions visibly

            // react several seconds late. 300 ms matches the configured combat reaction

            // delay closely enough to feel immediate without same-tick robotic response.

            try { await Task.Delay(TimeSpan.FromMilliseconds(300), _cts.Token); }

            catch (OperationCanceledException) { break; }

        }

    }



    private void ApplyCompanionFollow(AAEmu.Game.Models.Game.Char.Character bot, AAEmu.Game.Models.Game.Char.Character owner, bool defensiveOnly)

    {

        var runtime = BotHost.Instance.GetRuntime(bot.Id);
        if (runtime == null || bot.Transform == null || owner.Transform == null ||

            bot.ParentWorld == null || owner.ParentWorld == null ||

            !ReferenceEquals(bot.ParentWorld, owner.ParentWorld) ||

            bot.Transform.InstanceId != owner.Transform.InstanceId)

            return;

        if (BotJobRegistry.IsFarmer(bot.Id))
        {
            lock (runtime.SyncRoot)
            {
                BotJobRegistry.StepFarmer(runtime);
                // Stay and gathering deliberately stop following. Only recall a
                // Farmer that is actually following this assigned owner.
                if (!ReferenceEquals(runtime.MovementState.FollowTarget, owner) || bot.IsDead ||
                    owner.IsDead || owner.Buffs.CheckBuffTag((uint)BuffConstants.TagPrisoner)) return;
                if (bot.GetDistanceTo(owner, true) > 80f && AAEmu.Game.Bots.Navigation.BotSwimFollow.CanRecall(owner))
                {
                    BotCraftController.Stop(runtime,"Owner recall cancelled crafting.");
                    BotGatherController.Stop(runtime, "Owner recall cancelled gathering.");
                    bot.InterruptSkills();
                    botManager.MoveBotTo(bot, owner.Transform.World.Position.X,
                        owner.Transform.World.Position.Y, owner.Transform.World.Position.Z);
                    // Native teleport clears movement, so restore following.
                    botManager.SetFollowTarget(bot, owner, 2f);
                    BotJobRegistry.SuppressCombat(runtime);
                    Logger.Info("Farmer bot {0} recalled beside owner {1} after long-distance move", bot.Id, owner.Id);
                }
            }
            return;
        }



        // A companion should never hike across the map after its owner is teleported.

        // For same-world/same-instance jumps, recall the bot beside the owner. The jail

        // case is handled above and never recalls the companion into prison.

        if (bot.GetDistanceTo(owner, true) > 80f && AAEmu.Game.Bots.Navigation.BotSwimFollow.CanRecall(owner))

        {

            var ownerPos = owner.Transform.World.Position;

            botManager.MoveBotTo(bot, ownerPos.X, ownerPos.Y, ownerPos.Z);

            Logger.Info("Companion bot {0} recalled beside owner {1} after long-distance move", bot.Id, owner.Id);

        }



        var cs = runtime.CombatState;

        cs.KillGoal = null;

        cs.TargetTypeFilter = null;

        cs.IsActive = true;



        // Combat/defence has priority over formation following.  v0.13.1 used to

        // clear Target on every companion tick, which made the bot attack, sprint

        // back toward the owner, reacquire, and attack again.  Keep an encounter

        // once it has legitimately started and only return to follow when it ends.

        var threat = FindCompanionThreat(owner, bot, cs.Target, allowOwnerInitiated: !defensiveOnly);

        if (threat != null)

        {

            // v0.14.1 companion combat leash.  A role may decide HOW the bot fights,

            // but it must never drag the companion away from its owner indefinitely.

            // Measure from the owner (not the bot) so kiting cannot extend the leash.

            const float companionCombatLeash = 40f;

            if (owner.GetDistanceTo(threat, true) <= companionCombatLeash)

            {

                cs.SetForcedState(BotCombatStateType.Following);

                cs.Target = threat;

                bot.CurrentTarget = threat;

                cs.TransitionTo(BotCombatStateType.Combat);

                BotCombatManager.Instance.StartListening(bot);

                return;

            }



            // Threat crossed the leash: hard-cancel the combat controller before

            // restoring formation. Merely clearing Target is not enough because the

            // active Combat state can continue moving/attacking the previous target.

            cs.Target = null;

            bot.CurrentTarget = null;

            cs.IsActive = false;

            cs.SetForcedState(BotCombatStateType.Idle);

            cs.TransitionTo(BotCombatStateType.Idle);

            botManager.StopImmediately(bot);

            cs.IsActive = true;

        }



        // No active encounter: resume the normal owner formation.

        botManager.SetFollowTarget(bot, owner, 3f);

        cs.Target = null;

        bot.CurrentTarget = null;

        cs.SetForcedState(BotCombatStateType.Following);

        cs.TransitionTo(BotCombatStateType.Following);

        BotCombatManager.Instance.StartListening(bot);

    }



    private static Unit? FindCompanionThreat(AAEmu.Game.Models.Game.Char.Character owner, AAEmu.Game.Models.Game.Char.Character companion, Unit? currentEncounter, bool allowOwnerInitiated)

    {

        // v0.16.7: self-defence has first priority. Preserve the current target when

        // it is already attacking Albert; otherwise hand off to a real attacker.

        var selfThreat = FindCompanionSelfThreat(owner, companion, currentEncounter);

        if (selfThreat != null)

            return selfThreat;



        // Once the companion has entered a valid encounter, finish that encounter.

        // This also covers the case where the owner stops attacking while the mob

        // continues fighting the owner/party.

        if (currentEncounter != null && !currentEncounter.IsDead)

            return currentEncounter;



        // Defensive reactions are always allowed: protect the companion, owner, and

        // every online living party member from units that currently have aggro.

        var defensiveThreat = FindPartyDefensiveThreat(owner, companion);

        if (defensiveThreat != null)

            return defensiveThreat;



        // Active-owner mode may assist the owner's chosen combat target.  Requiring

        // IsInBattle prevents merely selecting a nearby mob from starting combat.

        // AFK defence calls this with allowOwnerInitiated=false, so AFK can never

        // turn target selection into proactive farming.

        if (allowOwnerInitiated && owner.IsInBattle && owner.CurrentTarget is Unit ownerTarget &&

            !ownerTarget.IsDead && ownerTarget.Id != owner.Id && ownerTarget.Id != companion.Id &&

            owner.GetDistanceTo(ownerTarget, true) <= 40f)

            return ownerTarget;



        return null;

    }



    private static Unit? FindCompanionSelfThreat(AAEmu.Game.Models.Game.Char.Character owner, AAEmu.Game.Models.Game.Char.Character companion, Unit? currentEncounter)

    {

        // Avoid target bouncing when the current encounter is already attacking Albert.

        if (currentEncounter != null && !currentEncounter.IsDead &&

            companion.IsInAggroListOf.ContainsKey(currentEncounter.ObjId))

            return currentEncounter;



        // A unit on Albert's reverse-aggro list has already gained real AAEmu aggro

        // on him. Neutral mobs remain ignored until this actually happens.

        foreach (var attackerBase in companion.IsInAggroListOf.Values.ToArray())

        {

            if (attackerBase is not Unit attacker || attacker.IsDead ||

                attacker.Id == owner.Id || attacker.Id == companion.Id ||

                owner.GetDistanceTo(attacker, true) > 40f)

                continue;



            return attacker;

        }



        return null;

    }



    private static Unit? FindPartyDefensiveThreat(AAEmu.Game.Models.Game.Char.Character owner, AAEmu.Game.Models.Game.Char.Character companion)

    {

        var team = TeamManager.Instance.GetActiveTeamByUnit(owner.Id);

        var members = team?.Members

            .Where(m => m?.Character != null && m.Character.IsOnline && !m.Character.IsDead)

            .Select(m => m.Character)

            .ToArray() ?? [owner, companion];

        var memberIds = members.Select(m => m.Id).ToHashSet();



        foreach (var member in members)

        {

            // PvP does not use the NPC reverse-aggro index. AAEmu already records

            // real player damage for PvP assist credit, so use that authoritative

            // damage history as the defensive trigger. Merely targeting, standing

            // nearby, or enabling Bloodlust is not enough to provoke the companion.

            foreach (var playerAttacker in member.GetRecentPvpAttackers(TimeSpan.FromSeconds(10)))

            {

                if (memberIds.Contains(playerAttacker.Id) || owner.GetDistanceTo(playerAttacker, true) > 40f)

                    continue;

                return playerAttacker;

            }



            // Character.IsInAggroListOf is AAEmu's reverse aggro index: units are

            // added here when THEY gain aggro on this character.  member.AggroTable

            // is the opposite direction and missed mobs that initiated combat.

            foreach (var attackerBase in member.IsInAggroListOf.Values.ToArray())

            {

                if (attackerBase is not Unit attacker || attacker.IsDead || memberIds.Contains(attacker.Id) ||

                    owner.GetDistanceTo(attacker, true) > 40f)

                    continue;

                return attacker;

            }

        }



        // v0.16.5: immediate multi-threat handoff fallback.

        // Only units that already have positive aggro toward a protected party member

        // qualify. This is not general mob searching and cannot proactively farm neutrals.

        foreach (var candidate in WorldManager.GetAround<Unit>(owner, 40f, true))

        {

            if (candidate == null || candidate.IsDead || memberIds.Contains(candidate.Id) ||

                !owner.CanAttack(candidate))

                continue;



            foreach (var member in members)

            {

                if (candidate.AggroTable.TryGetValue(member.ObjId, out var aggro) && aggro.TotalAggro > 0)

                    return candidate;

            }

        }



        return null;

    }



    private void BeginSafeDisconnect(AAEmu.Game.Models.Game.Char.Character bot)

    {

        var runtime = BotHost.Instance.GetRuntime(bot.Id);

        if (runtime == null) return;



        var cs = runtime.CombatState;

        botManager.StopFollow(bot);

        cs.KillGoal = null;

        cs.TargetTypeFilter = null;

        cs.IsActive = false;

        cs.SetForcedState(BotCombatStateType.Idle);



        // SetForcedState deliberately does not interrupt Combat. That lets the bot

        // finish the target it was already fighting; once Combat exits, forced Idle

        // prevents a new grinding/search target from being acquired.

        if (cs.CurrentState != BotCombatStateType.Combat && cs.CurrentState != BotCombatStateType.Dueling)

        {

            cs.Target = null;

            bot.CurrentTarget = null;

            cs.TransitionTo(BotCombatStateType.Idle);

            botManager.StopImmediately(bot);

        }

    }



    private static object ItemView(Item item, bool equipmentSlot = true)

    {

        var grade=Enum.IsDefined(typeof(ItemGrade),item.Grade)?((ItemGrade)item.Grade).ToString():item.Grade.ToString();

        var slotName=equipmentSlot && item.Slot>=0&&Enum.IsDefined(typeof(EquipmentItemSlot),(byte)item.Slot)?((EquipmentItemSlot)(byte)item.Slot).ToString():$"Bag Slot {item.Slot + 1}";

        var name=LocalizationManager.Instance.Get("items","name",item.TemplateId,item.Template?.Name ?? $"Item {item.TemplateId}");

        var equipment=item as EquipItem;
        return new{slot=item.Slot,slotName,instanceId=item.Id,templateId=item.TemplateId,name,gradeId=item.Grade,grade,count=item.Count,slotType=item.SlotType.ToString(),itemType=item.GetType().Name,
            description=LocalizationManager.Instance.Get("items","description",item.TemplateId,""),
            level=item.Template?.Level,requiredLevel=item.Template?.LevelRequirement,
            binding=item.Template?.BindType.ToString(),flags=item.ItemFlags.ToString(),maxStack=item.Template?.MaxCount,
            canSendToOwner=!equipmentSlot&&BotItemTransfer.Transferable(item),canSplitForTransfer=!equipmentSlot&&BotItemTransfer.Splittable(item),
            durability=equipment==null?(int?)null:equipment.Durability,
            maxDurability=equipment==null?(int?)null:equipment.MaxDurability,
            stats=equipment==null?null:new{strength=equipment.Str,agility=equipment.Dex,stamina=equipment.Sta,intelligence=equipment.Int,spirit=equipment.Spi,
                physicalDps=item is Weapon weapon?(double?)weapon.Dps:null,
                magicDps=item is Weapon magicWeapon?(double?)magicWeapon.MDps:null,
                healingDps=item is Weapon healingWeapon?(double?)healingWeapon.HDps:null,
                defense=item is Armor armor?(int?)armor.BaseArmor:item is Weapon shield?(int?)shield.Armor:null,
                magicResistance=item is Armor resistantArmor?(int?)resistantArmor.BaseMagicResistance:null}};

    }





    private static string? ResolveCharacterName(uint characterId)

    {

        if (characterId == 0) return null;



        // NameManager is populated from the characters table during AAEmu startup and

        // therefore resolves both online and offline characters without loading them

        // into the world.

        return NameManager.Instance.GetCharacterName(characterId);

    }



    private void LoadOwners()

    {

        try

        {

            if(!File.Exists(_ownersPath)) return;

            var loaded=JsonSerializer.Deserialize<Dictionary<uint,uint>>(File.ReadAllText(_ownersPath));

            if(loaded!=null) _owners=loaded;

        }

        catch(Exception ex){Logger.Error(ex,"Failed to load PlayerBots owner mappings from {0}",_ownersPath);}

    }



    private void SaveOwners()

    {

        Directory.CreateDirectory(Path.GetDirectoryName(_ownersPath)!);

        var json=JsonSerializer.Serialize(_owners,new JsonSerializerOptions{WriteIndented=true});

        var temp=_ownersPath+".tmp";

        File.WriteAllText(temp,json);

        File.Move(temp,_ownersPath,true);

    }



    private void LoadRoles()

    {

        try

        {

            if(!File.Exists(_rolesPath)) return;

            var loaded=JsonSerializer.Deserialize<Dictionary<uint,string>>(File.ReadAllText(_rolesPath));

            if(loaded!=null)

            {

                _roles=loaded;

                foreach (var pair in loaded) CompanionRoleRegistry.Set(pair.Key, pair.Value);

            }

        }

        catch(Exception ex){Logger.Error(ex,"Failed to load PlayerBots role mappings from {0}",_rolesPath);}

    }



    private void SaveRoles()

    {

        Directory.CreateDirectory(Path.GetDirectoryName(_rolesPath)!);

        var json=JsonSerializer.Serialize(_roles,new JsonSerializerOptions{WriteIndented=true});

        var temp=_rolesPath+".tmp";

        File.WriteAllText(temp,json);

        File.Move(temp,_rolesPath,true);

    }





    private object ReadManagerAccount(uint accountId)
    {
        using var connection = MySQL.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT access_level FROM accounts WHERE account_id = @accountId";
        command.Parameters.AddWithValue("@accountId", accountId);
        // Read only: AccountManager.GetAccountDetails can create an account row, so it is not used here.
        var accountAccess = Convert.ToInt32(command.ExecuteScalar() ?? 0);
        command.CommandText = "SELECT id, name, level, access_level FROM characters WHERE account_id = @accountId AND deleted = 0 ORDER BY id";
        var characterIds = new HashSet<uint>();
        var characters = new List<object>();
        var access = accountAccess;
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var characterId = reader.GetUInt32("id");
                characterIds.Add(characterId);
                characters.Add(new { id = characterId, name = reader.GetString("name"), level = reader.GetByte("level"), online = WorldManager.Instance.GetCharacterById(characterId) != null });
                access = Math.Max(access, reader.GetInt32("access_level"));
            }
        }
        KeyValuePair<uint, uint>[] assignments;
        lock (_ownersLock) assignments = _owners.ToArray();
        // Ownership metadata is retained for history. A deleted character must not
        // be offered to its former owner as an offline, spawnable companion.
        var candidates = assignments.Where(pair => characterIds.Contains(pair.Value)).ToArray();
        var existingBots = new HashSet<uint>();
        var savedBots = new Dictionary<uint, (string Name, byte Level, int Hp, int Mp, int Experience, uint Zone)>();
        if (candidates.Length != 0)
        {
            var parameters = candidates.Select((pair, index) =>
            {
                var parameter = $"@companion{index}";
                command.Parameters.AddWithValue(parameter, pair.Key);
                return parameter;
            }).ToArray();
            command.CommandText = "SELECT id,name,level,hp,mp,experience,zone_id FROM characters WHERE deleted=0 AND id IN (" + string.Join(',', parameters) + ")";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetUInt32("id");
                existingBots.Add(id);
                savedBots[id] = (reader.GetString("name"), reader.GetByte("level"), reader.GetInt32("hp"),
                    reader.GetInt32("mp"), reader.GetInt32("experience"), reader.GetUInt32("zone_id"));
            }
        }
        var active = _botManager.GetAllBots().ToDictionary(bot => bot.Id);
        var companions = candidates.Where(pair => existingBots.Contains(pair.Key)).Select(pair =>
        {
            var spawned = active.GetValueOrDefault(pair.Key);
            var saved = savedBots[pair.Key];
            var runtime = BotHost.Instance.GetRuntime(pair.Key);
            return new
            {
                id = pair.Key,
                name = spawned?.Name ?? saved.Name,
                active = spawned != null,
                level = spawned?.Level ?? saved.Level,
                build = ReadCompanionBuild(pair.Key),
                job = AAEmu.Game.Core.Managers.Bots.BotJobRegistry.Get(pair.Key).Job,
                task = spawned == null ? "Offline" : runtime != null && BotCraftController.IsActive(runtime) ? "Crafting" :
                    runtime != null && BotGatherController.IsActive(runtime) ? "Gathering" : runtime?.CombatState.CurrentState.ToString() ?? "Unavailable",
                hp = spawned?.Hp ?? saved.Hp, maxHp = (int?)spawned?.MaxHp,
                mp = spawned?.Mp ?? saved.Mp, maxMp = (int?)spawned?.MaxMp,
                experience = spawned?.Experience ?? saved.Experience,
                zoneId = spawned?.Transform?.ZoneId ?? saved.Zone,
                dataSource = spawned == null ? "saved" : "live",
                ownerId = pair.Value,
                ownerName = ResolveCharacterName(pair.Value)
            };
        }).OrderBy(bot => bot.id).ToArray();
        return new { accountId, isAdmin = access >= 100, characters, companions };
    }

    private bool Authorized(HttpListenerContext c)

    {

        var ip=c.Request.RemoteEndPoint?.Address.MapToIPv4().ToString() ?? "";

        if(_allowed.Count>0&&!_allowed.Contains(ip)) return false;

        var supplied=c.Request.Headers["X-PlayerBots-Key"] ?? "";

        var a=Encoding.UTF8.GetBytes(supplied); var b=Encoding.UTF8.GetBytes(_apiKey);

        return a.Length==b.Length && CryptographicOperations.FixedTimeEquals(a,b);

    }

    private static async Task<T?> Body<T>(HttpListenerContext c){try{return await JsonSerializer.DeserializeAsync<T>(c.Request.InputStream,new JsonSerializerOptions{PropertyNameCaseInsensitive=true});}catch{return default;}}

    private static async Task Reply(HttpListenerContext c,int status,object value){var bytes=JsonSerializer.SerializeToUtf8Bytes(value);c.Response.StatusCode=status;c.Response.ContentType="application/json";c.Response.ContentLength64=bytes.Length;await c.Response.OutputStream.WriteAsync(bytes);c.Response.Close();}

    private static bool TryEngineKind(string? value,out BotEngineKind kind)

    {

        switch((value ?? "").Trim().ToLowerInvariant())

        {

            case "co": case "combat": kind=BotEngineKind.Combat; return true;

            case "nc": case "noncombat": case "non-combat": kind=BotEngineKind.NonCombat; return true;

            case "de": case "dead": kind=BotEngineKind.Dead; return true;

            default: kind=default; return false;

        }

    }

    private static bool BoolEnv(string n,bool d)=>bool.TryParse(Environment.GetEnvironmentVariable(n),out var v)?v:d;

    private static int IntEnv(string n,int d,int min,int max)=>int.TryParse(Environment.GetEnvironmentVariable(n),out var v)?Math.Clamp(v,min,max):d;

    private sealed record MoveRequest(float x,float y,float z,bool run=true);

    private sealed record FollowRequest(uint targetId,float distance=2f);

    private sealed record StateRequest(string state,int? killGoal=null);

    private sealed record RotationRequest(string rotationId);

    private sealed record StrategyRequest(string engine,string operation,string[]? names);

    private sealed record EquipRequest(ulong instanceId,int? slot=null);

    private sealed record UnequipRequest(int slot);

    private sealed record OwnerRequest(uint ownerId);

    private sealed record SkillPriorityRequest(uint[] skillIds);
    private sealed record RoleRequest(string role);

    public void Dispose(){_cts.Cancel();_listener?.Close();_cts.Dispose();}

}
