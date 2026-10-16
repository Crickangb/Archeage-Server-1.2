using System.Text.Json;
using AAEmu.Game.Models.Game.Char;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>Independent labor for connectionless bots. Player account balances are never changed.</summary>
public static class BotLaborRegistry
{
    public const int Limit=5000, OnlineAmount=100, OfflineAmount=50, TickMinutes=5;
    private sealed record Balance(int Labor, DateTime TickAt, bool Farmer=false);
    public static int RecoveryInterval(bool farmer,bool online) => farmer
        ? Math.Clamp(online ? AAEmu.Game.Models.Game.Bots.BotConfig.Instance.FarmerLaborOnlineTickMinutes
            : AAEmu.Game.Models.Game.Bots.BotConfig.Instance.FarmerLaborOfflineTickMinutes,1,1440)
        : TickMinutes;
    private static readonly object Sync=new();
    private static readonly string FilePath=Path.Combine(AppContext.BaseDirectory,"Configurations","PlayerBotsLabor.json");
    private static Dictionary<uint,Balance> Balances=Load();
    private static readonly Dictionary<uint,Character> Online=new();
    private static Dictionary<uint,Balance> Load()
    {
        if(!File.Exists(FilePath))return new();
        var data=JsonSerializer.Deserialize<Dictionary<uint,Balance>>(File.ReadAllText(FilePath))??throw new InvalidDataException("PlayerBotsLabor.json is invalid.");
        if(data.Any(x=>x.Key==0||x.Value==null||x.Value.Labor<0||x.Value.Labor>Limit||x.Value.TickAt.Kind!=DateTimeKind.Utc))throw new InvalidDataException("PlayerBotsLabor.json contains invalid balances.");
        return data;
    }
    private static void Save(Dictionary<uint,Balance> next)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath+".tmp",JsonSerializer.Serialize(next));
        File.Move(FilePath+".tmp",FilePath,true);Balances=next;
    }
    public static int Recovery(int labor,DateTime since,DateTime now,bool online,int intervalMinutes=TickMinutes)
    {
        var ticks=Math.Max(0,Math.Floor((now-since).TotalMinutes/Math.Clamp(intervalMinutes,1,1440)));
        return (int)Math.Clamp(labor+ticks*(online?OnlineAmount:OfflineAmount),0,Limit);
    }
    public static void Attach(Character bot,DateTime now)
    {
        if(bot.Connection!=null||bot.Id==0)return;
        var farmer=BotJobRegistry.IsFarmer(bot.Id);
        lock(Sync)
        {
            if(Online.ContainsKey(bot.Id))return;
            var labor=Balances.TryGetValue(bot.Id,out var old)?Recovery(old.Labor,old.TickAt,now,false,RecoveryInterval(farmer,false)):Math.Clamp((int)bot.LaborPower,0,Limit);
            Save(new(Balances){[bot.Id]=new(labor,now,farmer)});
            Online[bot.Id]=bot;bot.InitializeLaborCache((short)labor,now);
        }
    }
    public static bool Managed(Character bot){lock(Sync)return Online.TryGetValue(bot.Id,out var current)&&ReferenceEquals(bot,current);}
    public static bool TrySet(Character bot,short labor)
    {
        lock(Sync)
        {
            if(!Online.TryGetValue(bot.Id,out var current)||!ReferenceEquals(current,bot))return false;
            var old=Balances[bot.Id];var next=Math.Clamp((int)labor,0,Limit);
            Save(new(Balances){[bot.Id]=old with {Labor=next}});
            bot.InitializeLaborCache((short)next,bot.LaborPowerModified);return true;
        }
    }
    public static void Tick(Character bot,DateTime now)
    {
        var farmer=BotJobRegistry.IsFarmer(bot.Id);
        lock(Sync)
        {
            if(!Online.TryGetValue(bot.Id,out var current)||!ReferenceEquals(current,bot))return;
            var old=Balances[bot.Id];
            if(old.Farmer!=farmer)
            {
                // A job switch starts a fresh interval; never reprice elapsed Farmer time as combat time.
                Save(new(Balances){[bot.Id]=old with {Farmer=farmer,TickAt=now}});return;
            }
            var minutes=RecoveryInterval(farmer,true);
            var ticks=Math.Floor((now-old.TickAt).TotalMinutes/minutes);
            if(ticks<1)return;
            var next=new Balance(Recovery(old.Labor,old.TickAt,now,true,minutes),old.TickAt.AddMinutes(ticks*minutes),farmer);
            Save(new(Balances){[bot.Id]=next});bot.InitializeLaborCache((short)next.Labor,now);
        }
    }
    public static bool TryChange(Character bot,short change)
    {
        lock(Sync)
        {
            if(!Online.TryGetValue(bot.Id,out var current)||!ReferenceEquals(current,bot))return false;
            return TrySet(bot,(short)Math.Clamp(Balances[bot.Id].Labor+(int)change,0,Limit));
        }
    }
    public static void Detach(Character bot,DateTime now)
    {
        lock(Sync)
        {
            if(!Online.TryGetValue(bot.Id,out var current)||!ReferenceEquals(current,bot))return;
            Tick(bot,now);Save(new(Balances){[bot.Id]=new(bot.LaborPower,now,Balances[bot.Id].Farmer)});Online.Remove(bot.Id);
        }
    }
}
