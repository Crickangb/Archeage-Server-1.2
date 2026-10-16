> Historical project guide preserved on 16 October 2026. It may refer to separately supplied packages or a different checkout; deployment and test claims below are historical. Private local addresses are generalized. Read ../../CHANGES-AND-STATUS.md and ../../HISTORY-INDEX.md for current import scope.

# R575 GM console test

Created `F:\AAEmu-R575-client\Start-GM-Test.bat`, matching the normal local launcher with one added option: `-devmode`. Normal launcher unchanged.

Evidence: running archeage process used x2game.dll; that library contains the toggle action name but lacks TOGGLE_GM_CONSOLE, WRITE_GM_CONSOLE and the gm_console.lua loader path. Installed x2game-dev.dll contains those events and loads scripts/x2ui/hud/gm_console.lua. Read-only inspection of archeage.exe confirms that its loader checks the literal -devmode option and selects x2game-dev.dll when present.

Dedicated test account r575gm has access 100. Character Thedarknoise is id 1003. Character was created while account access was temporarily 0, then restored to 100. GM-mode character creation still has an unresolved client issue.

User screenshot confirmed that Start-GM-Test.bat enables the GM window. Native GM commands have server handlers but individual operations require testing.

Installed English translations for all 182 Chinese/Korean text constants in the compiled gm_console.alb. Existing English and native command identifiers were retained. Reparsed the result and verified identical instruction words, flags, debug information and all other constants. Receipt and exact string changes are in output/gm-console-translation. Original script backup is under F:\AAEmu-R575-compiled\Translation\GMConsole. Restart the client with Start-GM-Test.bat to load translated labels; English rendering awaits user confirmation.

Latest user report: pressing GM buttons appears to do nothing. No command functionality confirmed; investigate client command emission, account authority and server/native-zone dispatch next session. User ended testing for the night. Do not continue game changes or restart services until resumed.
