> Historical project guide preserved on 16 October 2026. It may refer to separately supplied packages or a different checkout; deployment and test claims below are historical. Private local addresses are generalized. Read ../../CHANGES-AND-STATUS.md and ../../HISTORY-INDEX.md for current import scope.

# Golden Ruins specialty-workbench crash — 16 October 2026

User reproduced a client crash on both Testelf (20:08:30) and Thedarknoise (20:09:24), then supplied video 2026-10-16 20-09-15.mp4. The object is the Golden Ruins Specialty Workbench. Both dumps report 0xC0000005 at x2game-dev.dll RVA 0x81700b. The instruction dereferences a null record pointer. Crash files and screenshots are preserved locally and excluded from Git.

## Data finding

Both source and runtime main_world spawn files contained two obsolete template 12290 workbenches, at (17300,27457,140.095) and (17260.49,27534.59,141.701). Their DoodadFuncCraftPack functions reference IDs 839/840, which are absent in both client and server compact databases. This strongly supports a broken-data cause for the matching native interaction crashes; private native symbols are unavailable.

Template 16314 has the same name/model and valid interaction definitions, but its Golden Ruins recipes 11565/11581 are explicitly disabled in both databases. Enabling or substituting them would require a separate crafting/economy review. No recipe, database, client binary or interaction record was modified.

## Installed mitigation

Remove only the two obsolete 12290 spawns. Every remaining JSON array entry is unchanged; the staging tool also preserved the exact bytes of remaining entries. Source and runtime input hashes matched before installation; installed output hashes matched the validated staged file. Full original source/runtime files were backed up locally under TestLogs/GoldenWorkbench-before-20261016-201857. Startup/live verification pending at installation time.

This prevents approaching these two spawned objects from reaching the invalid interaction path. It does not restore Golden Ruins specialty crafting or claim that every obsolete workbench elsewhere is safe. Next: verify both objects are absent, then audit other spawned interaction references before deciding which retired services to restore.
