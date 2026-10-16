> Historical project guide preserved on 16 October 2026. It may refer to separately supplied packages or a different checkout; deployment and test claims below are historical. Private local addresses are generalized. Read ../../CHANGES-AND-STATUS.md and ../../HISTORY-INDEX.md for current import scope.

# Change log

## 16 October 2026

- Imported upstream R575 d2d0b3a with GamingLiberated server changes, tests and license notices.

- Added launcher source, Lua patch payloads, reviewed translation changes, setup and test guides.

- Golden Ruins: follow changed entry destinations rather than treating an old map readiness result as entry failure; warm a teleport destination before sending the client there. Full suite: 7,622 passing; World build: zero errors.

- Community: guard missing quest notification data so it cannot abort Guild Mission and Community window construction. Seven cases checked against source and compiled replacement; live confirmation pending.

- User verified seed inventory persistence and Guild Mission progress. Both characters subsequently reproduced the Community failure after mission actions.
