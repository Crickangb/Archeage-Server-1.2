# Companion spacing

Owner follow, manual follow and recall retain the separate slots assigned by BotManager. The remote controller no longer replaces every slot with zero or forces a one-member formation. Existing companions keep their slots when another companion leaves; new companions use a free slot.

Normal companions stand freely near their owner: a bot already 1.5 to 5 metres from the owner (with the normal 3-metre follow distance), within 2 metres vertically and clear of other bots by 1.5 metres, keeps its position. It only needs a catch-up/separation destination when outside that area or crowded. Those fallback destinations are varied around the owner, remain within about 3.5 metres at the default distance, and do not rotate when the owner turns. Explicit column-based follow commands retain their requested formation.

Movement uses the existing navigation checks. Clerics sharing a hostile target also move toward their separate casting positions even when already within casting range. Active casts remain uninterrupted by this positioning check.

After installing the server source and rebuilding/restarting Game, spawn two or three companions, walk and stop on open ground, then fight a target together. Check that they spread out again after combat and after Recall to Owner. Recall itself still immediately returns a bot to its owner before normal follow movement separates it.

This is formation positioning rather than physical character collision: narrow paths, navigation restrictions and immediate spawn/recall can still cause temporary overlap.

## Footsteps fallback

`FollowOwnerTrailEnabled` in `Configurations/BotConfig.json` defaults to `true`. When native navigation is unavailable or only reaches a partial endpoint, following can use a continuous trail of the owner's received grounded walking positions. The trail preserves height and corners and holds up to 4,096 positions in memory per character; it is not a permanent map or a database. A stopped owner's trail remains available for two minutes.

The bot must join within 0.6 metres in three dimensions, or use a separately navigation-checked ground connector within 3 metres, so a bot beneath stairs cannot attach to the upper floor. Walking gaps over two seconds, implausible movement, world/instance changes, airborne movement, water and object attachments break the trail. Mounted travel and boat boarding are not recorded. Existing walking, swimming and native route handling remain separate.

After rebuilding and restarting Game, start with owner and companion together at the bottom of the stairs. Walk up on foot without jumping or teleporting, then wait at the top. Check the Game log for `mode=owner-footsteps`. If a bot is already stranded below a different floor, return beside it or recall it before walking the route again. Historical walks from before installation are not available. Missing trails and missing native paths can still leave a bot waiting; this does not add an automatic teleport recovery.

### Owner trail diagnostics

The Game log includes `ev=owner_trail_capture` when it receives owner movement, with the grounded capture result, movement flags and saved point count. Failed fallback attempts include `ev=owner_trail_unavailable` with the capture state, trail age and nearest distance. Both are limited to one entry per character or bot every ten seconds. `reason=no_capture` means the current owner object has no recorded movement; `not_grounded` or `water` shows why samples were discarded; `too_far_to_join` means no saved point is within three metres. `no_safe_join_or_no_remaining_path` means the distance alone was insufficient to establish a safe remaining route. These diagnostics do not change movement rules.

A delayed grounded sample at the unchanged endpoint preserves the walked trail for up to two minutes. Movement after an unobserved gap still starts a new segment. Route requests tolerate up to 250 milliseconds of concurrent capture overlap; larger clock differences remain invalid.

### Stalled follow recovery

`FollowStuckRecallEnabled` defaults to true. `FollowStuckRecallSeconds` defaults to 10 seconds (values below 2 or nonfinite values disable recovery). Restart Game after changing configuration. A following companion more than six metres away (or its follow distance plus two metres, whichever is greater) may recall to the owner's exact observed position after it fails to move one metre for the configured duration and the owner has stopped for two seconds. Recalls have a 30-second cooldown. The owner must have a fresh grounded walking capture in the same world and instance. Raised stairs and bridge landings preserve the observed height; no arbitrary offset is added.

Recovery is blocked for combat, skill execution, movement impairment, jumping/falling, water, mounts and object attachments. It does not board boats or attach passengers. Native-client-controlled bot drivers do not support this server teleport and are excluded. This recovers lost paths; it does not reconstruct missing collision geometry or guarantee walkable native routes. Game logs successful recovery as `ev=follow_stuck_recall`.
