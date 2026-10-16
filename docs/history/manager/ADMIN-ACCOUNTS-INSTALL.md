> Historical project guide preserved on 16 October 2026. It may refer to separately supplied packages or a different checkout; deployment and test claims below are historical. Private local addresses are generalized. Read ../../CHANGES-AND-STATUS.md and ../../HISTORY-INDEX.md for current import scope.

# Admin Manager — icons, ownership and bot accounts

This patch extends the working Admin Manager. It requires **both Login and Game source updates and rebuilds**, plus the Node overlay. It does not change your working connection keys, IP settings, databases or game/login ports.

## Included

- Real local game icons beside equipment, inventory, active skills and passives; missing artwork keeps a labelled placeholder and readable name.
- A separate Ownership page: unassigned companions and groups by player owner, with reviewed assignment/unassignment. Dismiss a bot before changing its owner.
- Bot Accounts and Created Bots pages with account headings and the bot characters beneath them. Existing characters such as Albert keep their identity; factory-created characters also show their creation build.
- Existing dedicated account registration by username, editable capacity (default **four**), and automatic additional account provisioning when every registered account is full. The pool is limited to 20 accounts.
- Creation chooses the first available account in pool order. You pick name, level, race, gender and a server build preset; the page lists that preset's skill trees. Unlocks follow the native preset and level.
- A confirmed creation starts an unassigned character at its race spawn. Dismiss it, assign a player owner in Ownership, then start it again for personal-manager use.
- Generated account passwords are available only through the authenticated, freshly checked administrator interface. Existing account passwords are never fetched or changed.
- Encrypted local credential storage, durable audit records without passwords, one-use reviews, duplicate-confirmation protection, and pending-account recovery across Manager restarts.

Signing in creates no account or bot. Additional accounts are created only as part of an explicitly reviewed bot creation when the pool is full and automatic account creation is enabled. They receive ordinary Game access level 0, not administrator permissions.

## Install on your two PCs

