using System.Collections.Concurrent;
using System.Numerics;
using AAEmu.Game.Bots.Host;
using AAEmu.Game.Bots.Diagnostics;
using AAEmu.Game.Bots.Navigation;
using AAEmu.Game.Bots.Questing;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.Skills.Effects;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.World;

namespace AAEmu.Game.Bots.Gathering;

public static class BotGatherController
{
    private static readonly NLog.Logger Logger=NLog.LogManager.GetCurrentClassLogger();
    public const float SearchRadius=500;
    public const float MaximumTravel=15000;
    public const float HostileClearance=20;
    private sealed class Attempt(BotRuntime runtime,BotInteractionPlan plan,uint ownerId)
    {
        public Vector3 StartPosition=runtime.Bot.Transform.World.Position;
        public DateTime StartedUtc=DateTime.UtcNow;
        public BotRuntime Runtime=runtime;
        public BotInteractionPlan Plan=plan;
        public Vector3 ApproachPosition=plan.Position;
        public uint OwnerId=ownerId;
        public string State="walking", Detail="Walking to the selected resource.";
        public DateTime Deadline=DateTime.UtcNow.AddMinutes(15), SettleAt, NextAvoidanceCheck, NextActivityAt;
        public string BeforeBag="";
        public int BeforeLabor, BeforeExperience;
        public object CastTask;
        public bool Active=>State is "walking" or "casting" or "ready";
    }
    private static readonly ConcurrentDictionary<uint,Attempt> Attempts=new();
    public static bool IsActive(BotRuntime runtime)=>Attempts.TryGetValue(runtime.Bot.Id,out var attempt)&&ReferenceEquals(attempt.Runtime,runtime)&&attempt.Active;
    // Normal wild harvesting skills can carry CrimePoint metadata for the SAME action
    // used on somebody else's planted resource. Classify the interaction here; enforce
    // unowned/system resources in Allowed at scan, preview and execution instead.
    public static bool PlantSkill(SkillTemplate skill)
    {
        if(skill==null || skill.Effects.Count==0)return false;
        return skill.Effects.All(e=>e.Template is InteractionEffect i && i.WorldInteraction is
            WorldInteractionType.Harvest or WorldInteractionType.PlantCollect or WorldInteractionType.FiberCollect or
            WorldInteractionType.CropHarvest or WorldInteractionType.CerealHarvest or WorldInteractionType.FruitPick or
            WorldInteractionType.DyeingredientCollect or WorldInteractionType.SeedCollect or WorldInteractionType.SpiceCollect);
    }
    public static bool MiningSkill(SkillTemplate skill)
    {
        if(skill==null || skill.Effects.Count==0)return false;
        return skill.Effects.All(e=>e.Template is InteractionEffect i && i.WorldInteraction is
            WorldInteractionType.OreMine or WorldInteractionType.RockMine or WorldInteractionType.DigMine or WorldInteractionType.MedicalingredientMine);
    }
    public static bool TreeSkill(SkillTemplate skill)=>skill!=null && skill.Effects.Count>0 &&
        skill.Effects.All(e=>e.Template is InteractionEffect i && i.WorldInteraction is WorldInteractionType.Cutdown or WorldInteractionType.TreeproductCollect);
    public static bool OtherSkill(SkillTemplate skill)=>skill!=null && skill.Effects.Count>0 &&
        skill.Effects.All(e=>e.Template is InteractionEffect i && i.WorldInteraction is WorldInteractionType.CrystalCollect or WorldInteractionType.SoilCollect or WorldInteractionType.Mow);
    public static bool AnimalSkill(SkillTemplate skill)=>skill!=null && skill.Effects.Count>0 &&
        skill.Effects.All(e=>e.Template is InteractionEffect i && i.WorldInteraction is WorldInteractionType.Shearing or WorldInteractionType.DairyCollect or WorldInteractionType.SkinOff or WorldInteractionType.Butcher);
    public static string ResourceKind(SkillTemplate skill)=>MiningSkill(skill)?"ore":TreeSkill(skill)?"tree":PlantSkill(skill)?"plant":AnimalSkill(skill)?"animal":OtherSkill(skill)?"other":"";
    private static uint Group(Doodad d)=>AAEmu.Game.Core.Managers.World.ZoneManager.Instance.GetZoneByKey(d.Transform.ZoneId)?.GroupId??0;
    public static object View(BotRuntime runtime,bool scan=false,int radius=100,string kind="all",uint zoneId=0,bool area=false)
    {
        var bot=runtime.Bot;
        radius=radius is 100 or 250 or 500?radius:100;
        kind=kind is "ore" or "plant" or "tree" or "animal" or "other"?kind:"all";
        var currentZone=AAEmu.Game.Core.Managers.World.ZoneManager.Instance.GetZoneByKey(bot.Transform.ZoneId)?.GroupId??0;
        zoneId=zoneId==0||!BotResourceRegions.SameContinent(currentZone,zoneId)?currentZone:zoneId;
        var candidates=new List<object>();var truncated=false;
        var all=scan&&BotJobRegistry.IsFarmer(bot.Id)&&bot.ParentWorld!=null?bot.ParentWorld.GetAllDoodads().Where(d=>Allowed(bot,d)).ToArray():[];
        var zones=AAEmu.Game.Core.Managers.World.ZoneManager.Instance.GetAllZoneGroups()
            .Where(z=>BotResourceRegions.SameContinent(currentZone,z.Id))
            .OrderBy(z=>BotResourceRegions.Name(z.Id)).Select(z=>new{id=z.Id,name=BotResourceRegions.Name(z.Id)}).ToArray();
        if(scan)
        foreach(var d in all.Where(d=>Group(d)==zoneId && (area||bot.GetDistanceTo(d,true)<=radius)).OrderBy(d=>bot.GetDistanceTo(d,true)).ThenBy(d=>d.ObjId))
        foreach(var skillId in BotQuestInteractions.ActionSkills(d))
        {
            var skill=SkillManager.Instance.GetSkillTemplate(skillId);var category=ResourceKind(skill);
            if(category==""||(kind!="all"&&kind!=category))continue;
            var plan=BotQuestInteractions.ForDoodad(bot,d,skillId);if(!plan.Available)continue;
            if(candidates.Count>=512){truncated=true;goto SearchFinished;}
            var position=d.Transform.World.Position;
            candidates.Add(new{resourceId=d.ObjId,templateId=d.TemplateId,kind=category,
                name=LocalizationManager.Instance.Get("doodad_almighties","name",d.TemplateId,string.IsNullOrWhiteSpace(d.Name)?$"{category} resource {d.TemplateId}":d.Name),
                skillId,skillName=LocalizationManager.Instance.Get("skills","name",skillId,$"Gather {skillId}"),
                x=position.X,y=position.Y,distance=Math.Round(bot.GetDistanceTo(d,true),1),laborCost=skill!.ConsumeLaborPower,manaCost=skill.ManaCost,range=plan.Range,
                reachableDistance=bot.GetDistanceTo(d,true)<=MaximumTravel});
        }
        SearchFinished:
        Attempts.TryGetValue(bot.Id,out var attempt);
        if(attempt!=null&&!ReferenceEquals(attempt.Runtime,runtime)){Attempts.TryRemove(bot.Id,out _);attempt=null;}
        var selectedZone=AAEmu.Game.Core.Managers.World.ZoneManager.Instance.GetZoneGroupById(zoneId);
        return new{ok=true,radius,kind="resources",filter=kind,zoneId,currentZone,continent=BotResourceRegions.Continent(currentZone),zones,scan,area,truncated,
            bounds=selectedZone==null?null:new{x=selectedZone.X,y=selectedZone.Y,width=selectedZone.Width,height=selectedZone.Hight},
            reportedUtc=DateTime.UtcNow,
            journey=attempt==null?null:new{startedUtc=attempt.StartedUtc,start=new{x=attempt.StartPosition.X,y=attempt.StartPosition.Y},destination=new{x=attempt.Plan.Position.X,y=attempt.Plan.Position.Y}},
            botPosition=new{x=bot.Transform.World.Position.X,y=bot.Transform.World.Position.Y,z=bot.Transform.World.Position.Z,zoneId=currentZone,isDead=bot.IsDead},maximumTravel=MaximumTravel,hostileClearance=HostileClearance,
            offlineMiningEnabled=AAEmu.Game.Models.Game.Bots.BotConfig.Instance.OfflineMiningEnabled,labor=bot.LaborPower,laborLimit=BotLaborRegistry.Limit,
            laborOnlineAmount=BotLaborRegistry.OnlineAmount,laborOfflineAmount=BotLaborRegistry.OfflineAmount,
            laborTickMinutes=BotLaborRegistry.RecoveryInterval(BotJobRegistry.IsFarmer(bot.Id),true),laborOfflineTickMinutes=BotLaborRegistry.RecoveryInterval(BotJobRegistry.IsFarmer(bot.Id),false),
            freeSlots=bot.Inventory?.Bag?.FreeSlotCount??0,state=attempt?.State??"idle",detail=attempt?.Detail??"No gathering attempt started.",active=attempt?.Active??false,candidates};
    }
    public static float SegmentDistance(Vector3 point,Vector3 start,Vector3 end)
    {
        var delta=end-start;var length=delta.LengthSquared();
        return Vector3.Distance(point,start+delta*(length<=0?0:Math.Clamp(Vector3.Dot(point-start,delta)/length,0,1)));
    }
    internal static float ThreatClearance(Character bot,AAEmu.Game.Models.Game.Units.Unit unit)
    {
        if(unit.IsDead||unit.ObjId==bot.ObjId)return 0;
        var hostile=unit.GetRelationStateTo(bot)==AAEmu.Game.Models.Game.Faction.RelationState.Hostile;
        if(unit is AAEmu.Game.Models.Game.NPChar.Npc npc)
        {
            var attacking=npc.CurrentAggroTarget?.ObjId==bot.ObjId ||
                npc.AggroTable.TryGetValue(bot.ObjId,out var aggro)&&aggro.TotalAggro>0;
            return BotGatherThreatPolicy.Clearance(hostile,npc.Template?.Aggression==true,attacking,npc.Template?.AttackStartRangeScale??0);
        }
        if(unit is Character player)
            return hostile||player.IsActivelyHostile(bot)||bot.IsActivelyHostile(player)?HostileClearance:0;
        return 0;
    }
    internal static string Danger(BotRuntime runtime,Vector3? destination=null,bool wholeRoute=false,float minimumClearance=0)
    {
        var bot=runtime.Bot;var start=bot.Transform.World.Position;
        var points=new List<Vector3>{start};
        if(wholeRoute){if(runtime.MovementState.Destination is Vector3 next)points.Add(next);points.AddRange(runtime.MovementState.TravelWaypoints);}
        else
        {
            var remaining=new List<Vector3>();
            if(runtime.MovementState.Destination is Vector3 next)remaining.Add(next);
            remaining.AddRange(runtime.MovementState.TravelWaypoints);
            points=BotGatherDetour.LookAhead(start,remaining,60).ToList();
        }
        if(destination is Vector3 end)points.Add(end);
        var units=wholeRoute?bot.ParentWorld.GetAllNpcs().Cast<AAEmu.Game.Models.Game.Units.Unit>().Concat(bot.ParentWorld.GetAllCharacters()):
            AAEmu.Game.Core.Managers.World.WorldManager.GetAround<AAEmu.Game.Models.Game.Units.Unit>(bot,500,true);
        foreach(var unit in units)
        {
            if(unit.Transform?.World==null||!ReferenceEquals(unit.ParentWorld,bot.ParentWorld) )continue;
            var clearance=ThreatClearance(bot,unit);if(clearance<=0)continue;
            clearance=Math.Max(clearance,minimumClearance);
            var position=unit.Transform.World.Position;
            if(Vector3.Distance(start,position)<=clearance||points.Zip(points.Skip(1)).Any(p=>SegmentDistance(position,p.First,p.Second)<=clearance))
                return $"Gathering cancelled: aggressive or attacking {unit.Name} [{unit.ObjId}] is within {clearance:0.#}m of the Farmer or its route. Choose another resource after the danger clears.";
        }
        return null;
    }
    private static BotGatherThreat[] Threats(Character bot)=>bot.ParentWorld.GetAllNpcs()
        .Cast<AAEmu.Game.Models.Game.Units.Unit>().Concat(bot.ParentWorld.GetAllCharacters())
        .Where(u=>u.Transform?.World!=null && ReferenceEquals(u.ParentWorld,bot.ParentWorld))
        .Select(u=>new BotGatherThreat(u.Transform.World.Position,ThreatClearance(bot,u)))
        .Where(t=>t.Clearance>0 && float.IsFinite(t.Position.X+t.Position.Y+t.Position.Z)).ToArray();

