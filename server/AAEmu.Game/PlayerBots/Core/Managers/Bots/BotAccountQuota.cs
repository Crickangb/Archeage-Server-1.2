using AAEmu.Game.Bots.Remote;
namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>The shared active-bot allowance for all human characters on one account.</summary>
public static class BotAccountQuota
{
    public const int Limit = 3;
    public static readonly object Sync = new();
    public static uint AccountForBot(uint id) => PlayerBotsRemoteService.TryGetCompanionOwner(id,out var owner)
        ? NameManager.Instance.GetCharacterAccount(owner) : 0;
    public static int Used(IBotManager manager,uint account,uint except=0) => account==0 ? 0 :
        manager.GetAllBots().Count(b=>b.Id!=except && AccountForBot(b.Id)==account);
    public static bool CanSpawn(IBotManager manager,uint id)
    {
        if(manager.GetBot(id)!=null)return true;
        var account=AccountForBot(id);
        return account==0 || Used(manager,account)<Limit;
    }
}
