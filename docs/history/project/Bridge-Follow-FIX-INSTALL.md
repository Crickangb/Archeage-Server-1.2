> Historical project guide preserved on 16 October 2026. It may refer to separately supplied packages or a different checkout; deployment and test claims below are historical. Private local addresses are generalized. Read ../../CHANGES-AND-STATUS.md and ../../HISTORY-INDEX.md for current import scope.

# Bridge follow movement fix

This is a native AAEmu.Game source update. It includes the preceding Farming / Bridge update and retains its gathering changes. Apply it after the Skill Priority / Equipment update. The Node dashboard does not need replacing.

## Install

1. Stop AAEmu.Game and back up the source and runtime folders.
2. Merge `SERVER-SOURCE` into the AAEmu-develop source folder you build on the server. Replace matching files and include the new files.
3. Rebuild AAEmu.Game using .NET 10. Copy the newly built game server files into the runtime folder you start (`C:/AAEmu Server/AAEmu.Game/net10.0`). Preserve its configuration, Data folder, database, and credentials.
4. Keep `World.GeoDataMode` enabled and the server's client-data source accessible.
5. Restart AAEmu.Game, spawn the companions, and test the Lilyut stone bridge in both directions. Stop midway, then walk off onto each bank. Repeat with Sarah and the wooden bridge.

No SQL changes or character recreation are needed.

## Changes

- An active native follow route with an unchanged goal retains its waypoints and speed instead of restarting every second. Moving goals still allow replanning, and completed partial routes can continue toward the owner.
- Follow replanning waits until a jumping or falling bot lands, so its airborne position is not used as a new walkable graph connector.
- Automatic ambient and terrain-step jumps are suppressed on native routes and on the retained raised surface. Explicit jump requests remain supported.
- A waypoint on the deck cannot be completed from the riverbed just because the horizontal position matches.
- Failed native follow planning logs `ev=follow_route_unavailable mode=bai-surface` and an endpoint/search reason. Repeated identical failures are suppressed.

This corrects verified movement issues found while examining the videos. It does not yet establish why Sarah rejected her particular bridge approach. The previous patch's graph search could find deck routes, but those tests did not execute movement; this update adds movement execution tests.

## If a bot still stops

Attach the game-server log from the same attempt. The expected location in your setup is `C:/AAEmu Server/AAEmu.Game/net10.0/Logs/Server.log`; use the active runtime's Logs directory if you start another folder. Search for `follow_route_unavailable`, `bai-surface`, `navigation_hazard`, or `navigation_decision`. These distinguish an inaccessible starting connector from a movement rejection.

## Verified locally

All 1,170 native server tests passed, including five new bridge movement tests. The tests execute movement over a raised route with corners, retain the deck after stopping, retain unchanged follow routes, defer airborne replanning, suppress automatic bridge jumps while preserving land jumps, and reject wrong-floor waypoint completion. Existing search, farming danger checks, and the earlier fixes remain covered by the suite. Live bridge movement must still be verified after installation.
