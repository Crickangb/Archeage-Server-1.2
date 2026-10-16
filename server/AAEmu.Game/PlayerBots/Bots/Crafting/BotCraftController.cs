using System.Collections.Concurrent;
using System.Security.Cryptography;
using AAEmu.Game.Bots.Gathering;
using AAEmu.Game.Bots.Host;
using AAEmu.Game.Bots.Remote;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Crafts;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Static;

namespace AAEmu.Game.Bots.Crafting;

// Confirm a bounded batch; invoke one native craft at a time. No synthetic rewards or labor deductions.
public static class BotCraftController
{
    public const uint IronIngotRecipe = 49;
    public const int MaximumBatch = 100;
    // Only first-stage raw-material processing; no advanced alloys, packs or equipment.
    private static readonly Dictionary<uint,(uint Station,uint Material,int Amount,uint Product)> Basics=new()
    {
        [49]=(557u,8022u,3,8318u),
        [80]=(557u,3411u,3,8320u),
        [82]=(557u,8023u,3,17774u),
        [84]=(557u,8027u,3,17775u),
        [81]=(557u,2201u,3,3332u),
        [2834]=(557u,1386u,3,17776u),
        [83]=(558u,8017u,3,8337u),
        [115]=(559u,8008u,3,8343u),
        [122]=(560u,8007u,3,16327u),
        [2674]=(561u,8053u,3,8256u),
        [2831]=(561u,8357u,10,8256u),
    };
    internal static bool AccountAllowed(BotConfig config,uint accountId)=>config.CraftingEnabled&&
        (config.CraftingAccountPermissions?.TryGetValue(accountId,out var allowed)==true?allowed:config.CraftingDefaultAllowed);
    private static string Policy(Character owner)
    {
        if(!BotConfig.Instance.CraftingEnabled)return "Farmer crafting is disabled by the administrator.";
        if(owner==null||!owner.IsOnline)return "Log in as the assigned owner before crafting.";
        return AccountAllowed(BotConfig.Instance,owner.AccountId)?null:"Farmer crafting is disabled for this account by the administrator.";
    }
    private static string ProductName(Craft recipe)=>recipe==null?"Material":ItemName(recipe.CraftProducts[0].ItemId);
    private static string StationName(Craft recipe)=>LocalizationManager.Instance.Get("doodad_almighties","name",recipe?.ReqDoodadId??0,"Workbench");

