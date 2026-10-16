> Historical project guide preserved on 16 October 2026. It may refer to separately supplied packages or a different checkout; deployment and test claims below are historical. Private local addresses are generalized. Read ../../CHANGES-AND-STATUS.md and ../../HISTORY-INDEX.md for current import scope.

# Expedition party count and terrain preload

Changed `Expedition.Create.PartyMemberCount` from 5 to 1 in `F:\AAEmu-R575-compiled\World\Configurations\Expedition.json`. A comparison of the parsed configuration confirmed no other setting changed. Original backup: `F:\AAEmu-R575-compiled\TestLogs\Expedition-before-one-member-2026-10-14.json`.

This controls the minimum count of eligible founding party members for an Expedition (guild). The creation code still requires an actual party and enforces level, faction, existing-guild, name, and cost checks. It does not by itself add a party-free solo creation path.

`World.PreLoadTerrain` remains false. In `WorldManager.LoadHeightMapFromClientData`, true loops through terrain cells and loads their heightmaps. It does not start native zone hosts or dungeon instance copies. Navmesh preload is another separate setting; starting dungeon copies belongs to the native ZoneHost supervisor. The user's request to enable terrain preload was conditional on it starting instances, so that condition does not apply.

The test World is restarted to load the Expedition setting, and Gweonid zones 129 and 181 are warmed. `expedition-setting-installation.json` records readiness once startup completes.
