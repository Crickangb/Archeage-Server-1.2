> Historical project guide preserved on 16 October 2026. It may refer to separately supplied packages or a different checkout; deployment and test claims below are historical. Private local addresses are generalized. Read ../../CHANGES-AND-STATUS.md and ../../HISTORY-INDEX.md for current import scope.

# Patron product names

The user confirmed the product-name translations look good in-game. A separate three-heading client translation is prepared for the Benefits tab; see `Patron-benefits-headings.md`.

The user selected **Patron Subscription: 30 days** as the preferred wording and logged out before the World restart.

All 13 catalog products were translated in a fresh server database copy. Standard names are `Patron Subscription: 1 day`, `3 days`, `7 days`, `15 days`, `30 days`, `90 days`, `180 days`, and `365 days`. The five development products retain `[TEST]` and their authored tier, for example `[TEST] Patron Subscription: Tier 2, 2 days`.

Only those item names and their display localization entries changed. Prices, buy limits, duration values, account entitlements, product IDs, gameplay data, existing client translations, and the Patron purchase-rejection behavior are unchanged. The malformed no-expiry date display is a separate pending fix.

The fresh server backup is `F:\AAEmu-R575-compiled\Translation\Patron\2026-10-14\server-before-patron.sqlite3`. The verified copy is `server-patron-english.sqlite3` in the same folder. Both passed SQLite integrity checks, and an exhaustive comparison confirmed only the approved names/localization cells changed. Installed SHA-256: `2e36896c22c53146b47ed430c3ba419205f4caf793a93ca18e93643c80c8396f`.

The World restart and zone warming are handled by `inspection/install-patron-and-restart.ps1`. It keeps Login running and warms zones 129 and 181 so the next Gweonid login does not need to wait on a cold map. `sqlite-translation-audit/patron-installation.json` records successful World/map readiness when complete. The changes need confirmation in the Patron screen.
