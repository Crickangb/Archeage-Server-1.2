#if !PLAYERBOTS_AAEMU_3_0
using System.Numerics;

namespace AAEmu.Game.Bots.Navigation;

// Recover stalled following only after the owner stops on observed solid land.
internal sealed class BotFollowRecovery
{
    private bool _tracking;
    private Vector3 _botAnchor;
    private Vector3 _ownerAnchor;
    private DateTime _progressAt;
    private DateTime _ownerMovedAt;
    private DateTime _nextRecovery;

    internal bool Observe(Vector3 bot, Vector3 owner, DateTime now, bool eligible, double delaySeconds)
    {
        if (!eligible || !double.IsFinite(delaySeconds) || delaySeconds < 2)
        {
            _tracking = false;
            return false;
        }
        if (!_tracking || now < _progressAt || now < _ownerMovedAt)
        {
            _tracking = true;
            _botAnchor = bot; _ownerAnchor = owner;
            _progressAt = _ownerMovedAt = now;
            return false;
        }
        if (Vector3.Distance(bot,_botAnchor) >= 1f)
        {
            _botAnchor = bot; _progressAt = now;
        }
        if (Vector3.Distance(owner,_ownerAnchor) >= .5f)
        {
            _ownerAnchor = owner; _ownerMovedAt = now;
        }
        if (now < _nextRecovery || (now - _progressAt).TotalSeconds < delaySeconds ||
            (now - _ownerMovedAt).TotalSeconds < 2)
            return false;
        _tracking = false;
        _nextRecovery = now.AddSeconds(30);
        return true;
    }
}
#endif
