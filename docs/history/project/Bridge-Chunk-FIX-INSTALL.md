> Historical project guide preserved on 16 October 2026. It may refer to separately supplied packages or a different checkout; deployment and test claims below are historical. Private local addresses are generalized. Read ../../CHANGES-AND-STATUS.md and ../../HISTORY-INDEX.md for current import scope.

# Bridge navigation chunk handoff fix

This native AAEmu.Game source update includes the preceding Farming / Bridge and Bridge Follow fixes. The Node dashboard does not need replacing.

## Install

1. Stop AAEmu.Game and back up your source and runtime folders.
2. Merge `SERVER-SOURCE` into the AAEmu-develop source folder you build on the server, replacing the matching files and keeping new files.
3. Rebuild AAEmu.Game with .NET 10. Copy the newly built game-server files into the runtime folder you start (`C:/AAEmu Server/AAEmu.Game/net10.0`). Preserve its configuration, Data folder, database, and credentials.
4. Keep `World.GeoDataMode` enabled and the configured client data accessible on the server computer.
5. Restart AAEmu.Game and spawn the companions. Retest the windmill bridge shown in the latest clips, walking both ways and stopping midway and on each bank. Also retest the original Lilyut bridge.

No SQL changes, dashboard changes, or character recreation are needed.

## What the new logs revealed

The screenshot reports failed searches including start 1174 to goals 499 and 394, and start 2888 to goal 509. Those exact pairs fail against the local ArcheAge 1.2 navigation data in chunk 049_060. A nearest goal in that file can belong to an unreachable graph component at the boundary with chunk 049_059. The old planner chose that one nearest node and stopped even when another connected endpoint allowed progress.

## Changes

- Choose a destination from nodes actually reachable through permitted native edges, rather than choosing the nearest destination node before searching.
- Prefer a reachable complete route. Following can otherwise use a connected partial route that makes at least five metres of progress toward the owner.
- Try nearby starting nodes from overlapping loaded navigation chunks. Load the adjacent world cell when standing close to its boundary so a subsequent route can use its data.
- A partial route ends at a real connected node. Following replans after completing it, allowing a handoff to the neighboring chunk without mixing node IDs between files.
- Keep the previous route-height, corner, automatic-jump, unchanged-route, airborne-replanning, wrong-floor, and gathering-threat fixes.
- Failure messages now distinguish `no_reachable_endpoint` from `search_budget_exhausted`. Selected-route details include the endpoint and whether the route is partial.

## Verification and limits

All 1,173 native server tests passed, including three new endpoint/chunk regressions and the previous bridge movement tests.

An offline check using the real client data started at the logged node 1174 in chunk 049_060. It found a 35-point connected partial route, then a 17-point route through neighboring chunk 049_059 to a navigation node near the recorded owner destination. This verifies graph selection and handoff; it is not a live game movement test. The original failing node pairs remain disconnected, and the fix does not pretend those edges are walkable.

After installing, please test the same crossing with Mike, Hansen, and Sarah. If anyone still stops, retain the log from that attempt. Search the active server's `Logs/Server.log` for `follow_route_unavailable`, `navigation_hazard`, `bai-surface`, and the new `partial=` route details.
