> Historical project guide preserved on 16 October 2026. It may refer to separately supplied packages or a different checkout; deployment and test claims below are historical. Private local addresses are generalized. Read ../../CHANGES-AND-STATUS.md and ../../HISTORY-INDEX.md for current import scope.

# Bridge water and moving-owner fix

This cumulative native source patch includes the Farming / Bridge, Bridge Follow, Bridge Chunk and Owner Refresh fixes. No Node dashboard replacement or database changes are required.

## Install

1. Stop AAEmu.Game and back up source and runtime files.
2. Merge SERVER-SOURCE into the AAEmu-develop source folder you build on the server. Replace matching files and retain new files.
3. Rebuild AAEmu.Game using .NET 10. Copy the newly built game-server files into the runtime directory you start (C:/AAEmu Server/AAEmu.Game/net10.0). Preserve configuration, Data, database and credentials.
4. Restart AAEmu.Game. Spawn Clara and test the same bridge in both directions, stopping on the deck and each bank. Also try moving while Clara is midway across. Keep geodata enabled and the server client data accessible.

Keep Clara; recreating the bot does not correct the navigation code.

## Findings from the attached log

At 10:09:44-10:09:49 Clara (character 19, object 44358) progresses along raised waypoints. As the owner moves, route selections change from a short route ending at node 2521 to a 46-point route ending at node 298. The later route approaches the riverbank. From 10:10:03 the movement guard repeatedly rejects it with WaterTraversalRejected.

The planner previously relied on native graph water-depth metadata. Connected edges can have zero water-depth metadata even when the running world's water data rejects those positions. Planning and movement therefore disagreed.

## Changes

Native route edges and their start/end connections now sample the running world's water data at intervals of at most one metre, using the interpolated native route height. Submerged edges are excluded before selecting a route. A bridge above the water remains eligible. The movement water guard remains enabled.

An active raised crossing now keeps its route while the owner moves, until the existing route ends. This prevents a moving owner from causing mid-deck replanning onto another floor or component. Normal ground following can still retarget. Previous route-refresh, height, chunk handoff and farming fixes are included.

## Verification and limits

All 1,177 native tests passed, including regressions for preferring a raised dry route over an underwater shortcut, rejecting wet start/end connections and preserving a crossing when the owner moves.

The older local water file does not reproduce the riverbank rejection in the attached log. These tests verify the code using water predicates, but do not confirm this actual bridge in the running server. Live verification is required after rebuilding and restarting. If it still fails, retain a fresh Server.log from the same attempt.
