> Historical project guide preserved on 16 October 2026. It may refer to separately supplied packages or a different checkout; deployment and test claims below are historical. Private local addresses are generalized. Read ../../CHANGES-AND-STATUS.md and ../../HISTORY-INDEX.md for current import scope.

# Farming and bridge navigation update

This updates the native AAEmu.Game server source. The Node dashboard does not need replacing for this patch. Apply it after the Skill Priority / Equipment update.

## Install on the game server

1. Stop AAEmu.Game and back up the source and runtime folders.
2. Copy `SERVER-SOURCE` into your `AAEmu-develop` source folder, merging the directories and replacing the matching files. Keep the new files too.
3. Build AAEmu.Game with your usual .NET 10 build process. Copy the newly built game server files into the runtime folder you actually start (`C:/AAEmu Server/AAEmu.Game/net10.0`). Keep its existing configuration, database, Data folder, and credentials.
4. Verify the effective `World.GeoDataMode` setting is `true`. The server's `ClientData.Sources` must point to an accessible ArcheAge 1.2 game_pak or extracted client data. The source configuration already enables geodata, but runtime overrides can change it. Use the pack path on the server computer, not a path that exists only on your gaming computer.
5. Restart AAEmu.Game and spawn the companions.

No SQL changes or character recreation are required.

## What changes

- Passive/neutral creatures do not cancel gathering just because they can be attacked or have a hostile faction relation. Aggressive enemies still block their detection range, with a two-metre margin. An NPC actively attacking the Farmer still blocks gathering even if its faction is neutral. Hostile players remain protected against.
- The scan checks the route, rather than only the resource endpoint. During travel it rechecks nearby danger. Ownership, labor, inventory, and resource-phase checks remain enforced.
- Supported local BAI routes preserve the connected graph's heights and intermediate turns. Bridge decks are no longer flattened onto the riverbed. Blocked links, underwater links, steep jumps, disconnected graphs, and exhausted searches are rejected.
- Following uses connected native routes where available. On narrow raised surfaces it follows the owner's surface instead of requesting an offset outside the bridge. On land the existing free nearby spacing remains. Native routes disable corner lookahead so bots do not shortcut their route bends.
- A companion standing on a completed bridge route retains that occupied surface height.

## Test in game

1. Choose a resource near a passive creature. Gathering should proceed if there is a supported route and enough labor/mana/bag space.
2. Try near a genuinely aggressive enemy. Gathering should still cancel; the message now identifies an aggressive or attacking unit and the applicable distance.
3. With Mike and Hansen following, walk the Lilyut stone bridge slowly in both directions. Stop midway and on the far bank. Check that both walk onto the deck, remain on it while stopped, and leave by its entrance/exit.
4. Repeat at the wooden bridge and around a building or rock. Check that the companions use connected navigation turns rather than cutting through the obstacle.
5. Try gathering across the bridge with a clear route. Confirm the Farmer reaches interaction range and the native gathering action completes.

## Scope and verification

Local gathering routes try the new native graph search within 400 metres; longer trips still use the existing road/travel planner. Following can advance through an available local graph toward a farther owner, replanning as it progresses. Missing, disconnected, or inaccessible client navigation data cannot supply a walkable bridge or obstacle route. This does not add arbitrary climbing, swimming, teleporting, or navigation for every object in the world.

The offline search was checked against the local ArcheAge 1.2 game pack at the reported Lilyut coordinates: connected routes from the recorded bank positions to the bridge deck were found in both overlapping navigation chunks, using 19 and 23 graph points. The deck nodes are around Z=149 while nearby riverbed nodes are around Z=144. Live movement still needs the tests above after deployment.

Validation: all 1,165 native server tests passed after the final source changes (zero failures).