    private static bool TryDetour(Attempt attempt)
    {
        var runtime=attempt.Runtime;var bot=runtime.Bot;var movement=runtime.MovementState;
        var points=new List<Vector3>();
        if(movement.Destination is Vector3 next)points.Add(next);
        points.AddRange(movement.TravelWaypoints);
        var route=new BotTravelRoute(movement.TravelMode,points,0,"live_threat_replan")
            {NativeSurface=movement.NativeRouteSurface!=null};
        var avoided=BotGatherDetour.Plan(bot.Transform.World.Position,route,Threats(bot),
            bot.ParentWorld.GetHeight,bot.ParentWorld.IsWater);
        if(avoided==null || ReferenceEquals(avoided,route) || !BotGatherApproach.DryRoute(bot.Transform.World.Position,avoided,
            bot.ParentWorld.GetHeight,bot.ParentWorld.IsWater))
        {
            BotCompanionActivity.Record(bot.Id,"gather_hostile_detour_unavailable",$"BOT id={bot.Id} ev=gather_hostile_detour_unavailable resource={attempt.Plan.DoodadObjectId} start={bot.Transform.World.Position} mode={route.Mode} native={route.NativeSurface} detail={(avoided==null ? "no_supported_detour" : ReferenceEquals(avoided,route) ? "minimum_clearance_route_unchanged" : "wet_or_invalid_route")}",bot.Transform.World.Position,attempt.Plan.DoodadObjectId);
            Logger.Info($"BOT id={bot.Id} ev=gather_hostile_detour_unavailable resource={attempt.Plan.DoodadObjectId} start={bot.Transform.World.Position} mode={route.Mode} native={route.NativeSurface} detail={(avoided==null ? "no_supported_detour" : ReferenceEquals(avoided,route) ? "minimum_clearance_route_unchanged" : "wet_or_invalid_route")}");
            return false;
        }
        if(!BotManager.ApplyTravelRoute(bot,movement,attempt.ApproachPosition,true,
            BotTravelIntent.QuestObjective,BotMovementOwner.External,avoided))return false;
        attempt.Detail="Walking around a hostile toward the selected resource.";
        BotCompanionActivity.Record(bot.Id,"gather_hostile_detour",$"BOT id={bot.Id} ev=gather_hostile_detour resource={attempt.Plan.DoodadObjectId} waypoints={avoided.Waypoints.Count} detail={avoided.Detail}",bot.Transform.World.Position,attempt.Plan.DoodadObjectId);
        Logger.Info($"BOT id={bot.Id} ev=gather_hostile_detour resource={attempt.Plan.DoodadObjectId} waypoints={avoided.Waypoints.Count} detail={avoided.Detail}");
        return true;
    }

