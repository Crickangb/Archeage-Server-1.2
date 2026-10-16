> Historical project guide preserved on 16 October 2026. It may refer to separately supplied packages or a different checkout; deployment and test claims below are historical. Private local addresses are generalized. Read ../../CHANGES-AND-STATUS.md and ../../HISTORY-INDEX.md for current import scope.

# Development status — 16 October 2026

## User verified

- Inventory: seed purchase, split, merge and persistence after restarting; no issue observed in this test.

- Guild Missions: acceptance/progress working; screenshot shows Use Recall progress 1/2. Testelf subsequently completed two missions, guild XP increased from 40 to 60 on the second, and the reward box arrived. Community reopened after a client restart. Daily resets and Thedarknoise retest remain pending.

- GM Quick Tools: all twelve actions tested successfully.

- Lilyut teleport succeeds after the cold-map changes.

- Patron product translations and benefit headings display in English.

- Quest placeholders display Not started.

## Open issues

- **Community window — r575gm / Thedarknoise:** shortcut and menu both fail; client restart did not resolve it. Client log reports `quest_assignment/component.lua:556: attempt to index local 'notifierData' (a nil value)`, followed by failure loading `community.alb`. Both characters now affected after Guild Mission actions. A one-function client guard was installed: absent notification data returns false rather than crashing window creation. Seven cases passed against Lua source and the emitted function; original nil crash reproduced. Testelf user retest passed, including after restart and another mission completion. Thedarknoise retest remains pending. Do not assume Guild Mission progression itself is broken.

- **Golden Ruins teleport:** native client crash observed after `/teleport goldenruins` from swimming at sea. Logs show the previous sea entry wait completing after the destination changed and returning the player to character selection. Pending source change rechecks the current destination and waits for map readiness before teleporting. Native crash cause is not symbolicated; live retesting remains necessary. Installed locally with the Community guard; live retest pending.

- Patron purchase handler currently refuses purchases. One-day test dates were stored for the two test accounts; automatic development benefit grants remain enabled. Payment display is not proof that timed entitlement expiry works.

- A separate client error in `premium_service.lua:34` reports a missing numeric format argument; investigate independently of the Community failure.

- Coinpurse Lucky Supplies giant chest interaction remains deferred.

- Chinese community QR promotion intentionally retained until our replacement page exists. Background changes are on hold.

## Planned

Complete travel and inventory regression tests; test Guild Mission rewards/reset, combat, repair, dungeon entry and NPC retaliation. Continue translations in small reviewed sections, preserving existing English, formatting tokens and DO NOT TRANSLATE text. Later: Patron policy/store, Discord bot, playerbots/dashboard, launcher improvements, website/wiki.

Seven upstream R575 updates through d2d0b3a were installed locally on 16 October, retaining local GM, zone-startup, boat passenger, naming and Guild Mission fixes. Open upstream PRs have not automatically been merged.

Validation for this import: 7,622 tests passed, zero failed/skipped. World build succeeded with zero errors and 217 warnings. The six added tests cover changed entry destinations, stale readiness, logout and cancellation. Updated assemblies installed locally; server restarted successfully and starting zones 129/181 reported ZoneLoaded. In-game confirmation remains pending.

- New login report: r575test failed to log in while r575gm stayed online. Backend restarted at the user request; reproduction after restart pending. No explicit authentication error found in the reviewed Login log.

- Both characters successfully arrived at Golden Ruins through the new wait-before-teleport path.

- Player visibility temporarily differed between clients; teleporting both to another location restored it. Movement-checker dictionary corruption was found in contemporaneous packet-processing logs. A concurrency fix passed 7,624 tests and was installed with backups; server restart completed with starting zones 129/181 ready. Live retest is pending. Guard behavior and NPC animation sliding are separate open investigations; see NPC-MOVEMENT-INVESTIGATION.md.

- Golden Ruins specialty-workbench crash reproduced by both characters. Installed mitigation removes two obsolete template 12290 spawns whose crafting interactions reference missing records. Alternative recipes are disabled, so crafting restoration remains deferred. See GOLDEN-RUINS-WORKBENCH.md; live verification pending.
