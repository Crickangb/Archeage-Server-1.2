> Historical project guide preserved on 16 October 2026. It may refer to separately supplied packages or a different checkout; deployment and test claims below are historical. Private local addresses are generalized. Read ../../CHANGES-AND-STATUS.md and ../../HISTORY-INDEX.md for current import scope.

# Shared login and administrator access update

This guide describes the first account-access milestone. The subsequent full Admin Manager source update supersedes the active-only roster; install it with [ADMIN-INSTALL.md](ADMIN-INSTALL.md), preserving the working account/LAN settings below.

This is the first account-access milestone, based on the user's confirmed AAEmu source/schema and the supplied dashboard v0.17.8.6. Builds and automated/browser tests pass; it has not been installed on or tested against the user's live database.

## Included behavior

- One ArcheAge username/password/Sign In form for players and administrators.
- The personal Companion Manager opens first, retaining its original navy/gold design and all eight sidebar views. Duplicate horizontal tabs are hidden.
- Server-derived identity, current ownership checks, and administrator permission checks. The personal manager remains owner-scoped even for administrators.
- An Admin Manager button inside the personal manager, visible to administrators only, plus a protected separate admin page with an initial active-bot roster.
- No automatic account/bot creation and no hard-coded Albert/#4 default.
- Integrated `Start-PlayerBots.bat` and `Start-Manager.bat`; old launcher names call the new ones.

The admin roster in this milestone shows **active bots only**, with owners, Combat/Farmer mode, level, HP/MP, and reported state/task. Full offline/unassigned roster, skills/inventory/equipment/XP inspection, durable logs, and admin create/assign/unassign workflows remain planned. Multi-bot dropdown redesign remains on hold.

## 1. Back up and apply the source update

Back up your Node folder, AAEmu source, private configuration, and bot ownership mappings before applying the update. Do not replace your database or run a SQL import.

The package contains `NODE/ArcheAge-PlayerBots/`, matching changed files under `SERVER/`, server unit tests under `SERVER/AAEmu.UnitTests/`, and original replacement-file copies under `ROLLBACK/`.

For the Node app, either use the clean `NODE/ArcheAge-PlayerBots/` copy and privately configure it, or copy its changed application/launcher files into your existing app after backing it up. Preserve your existing private `config.json` and `dashboard-auth.json`; neither is distributed.

Copy each `SERVER/` file into the matching path in your confirmed AAEmu source tree. This adds two Login account-service files and their Program registration, updates the Game bridge, and adds focused tests/documentation. Rebuild AAEmu Login and Game, then deploy each complete build through your normal process. Do not copy a single DLL from this package; no runtime binaries are supplied.

## 2. Enable the private Login account service

For a Manager on a different LAN PC, use [LAN-SETUP.md](LAN-SETUP.md) with the LAN source patch. The following defaults describe a same-PC installation.

Merge this section into **AAEmu.Login's private `Config.Local.json`**, keeping its other settings:

```json
{
  "PlayerBotsManager": {
    "Enabled": true,
    "Port": 17842,
    "Key": "REPLACE_WITH_A_PRIVATE_RANDOM_KEY_OF_AT_LEAST_32_CHARACTERS"
  }
}
```

Generate your own random key privately. Do not use the placeholder or post the key in chat. The service is disabled by default and listens on IPv4 loopback when enabled. An environment alternative is `PlayerBotsManager__Enabled`, `PlayerBotsManager__Port`, and `PlayerBotsManager__Key`.

The service uses the existing Login database connection and existing password verifier. It validates account bans and configured second-factor requirements, performs no account creation, and does not change stored password hashes. Accounts needing OTP, PC certificate, or ARS verification are **denied in this milestone**; the username/password-only website cannot bypass them.

## 3. Configure the Node manager privately

Retain your existing Game `serverUrl`, `apiKey`, and other settings. Add these fields to private `config.json`:

