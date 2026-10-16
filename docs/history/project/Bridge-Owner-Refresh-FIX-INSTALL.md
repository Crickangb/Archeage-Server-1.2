> Historical project guide preserved on 16 October 2026. It may refer to separately supplied packages or a different checkout; deployment and test claims below are historical. Private local addresses are generalized. Read ../../CHANGES-AND-STATUS.md and ../../HISTORY-INDEX.md for current import scope.

# Owner-follow route reset fix

This native AAEmu.Game source patch includes the preceding Farming / Bridge, Bridge Follow, and Bridge Chunk fixes. It adds a correction identified from Clara's attached Server.log. The Node dashboard does not need replacing.

## Install

1. Stop AAEmu.Game and back up source and runtime files.
2. Merge `SERVER-SOURCE` into the AAEmu-develop source folder you build on the server. Replace matching files and retain new files.
3. Rebuild AAEmu.Game using .NET 10. Copy the newly built game-server files into the runtime directory you start (`C:/AAEmu Server/AAEmu.Game/net10.0`). Preserve configuration, Data, database, and credentials.
4. Restart AAEmu.Game. Spawn Clara and test the same bridge in both directions, stopping on the deck and each bank. Keep geodata enabled and the server's client data accessible.

No database changes or bot recreation are needed.

## Cause confirmed in the log

Clara is character 19, object 44423. At 09:49:51-09:50:01, the log repeatedly accepts and selects the same 31-waypoint native route toward an effectively stationary owner. Between route selections it reports multiple `ev=follow target=1` refreshes. The same initial waypoint is repeatedly selected instead of progressing along the route.

`BotManager.SetFollowTarget` cleared the route unconditionally whenever owner following was refreshed. That caller invalidated the movement task's route-retention fix. The earlier tests covered movement and route planning separately but did not combine those with repeated owner-follow refreshes.

## Change

Refreshing the same owner while a native party-rendezvous route is active preserves the route, destination, speed, and navigation approval. Follow distance is still updated. Following a different owner or overriding other kinds of movement still clears the old route as before. The previous bridge and chunk checks remain.

## Verification

All 1,174 native server tests passed. The new regression executes a raised route with corners while refreshing the same owner every three movement ticks. It checks that refreshes preserve the active surface, waypoint and speed, verifies arrival near the owner, and checks that switching owners still replaces the route.

Live movement remains to be confirmed after installation. If Clara still stops, retain the server log from that same attempt. The repeated `ev=follow` resets and repeated initial-route selections from this log should no longer occur while the same owner's route is active. Changed owner destinations can still cause legitimate replanning.
