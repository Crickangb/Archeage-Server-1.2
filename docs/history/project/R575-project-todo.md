> Historical project guide preserved on 16 October 2026. It may refer to separately supplied packages or a different checkout; deployment and test claims below are historical. Private local addresses are generalized. Read ../../CHANGES-AND-STATUS.md and ../../HISTORY-INDEX.md for current import scope.

# R575 project to-do

## Current work

- [x] Install English Mail expiry warning and roster save-cooldown message, with original scripts backed up; in-game confirmation pending. See `Mail-roster-translation.md`.

- [ ] Translate remaining small compiled UI strings: Mail expiry notice, roster cooldown, auction bidding status, and item awakening grade label; inspect context before editing.
- [ ] Translate Voyage/start-event labels and reward-state messages as one consistent section.
- [ ] Review GM-console translation separately from player-facing UI.
- [x] Scan installed client database and all 1,058 compiled UI files for remaining Asian text; preserve `DO NOT TRANSLATE` entries. See `remaining-language-scan/Report.md`.

- [x] Install 13 English Patron product names using the user's chosen `Patron Subscription: 30 days` wording; retain `[TEST]` labels for development products. World and Gweonid zones 129/181 restarted and warmed successfully. Patron screen confirmation pending.

- [ ] Fix the coinpurse Lucky Supplies spawn/interaction path. October 14 at 21:04:17, item-use skill 23140 spawned NPC 20029, object 974, in zone 181. The template is level 50 with no authored NPC interaction set; right-click attempts received an interaction-list reply. Trace intended reward/targeting behavior before adding an arbitrary loot or interaction action. See `R575-lucky-supplies-issue.md`.

- [ ] Live-test the installed cold-zone loading fix (20 tests passed; server restarted with zones 129/181 ready; see `Cold-zone-loading-fix.md`). Original issue: October 14 logs show zone 181 starting at 21:00:47, game connection disconnecting at 21:01:20, and ZoneLoaded arriving at 21:01:23. Investigate a keepalive timeout and make loading wait without blocking packet processing.
- [x] User confirmed the replacement quest labels display `Not started` in-game.

- [ ] Confirm reliable world entry after recovering Testelf from undefined zone 202.
- [ ] Test the installed achievement translations in the game.
- [ ] Review achievement and subcategory wording together; offer alternatives when multiple English terms fit.
- [ ] Continue client translation in small, agreed sections. Preserve existing English and skip `DO NOT TRANSLATE` entries.

## Planned project features

- [ ] Investigate Seasonal category visibility, especially Winter; preserve hidden/retired content until intended eligibility is confirmed.
- [ ] Agree consistent Autumn/Fall achievement wording.
- [ ] Correct the Patron no-expiry date display and translate its server product names.
- [ ] Make new player accounts start without Patron; design and implement paid Patron purchases, pricing, duration, and entitlement updates. Preserve the existing test grant until we deliberately change it.
- [ ] Translate and test ArchePass registration, progress, and reward claims separately.
- [ ] Replace the publisher QR promotion with our community/reward page once its URL and reward rules are defined.

- [x] Build and install the first R575 launcher: Normal/GM selection, Play, TCP server status, existing working connection/window settings; 11 checks passed and preview reviewed. Installed `F:\AAEmu-R575-client\ArcheAge-Launcher.exe`. User game-launch confirmation pending. See `r575-launcher/README.md`.
- [ ] Build a Discord bot.
- [ ] Build playerbot support, including a dashboard.
- [ ] Build the project website and wiki, using our 1.2 project as a reference.

These features are queued for later implementation. Review the existing 1.2 tools and design with the user before deciding what to reuse or change for R575.

- [x] User confirmed all 12 GM Quick Tools work in game, including summon, gold, visibility, and readable labels.

- [x] Install Patron expiry display fix: inactive shows No active subscription, unset expiry on active test grant shows No expiry, valid dates retain existing formatter. 15 cases passed against source and emitted handler. Original client file backed up under Translation\PatronExpiry\20261015-205312. In-game confirmation pending; no entitlement or payment changes.
- [x] User confirmed teleport to Lilyut succeeded after cold-zone fix; broader travel testing remains ongoing.

