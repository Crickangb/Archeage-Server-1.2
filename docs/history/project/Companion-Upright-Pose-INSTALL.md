> Historical project guide preserved on 16 October 2026. It may refer to separately supplied packages or a different checkout; deployment and test claims below are historical. Private local addresses are generalized. Read ../../CHANGES-AND-STATUS.md and ../../HISTORY-INDEX.md for current import scope.

# Companion upright pose correction

This cumulative native source update includes the companion swimming, farming and bridge changes. It does not replace the Node dashboard or change the database.

## Install

Stop AAEmu.Game and back up source/runtime files. Merge SERVER-SOURCE into your AAEmu-develop source folder, rebuild AAEmu.Game with .NET 10, and copy the newly built files into your launch directory, preserving configuration, Data, databases and credentials. Restart and spawn the existing companions. No bot recreation is needed.

## Correction

Land stop, face and teleport packets now advertise StandingOnSolid. Walking packets also retain the grounded flag. Grounded movement clears stale local roll/pitch and sends upright rotation while preserving yaw. Swimming and jumping retain their own movement flags and stances.

The attached Server - Copy.log contains accepted routes and repeated teleports, with no explicit jump events; it does not record the outgoing packet fields needed to prove the client pose's cause. The correction addresses the grounded packet flags and possible stale tilt; live confirmation of the upside-down pose remains required.

## Verification

Regression coverage checks actual emitted stop/walk/swim packets, upright roll/pitch, preserved facing, neutral idle input and independent swimming stance/flags. Test results are included in VALIDATION.txt.

Test standing still and ordinary land following first. Then cross the bridge, teleport/recall, and try swimming. If the pose is still wrong, preserve the log and describe whether it starts at spawn, after moving, or after crossing water.

## Included swimming behavior and limits

FollowSwimmingEnabled defaults to true. Set it to false in the server's BotConfig.json to disable swimming follow.

Following companions move at 3 metres per second at approximately 0.5 metres below the water surface, keeping their feet below water. They stop about 2 metres from the owner and remain afloat. They use Swim/CoSwim movement stances instead of land movement. Entering and leaving water requires a gentle height transition (at most 0.75 metres per tick); bridge drops, steep banks and dock walls are not accepted as swim shortcuts.

Swimming applies to the following behavior. Gathering still requires dry approved routes; this patch does not allow underwater farming or implement underwater combat/navigation.

An owner detected as attached to a boat makes following companions wait on land, or hold their position afloat. Boarding and attaching companions to boats is not implemented. Automatic long-distance recall now requires the owner to be on terrain ground, outside water and unattached to a vehicle; it will not recall onto a bridge deck, into water or onto a boat.

The cumulative bridge changes also accept positions lying on a passable native graph edge, rather than requiring proximity to a node, and re-establish an occupied graph surface after teleport when the landing position matches a dry native edge. Previous water-aware routes, crossing retention, chunk handoff and follow-refresh fixes remain included.
