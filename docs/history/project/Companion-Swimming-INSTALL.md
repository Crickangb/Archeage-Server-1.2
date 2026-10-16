> Historical project guide preserved on 16 October 2026. It may refer to separately supplied packages or a different checkout; deployment and test claims below are historical. Private local addresses are generalized. Read ../../CHANGES-AND-STATUS.md and ../../HISTORY-INDEX.md for current import scope.

# Companion swimming update (.NET 10 native server)

This source update adds swimming while following an owner and includes the preceding farming and bridge fixes. It does not replace the Node dashboard or modify the database.

## Install

1. Stop AAEmu.Game and back up source and runtime files.
2. Merge SERVER-SOURCE into the AAEmu-develop source folder you build on the server. Replace matching files and keep new files.
3. Rebuild AAEmu.Game with .NET 10. Copy the newly built server files into C:/AAEmu Server/AAEmu.Game/net10.0 (or your actual launch directory), preserving configuration, Data, databases and credentials.
4. Restart and spawn an existing companion. No bot recreation is needed.

## Behavior

FollowSwimmingEnabled defaults to true. Set it to false in the server's BotConfig.json to disable swimming follow.

Following companions move at 3 metres per second at approximately 0.5 metres below the water surface, keeping their feet below water. They stop about 2 metres from the owner and remain afloat. They use Swim/CoSwim movement stances instead of land movement. Entering and leaving water requires a gentle height transition (at most 0.75 metres per tick); bridge drops, steep banks and dock walls are not accepted as swim shortcuts.

Swimming applies to the following behavior. Gathering still requires dry approved routes; this patch does not allow underwater farming or implement underwater combat/navigation.

An owner detected as attached to a boat makes following companions wait on land, or hold their position afloat. Boarding and attaching companions to boats is not implemented. Automatic long-distance recall now requires the owner to be on terrain ground, outside water and unattached to a vehicle; it will not recall onto a bridge deck, into water or onto a boat.

The cumulative bridge changes also accept positions lying on a passable native graph edge, rather than requiring proximity to a node, and re-establish an occupied graph surface after teleport when the landing position matches a dry native edge. Previous water-aware routes, crossing retention, chunk handoff and follow-refresh fixes remain included.

## Validation

All 1,182 native server tests passed. Regressions execute swimming follow toward a moving target and floating at rest, verify surface height rather than riverbed height, gentle entry and exit, rejection of steep banks and bridge drops, swimming packet stance and idle input, water recall refusal, and mid-edge native connectors rejecting other floors and off-edge positions.

This has not been deployed to or verified in the running game. Test at a gentle shoreline first: walk into water, swim away, stop, and walk back onto the gentle shore. Then test owner boarding/disembarking: companions should wait rather than teleport into the sea. Finally retest the bridge in both directions.

Limitations: swimming follows directly toward the owner; it does not calculate routes around underwater objects, piers or shore barriers. Client animation and collision behavior need live verification. Bridge crossing is also still awaiting live confirmation. If a companion sticks or animates incorrectly, retain Server.log from that same attempt.
