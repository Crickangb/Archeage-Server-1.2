> Historical project guide preserved on 16 October 2026. It may refer to separately supplied packages or a different checkout; deployment and test claims below are historical. Private local addresses are generalized. Read ../../CHANGES-AND-STATUS.md and ../../HISTORY-INDEX.md for current import scope.

# Patron Benefits headings — compiled correction installed

**Runtime correction:** the user reported that the Benefits headings remained Chinese after the database installation. Inspection found the three literal Chinese headings hardcoded in `game/scriptsbin64/x2ui/premiumservice/tab_benefit.alb`. That compiled script overrides the database-based route assumed initially. The database installation passed structural checks but did not fix the visible headings.

A corrected compiled script is prepared under `output/native-patron-headings`. It replaces exactly three constants with Patron, Life Skills, and Combined. The edited chunk reparses successfully; all instruction words, function flags, debug data, and other constants are unchanged. After the user closed the client, installation completed and the installed hash matched the verified script. Original compiled script backup: `F:\AAEmu-R575-compiled\Translation\PatronBenefits\2026-10-14\tab_benefit-before-heading-fix.alb`. Receipt: `native-patron-headings/installation.json`. No benefit values changed. The user requested a full restart separately; `latest-local-restart.json` records its completion. In-game confirmation of this corrected script remains pending.

The three Chinese column headings come from client `ui_texts` IDs 90001, 90002, and 90003 (`new_premium_level_1`, `_2`, `_3`). They are separate from the server's Patron product names.

Prepared English headings: **Patron**, **Life Skills**, **Combined**. Combined means the two memberships together. Longer alternatives are offered to the user; these shorter labels fit the existing narrow columns.

Only those three UI text values and their corresponding localization slots changed. Benefit numbers, grades, entitlement calculations, prices, earlier achievement/ArchePass translations, and server data are preserved. Full SQLite integrity, schema comparison, and allowed-cell checks passed.

Backup: `F:\AAEmu-R575-compiled\Translation\PatronBenefits\2026-10-14\client-before-benefit-headings.sqlite3`.

Working copy: `client-benefit-headings-english.sqlite3` in the same folder. Detailed hashes and the original/replacement values are in `sqlite-translation-audit/patron-benefit-headings.json`.

The user chose the short headings and confirmed the client was closed. Installation completed using `inspection/install-patron-benefit-headings.ps1`; the installed hash matches the verified copy. Receipt: `sqlite-translation-audit/patron-benefit-headings-installation.json`. No server restart is needed for these three client headings. In-game confirmation is pending.
