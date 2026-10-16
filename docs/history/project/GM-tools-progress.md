> Historical project guide preserved on 16 October 2026. It may refer to separately supplied packages or a different checkout; deployment and test claims below are historical. Private local addresses are generalized. Read ../../CHANGES-AND-STATUS.md and ../../HISTORY-INDEX.md for current import scope.

# GM console repair — 15 October 2026

## Installed for live testing

The prior client log proved X2Gm was nil, including during GM-window initialization and button clicks. Read-only native inspection found that x2game-dev.dll only constructs its X2Gm scripting object if the separate engine development-mode predicate passes. Loading the development DLL through -devmode did not satisfy that predicate in this package.

Changed only the two-byte conditional jump guarding X2Gm object construction in x2game-dev.dll (file offset 3978549, 74 74 -> 90 90). The adjacent Test binding, normal x2game.dll, engine libraries and server GM authorization are unchanged. Original DLL and script are backed up in F:\AAEmu-R575-compiled\Translation\GMInterface\20261015-165217. Exact hashes and validation are in gm-interface-fix/installation.json.

Added Manage -> Quick Tools, providing Give item, Add gold, Add labor, Set level, Heal, Revive, Go to player, Summon player, Invisible, Visible, Character info, Reset cooldowns. Target-unit field uses player (self) or target (selected unit); teleport/summon use the separate online-player-name field. Gold is converted to copper. Numeric ranges are checked before emission. Set level raises levels through the normal experience path and refuses level reduction.

The original translated GM script is preserved; four root instructions execute the extra UI block after the original initialization. Original command IDs/functions, child prototypes and earlier instructions are unchanged. Root stack size and line-debug metadata were adjusted to accommodate the addition.

Server AAEmu.Game.dll changes: GM replies additionally appear in chat; health/mana changes synchronize with the authoritative native zone; SetAttribute(..., "level", ...) uses the canonical experience/ability level-up path. Server build passed with 0 errors (217 pre-existing warnings). Installed binary backups: F:\AAEmu-R575-compiled\TestLogs\GMTools-before-20261015-165337.

## Verified locally

- Lua 5.1 syntax check passed.
- Exercised all 12 real button handlers with a UI/native API mock: 13 expected requests, including two for Heal.
- Verified item arguments, gold-to-copper conversion, labor/level requests, teleport/summon names, visibility true/false, character info and cooldown reset.
- Invalid quantity, missing teleport name, invalid unit token and missing X2Gm emit no command.
- UI construction emits no commands.
- Patched native DLL differs only at the verified guard bytes; normal client unchanged.

## Pending — do not call the entire console fully functional

First live test requested: Manage, name Thedarknoise, Character info; open Quick Tools. Need verify X2Gm exists, native request reaches CSGmCommandPacket, parameters decode correctly, response is shown and two clients observe intended effects.

Existing server dispatcher still has placeholder handlers for housing administration, several event/territory/battleground actions, progression and other tools. Some legacy buttons have no command callback in the publisher's Lua (Mute chat and whisper controls). Exposing X2Gm fixes the common transport blocker but does not implement those missing systems. Existing SpawnMate currently substitutes an NPC and SpawnGimmick is still a placeholder. These need follow-up implementation; no claim they have been fixed or tested.

Current test backend log: F:\AAEmu-R575-compiled\TestLogs\Restarts\GMTools-20261015-165403\world.stdout.log. Login and World restarted; warming native zones 129 and 181. No client was automatically launched.
