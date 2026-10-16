> Historical project guide preserved on 16 October 2026. It may refer to separately supplied packages or a different checkout; deployment and test claims below are historical. Private local addresses are generalized. Read ../../CHANGES-AND-STATUS.md and ../../HISTORY-INDEX.md for current import scope.

# Admin Manager makeover (preceding milestone)

**For the latest icons and account-pool patch, use [ADMIN-ACCOUNTS-INSTALL.md](ADMIN-ACCOUNTS-INSTALL.md).** This guide describes the preceding update, including its single existing-account creation limitation.

Source update for the working shared-login/LAN milestone. AAEmu is on **YOUR_LAN_IP**, Node Manager on **YOUR_LAN_IP**, and the browser opens **http://127.0.0.1:17841** on the Manager PC. Existing Login settings and keys stay as configured.

## What this update implements

- Original navy/gold ArcheAge styling, admin sidebar, overview totals and responsive layout.
- Registered online/offline/unassigned roster, search and status/mode/ownership filters.
- Owners and current owner online status; Combat/Farmer mode, role, level, total XP, HP/MP and reported behavior.
- Bot detail tabs for overview/control, skills/build, equipment, inventory, tasks, and logs.
- Offline character/skill/item inspection from existing Game database records; live detail sections while online.
- Search existing human owner characters, assign/unassign, switch mode, start/dismiss and stop movement.
- Explicit creation through the existing server-owned native identity factory, using name/race/gender/build/level. Creation starts the bot at its race spawn, unassigned; it does not create an account.
- Session-bound, one-use reviews expire after 60 seconds. Game checks current admin access and expected owner/mode/online state before admin mutations.
- Durable audit intents/outcomes, actor account IDs, observed roster transitions and inspected combat events. Logs survive a Manager restart; failures/uncertain outcomes are labeled.

The personal Companion Manager still opens first and stays owner-scoped, including for admins. No bot is created on login. Albert remains test-only. Personal multi-bot dropdown changes remain on hold.

## Installation

1. Back up your current PlayerBots folder and Game source/runtime deployment. Preserve configuration, ownership/job/role files, roster data and existing logs.
2. Stop the Node Manager on .30. Copy the patch's `NODE/ArcheAge-PlayerBots/` contents into your installed `C:\ArcheAge-PlayerBots` folder. These are replacement/additional application files, not a complete clean installation. Keep your private `config.json`.
3. On .16, copy `SERVER/AAEmu.Game/PlayerBots/Bots/Remote/PlayerBotsRemoteService.cs` into its matching source location, and add the new adjacent `PlayerBotsAdminService.cs`. Copy the optional wiki documentation if maintaining the source docs.
4. **Rebuild and deploy the complete AAEmu.Game build**, then restart Game normally. No Login rebuild is needed for this admin-only patch. Keep the working LAN settings and bridge environment variables.
5. Restart `Start-Manager.bat` on .30. Reload the browser with Ctrl+F5, sign in, and open Admin Manager.
6. Before trying changes, inspect Albert online, then dismiss him and verify his offline record. Search an existing human character, review assignment, confirm it, and check the Activity Log. Confirm a regular account cannot open Admin Manager.

No SQL import or schema change is supplied. The optional Node tests use artificial fixtures; they are not runtime installation requirements. Runtime DLLs are not supplied.

## Enabling explicit creation

The existing native identity factory requires **`AAEMU_PLAYERBOTS_ACCOUNT_ID`** in Game's process environment: the numeric ID of an existing dedicated server-owned bot account. This is an account ID, not Albert's character ID or a human owner's character ID. The account must meet the existing factory's checks (including not being logged in as a player). Restart Game after changing its environment.

If this setting is absent, Create Companion is disabled and Server Setup explains why. When configured, native creation still validates the account, name uniqueness, race/template, build, level, placement, persistence and admission. A reported creation/rollback failure must be investigated before retrying. No fallback account or placeholder bot is created.

The native factory persists its identity roster at `AAEMU_PLAYERBOTS_ROSTER_PATH`, defaulting to `Data/PlayerBots/bot-roster.json`, and uses the existing archetype definitions. An explicit successful Create starts the bot; dismiss it before assigning an owner or changing its mode. Starting an existing assigned bot continues to enforce the server's active-bot quota.

## Offline roster and data sources

The roster includes active bots, recorded owners/roles, the native identity roster, characters belonging to the configured server-owned bot account, and bots remembered in `Configurations/PlayerBotsKnown.json`. Reading the admin roster remembers discovered identities; bridge spawn/despawn also remembers identities. Deleted character rows are excluded. Arbitrary human characters are not assumed to be bots.

Legacy offline bots that have never been registered, assigned, given a saved role, or observed cannot be identified automatically. Start an existing legacy bot normally and refresh the admin roster once to register it. Keep PlayerBotsKnown.json with your other bot configuration backups.

Offline HP/MP, XP, skills and items reflect the last saved Game database data; computed max HP/MP and live task state are unavailable offline. Equipment and inventory are inspections in this update, not admin item-editing tools. Skill learning/respec remains in the existing personal Companion Manager.

## Logs and limits

Node stores `logs/admin-audit.jsonl`, rotating at 5 MiB to `.1`; the UI shows the latest 200 records across both files, and the selected bot's latest 50. `adminAuditPath` can override the path privately. Audit write permission is required before a confirmed admin command is dispatched.

The log distinguishes requested/completed/failed/unknown actions and observed Game events. Observed roster state is sampled on admin refresh, not a complete continuous server event feed. Recent combat events are archived when an online bot's details are inspected; this does not capture every combat event that occurs while the admin page is closed. Archived events remain visible after the bot goes offline, within the rolling retention window. Creation/assignment command audit does not cover actions issued through unrelated in-game/CLI tools; roster observations may record their resulting state.

Source builds and Node access/regression/browser-fixture checks pass locally. The new native SQL reads and mutation workflows have **not** been run against your live database. Install and validate the real setup before treating those paths as live verified. Public HTTPS hosting, bulk administration, destructive bot deletion, and universal server-log ingestion are not part of this update.

## Rollback

Stop Manager and Game. Restore your pre-update Node files and `PlayerBotsRemoteService.cs`; remove the added `PlayerBotsAdminService.cs` if returning to the earlier source; rebuild/deploy Game. Preserve ownership, job, identity roster, known registry and logs. Application rollback does not undo any admin actions you already confirmed; restore/reassign intentionally through the existing tools as appropriate.
