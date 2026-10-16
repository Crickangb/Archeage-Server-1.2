# Gathering confirmation search timeout

The 2026-10-11 logs show resource 13989 exhausting 34 approach candidates and returning `route_crosses_water_or_invalid_surface` at 13:41:26 and 13:41:53. The bot host recorded a maximum tick of about 17.8 seconds. Dashboard response writes then failed with HttpListenerException 64 because the client connection had already closed. No gathering start for those attempts was recorded.

Gathering confirmation now shares a two-second cooperative search budget across all standing-point candidates, native graph search, legacy pathfinding, hostile detours, and dry-route validation. A route returned after the deadline is discarded. The deadline is checked before movement ownership changes, native skill execution, or labor spending. Expiration returns a clear failure: Gathering route search took too long. No gathering started. Move the Farmer closer or choose another resource.

The fallback planner skips the native surface pass when the gathering planner has already attempted it. Other route callers keep the default behavior. Cancellation is propagated through optional terrain/fallback handlers instead of being mistaken for missing map data. Individual map-loading operations cannot be forcibly interrupted; this is a cooperative deadline, not a hard wall-clock guarantee under server or disk stalls.

The failure still protects against crossing water and unsupported surfaces. It does not invent a route or automatically retry a command. Client disconnect warnings may still occur for genuine network failures or browser cancellation.

Deploy by merging SERVER-SOURCE into the existing source checkout, rebuilding AAEmu.Game, deploying the resulting runtime, and restarting. No dashboard or database update is required.