```json
{
  "authMode": "archeage",
  "accountServerUrl": "http://127.0.0.1:17842",
  "accountApiKey": "THE_SAME_PRIVATE_KEY_CONFIGURED_IN_AAEMU_LOGIN"
}
```

The example above shows only the fields to merge; it is not a complete controller configuration. Alternatively set `PLAYERBOTS_ACCOUNT_API_KEY` in the manager process environment. Both private service URLs must use loopback HTTP or HTTPS, without credentials embedded in the URL, unless the explicit LAN patch/settings in LAN-SETUP.md are used. The Login account service defaults to loopback-only; public hosting/proxy configuration is not supplied.

Start AAEmu Login and Game normally. Double-click `Start-PlayerBots.bat` for the controller and `Start-Manager.bat` for the website. Open `http://127.0.0.1:17841` using the exact printed address. Keep the service consoles open; the Node launchers do not start AAEmu itself.

`npm start` remains the controller command; `npm run manager` starts the website. The account mode does not generate a local dashboard password or expose service keys to the browser.

## 4. Verify with existing accounts

1. Sign in with an existing regular ArcheAge account. Verify that only its assigned companions are available and no Admin Manager button appears.
2. For an account without assignments, verify the empty state and that no companion is created.
3. Sign in with an existing administrator account. Verify that its own personal manager opens first, then use the Admin Manager button.
4. Verify all eight sidebar views and ordinary companion operations against your server.
5. Verify that a player cannot open `/admin` or request another account's bot by editing an ID.
6. Verify sign-out and changes to ownership/admin permissions.

This milestone derives administrator access from the maximum of the game account and its undeleted characters' access levels, using **100 or greater**. Missing account rows do not grant admin permission or trigger first-account promotion. Do not change real account permissions merely to make a test pass.

## Compatibility and rollback

Existing configuration without `authMode`, or with `authMode: "local"`, retains legacy dashboard-password authentication. Legacy mode is not account-scoped access and never exposes the Admin Manager. Explicitly select `"archeage"` to enable the new account flow; it fails closed when the matching services cannot verify identity/access.

To roll back: stop the manager, restore your backed-up Node application and configuration, disable `PlayerBotsManager` in private Login configuration, restore the original Login `Program.cs` and Game `PlayerBotsRemoteService.cs` from `ROLLBACK/`, remove the newly added ManagerAccounts source files/test if returning to the original tree, and rebuild/redeploy the original source through your normal process. No SQL rollback is needed because this update makes no schema changes.

## Security and remaining deployment work

The website remains loopback-only HTTP. It uses HttpOnly/SameSite Strict cookies, eight-hour sessions, session/CSRF rotation on login, logout invalidation, and Origin/CSRF checks for browser mutations. Account status, admin permissions, and assignments are rechecked for protected requests. Scoped Game bridge calls check ownership under the same mutation gate as remote owner changes.

Public hosting still needs a separately verified HTTPS/origin/session configuration, Secure cookies, and operational hardening. Do not expose this loopback dashboard by simply changing its listen address. Keep configuration, service keys, old dashboard credentials, and logs private. The package's `.gitignore` excludes local secrets but does not remove secrets already committed elsewhere.

## Verification performed here

- AAEmu Login and Game builds succeed with the installed dependencies.
- New account service tests: 3 passed, covering legacy/current hashes, incorrect/unknown credentials, bans, deleted accounts, configured second factors, private HTTP key/origin checks, and safe identity responses.
- Existing AAEmu password-verification tests: 16 passed.
- Node account-access/transport tests and the existing companion regression script pass, covering spoofed identity/role rejection, cross-owner reads/actions, Origin/CSRF checks, role/ownership revocation, service failure, logout/expiry, private bridge headers, and existing companion workflows.
- Browser fixture checks verify the shared form, all eight sidebar views, player/admin entry behavior, and responsive admin layout.

The screenshots and browser checks use artificial fixture data. They are not live-server screenshots. No live SQL commands were run and no real account credentials are included.