    private sealed record Pending(BotRuntime Runtime,uint OwnerId,uint StationId,int Quantity,string Bag,string Token,DateTime Expires,uint RecipeId);
    private sealed class Attempt(BotRuntime runtime,uint ownerId,uint stationId,int products,int labor,int quantity)
    {
        public BotRuntime Runtime=runtime;
        public uint OwnerId=ownerId,StationId=stationId,RecipeId=IronIngotRecipe;
        public int BeforeProducts=products,BeforeLabor=labor,Quantity=quantity,Completed;
        public DateTime NextAt,QuietSince;
        public object CastTask;
        public DateTime Deadline=DateTime.UtcNow.AddMinutes(2),SettleAt=DateTime.UtcNow.AddSeconds(2);
        public string State="crafting",Detail="Native crafting started; waiting for the result.";
        public bool Active=>State is "crafting" or "waiting";
    }
    private static readonly ConcurrentDictionary<uint,Pending> Previews=new();
    private static readonly ConcurrentDictionary<uint,Attempt> Attempts=new();
    public static bool IsActive(BotRuntime runtime)=>Attempts.TryGetValue(runtime.Bot.Id,out var a)&&ReferenceEquals(a.Runtime,runtime)&&a.Active;
    private static Craft Recipe(uint id=IronIngotRecipe){if(!Basics.ContainsKey(id))return null;try{return CraftManager.Instance.GetCraftById(id);}catch(KeyNotFoundException){return null;}}
    private static string ItemName(uint id)=>LocalizationManager.Instance.Get("items","name",id,$"Item {id}");
    private static int Count(Character bot,uint itemId)=>bot.Inventory?.Bag?.Items.Where(i=>i!=null&&i.TemplateId==itemId).Sum(i=>i.Count)??0;
    private static string Bag(Character bot)=>string.Join("|",bot.Inventory.Bag.Items.Where(i=>i!=null).OrderBy(i=>i.Id).Select(i=>$"{i.Id}:{i.TemplateId}:{i.Count}"));
    internal static bool StationAllowed(Character bot,Doodad station,Craft recipe)=>station!=null&&recipe!=null&&
        station.TemplateId==recipe.ReqDoodadId&&station.Despawn==DateTime.MinValue&&station.OwnerId==0&&station.OwnerDbId==0&&station.ParentObjId==0&&
        (station.OwnerType==DoodadOwnerType.System||(byte)station.OwnerType==0)&&station.FuncPermission==DoodadFuncPermission.Any&&
        bot.Transform?.World!=null&&station.Transform?.World!=null&&bot.ParentWorld!=null&&ReferenceEquals(bot.ParentWorld,station.ParentWorld)&&
        bot.Transform.InstanceId==station.Transform.InstanceId&&float.IsFinite(station.Transform.World.Position.X)&&
        float.IsFinite(station.Transform.World.Position.Y)&&float.IsFinite(station.Transform.World.Position.Z);
    private static float Range(Craft recipe)=>Math.Min(4f,SkillManager.Instance.GetSkillTemplate(recipe.SkillId)?.MaxRange??0);
    private static string Check(BotRuntime runtime,uint ownerId)
    {
        var bot=runtime.Bot;
        if(runtime.Retired||!BotJobRegistry.IsFarmer(bot.Id)||bot.IsDead||bot.ParentWorld==null||bot.Transform?.World==null)return "A living spawned Farmer is required.";
        if(BotDrivers.For(bot).Owns(bot))return "Detach the client-controlled actor before server crafting.";
        if(!PlayerBotsRemoteService.TryGetCompanionOwner(bot.Id,out var actual)||actual!=ownerId)return "Companion ownership changed.";
        // Crafting has its own online-owner guard; OfflineMiningEnabled remains a gathering policy.
        var policy=Policy(WorldManager.Instance.GetCharacterById(ownerId));if(policy!=null)return policy;
        if(bot.IsInBattle||bot.IsInAggroListOf.Values.Any(x=>x is AAEmu.Game.Models.Game.Units.Unit u&&!u.IsDead))return "Crafting stopped because the Farmer is threatened.";
        return BotGatherController.Danger(runtime)?.Replace("Gathering","Crafting");
    }
    internal static string Requirements(BotRuntime runtime,Craft recipe,uint stationId,bool starting)
    {
        var bot=runtime.Bot;
        // IsPack is a category flag. Validate the actual basic recipe/output instead.
        if(recipe==null||!Basics.TryGetValue(recipe.Id,out var expected)||recipe.NeedBind||recipe.ToolId!=0||recipe.ActabilityLimit!=0||recipe.ResultsInBackpack||
            recipe.ReqDoodadId!=expected.Station||recipe.CraftProducts.Count!=1||recipe.CraftMaterials.Count!=1||
            recipe.CraftProducts[0].ItemId!=expected.Product||recipe.CraftProducts[0].Amount!=1||
            recipe.CraftMaterials[0].ItemId!=expected.Material||recipe.CraftMaterials[0].Amount!=expected.Amount)
            return "This basic processing recipe is unavailable in the server data.";
        var skill=SkillManager.Instance.GetSkillTemplate(recipe.SkillId);
        var station=bot.ParentWorld?.GetDoodad(stationId);
        if(!StationAllowed(bot,station,recipe)||Range(recipe)<=0||bot.GetDistanceTo(station,true)>Range(recipe))return $"Stand the Farmer within 4 metres of a public {StationName(recipe)} in the same world instance.";
        if(starting&&(IsActive(runtime)||BotGatherController.IsActive(runtime)||bot.Craft.IsCrafting||bot.SkillTask!=null||bot.ActivePlotState!=null))return "Stop gathering or the current action before crafting.";
        if(bot.Inventory?.Bag==null||(starting&&bot.Inventory.Bag.FreeSlotCount<recipe.CraftProducts.Count))return "Free an inventory slot before crafting.";
        if(!bot.Inventory.CanReplaceGliderInBackpackSlot())return "Remove the occupied backpack before crafting.";
        if(starting&&recipe.CraftMaterials.Any(m=>Count(bot,m.ItemId)<m.Amount))return "The Farmer does not have enough materials in its bag.";
        if(starting&&bot.LaborPower<skill.ConsumeLaborPower)return $"Insufficient labor: {bot.LaborPower} available; {skill.ConsumeLaborPower} required.";
        if(starting&&bot.Mp<skill.ManaCost)return $"Insufficient mana: {bot.Mp} available; {skill.ManaCost} required.";
        return null;
    }
    internal static string BatchRequirements(BotRuntime runtime,Craft recipe,int quantity)
    {
        if(quantity<1||quantity>MaximumBatch)return $"Choose a whole-number amount from 1 to {MaximumBatch}.";
        if(recipe==null)return "The recipe is unavailable.";
        var bot=runtime.Bot;var skill=SkillManager.Instance.GetSkillTemplate(recipe.SkillId);
        if(skill==null)return "The crafting skill is unavailable.";
        if(recipe.CraftMaterials.Any(m=>Count(bot,m.ItemId)<(long)m.Amount*quantity))return "Not enough materials in the Farmer’s bag for the selected amount.";
        var labor=(long)skill.ConsumeLaborPower*quantity;
        if(bot.LaborPower<labor)return $"Insufficient labor for this batch: {bot.LaborPower} available; {labor} base labor required.";
        if(bot.Mp<skill.ManaCost)return "Insufficient mana for the next craft.";
        return null;
    }
    private static int MaxCraftable(Character bot,Craft recipe)
    {
        if(recipe==null||recipe.CraftMaterials.Count==0)return 0;
        var skill=SkillManager.Instance.GetSkillTemplate(recipe.SkillId);if(skill==null)return 0;
        var amount=Math.Min(MaximumBatch,recipe.CraftMaterials.Min(m=>m.Amount>0?Count(bot,m.ItemId)/m.Amount:0));
        return Math.Max(0,skill.ConsumeLaborPower>0?Math.Min(amount,bot.LaborPower/skill.ConsumeLaborPower):amount);
    }
    public static object View(BotRuntime runtime,uint selectedRecipeId=IronIngotRecipe)
    {
        var bot=runtime.Bot;var recipe=Recipe(selectedRecipeId);var skill=recipe==null?null:SkillManager.Instance.GetSkillTemplate(recipe.SkillId);
        Attempts.TryGetValue(bot.Id,out var a);if(a!=null&&!ReferenceEquals(a.Runtime,runtime))a=null;
        if(a?.Active==true){recipe=Recipe(a.RecipeId);skill=recipe==null?null:SkillManager.Instance.GetSkillTemplate(recipe.SkillId);}
        PlayerBotsRemoteService.TryGetCompanionOwner(bot.Id,out var ownerId);
        var policy=Policy(WorldManager.Instance.GetCharacterById(ownerId));
        var recipes=Basics.Keys.Select(id=>Recipe(id)).Where(c=>c!=null&&c.CraftProducts.Count==1&&c.CraftMaterials.Count==1)
            .Select(c=>new{recipeId=c.Id,name=$"{ProductName(c)} (from {ItemName(c.CraftMaterials[0].ItemId)})",stationName=StationName(c)}).ToArray();
        var stations=recipe==null||bot.ParentWorld==null?[]:bot.ParentWorld.GetAllDoodads().Where(s=>StationAllowed(bot,s,recipe)&&bot.GetDistanceTo(s,true)<=20)
            .OrderBy(s=>bot.GetDistanceTo(s,true)).Take(32).Select(s=>new{stationId=s.ObjId,name=LocalizationManager.Instance.Get("doodad_almighties","name",s.TemplateId,"Smelter"),distance=Math.Round(bot.GetDistanceTo(s,true),1),inRange=bot.GetDistanceTo(s,true)<=Range(recipe)}).ToArray();
        return new{ok=true,recipeId=recipe?.Id??selectedRecipeId,name=ProductName(recipe),stationName=StationName(recipe),recipes,craftingAllowed=policy==null,policyMessage=policy,available=recipe!=null&&skill!=null,labor=bot.LaborPower,laborCost=skill?.ConsumeLaborPower??0,
            maximumBatch=MaximumBatch,maxCraftable=MaxCraftable(bot,recipe),durationMs=recipe?.CastDelay??0,completed=a?.Completed??0,quantity=a?.Quantity??0,
            manaCost=skill?.ManaCost??0,materials=recipe?.CraftMaterials.Select(m=>new{itemId=m.ItemId,name=ItemName(m.ItemId),required=m.Amount,available=Count(bot,m.ItemId)}).ToArray(),
            products=recipe?.CraftProducts.Select(p=>new{itemId=p.ItemId,name=ItemName(p.ItemId),quantity=p.Amount}).ToArray(),stations,
            active=a?.Active??false,state=a?.State??"idle",detail=a?.Detail??"Choose a basic recipe and its public workbench, preview, then confirm the selected amount. The owner must be online."};
    }
    public static object Preview(BotRuntime runtime,uint ownerId,uint recipeId,uint stationId,int quantity,out string error)
    {
        Previews.TryRemove(runtime.Bot.Id,out _);var recipe=Recipe(recipeId);
        error=!Basics.ContainsKey(recipeId)?"Choose a supported basic processing recipe.":Check(runtime,ownerId)??Requirements(runtime,recipe,stationId,true)??BatchRequirements(runtime,recipe,quantity);
        if(error!=null)return null;
        var pending=new Pending(runtime,ownerId,stationId,quantity,Bag(runtime.Bot),Convert.ToHexString(RandomNumberGenerator.GetBytes(24)),DateTime.UtcNow.AddMinutes(2),recipeId);
        Previews[runtime.Bot.Id]=pending;
        return new{ok=true,recipeId,stationId,ownerId,quantity,name=runtime.Bot.Name,recipeName=ProductName(recipe),laborCost=SkillManager.Instance.GetSkillTemplate(recipe.SkillId).ConsumeLaborPower*quantity,
            availableLabor=runtime.Bot.LaborPower,materials=recipe.CraftMaterials.Select(m=>new{name=ItemName(m.ItemId),required=m.Amount*quantity,available=Count(runtime.Bot,m.ItemId)}),
            products=recipe.CraftProducts.Select(p=>new{name=ItemName(p.ItemId),quantity=p.Amount*quantity}),token=pending.Token,expiresUtc=pending.Expires,
            message="Craft the selected amount at the selected public workbench, one native craft at a time. Following stops. The owner must remain online. Stop Crafting cancels the remainder; completed items stay in the bag. No retry on failure."};
    }
    public static string Confirm(BotRuntime runtime,uint ownerId,uint recipeId,uint stationId,int quantity,string token)
    {
        var bot=runtime.Bot;
        if(!Previews.TryRemove(bot.Id,out var p)||!ReferenceEquals(p.Runtime,runtime)||p.OwnerId!=ownerId||p.StationId!=stationId||p.Quantity!=quantity||p.RecipeId!=recipeId||p.Token!=token||p.Expires<=DateTime.UtcNow)return "A matching crafting preview is required. Preview again.";
        var recipe=Recipe(recipeId);var error=Check(runtime,ownerId)??Requirements(runtime,recipe,stationId,true)??BatchRequirements(runtime,recipe,quantity);if(error!=null)return error;
        if(p.Bag!=Bag(bot))return "Inventory changed. Preview crafting again.";
        BotManager.Instance.StopFollow(bot);BotManager.Instance.StopImmediately(bot);
        var attempt=new Attempt(runtime,ownerId,stationId,Count(bot,recipe.CraftProducts[0].ItemId),bot.LaborPower,quantity){RecipeId=recipeId};Attempts[bot.Id]=attempt;
        try{bot.Craft.Craft(recipe,1,stationId);attempt.CastTask=bot.SkillTask;Step(runtime);}
        catch{Stop(runtime,"Native crafting could not start. Refresh the inventory before retrying.");throw;}
        return attempt.State=="cancelled"?attempt.Detail:null;
    }
    public static string Cancel(BotRuntime runtime,uint ownerId)
    {
        if(!PlayerBotsRemoteService.TryGetCompanionOwner(runtime.Bot.Id,out var actual)||actual!=ownerId)return "The assigned owner is required.";
        Stop(runtime);return null;
    }
    public static void Stop(BotRuntime runtime,string reason="Crafting cancelled.")
    {
        Previews.TryRemove(runtime.Bot.Id,out _);
        if(!Attempts.TryGetValue(runtime.Bot.Id,out var a)||!ReferenceEquals(a.Runtime,runtime)||!a.Active)return;
        if(runtime.Bot.Craft.IsCrafting&&(runtime.Bot.SkillTask==null||ReferenceEquals(a.CastTask,runtime.Bot.SkillTask)))runtime.Bot.Craft.CancelCraft();
        a.State="cancelled";a.Detail=$"{reason} Completed {a.Completed}/{a.Quantity}; completed items remain in inventory.";
    }
    // Record only an observed native product. A failed craft never advances the batch.
    internal static bool ObserveProduct(BotRuntime runtime,int productCount)
    {
        if(!Attempts.TryGetValue(runtime.Bot.Id,out var a)||!ReferenceEquals(a.Runtime,runtime)||a.State!="crafting")return false;
        var gained=productCount-a.BeforeProducts;
        if(gained!=1)return false;
        a.Completed++;
        if(a.Completed>=a.Quantity){a.State="completed";a.Detail=$"Native crafting finished: {a.Completed} × {ProductName(Recipe(a.RecipeId))} received. Labor spent: {a.BeforeLabor-runtime.Bot.LaborPower}.";}
        else{a.State="waiting";a.NextAt=runtime.Bot.GlobalCooldown>DateTime.UtcNow?runtime.Bot.GlobalCooldown.AddMilliseconds(750):DateTime.UtcNow.AddMilliseconds(750);
            a.Detail=$"Completed {a.Completed}/{a.Quantity} {ProductName(Recipe(a.RecipeId))}. Waiting for the next craft.";}
        return true;
    }
    internal static bool ObserveSettledProduct(BotRuntime runtime,DateTime now)
    {
        if(!Attempts.TryGetValue(runtime.Bot.Id,out var a)||!ReferenceEquals(a.Runtime,runtime)||a.State!="crafting")return false;
        var bot=runtime.Bot;var recipe=Recipe(a.RecipeId);
        // SkillTask clears before effects award items and EndCraft finishes cleanup.
        if(bot.SkillTask!=null||bot.ActivePlotState!=null){a.QuietSince=default;return false;}
        if(a.QuietSince==default)a.QuietSince=now;
        return now>=a.QuietSince.AddSeconds(1)&&!bot.Craft.IsCrafting&&recipe!=null&&
            ObserveProduct(runtime,Count(bot,recipe.CraftProducts[0].ItemId));
    }
    public static void Step(BotRuntime runtime)
    {
        if(!Attempts.TryGetValue(runtime.Bot.Id,out var a)||!ReferenceEquals(a.Runtime,runtime)||!a.Active)return;
        var bot=runtime.Bot;var recipe=Recipe(a.RecipeId);
        // A completed product may have filled the last bag slot.
        if(ObserveSettledProduct(runtime,DateTime.UtcNow))return;
        var error=Check(runtime,a.OwnerId)??Requirements(runtime,recipe,a.StationId,false);
        if(error!=null){Stop(runtime,error);return;}
        if(DateTime.UtcNow>a.Deadline){Stop(runtime,"Crafting timed out. No retry performed.");return;}
        if(a.State=="waiting")
        {
            if(DateTime.UtcNow<a.NextAt||bot.GlobalCooldown>DateTime.UtcNow||
                bot.Cooldowns.CheckCooldown(recipe.SkillId))return;
            if(bot.Inventory.Bag.FreeSlotCount<recipe.CraftProducts.Count){Stop(runtime,"Free an inventory slot before the next craft.");return;}
            error=BatchRequirements(runtime,recipe,1);
            if(error!=null){Stop(runtime,error);return;}
            if(bot.SkillTask!=null||bot.ActivePlotState!=null||bot.Craft.IsCrafting||BotGatherController.IsActive(runtime))
            {Stop(runtime,"Another action interrupted the crafting batch.");return;}
            a.BeforeProducts=Count(bot,recipe.CraftProducts[0].ItemId);a.QuietSince=default;a.State="crafting";
            a.Detail=$"Crafting {a.Completed+1}/{a.Quantity} {ProductName(Recipe(a.RecipeId))}.";a.SettleAt=DateTime.UtcNow.AddSeconds(2);a.Deadline=DateTime.UtcNow.AddMinutes(2);
            try{bot.Craft.Craft(recipe,1,a.StationId);a.CastTask=bot.SkillTask;}
            catch{Stop(runtime,"Native crafting failed. No retry performed.");throw;}
            return;
        }
        // Give late native results time to arrive; never retry an uncertain craft.
        if(a.QuietSince==default||DateTime.UtcNow<a.QuietSince.AddSeconds(5)||DateTime.UtcNow<a.SettleAt)return;
        Stop(runtime,"Native crafting finished without the expected product. Check inventory before retrying.");
    }
}
