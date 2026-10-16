> Historical project guide preserved on 16 October 2026. It may refer to separately supplied packages or a different checkout; deployment and test claims below are historical. Private local addresses are generalized. Read ../../CHANGES-AND-STATUS.md and ../../HISTORY-INDEX.md for current import scope.

# Companion follow recovery — October 10, 2026

This cumulative package includes the existing navigation, swimming and footsteps changes plus stalled follow recovery. The latest log proves the timing fix is active, but the remaining trail is too far from Sandra to join. This update uses the previously suggested teleport fallback for that case, instead of waiting indefinitely for an unavailable path.

## Install once

Stop Game. Back up matching files. Merge all three SERVER-SOURCE folders into the AAEmu-develop source checkout, replace matching files, rebuild AAEmu.Game with .NET 10, deploy the rebuilt Game output into the runtime directory you launch, and restart Game. Do not delete or recreate bots. No dashboard or database update is needed.

## Behavior

Following continues normally while the bot makes progress. If the bot is more than six metres away and stalls for ten seconds, it can recall to the owner's exact recorded grounded position after the owner stops for two seconds. The upper stair/bridge height is preserved while idle. A 30-second cooldown prevents repeated recalls.

Boat attachments, mounted or swimming owners, airborne samples, combat, active skill execution, impaired movement, different worlds/instances and stale or missing captures block this recovery. It does not board boats. Server-driven bots are supported; native-client-controlled bot drivers cannot use the current server teleport.

Configurations/BotConfig.json settings: FollowStuckRecallEnabled defaults to true; FollowStuckRecallSeconds defaults to 10. Restart Game after changing them.

## One live check

Walk on foot up the stairs and stop at the landing. Give a stalled bot about 12 seconds to catch up. Walking should remain preferred while it progresses. Recovery logs ev=follow_stuck_recall. The package is verified locally; the live server still needs this single check. Boat boarding and a complete collision/navigation mesh remain separate work.
