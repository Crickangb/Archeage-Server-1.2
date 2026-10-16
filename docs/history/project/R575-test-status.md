> Historical project guide preserved on 16 October 2026. It may refer to separately supplied packages or a different checkout; deployment and test claims below are historical. Private local addresses are generalized. Read ../../CHANGES-AND-STATUS.md and ../../HISTORY-INDEX.md for current import scope.

R575 local test
==============

Server: F:\AAEmu-R575-compiled
Client: F:\AAEmu-R575-client
Source branch: client_version/zone-10.0.2_r575
Source commit: 1e0180de52f9fce000daa8da3c8a58a746421de3

Confirmed
---------
- Login and World start successfully against separate local test databases.
- The native Gweonid zone registers as zoneId 129, instanceId 0.
- Manual client login reaches server selection and character creation.
- Local game account: r575test / test1234 (temporary test credentials).
- Testelf was created with CharacterManager.Create and saved in aaemu_r575_test_game: id 1001, account 2, male Elf, level 1, Fight skillset, standard Gweonid spawn (zone 129).
- The guarded one-time seed script was compiled, executed successfully, and moved out of Scripts. Original script-loading configuration was restored.
- User screenshot confirms Testelf entered Gweonid successfully: player model, terrain, NPCs, quest markers, map, chat channels and action bars are visible.
- Screenshots after leaving and re-entering confirm level 15 and the prior quest remained; new quests and quest items were received.
- `/teleport Marcala` selected zone 233 (o_seonyeokmari), at (20140,24768,189). World refused the transfer because no native host for zone 233 was loaded, and returned Testelf to character selection.
- Marcala's native host was subsequently started and finished loading; NPC traffic from zone 233 is confirmed. Start-Marcala.bat is saved beside the other test start files. Player entry into Marcala awaits verification.
- A later handoff failed to Silent Sea zone 289. Its host was started, and the user confirmed Testelf could enter the world afterward.
- Available main-world zone inventory: 163 native folders, 147 matched named content zones, including 30 names beginning o_. The batch prioritized zone 289 and Auroria, launching 25 additional hosts (27 total). 24 additional hosts had confirmed native loading at verification; the final host was still loading.
- Full simultaneous startup exceeded practical test-PC capacity: committed memory reached about 65.7 GiB and Windows expanded its pagefile. The batch controller was stopped, leaving zone hosts running. Start-All-Main-Zones.ps1 is saved with a 27-host cap and memory guard; it does not currently boot every zone.
- On October 13 the user stopped map hosts and tested creation again. Video confirms an entered name and enabled Confirm close the dialog without creating a character. The callback trace is recorded in C:\Users\YOUR_USER\Documents\ArcheAge\ArcheAge.log; it confirms the callback and native preview exist. Earlier claims that the preview was missing were based on an earlier state and do not explain this failed attempt.
- Native read-only checks during the failed attempt confirmed a registered preview, a valid model reference/type, a creatable race/gender template, unrestricted per-race creation statuses, and starter skillset 7. Character creation remains unresolved; lowercase input also failed.
- Start-Test-Server.bat and .ps1 are now installed in F:\AAEmu-R575-compiled. They guard the isolated test schemas, check MySQL, start/reuse Login and World, and start/reuse Gweonid and Silent Sea 289. Executed successfully; core server ports were already listening. The temporary preview trace remains installed with exact original backup in LoginUiBackup.
- Discord supplied a lead about GM-mode character creation limits. Verified aaemu_r575_test_game.accounts account 2 had access_level 100 and Testelf existed; EnterWorldManager enables native gmFlag when account access >=100. Temporarily changed ONLY account 2 to access_level 0 to test normal client mode. Testelf's character access remains 100. Original account value is recorded in F:\AAEmu-R575-compiled\TestLogs\r575test-access-before-normal-creation.json. Full client restart/relogin is required; creation outcome awaits user verification.
- Confirmed success after normal-account relog: user created Sandra through the regular race/skillset/appearance/name flow. Database verified id 1002, account 2, race 5 (Hariharan), gender 1, level 1, character access 0. Testelf (id1001) remains level15, access100. Client showed explicit name rejection for prior attempted names, then accepted Sandra. GM mode was a blocking condition; normal-mode name validation still applies. Exact cause of rejecting the other names is not yet established.
- Original handle_name.alb was restored from LoginUiBackup, removing the temporary preview diagnostic. Keep account 2 at normal access 0 for normal creation; its previous100 value is saved. Baseline startup now includes Rainbow Fields 146 for Sandra in addition to Gweonid129 and SilentSea289. Sandra's world entry still awaits confirmation.
- Sandra's first world entry failed because the actual destination is zone 187 (e_rainbow_field_2), whereas the initial starter used legacy146. World explicitly logged EnterZone refused zoneId187. Removed the obsolete146 host and corrected Start-Test-Server.ps1 to load Rainbow187/188/189 alongside129/289. All current Rainbow hosts show NPC activity after loading; awaiting retry. A separate initial UnitStateCharacterSerializer NullReference error also appeared before this handoff and remains a potential follow-up if entry still fails.
- Implemented outdoor-zone on-demand startup in the R575 World source, deployed its World DLL/PDB and enabled OutdoorZones in the isolated test configuration. EnterZone and handoff wait for ZoneLoaded; handoff keeps source-host presence until destination readiness. Per-zone locking prevents duplicate on-demand starts. Failed startup retries and a maximum of8 hosts bound startup; owned hosts without players retire after5 minutes. Dungeon supervisor remains separate.
- Four focused tests passed: concurrent entrant/start-once/wait readiness; timeout cleanup and retry; reuse loaded zones and avoid dungeon copies; host capacity and missing data refusal. World Release build passed with existing warnings. Previous World DLL/PDB and Config.Local.json are backed up in F:\AAEmu-R575-compiled\TestLogs\Before-Outdoor-Autostart.
- Start-Test-Server now starts/reuses Login and World only when outdoor automation is enabled. Native logs for automatic hosts: World\TestLogs\Outdoor\<zoneId>\zone.log. World restarted successfully without any prestarted map hosts. User cold-entry test is pending; do not claim full live verification until observed.
- Native Korean challenge authentication is required and is enabled in Login/Config.Local.json.
- Original source pack and old server/player databases remain untouched.

