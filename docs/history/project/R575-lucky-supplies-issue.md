> Historical project guide preserved on 16 October 2026. It may refer to separately supplied packages or a different checkout; deployment and test claims below are historical. Private local addresses are generalized. Read ../../CHANGES-AND-STATUS.md and ../../HISTORY-INDEX.md for current import scope.

# Lucky Supplies chest investigation

The screenshot shows Testelf level 18 after opening Jester's Coinpurses, and a large chest labelled Level 50 Lucky Supplies with “Can't select this target.”

Runtime evidence: at 21:04:17, item-use skill 23140 invoked a SpawnEffect creating NPC template 20029, object 974. The World sent NPC state and forwarded it into zone 181. The native integration log describes the World-authored NPC as `localSim=False`. This is a spawned NPC, not an inventory chest or the achievement UI.

The NPC template's name is Lucky Supplies, level is 50, model is 2690, scale is 1.5, faction is 1, AI is default (26), and `npc_interaction_set_id` is NULL. Its merchant/trainer/service flags are false. Level 50 is the template's level; these fields do not establish a level-50 interaction prerequisite.

The server received right-click interaction requests for object 974 at 21:04:38 and 21:04:59 and sent NPC interaction-list replies. The interaction handler composes actions from service flags, quest talk availability, and the template's interaction set. That template has no ordinary authored action menu. The exact returned packet contents and native selection restriction have not yet been decoded, so this is not proof of one specific missing interaction implementation.

The database also contains skill 43978, Salvage Lucky Supplies, and doodad-activity data with that name. This skill has water-related targeting properties and is not automatically an appropriate action to attach to the dry-land NPC. Trace the spawn/reward lifecycle before connecting those names or awarding arbitrary loot.

Spawn-effect rows 3605 and 3619 reference NPC 20029 with life_time 0 and no creator-death despawn. The runtime native relay made the spawned NPC hostile toward Testelf and removed buff 815 (Untouchable) immediately after spawn. That combination merits checking the native initialization/ownership path; it does not prove the chest should be attacked.

No NPC flags, reward tables, target rules, or character levels were changed during this investigation. Remaining work is to identify the intended interaction/loot path, confirm whether native initialization contains the needed state, implement the missing behavior if demonstrated, and verify expiry/cleanup.
