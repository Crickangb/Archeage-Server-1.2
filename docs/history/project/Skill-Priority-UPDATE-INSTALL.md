> Historical project guide preserved on 16 October 2026. It may refer to separately supplied packages or a different checkout; deployment and test claims below are historical. Private local addresses are generalized. Read ../../CHANGES-AND-STATUS.md and ../../HISTORY-INDEX.md for current import scope.

# Skill Priority and Equipment update — October 9, 2026

Install over the working Companion Control update (dashboard 0.17.8.6). Includes the Free Nearby Follow fix.

1. Stop the Node Companion Manager and AAEmu.Game. Back up the matching source files and runtime Data folder.
2. Copy MANAGER contents into your existing playerbots-manager folder, replacing matching files. Keep your settings, credentials and request history. Restart Node after the Game update.
3. Copy SERVER-SOURCE contents into your AAEmu source checkout, replacing matching files. Rebuild AAEmu.Game and deploy its matching build output to the actual Game runtime folder. Source files in the runtime folder alone do not install the change.
4. Restart Game and Node, then refresh the browser (Ctrl+F5).
5. Spawn a companion, open Skill Priority in the player sidebar, move skills up/down and choose Save Priority. Use Automatic Priority restores the normal role rules.

Priority is saved per companion in the Game runtime Data/PlayerBotsSkillPriorities.json. Preserve this file when deploying future builds. No database edits or settings changes are needed.

The bot checks the highest usable learned combat skill on each combat decision. A cooldown, unsuitable target, range restriction or insufficient mana skips the skill and tries lower ones. When ready, a higher skill wins again on the next eligible decision; it does not interrupt a cast in progress. Current global cooldowns and the mana reserve remain in place. Learned offensive active skills are listed with their existing icons. Healing retains health checks, passive skills remain automatic, and friendly buffs are not added to this offensive priority page. An unlisted newly learned skill uses the normal role fallback below saved skills until you arrange it.

Equipment: a two-handed staff and shield cannot remain equipped together through the corrected item move. When spawning an existing companion with that invalid combination, its shield moves to its bag if there is room. Both items are kept. If the bag is full, free a slot and dismiss/spawn again (the five-minute cooldown still applies), or manually unequip one item. New conflicting equips fail when the bag has no space.

Gathering: live companion polling now preserves resource marker/list buttons instead of replacing them mid-click. Actual search result changes still redraw the results. This fixes a refresh interference cause; server route errors and the 25-metre hostile protection still apply. Bridge deck navigation is not solved by this update.

Validation: all 1,157 native tests and 23 dashboard tests passed, including cooldown skipping/return, saved order reload, draft preservation, authenticated priority saving, resource marker stability, two-handed equip compatibility and item preservation. Live game verification remains after installation.
