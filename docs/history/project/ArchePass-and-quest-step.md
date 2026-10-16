> Historical project guide preserved on 16 October 2026. It may refer to separately supplied packages or a different checkout; deployment and test claims below are historical. Private local addresses are generalized. Read ../../CHANGES-AND-STATUS.md and ../../HISTORY-INDEX.md for current import scope.

# ArchePass and quest labels — installed

Installed after the user closed the client on October 14. The installed database and four UI files match the verified prepared copies. Both database and UI backups are preserved; `sqlite-translation-audit/archepass-installation.json` records their paths. The achievement translations remain installed. Prices, rewards, seasonal flags, Patron entitlement, and quest progress are unchanged. In-game validation is still pending.

The user subsequently confirmed that quest titles display `Not started` correctly in-game. ArchePass screen validation is still pending. A separate disconnect coincided with a cold zone-181 startup; the host reached ZoneLoaded three seconds after the game connection dropped. This is evidence for a startup/keepalive timing problem, not a confirmed translation failure.

ArchePass translation: 207 Asian-language fields translated in `arche_passes` and `arche_pass_categories`, including the Chinese publisher pass 102 (`ArcheAge Returns`). Existing English is preserved. The embedded typo `Frest Start Pass` was corrected to `Fresh Start Pass` when applying that candidate to its Asian source. No remaining eligible ArchePass fields need translation review in this pass.

SQLite integrity and exhaustive allowed-cell checks passed. Prices, rewards, enabled flags, expiry dates, achievement translations, and unrelated tables are unchanged. A fresh before-ArchePass database backup is saved at `F:\AAEmu-R575-compiled\Translation\ArchePass\2026-10-14\client-before-archepass.sqlite3`.

The translated working copy is `client-archepass-english.sqlite3` in the same folder. Detailed changes and hashes are in `sqlite-translation-audit/archepass-changes.csv` and `archepass-translation.json`.

Quest labels: two compiled UI files and their source copies are staged under `native-ui-step1`. Four string constants change `???` to `Not started` and `%d. ???` to `%d. Not started`. Compiled instruction words are unchanged, and both edited chunks reparse successfully. No titles are revealed, and quest progress/eligibility is unchanged.

Installation used `inspection/install-archepass-and-quest-labels.ps1`, which refuses to run with an active client, changed source files, or missing backup. `archepass-installation.json` records the successful installation. Runtime validation remains necessary.

Alternative quest labels: **Not started** (clear progress status, recommended), **Undiscovered** (more atmospheric), **Locked** (suggests a prerequisite or restriction). Current prepared wording is Not started.

Seasonal visibility remains unchanged. See `Seasonal-test-guide.md` and `Seasonal-test-visibility.sql` for a separate test-only change.
