# Companion spawning and dismissal

In the deployed Game server Configurations/BotConfig.json, merge these settings into the existing JSON object:

```json
"AutoSpawnEnabled": false,
"DismissCooldownSeconds": 300
```

Do not replace the existing configuration with this fragment. AutoSpawnEnabled defaults to false and controls automatic spawning for online/AFK owners, the startup auto-spawn task and the activity director. Manual Spawn/Start and explicit administrator Create & Start remain available. Existing active bots remain active until dismissed or normally logged out.

DismissCooldownSeconds defaults to 300 (five minutes); use 600 for ten minutes. Values are limited to 300–600. Dismissal from the player dashboard, admin dashboard or RemoveBot command starts the cooldown for that bot only. Every subsequent spawn goes through the same server-side guard. Ordinary internal logout/reload operations do not create a dismissal cooldown. Cooldown timers are held for the current Game process and reset when the Game server restarts.

A departing bot leaves its native party/raid before world removal. Party synchronization shares the lifecycle lock and checks that the bot is still active, so the companion loop cannot re-add a dismissed bot. Automatic offline-owner and AFK logout also remove party membership.

The player dashboard shows a Spawn countdown; admin Start is disabled during the remaining cooldown. Apply settings while stopped, then restart Game and Manager after deploying the corresponding build/source update.

## Request owner picker

Request Companion selects an owner from the signed-in account's undeleted characters. The online character is selected first; otherwise the first character is selected. Players can choose another character on that same account. The form shows the account name/ID, and each owner option shows character name/ID. Refresh preserves the player's choice. Submission validates that the chosen character belongs to the requester and stores its canonical name and ID. Existing request history remains unchanged.

The Game account-access response supplies the complete character list. Against an older Game build, Manager can infer only owner characters already associated with assigned companions; the matching Game update is required for an account with no existing companions or for a complete alternate-character list.

### Gathering approach points

Gathering searches for reachable standing points within the native skill's three-dimensional range instead of requiring a route to the resource object's exact origin. This allows an above-ground vein origin to be mined from nearby supported ground. Several sides are checked through the existing navigation planner; no teleport or invented surface is used. The native action still checks range and the live resource phase before casting. If no standing point has a supported route, gathering is rejected before following is stopped. Neutral-creature threat handling remains governed by the gathering threat policy.

Gathering also validates water exposure along every route segment before it stops following or accepts the trip. Terrain-driven routes are sampled at terrain height; native surface routes retain their deck height. Gathering tries connected native routes beyond the generic planner's 400-metre cutoff, with the existing bounded search and water checks. If every approach is wet or unsupported, the request fails immediately instead of starting a walk that later stops at the shore. Gathering cancellations are logged as `ev=gather_cancelled`.

### Gathering hostile avoidance

Gathering prefers 20 metres of clearance around aggressive or attacking creatures and tries a minimum of 15 metres when a tighter ground detour is needed. Creatures with a larger native detection range retain that larger clearance. Passive neutral creatures do not create avoidance zones unless they attack the Farmer.

The Farmer checks the initial route and tries bounded local detours around threats. When a threat appears on the next route segment during walking, it tries to replace the remaining route before cancelling. Detour connectors use existing terrain slope checks and water sampling. This is ground-route avoidance, not full building collision or bridge navigation; raised native routes are never flattened to terrain to create a detour. If the Farmer is already inside the minimum clearance, the resource is unsafe, combat starts, or no supported detour exists, gathering stops. There is no automatic fight or harvesting retry. Successful live detours are logged as `ev=gather_hostile_detour`.

Live gathering checks look 60 metres along the remaining waypoint path, rather than stopping at the next short waypoint. A preferred-clearance check runs once per second to attempt avoidance before reaching the minimum clearance; minimum-clearance danger still checks every gathering step. Cluster detours extend their entry and exit beyond all overlapping threats. Native routes may use ground detours only when all their connectors pass the existing ground support checks; raised decks remain excluded. Failed live detours log `gather_hostile_detour_unavailable` with the route mode and reason.
