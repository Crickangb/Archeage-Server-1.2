using System.Diagnostics;
using AAEmu.Game.Bots.Navigation;

namespace AAEmu.Game.Bots.Gathering;

/// <summary>One shared deadline for every candidate in a gathering confirmation.</summary>
internal sealed class BotGatherSearchBudget
{
    private readonly Func<TimeSpan> _elapsed;
    private readonly TimeSpan _limit;
    internal BotGatherSearchBudget(TimeSpan? limit = null, Func<TimeSpan> elapsed = null)
    {
        _limit = limit ?? TimeSpan.FromSeconds(2);
        var watch = Stopwatch.StartNew();
        _elapsed = elapsed ?? (() => watch.Elapsed);
    }

    internal void Check()
    {
        if (_elapsed() >= _limit) throw new SearchExpiredException();
    }

    internal BotTravelRoute Run(Func<BotTravelRoute> search, out bool timedOut)
    {
        timedOut = false;
        try
        {
            Check();
            var route = search();
            // A native lookup can finish after the deadline. Never start it late.
            Check();
            return route;
        }
        catch (SearchExpiredException)
        {
            timedOut = true;
            return null;
        }
    }

    private sealed class SearchExpiredException : OperationCanceledException { }
}
