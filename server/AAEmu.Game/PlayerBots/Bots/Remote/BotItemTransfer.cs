using System.Collections.Concurrent;
using System.Security.Cryptography;
using AAEmu.Game.Bots.Gathering;
using AAEmu.Game.Bots.Host;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Items.Templates;
using NLog;

namespace AAEmu.Game.Bots.Remote;

/// <summary>One confirmed, native bag transfer to the companion's assigned human owner.</summary>
public static class BotItemTransfer
{
    public const float Range=5f;
    private static readonly Logger Logger=LogManager.GetCurrentClassLogger();
    private sealed record Pending(Character Bot,Character Owner,Item Item,int Slot,int Count,byte Grade,
        ItemFlag Flags,int Quantity,string Token,DateTime Expires);
    private static readonly ConcurrentDictionary<uint,Pending> Previews=new();
    public sealed record Result(int Status,object Data);
    private static Result Reject(string message,int status=409)=>new(status,new{message});

    public static bool Transferable(Item item)=>item?.Template!=null && item.Count>0 &&
        (item.ItemFlags&(ItemFlag.SoulBound|ItemFlag.Secure))==0 &&
        item.Template.BindType is not (ItemBindType.BindOnPickup or ItemBindType.BindOnPickupPack) &&
        item.Template.LootQuestId==0 && item.Template is not BackpackTemplate;
    public static bool Splittable(Item item)=>Transferable(item) && item.GetType()==typeof(Item) &&
        item.Template.MaxCount>1 && (item.Template.FixedGrade<0||item.Grade==item.Template.FixedGrade) && item.ItemFlags==ItemFlag.None && item.MadeUnitId==0 &&
        item.ImageItemTemplateId==0 && item.LifespanMins==0 && item.ExpirationOnlineMinutesLeft<=0 &&
        item.ExpirationTime==default && item.UnpackTime==default;

