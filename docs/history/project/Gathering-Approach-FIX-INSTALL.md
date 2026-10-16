> Historical project guide preserved on 16 October 2026. It may refer to separately supplied packages or a different checkout; deployment and test claims below are historical. Private local addresses are generalized. Read ../../CHANGES-AND-STATUS.md and ../../HISTORY-INDEX.md for current import scope.

# Gathering standing-point fix — October 10, 2026

This update fixes route selection for gathering. Previously, the bot had to find a route to the exact resource object position. That could fail for a vein origin above ground or inside rock, although a nearby standing point would be in mining range.

The bot now tries supported ground positions within the action's actual three-dimensional range, starting on the near side, and checks other sides if necessary. Raised native surfaces retain their existing route option. Native range, labor, mana, inventory, resource phase and threat checks still apply. Gathering does not use the companion teleport fallback. If route planning fails, following remains active.

## Install

Stop Game and back up the matching source files. Merge SERVER-SOURCE into the AAEmu-develop source checkout, rebuild AAEmu.Game with .NET 10, deploy its newly built output to the runtime directory you launch, and restart Game. No dashboard or database changes are required. Keep the current bots and the paused stairs/bridge work.

## Check

Cancel the old failed preview. Refresh resources, preview the iron vein again and confirm. Sandra should walk to a reachable point within mining range and perform the native mining action. The patch is verified locally; the shown live vein still needs checking. A genuinely unavailable route can still be rejected.