- [x] Installed Guild Missions level correction (guild ranges use guild level; character unit requirements retained), automatic boat destination startup, deck-passenger handoff/recovery, and server English Harpoon Clipper name on 2026-10-15. 28 checks passed; build succeeded. Live verification pending. See guild-missions/Progress.md.
- [x] User confirmed Patron expiry display fixed. Converted test accounts 2 and 3 to one saved day: 2026-10-15 19:25:37 UTC to 2026-10-16 19:25:37 UTC. This tests payment date display; automatic stacked benefit grants remain enabled. Full purchase/renewal/benefit-expiry behavior remains queued.

- [x] Corrected saved boat names: recognize authored names across languages so old Korean defaults follow the English template; preserve player custom names. Includes saved child attachment names. Four regression tests passed; build succeeded. Installed with Harpoon Clipper (14) and Harpoon Launcher: Basic (48) server localization. Backup: F:\AAEmu-R575-compiled\TestLogs\BoatNames-before-20261015-214304. Live confirmation pending.

- [x] Installed welcome-message rebrand on 2026-10-16 (backup and integrity check passed): output/welcome-branding/compact.sqlite3, manifest.json. Exactly one English localized_texts cell (UI text 9074) changed to Welcome to GamingLiberated. Integrity passed; not installed yet. User explicitly deferred background changes. Back up live client DB and require client closed before installation. Server shut down at user request for the night, 2026-10-15.

- [x] Installed seven merged upstream R575 updates through d2d0b3a (16 October), preserving local fixes; 7,616 unit tests passed. Full test game DB and affected runtime files backed up under F:\AAEmu-R575-compiled\TestLogs\Upstream-before-20261016-170633. Eight additive faction tables verified. Active source is now inspection/aaemu-r575-update; keep inspection/aaemu-r575 as pre-update source. Startup/live validation pending. See upstream-update-20261016/Report.md.

- [x] Visually reviewed seven user-provided Discord bug-forum recordings on 2026-10-16 and created a timestamped triage list: bug-videos-20261016/Review.md. These are historical community reports, not confirmed current GamingLiberated defects. Prioritize inventory integrity, Guild Missions/buffs, two-player zone travel, repair, combat-resource display and NPC retaliation. Match today's merged updates before new fixes; repair PRs remain unmerged. No speculative gameplay changes made from the recordings.

- [x] User reports seed purchase/split/merge and persistence after restart working on 2026-10-16. Guild Mission progress also verified; completion/rewards/reset still to test.
- [ ] Community window fails on r575gm/Thedarknoise through shortcut and menu, including after restart. Client component.lua:556 indexes nil notifierData; community.alb then fails to load.
- [ ] Golden Ruins entry race fix: 7,622 unit tests passed and World built successfully; deployment and live retest pending.
- [ ] Import source/client tooling and guides to private Crickangb/Archeage-Server-10; in progress.

- [x] Imported 5,630 server files, client modification tools and guides to private Crickangb/Archeage-Server-10; source/guide checks passed and pushes verified. Live databases, credentials, binary client assets and logs excluded.
- [x] Installed Community notification-list guard and Golden Ruins entry fix, backed up under TestLogs\Community-entry-before-20261016-184112. Server restarted; zones 129/181 ready. User gameplay confirmation pending.

- [x] User confirmed Testelf second Guild Mission completed, guild XP increased 40 to 60, reward box received, Community works after restart.
- [ ] User later reported r575test login failed while r575gm remained connected; requested backend restart. Investigate if reproducible after restart.

- User preference: for this local test installation, close game clients and perform the requested restart without another logout confirmation; announce the action and allow disconnect saves before stopping World.

- [x] Installed movement audit concurrency correction after user authorized closing clients directly. Backup: TestLogs\Movement-audit-before-20261016-195315. Server restarted and starting zones129/181 ready. 7,624 tests passed. Visibility live retest pending; NPC animations/guard behavior unresolved.

- [x] New crash reproduced on Testelf and Thedarknoise at Golden Ruins Specialty Workbench. Both dumps: x2game-dev.dll RVA0x81700b null read. Removed two obsolete template12290 spawns referencing missing839/840 interactions; all remaining entries unchanged. Source/runtime backed up under TestLogs\GoldenWorkbench-before-20261016-201857. Restart verification pending.
- [ ] Restore Golden Ruins specialty crafting only after reviewing intentionally disabled recipes11565/11581; do not substitute modernbench16314 and enable recipes blindly.