    public static Result Handle(BotRuntime runtime,uint ownerId,ulong itemId,int quantity,bool confirm,string token)
    {
        var bot=runtime.Bot;
        if(!PlayerBotsRemoteService.TryGetCompanionOwner(bot.Id,out var assigned)||assigned!=ownerId)
            return Reject("The assigned owner is required.");
        var owner=WorldManager.Instance.GetCharacterById(assigned);
        var connection=owner?.Connection;
        if(owner?.IsOnline!=true||connection==null||!ReferenceEquals(connection.ActiveChar,owner))
            return Reject("The assigned human owner must be online to receive items.");
        // Incoming packets and disconnect use this same gate. Always acquire it before the bot runtime.
        lock(connection)
        lock(runtime.SyncRoot)
        {
            if(connection.LeaveTask!=null)return Reject("The owner is logging out. Cancel logout or log back in before receiving items.");
            if(runtime.Retired||bot.Connection!=null||bot.IsDead||owner.IsDead||!owner.IsOnline||
                !ReferenceEquals(owner.Connection,connection)||!ReferenceEquals(connection.ActiveChar,owner))
                return Reject("A living spawned server companion and its online owner are required.");
            if(bot.Transform==null||owner.Transform==null||bot.ParentWorld==null||
                !ReferenceEquals(bot.ParentWorld,owner.ParentWorld)||bot.Transform.InstanceId!=owner.Transform.InstanceId||
                bot.Transform.ZoneId!=owner.Transform.ZoneId||bot.GetDistanceTo(owner,true)>Range)
                return Reject("Bring the companion within 5 metres of its owner in the same zone.");
            if(bot.IsInBattle||owner.IsInBattle||runtime.CombatState.Target!=null||
                bot.IsInAggroListOf.Values.Any(x=>x is AAEmu.Game.Models.Game.Units.Unit u&&!u.IsDead)||
                owner.IsInAggroListOf.Values.Any(x=>x is AAEmu.Game.Models.Game.Units.Unit u&&!u.IsDead))
                return Reject("Finish combat before transferring items.");
            if(bot.SkillTask!=null||owner.SkillTask!=null||bot.ActivePlotState!=null||owner.ActivePlotState!=null||BotGatherController.IsActive(runtime)||AAEmu.Game.Bots.Crafting.BotCraftController.IsActive(runtime))
                return Reject("Stop gathering and finish the current action before transferring items.");
            if(TradeManager.Instance.HasActiveTrade(owner)||TradeManager.Instance.HasActiveTrade(bot))
                return Reject("Close the active trade before transferring items.");
            if(!PlayerBotsRemoteService.TryGetCompanionOwner(bot.Id,out var currentOwner)||currentOwner!=ownerId)
                return Reject("Companion ownership changed. Preview again.");
            var source=bot.Inventory?.Bag;var target=owner.Inventory?.Bag;
            var item=source?.Items.SingleOrDefault(x=>x.Id==itemId);
            if(source==null||target==null||item==null||!ReferenceEquals(item._holdingContainer,source)||
                item.OwnerId!=bot.Id||item.SlotType!=SlotType.Inventory||item.Slot<0||item.Slot>byte.MaxValue)
                return Reject("Choose an item currently carried in this companion's inventory.");
            if(!Transferable(item))return Reject("Bound, secured, quest and backpack items cannot be sent.");
            if(quantity<1||quantity>item.Count)return Reject("Choose a quantity between 1 and the current stack count.");
            if(quantity<item.Count&&!Splittable(item))return Reject("This item can only be sent as a whole stack.");
            var targetSlot=target.GetUnusedSlot(-1);
            if(targetSlot<0||targetSlot>byte.MaxValue||!target.CanAccept(item,targetSlot))
                return Reject("The owner needs one free inventory slot. Make room and preview again.");
            var splitSlot=quantity<item.Count?source.GetUnusedSlot(-1):-1;
            if(quantity<item.Count&&(splitSlot<0||splitSlot>byte.MaxValue))
                return Reject("A partial transfer needs one free companion bag slot for the native stack split. Send the whole stack or make room.");
            if(!confirm)
            {
                var pending=new Pending(bot,owner,item,item.Slot,item.Count,item.Grade,item.ItemFlags,quantity,
                    Convert.ToHexString(RandomNumberGenerator.GetBytes(24)),DateTime.UtcNow.AddMinutes(2));
                Previews[bot.Id]=pending;
                return new(200,new{ok=true,previewOnly=true,name=bot.Name,ownerId,ownerName=owner.Name,itemId,quantity,
                    itemName=LocalizationManager.Instance.Get("items","name",item.TemplateId,item.Template.Name),
                    currentCount=item.Count,remainingCount=item.Count-quantity,ownerFreeSlots=target.FreeSlotCount,
                    distance=Math.Round(bot.GetDistanceTo(owner,true),1),token=pending.Token,expiresUtc=pending.Expires,
                    message="Review the quantity and recipient. No items have moved."});
            }
            if(!Previews.TryRemove(bot.Id,out var preview)||preview.Expires<=DateTime.UtcNow||preview.Token!=token||
                !ReferenceEquals(preview.Bot,bot)||!ReferenceEquals(preview.Owner,owner)||!ReferenceEquals(preview.Item,item)||
                preview.Slot!=item.Slot||preview.Count!=item.Count||preview.Grade!=item.Grade||preview.Flags!=item.ItemFlags||preview.Quantity!=quantity)
                return Reject("A matching unexpired item-transfer preview is required. Item or owner may have changed.");
            try
            {
                var moving=item;
                if(quantity<item.Count)
                {
                    if(!bot.Inventory.SplitOrMoveItem(ItemTaskType.SwapItems,item.Id,SlotType.Inventory,(byte)item.Slot,
                        0,SlotType.Inventory,(byte)splitSlot,quantity))
                        return Reject("Native stack split failed. Refresh both inventories before retrying.");
                    moving=source.GetItemBySlot(splitSlot);
                    if(moving==null||moving.Count!=quantity||moving.TemplateId!=item.TemplateId)
                        return Reject("Stack split outcome is uncertain. Refresh both inventories before retrying.",500);
                    moving.OwnerId=bot.Id;
                }
                // Native movement preserves the item instance and updates ownership, dirty tracking and packets.
                if(!target.AddOrMoveExistingItem(ItemTaskType.Trade,moving))
                {
                    if(!ReferenceEquals(moving,item))
                        bot.Inventory.SplitOrMoveItem(ItemTaskType.SwapItems,moving.Id,SlotType.Inventory,(byte)moving.Slot,
                            item.Id,SlotType.Inventory,(byte)item.Slot,quantity);
                    return Reject("Native transfer failed. No items were delivered; refresh the companion inventory before retrying.");
                }
                Logger.Info("PlayerBot {0} sent item {1} template {2} quantity {3} to assigned owner {4}",bot.Id,moving.Id,moving.TemplateId,quantity,owner.Id);
                return new(200,new{ok=true,previewOnly=false,botId=bot.Id,ownerId,ownerName=owner.Name,itemId,
                    receivedItemId=moving.Id,quantity,remainingCount=source.Items.Where(x=>x.TemplateId==item.TemplateId).Sum(x=>x.Count),
                    message=$"Sent {quantity} × {LocalizationManager.Instance.Get("items","name",item.TemplateId,item.Template.Name)} to {owner.Name}."});
            }
            catch(Exception ex)
            {
                Logger.Error(ex,"PlayerBot item transfer failed after confirmation for bot {0}, owner {1}",bot.Id,owner.Id);
                return Reject("Transfer interrupted after starting; items may have moved. Refresh both inventories before previewing again.",500);
            }
        }
    }
}
