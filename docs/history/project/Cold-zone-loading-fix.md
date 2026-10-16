> Historical project guide preserved on 16 October 2026. It may refer to separately supplied packages or a different checkout; deployment and test claims below are historical. Private local addresses are generalized. Read ../../CHANGES-AND-STATUS.md and ../../HISTORY-INDEX.md for current import scope.

# Cold-zone loading fix — staged 2026-10-15

The server previously waited synchronously for an outdoor zone to start while processing world-entry packets. A startup longer than the connection timeout could disconnect a player before the zone became ready.

The staged change waits in the background, leaving packet processing available. Requests are cancelled when a player leaves or disconnects; duplicate requests share the pending operation. Early client load-completed notifications wait until the character has spawned. During an outdoor handoff, the old zone presence remains until the destination is ready. An unidentified zone connection disconnect no longer triggers recovery for every player.

Validation: 10 deferred-entry tests, 8 outdoor supervisor tests, and 2 world-exit tests passed. The World server build succeeded with zero errors and 213 existing warnings.

Staged assemblies: `cold-zone-fix/server/AAEmu.Game.dll` and `AAEmu.World.dll`, with matching PDB files. Installed on 2026-10-15; assembly hashes verified. Backup: `F:\AAEmu-R575-compiled\TestLogs\ColdZone-before-20261015-184545`. Live cold-zone entry testing remains pending. Preserve runtime configuration and databases. Back up the installed assemblies before replacement, and restart only after both clients have logged out to save progress.

This does not verify every dungeon or teleport route. A live test must confirm entry into a previously unhosted outdoor zone without a disconnect.

Restart completed: Login/World ports ready; zones 181 and 129 reported ZoneLoaded at 18:49:19 and 18:49:23. The unidentified zone-disconnect guard also ran during readiness probing without a global recovery. Startup still reports separate existing content-data errors; these were not part of this fix. Logs: F:\AAEmu-R575-compiled\TestLogs\Restarts\GMTools-20261015-184631.