1. Back up the current Manager folder on **YOUR_LAN_IP**, the Login/Game source and deployed folders on **YOUR_LAN_IP**, and both MySQL databases. Keep the existing owner/job/known-bot/creation-roster files.
2. Stop Manager. Merge `NODE/ArcheAge-PlayerBots/` into `C:\ArcheAge-PlayerBots`, replacing matching files and including the complete new `skill-icons` folder and mapping. The package includes the existing item icons too. Keep your existing `config.json`, authentication files, logs and private credentials.
3. Merge `SERVER/AAEmu.Login/` and `SERVER/AAEmu.Game/` into the matching **source** project directories, preserving the relative paths. These are C# source files; putting them beside old executables alone does not activate the patch.
4. Rebuild **AAEmu.Login** and **AAEmu.Game** using your existing .NET 10 build workflow. Stop the deployed services and replace their complete matching build output, retaining configuration and data. Restart Login, then Game.
5. The package contains `SERVER-RUNTIME/AAEmu.Game/Configurations/PlayerBotsAccounts.json`, configured for your existing **PlayerBot1 (#3)** and **PlayerBot2 (#4)** accounts, with capacity four. Copy this beside the deployed Game executable under `Configurations`, if no account-pool file already exists. Preserve an existing pool file rather than overwriting it.
6. Start Manager and open `http://127.0.0.1:17841`. Hard-refresh the browser, sign in with your normal administrator account, and open Admin Manager → Bot Accounts. If an account shows **Game account missing**, use **Prepare Existing Account**, review and confirm. This verifies its existing Login username and creates only its ordinary Game account row. No existing password changes.
7. Confirm both account headings and **Albert, character #4**, beneath **PlayerBot2, account #4**. The repeated number four is coincidental: account IDs and character IDs are separate.
8. Create one disposable test bot and verify its name/build, character in Game, account grouping, then owner assignment and normal personal access before using larger numbers. New MySQL provisioning has not been exercised against your remote server by this development session.

`AAEmu.UnitTests` files are optional developer checks. You do not launch or deploy UnitTests to run the game or Manager. No SQL schema migration or full database import is required by this patch.

## Account example

```text
PlayerBot1 — account #3 — up to 4 bot characters
  Bot name / character ID / build / online status / player owner
PlayerBot2 — account #4 — up to 4 bot characters
  Albert / character #4 / current saved build / status / assigned owner
accountBot_<unique suffix> — generated only when the existing pool is full
  Newly created bot and later bots added to that account
```

Accounts must be logged out of the normal game client before identity creation. An online account with spare space is unavailable; Manager does not generate another account merely because existing accounts are logged in. Deleted characters do not occupy this Manager capacity. Capacity counts undeleted characters, including offline ones. This storage policy is separate from the existing limit on simultaneously active companions for a player owner.

To add another existing account or change capacity/automatic creation, use the reviewed form in Bot Accounts. Only dedicated accounts belong in this pool: their characters are treated as bots and are excluded from human-owner searches. Use a username with one unambiguous, enabled Login account and no required second factor. Administrator accounts are refused by Game enrollment.

## Server Setup explained

| Connection | Runs where | Purpose |
| --- | --- | --- |
| `127.0.0.1:17841` | Your Manager PC, `.30` | Browser → local Node Manager |
| `YOUR_LAN_IP:17842` | Server `.16`, Login | Manager → existing ArcheAge account checks and private bot-account provisioning |
| `YOUR_LAN_IP:17840` | Server `.16`, Game | Manager → roster, details and administrator actions |

Keep your working `serverUrl`, `accountServerUrl`, `trustedLanServerIp`, matching private keys and exact client-IP allowances. The Login bridge is private service traffic. The existing explicit trusted-LAN exception remains; use HTTPS for deployments outside this agreed private LAN. The browser does not connect directly to MySQL or receive bridge keys.

The Game bridge indicator means a roster request succeeded. It does not independently test firewall configuration, every port or every account. Offline HP/MP, skills and items are last saved data. Live tasks require an online bot. Combat logs are sampled during inspection, not a complete unattended event stream.

## Password storage and interrupted operations

Manager stores generated credentials under `.admin-private/`, using AES-256-GCM with a separate local `accounts.key`. Both stay outside static assets and are excluded from Git and this package. The application administrator and OS account protecting these local files remain trusted; encryption does not protect against somebody who can read the key as well. Back up the encrypted file and its matching key together. Losing them does not reset the account's password in Login.

Viewing a generated password requires current admin permission and a same-origin session/CSRF request; only the fact that credentials were viewed is audited. The view starts masked and clears when closed. Existing PlayerBot1/PlayerBot2 passwords are not stored by this patch.

If account provisioning or bot creation reports an unknown outcome, refresh before retrying. A pending generated username/password is saved before dispatch. Retrying reuses those credentials; it never overwrites a different existing account's password. If Game registration completed but its reply was lost, roster refresh settles the pending credentials. A newly provisioned account may remain empty if later character creation fails; it is available for a subsequent reviewed creation. The patch does not automatically delete accounts or characters.

## Appearance: supported versus planned

All new companions start at level 1. Starting level is disabled in the creation form; Manager rejects other levels and Game independently rejects non-level-1 creation requests. Existing companions retain their saved level. New companions must progress through normal gameplay.

Current: supported race/gender combinations come from loaded character templates; the native factory chooses an existing non-NPC default customization where available. It persists the character's actual model and starter hair using the existing server path.

Planned: choosing hair, hair colour, skin colour, face presets and a character preview. The native model format contains these fields, but safe selectable catalogs, validation and a web preview are not implemented in this patch. Arbitrary custom skill-tree combinations and individual skill/item editing in the Admin Manager are also outside this update; use the supplied build presets.

## Game startup correction

This package includes the corrected Game `Program.cs`. `PlayerBotsKnown.json` (a list of bot IDs) and `PlayerBotsAccounts.json` are runtime documents read by the bot services. They must be excluded from the ordinary host settings loader. Copy the included Program.cs into Game source and rebuild/deploy Game along with the included service files. Keep both runtime documents; do not delete or rename them. The earlier package could stop Game with "Top-level JSON element must be an object" when it tried to load the known-ID list as settings. Controller "fetch failed" can then be a consequence of Game being stopped.

## Rollback procedure

Restore the backed-up Node and server source/build files, keeping current runtime registries, logs and the matching private vault/key. Reverting code does not undo confirmed account creation, character creation or ownership changes. Keep the account pool data to avoid losing the list of generated accounts. Do not replace your databases with an old backup merely to revert this interface patch.
