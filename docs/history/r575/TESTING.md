> Historical project guide preserved on 16 October 2026. It may refer to separately supplied packages or a different checkout; deployment and test claims below are historical. Private local addresses are generalized. Read ../../CHANGES-AND-STATUS.md and ../../HISTORY-INDEX.md for current import scope.

# Gameplay test checklist

Use two different accounts for multiplayer checks. Record character, map, action, time, expected result and actual result. A historical Discord report is a test lead, not confirmation of a current bug.

| Test | Steps | Expected result |

| --- | --- | --- |

| Inventory and saving | Buy seeds; note total; split and merge stacks; log out; restart; inspect | Same total, stack sizes and items; user reports this worked |

| Guild Missions | Open Community > Guild > Mission; accept; perform listed objective | Counter updates; complete separately and verify rewards, XP and reset |

| Community failure | Try shortcut and menu on both characters; relog and repeat | Window opens; currently fails on Thedarknoise |

| Cold map travel | Finish initial entry, then teleport to a cold valid destination | Map becomes ready before travel; no kick or stuck loading |

| Golden Ruins regression | After installing the pending fix, swim in sea, then `/teleport goldenruins` | Safe wait then entry; early command during initial loading is rejected clearly |

| Boat boundary | Two players, one seated and one standing aboard; cross zone boundary | Both remain connected and boat remains controllable |

| Repair and vendor | Damage gear, note money/durability, repair, buy and sell one item | Correct cost, durability, quantity and saved balance |

| Combat | Attack with each skillset; observe resources and enemy response | Correct resource bars, effects, retaliation and death/respawn |

| Dungeon | Enter eligible instance, observe HUD, exit and return | Correct spawns, instance membership and exit location |

| Patron | Compare active/inactive dates, relog, observe expiry boundary | Display matches saved dates; entitlement/purchases remain unfinished |

If inventory disappears visually, stop modifying items and preserve logs/database before further tests. For a crash, preserve timestamped client log and dump locally before another launch overwrites evidence. Do not commit these files to Git.