Client fixes
------------
- Restored ID/password visibility in the shipped 64-bit compiled login scripts and the loose Lua fallback.
- Exact original files are backed up under F:\AAEmu-R575-compiled\LoginUiBackup.
- Browser startup crash was traced to `Check failed: !locale.empty()` in libcef.dll.
- Avoid `-lang en_us`: it collides with Chromium's language switch. The direct shortcut uses `+locale en_us`.
- Start the client with F:\AAEmu-R575-client\Start-Local-Test.bat.
- The old AAEmu launcher profile is separate. Its authenticated-ticket mode did not start this client correctly during testing; use the direct shortcut.

Unverified / outstanding
------------------------
- Character-name confirmation dialog currently does not submit a creation request.
- Combat and broader gameplay still need testing; movement, quest acceptance/items, and retention of level/quest after re-entry have been observed.
- UI contains a mixture of Korean, Chinese and English text.
- Mistmerrow is still marked closed in server content data. Auroria access has not yet been tested.
- The saved client shortcut connects directly to Login on 1237.
- Fullscreen/borderless positioning and persistent screen brightness are not yet verified as fixed.

Service ports
-------------
Login 1237; Login internal 1234; World 1239; Zone link 1240; Stream 1250.

Start order after reboot: MySQL, Start-Login.bat, Start-World.bat, Start-Gweonid.bat, then the direct client shortcut. Allow World and Gweonid to finish loading. Do not start duplicate services while these processes are already running.