    private static bool Allowed(Character bot,Doodad d)=>d.OwnerId==0 && d.OwnerDbId==0 && d.ParentObjId==0 &&
        (d.OwnerType==DoodadOwnerType.System || (byte)d.OwnerType==0) &&
        BotQuestInteractions.AllowedDoodad(bot,d) && d.Transform!=null && bot.Transform!=null &&
        d.Transform.InstanceId==bot.Transform.InstanceId &&
        float.IsFinite(d.Transform.World.Position.X)&&float.IsFinite(d.Transform.World.Position.Y)&&float.IsFinite(d.Transform.World.Position.Z);
    internal static BotInteractionPlan Resolve(BotRuntime runtime,uint resourceId,uint skillId)
    {
        var bot=runtime.Bot;
        var d=bot.ParentWorld?.GetDoodad(resourceId);
        if(d==null || !Allowed(bot,d) || bot.GetDistanceTo(d,true)>MaximumTravel)return BotInteractionPlan.Blocked("Resource must be unowned, live and within 15km in the same world instance.");
        var botZone=AAEmu.Game.Core.Managers.World.ZoneManager.Instance.GetZoneByKey(bot.Transform.ZoneId)?.GroupId??0;
        if(!BotResourceRegions.SameContinent(botZone,Group(d)))return BotInteractionPlan.Blocked("Choose a resource on the Farmer’s current continent. Cross-continent gathering is not allowed.");
        if(ResourceKind(SkillManager.Instance.GetSkillTemplate(skillId))=="")return BotInteractionPlan.Blocked("Only supported native resource-gathering actions are allowed.");
        return BotQuestInteractions.ForDoodad(bot,d,skillId);
    }
    internal static string Check(BotRuntime runtime,uint ownerId)
    {
        var bot=runtime.Bot;
        if(runtime.Retired || !BotJobRegistry.IsFarmer(bot.Id) || bot.IsDead || bot.ParentWorld==null || bot.Transform?.World==null)return "A living spawned Farmer is required.";
        if(BotDrivers.For(bot).Owns(bot))return "Detach the client-controlled actor before server Farmer gathering.";
        if(!AAEmu.Game.Bots.Remote.PlayerBotsRemoteService.TryGetCompanionOwner(bot.Id,out var actual) || actual!=ownerId)return "Companion ownership changed.";
        // Recheck the admin policy on every step, including an in-progress attempt.
        if (!AAEmu.Game.Models.Game.Bots.BotConfig.Instance.OfflineMiningEnabled &&
            AAEmu.Game.Core.Managers.World.WorldManager.Instance.GetCharacterById(ownerId)?.IsOnline != true)
            return "Offline mining is disabled by the administrator. The assigned owner must be online.";
        if(bot.IsInBattle || bot.IsInAggroListOf.Values.Any(x=>x is AAEmu.Game.Models.Game.Units.Unit u && !u.IsDead))return "Gathering stopped because the Farmer is threatened.";
        var danger= Danger(runtime);
        if(Attempts.TryGetValue(bot.Id,out var active) && ReferenceEquals(active.Runtime,runtime) &&
            active.State=="walking" && OwnsMovement(active) &&
            (danger!=null || DateTime.UtcNow>=active.NextAvoidanceCheck))
        {
            active.NextAvoidanceCheck=DateTime.UtcNow.AddSeconds(1);
            if((danger!=null || Danger(runtime,minimumClearance:20)!=null) && TryDetour(active))return null;
        }
        return danger;
    }
    internal static string Start(BotRuntime runtime,BotInteractionPlan plan,uint ownerId)
    {
        if(Attempts.TryGetValue(runtime.Bot.Id,out var existing)&&ReferenceEquals(existing.Runtime,runtime)&&existing.Active)return "A gathering attempt is already active.";
        if(AAEmu.Game.Bots.Crafting.BotCraftController.IsActive(runtime))return "Stop crafting before gathering.";
        var error=Check(runtime,ownerId);if(error!=null)return error;
        var bot=runtime.Bot;
        var skill=SkillManager.Instance.GetSkillTemplate(plan.SkillId);
        if(bot.SkillTask!=null || bot.ActivePlotState!=null)return "The Farmer is already performing an action.";
        if(bot.Inventory?.Bag==null || bot.Inventory.Bag.FreeSlotCount<1)return "Free an inventory slot before gathering.";
        if(skill==null)return "The gathering skill is unavailable. Search and preview again.";
        if(bot.LaborPower<skill.ConsumeLaborPower)return $"Insufficient labor: {bot.LaborPower} available; {skill.ConsumeLaborPower} required. No gathering started.";
        if(bot.Mp<skill.ManaCost)return $"Insufficient mana: {bot.Mp} available; {skill.ManaCost} required. No gathering started.";
        var attempt=new Attempt(runtime,plan,ownerId);
        BotCompanionActivity.Record(bot.Id,"gather_requested",$"Skill {plan.SkillId}; range {plan.Range:F1}m; resource position: {plan.Position}",bot.Transform.World.Position,plan.DoodadObjectId);
        BotTravelRoute route=null;
        var threats=Threats(bot);
        if(!BotQuestInteractions.InRange(bot.Transform.World.Position,plan.Position,plan.Range))
        {
            var candidates=0;
            var lastFailure="none";
            var budget = new BotGatherSearchBudget();
            float Ground(float x,float y) { budget.Check(); return bot.ParentWorld.GetHeight(x,y); }
            bool Water(Vector3 point) { budget.Check(); return bot.ParentWorld.IsWater(point); }
            route=budget.Run(()=>BotGatherApproach.Plan(bot.Transform.World.Position,plan.Position,plan.Range,
                Ground,point=>
                {
                    candidates++;
                    // Gathering may be beyond the generic planner's 400m native
                    // surface cutoff. Try connected dry native edges first.
                    var candidate=BotBaiRoute.Plan(bot,point,checkBudget:budget.Check);
                    budget.Check();
                    candidate ??= BotTravelRoutePlanner.Plan(bot,point,BotTravelIntent.QuestObjective,skipNativeSurface:true,checkBudget:budget.Check);
                    budget.Check();
                    lastFailure=candidate.Detail;
                    var avoided=BotGatherDetour.Plan(bot.Transform.World.Position,candidate,threats,Ground,Water);
                    if(avoided==null)lastFailure="no_safe_hostile_detour";
                    return avoided;
                },candidate=>
                {
                    var dry=BotGatherApproach.DryRoute(bot.Transform.World.Position,candidate,Ground,Water);
                    if(!dry)lastFailure="route_crosses_water_or_invalid_surface";
                    return dry;
                },budget.Check),out var timedOut);
            if(timedOut)
            {
                BotCompanionActivity.Record(bot.Id,"gather_search_timeout",$"BOT id={bot.Id} ev=gather_search_timeout resource={plan.DoodadObjectId} candidates={candidates} last_failure={lastFailure}",bot.Transform.World.Position,plan.DoodadObjectId);
                Logger.Warn($"BOT id={bot.Id} ev=gather_search_timeout resource={plan.DoodadObjectId} candidates={candidates} last_failure={lastFailure}");
                return "Gathering route search took too long. No gathering started. Move the Farmer closer or choose another resource.";
            }
            if(route==null)
            {
                BotCompanionActivity.Record(bot.Id,"gather_approach_unavailable",$"BOT id={bot.Id} ev=gather_approach_unavailable resource={plan.DoodadObjectId} range={plan.Range} candidates={candidates} start={bot.Transform.World.Position} resource_pos={plan.Position} detail={lastFailure}",bot.Transform.World.Position,plan.DoodadObjectId);
                Logger.Warn($"BOT id={bot.Id} ev=gather_approach_unavailable resource={plan.DoodadObjectId} range={plan.Range} candidates={candidates} start={bot.Transform.World.Position} resource_pos={plan.Position} detail={lastFailure}");
                return lastFailure=="no_safe_hostile_detour"
                    ? "No safe gathering route around nearby hostiles was found. Move the Farmer or choose another resource."
                    : "AAEmu could not find a supported standing point within gathering range of this resource.";
            }
            attempt.ApproachPosition=route.Waypoints[^1];
        }
        else attempt.State="ready";
        BotManager.Instance.StopFollow(bot);
        if(route!=null && !BotManager.ApplyTravelRoute(bot,runtime.MovementState,attempt.ApproachPosition,true,
            BotTravelIntent.QuestObjective,BotMovementOwner.External,route))
            return "AAEmu could not start the route to this resource.";
        var danger=Danger(runtime,plan.Position,true);
        if(danger!=null){BotManager.Instance.StopImmediately(bot);BotCompanionActivity.Record(bot.Id,"gather_cancelled",danger,bot.Transform.World.Position,plan.DoodadObjectId);return danger;}
        Attempts[bot.Id]=attempt;
        BotCompanionActivity.Record(bot.Id,"gather_"+attempt.State,attempt.Detail,bot.Transform.World.Position,plan.DoodadObjectId);
        Step(runtime);
        return attempt.State is "failed" or "cancelled"?attempt.Detail:null;
    }
    public static void Stop(BotRuntime runtime,string reason="Gathering cancelled.")
    {
        if(!Attempts.TryGetValue(runtime.Bot.Id,out var a)||!ReferenceEquals(a.Runtime,runtime)||!a.Active)return;
        if(a.State=="casting"&&a.CastTask!=null&&ReferenceEquals(runtime.Bot.SkillTask,a.CastTask))runtime.Bot.InterruptSkills();
        if(a.State=="walking"&&OwnsMovement(a))BotManager.Instance.StopImmediately(runtime.Bot);
        a.State="cancelled";a.Detail=reason;
        BotCompanionActivity.Record(runtime.Bot.Id,"gather_cancelled",$"BOT id={runtime.Bot.Id} ev=gather_cancelled resource={a.Plan.DoodadObjectId} detail={reason}",runtime.Bot.Transform.World.Position,a.Plan.DoodadObjectId);
        Logger.Info($"BOT id={runtime.Bot.Id} ev=gather_cancelled resource={a.Plan.DoodadObjectId} detail={reason}");
    }
    private static bool OwnsMovement(Attempt a)=>Vector3.Distance(a.Runtime.MovementState.TravelDestination??a.Runtime.MovementState.Destination??new(float.MaxValue),a.ApproachPosition)<.5f;
    private static string Bag(Character bot)=>string.Join("|",bot.Inventory.Bag.Items.Where(i=>i!=null).OrderBy(i=>i.Id).Select(i=>$"{i.Id}:{i.TemplateId}:{i.Count}"));
    public static void Step(BotRuntime runtime)
    {
        if(!Attempts.TryGetValue(runtime.Bot.Id,out var a)||!ReferenceEquals(a.Runtime,runtime)||a.State is not ("walking" or "casting" or "ready"))return;
        var bot=runtime.Bot;
        var error=Check(runtime,a.OwnerId);if(error!=null){Stop(runtime,error);return;}
        if(DateTime.UtcNow>a.Deadline){Stop(runtime,"Gathering timed out; no retry was performed.");return;}
        if(a.State=="casting")
        {
            if(bot.SkillTask!=null || bot.ActivePlotState!=null || DateTime.UtcNow<a.SettleAt)return;
            var changed=Bag(bot)!=a.BeforeBag;
            a.State=changed?"completed":"finished";
            a.Detail=changed?$"Native gathering finished; inventory changed. Labor spent: {a.BeforeLabor-bot.LaborPower}; XP gained: {bot.Experience-a.BeforeExperience}.":"Native action finished, but no inventory reward was observed. No retry was performed.";
            BotCompanionActivity.Record(bot.Id,"gather_"+a.State,a.Detail,bot.Transform.World.Position,a.Plan.DoodadObjectId);
            return;
        }
        if(a.State=="walking" && DateTime.UtcNow>=a.NextActivityAt)
        {
            a.NextActivityAt=DateTime.UtcNow.AddSeconds(10);
            BotCompanionActivity.Record(bot.Id,"gather_progress",$"Resource distance: {Vector3.Distance(bot.Transform.World.Position,a.Plan.Position):F1}m; remaining waypoints: {runtime.MovementState.TravelWaypointCount}; route mode: {runtime.MovementState.TravelMode}",bot.Transform.World.Position,a.Plan.DoodadObjectId);
        }
        var current=Resolve(runtime,a.Plan.DoodadObjectId,a.Plan.SkillId);
        if(!current.Available || current.Phase!=a.Plan.Phase || Vector3.Distance(current.Position,a.Plan.Position)>.5f){Stop(runtime,"The resource disappeared or changed phase. Search again.");return;}
        if(!BotQuestInteractions.InRange(bot.Transform.World.Position,a.Plan.Position,a.Plan.Range))
        {if(!OwnsMovement(a))Stop(runtime,"Another command interrupted gathering movement.");return;}
        BotManager.Instance.StopImmediately(bot);
        var skill=SkillManager.Instance.GetSkillTemplate(a.Plan.SkillId);
        if(bot.Inventory.Bag.FreeSlotCount<1 || skill==null || bot.LaborPower<skill.ConsumeLaborPower || bot.Mp<skill.ManaCost){Stop(runtime,"Inventory, labor or mana changed before gathering.");return;}
        a.BeforeBag=Bag(bot);a.BeforeLabor=bot.LaborPower;a.BeforeExperience=bot.Experience;
        var native=BotQuestInteractions.Execute(bot,a.Plan);
        if(!native.Started){a.State="failed";a.Detail=native.Reason;BotCompanionActivity.Record(bot.Id,"gather_failed",a.Detail,bot.Transform.World.Position,a.Plan.DoodadObjectId);return;}
        a.CastTask=bot.SkillTask;a.State="casting";a.Detail="Native gathering action started; waiting for completion.";
        BotCompanionActivity.Record(bot.Id,"gather_casting",a.Detail,bot.Transform.World.Position,a.Plan.DoodadObjectId);
        a.SettleAt=DateTime.UtcNow.AddMilliseconds(a.Plan.DurationMs+2000);a.Deadline=a.SettleAt.AddSeconds(30);
    }
}
