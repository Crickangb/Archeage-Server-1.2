namespace AAEmu.Game.Bots.Gathering;

/// <summary>Attackable passive creatures are not proximity threats to a Farmer.</summary>
internal static class BotGatherThreatPolicy
{
    internal static float Clearance(bool hostileRelation, bool aggressive, bool attackingFarmer, float attackStartRangeScale)
    {
        if (attackingFarmer) return BotGatherController.HostileClearance;
        if (!hostileRelation || !aggressive) return 0;
        // NPC AI searches AttackStartRangeScale * 10 metres. Leave a small margin
        // rather than treating every passive creature within 25m as dangerous.
        return float.IsFinite(attackStartRangeScale)
            ? Math.Clamp(attackStartRangeScale * 10f + 2f, 15f, 500f)
            : BotGatherController.HostileClearance;
    }
}
