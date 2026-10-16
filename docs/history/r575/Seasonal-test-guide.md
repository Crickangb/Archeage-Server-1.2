> Historical project guide preserved on 16 October 2026. It may refer to separately supplied packages or a different checkout; deployment and test claims below are historical. Private local addresses are generalized. Read ../../CHANGES-AND-STATUS.md and ../../HISTORY-INDEX.md for current import scope.

# Testing all seasonal achievements

Use separate backed-up **test copies** of the client and server databases. These are gameplay visibility changes, not translation changes. The attached SQL is prepared but has not been run.

1. Close the client and stop the test World server before replacing database files. Keep copies of both installed files: client `F:\AAEmu-R575-client\game\db\compact.sqlite3` and server `F:\AAEmu-R575-compiled\World\Data\compact.sqlite3`.
2. In a SQLite editor, open each separate test copy and inspect `achievements`. Filter `achievement_sub_category_id` to **31, 32, 33, 34**: Spring, Summer, Autumn, Winter.
3. Set `is_hidden` to **`f`** and `season_off` to **`f`** for the intended seasonal entries. The prepared SQL limits changes to these four subcategories and leaves protected `DO NOT TRANSLATE` entries untouched.
4. Install the test copies while the programs are stopped, restart World and the client, open achievements, and select **All**. Check that all four subcategories appear.
5. Test actual progress and reward claims on a test character. Making an entry visible does not start its festival, spawn event NPCs, satisfy level/race prerequisites, or make a quest completable. Each event's world setup still needs testing separately.
6. To undo visibility changes, restore the before-test copies. Do not use this as a permanent enable-everything setting without reviewing the individual entries.

`is_hidden='f'` makes an entry eligible to be shown. `season_off='f'` removes that entry's season-off state. Neither field is a calendar schedule for the entire world.

The translation and reward tables remain untouched by this SQL. Existing English remains unchanged. Autumn/Fall naming can be chosen separately.
