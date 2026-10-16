> Historical project guide preserved on 16 October 2026. It may refer to separately supplied packages or a different checkout; deployment and test claims below are historical. Private local addresses are generalized. Read ../../CHANGES-AND-STATUS.md and ../../HISTORY-INDEX.md for current import scope.

# Companion footsteps fallback — October 10, 2026

This cumulative source update adds an in-memory owner walking trail when native navigation is unavailable or reaches only a partial endpoint. It preserves the observed height and corners instead of using ground terrain beneath stairs. It also corrects surface height selection on sloped route edges.

## Install

1. Stop AAEmu.Game and back up matching source files.
2. Merge SERVER-SOURCE into your AAEmu-develop source checkout, replacing matching files. This includes a change in Core/Packets/C2G/CSMoveUnitPacket.cs; install that file too, as it records the owner trail.
3. Rebuild AAEmu.Game with .NET 10, deploy the rebuilt output to the runtime directory you actually run, and restart Game.
4. Keep the existing bots. No database or dashboard changes are required.

`FollowOwnerTrailEnabled` defaults to true. To disable it, set that property to false in Configurations/BotConfig.json and restart Game.

## Test the stairs and bridge

Start with you and the bot together at the bottom. Walk on foot over the bridge or up the stairs without jumping, teleporting or mounting. Wait at the other end. The log should show mode=owner-footsteps where a native route cannot finish. Try both directions and tight corners.

If Sandra is already stranded beneath the stairs, return beside her or recall her first, then walk the route again. The server needs to observe a new walking trail after installation; it cannot reconstruct earlier movement. A bot must join within 0.6 metres in three dimensions, or use a separately navigation-checked ground connector within 3 metres, so a bot on a lower floor cannot attach directly to an upper-floor trail.

## Limits

Trails are temporary, bounded to 4,096 positions per character and expire two minutes after their last movement update. Invalid samples, gaps over two seconds, teleports, jumps, water, mounted travel and object attachments break the recorded path. This walking update does not add boat boarding or automatic teleport recovery. Native navigation and swimming retain their separate behavior. Missing trails and missing native paths can still leave a bot waiting.

The trail follows received grounded player positions; it does not create a general collision mesh. Live stair and bridge testing remains necessary. Test results are included in VALIDATION.txt.
