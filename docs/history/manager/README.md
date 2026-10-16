> Historical project guide preserved on 16 October 2026. It may refer to separately supplied packages or a different checkout; deployment and test claims below are historical. Private local addresses are generalized. Read ../../CHANGES-AND-STATUS.md and ../../HISTORY-INDEX.md for current import scope.

# ArcheAge PlayerBots Companion Manager

A local Node Companion Manager and a separate administrator view, using the original navy/gold ArcheAge design. The shared ArcheAge login opens the personal manager first for both players and administrators. Authorized administrators see an **Admin Manager** button inside it.

The user has verified shared login, the two-PC connection, Albert's roster visibility, and the preceding saved-data Admin Manager on their server. The new icons/account-pool patch is implemented and checked locally; its new account provisioning and character creation need a live test after installing the matching Login and Game builds. See [installation](ADMIN-ACCOUNTS-INSTALL.md) and the package's VERIFICATION.md.

## Current interface

- Login has only ArcheAge username, password and Sign In, with the same flow for admins and players.
- Personal sidebar: Overview, Skills & Build, Companion Control, Gathering, Crafting, Equipment, Inventory, Activity Log. Duplicate horizontal navigation is hidden while preserving functions and the sidebar.
- Separate same-themed Admin Manager: Overview, All Companions, Ownership, Bot Accounts, Created Bots, Activity Log, Server Setup.
- Registered online/offline companions, owners, Combat/Farmer modes, roles, skills, inventory, equipment, saved/live HP/MP, XP/level, tasks and meaningful observed/audited logs.
- Local game artwork for admin equipment, inventory, skills and passives. Unmapped artwork has a readable fallback; no icon availability is inferred from a name.
- Ownership is a distinct assignment page grouped by owner, including unassigned companions. Reviewed assignment/unassignment and mode changes require an offline bot.
- Reviewed create/start/dismiss/stop controls. Creation uses the native identity factory, not a dashboard-only record.

## Bot accounts and creation

Bot Accounts groups each account's bot characters beneath its username. Created Bots shows the same account grouping with creation-build information where a native factory roster entry exists. Albert remains a test character and is never automatically inserted, renamed, duplicated or moved by the interface.

Register existing dedicated accounts by their ArcheAge usernames. The included runtime seed identifies **PlayerBot1 (#3)** and **PlayerBot2 (#4)**, with Albert's character #4 remaining on account #4. Capacity defaults to four undeleted characters per account and is editable. Account IDs and character IDs are different. The supported account pool is capped at 20 accounts.

Create Companion fills the first available, logged-out account in pool order. If all accounts are full and automatic account creation is enabled, a confirmed creation generates another ordinary account and a password, registers it with Game, then creates the character. Generated credentials are visible only in Admin Manager, with current administrator authority checked. Existing passwords are not fetched or changed. Website sign-in itself never creates accounts or bots.

Creation accepts name, level, supported race/gender and an existing server build preset. Its skill trees are listed; unlocks follow native level progression. The new character starts unassigned at its race spawn. Dismiss it, assign a player character owner, then start it for personal-manager use. Existing owner active-bot quotas still apply and are separate from account storage capacity.

## Server setup and launchers

| Service | Your setup | Purpose |
| --- | --- | --- |
| Manager website | PC .30, local `127.0.0.1:17841` | Browser interface |
| Login account service | Server `YOUR_LAN_IP:17842` | Account checks and explicit private bot-account provisioning |
| Game bridge | Server `YOUR_LAN_IP:17840` | Companion data and admin actions |

Use Node.js 20 or newer. `npm start` starts the remote controller; `npm run manager` starts the website. The existing PlayerBots and Manager launchers identify their services; keep each service console open while it is in use. The launcher does not start AAEmu Login/Game automatically. See [detailed setup](ADMIN-ACCOUNTS-INSTALL.md) for rebuilding and overlay installation. UnitTests are developer checks, not a service to launch.

## Security and persistence

Current roles and account status are checked server-side; personal reads/actions remain scoped to assigned companions even for admins. Sessions rotate at login, use HttpOnly/SameSite Strict cookies, expire after eight hours, and are removed on logout. Browser actions require Origin/CSRF checks; reviews expire after 60 seconds, are session-bound and consumed once. Game rechecks permission, account capacity and relevant bot state before mutations.

Use HTTPS outside the explicitly configured private LAN. The existing test-LAN option permits HTTP only to the exact private `trustedLanServerIp`; service keys remain in local configuration/environment and are never sent to the browser. Accounts with required second factors are denied until a supported website flow exists. Legacy local dashboard-password mode remains for compatibility and does not provide the account-based Admin Manager.

Generated credentials are encrypted in `.admin-private/` with a separate local key; both are excluded from Git and distribution. The OS account protecting these files remains trusted. Audit logs never contain passwords. Preserve the vault/key together, account-pool configuration, owner/job/known-bot/creation registries, and logs when updating or backing up.

Account provisioning saves pending credentials before dispatch and can recover across a Manager restart or lost reply. Refresh after an unknown outcome before retrying. A successfully provisioned account may remain empty if later character creation fails; it can be reused. The patch does not silently delete accounts or characters to hide partial outcomes.

Offline details are last saved data; live tasks need an online bot. Administrator intents/outcomes are durable. Roster changes are sampled and combat events archived during inspection; the site is not a complete unattended Game log collector.

## Implemented versus planned

| Feature | Status |
| --- | --- |
| Shared login, personal/admin access, account-scoped controls | User verified on the LAN setup |
| Prior admin roster and saved item/character inspection | User verified for Albert |
| Admin icons, separate Ownership, grouped bot accounts, reviewed rollover creation | Implemented; local tests/builds/browser checks pass; new remote SQL flow awaits a live test |
| Default race/gender appearance | Existing native factory path |
| Hair/colour/face selectors and character preview | Planned; native model fields exist, selectable validated web catalogs do not |
| Arbitrary custom skill-tree combinations and admin skill/item editing | Planned; this patch uses server build presets |
| Personal multi-bot dropdown redesign | On hold |
| Complete unattended server-event ingestion | Planned |

The original uploaded ZIP and earlier install guides predate these account-pool changes. The current guide above is authoritative for this patch. No live credentials, user database contents, configuration keys or private vault files are included in it.
