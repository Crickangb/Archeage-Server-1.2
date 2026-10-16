> Historical project guide preserved on 16 October 2026. It may refer to separately supplied packages or a different checkout; deployment and test claims below are historical. Private local addresses are generalized. Read ../../CHANGES-AND-STATUS.md and ../../HISTORY-INDEX.md for current import scope.

# Guard, NPC animation and visibility investigation — 16 October 2026

Video: 2026-10-16 19-37-33.mp4, duration 26.95 seconds, Golden Ruins. Eight sampled frames saved locally. User observes NPCs sliding/strafe-moving instead of walking/running and guards shouting about criminals without an apparent attack/arrest.

Separate screenshot: two clients side by side. Thedarknoise client shows both characters; Testelf client does not show Thedarknoise. Both display closed-zone warning for zone key 281. Test GM Visible before deciding this is a player-spawn/visibility defect.

Server logs during the video show repeated CSMoveUnitPacket exceptions: IndexOutOfRangeException in Dictionary.Add, SusManager.AnalyzePlayerDeltaMovement line 75, called from Transform.FinalizeTransform. CSMoveUnitPacket broadcasts player movement after FinalizeTransform, so these exceptions abort the packet before its broadcast. This is a concrete defect; it is not yet proof of the missing character's full cause.

SusManager uses ordinary mutable dictionaries for player and mount samples without synchronizing movement reads/writes or teleport resets. Pending correction locks each complete sampling operation and its corresponding reset. Player and mount stores use separate locks. Stress regression tests are being validated; no deployment yet.

Guard requests in the same period include successful casts and many NoTarget or CooldownTime results. Resolve skill identity/targets and actual wanted/criminal state before changing arrest rules. Current server code supports arrest states, court escort, trial/sentence, and a wanted-death court path; a guard shout alone does not verify this chain works.

NPC animation remains unresolved. The movement relay parses and rewrites UnitMoveType, including velocity, orientation, DeltaMovement, stance and actor flags. Need actual moving-NPC samples and observer comparison before modifying animation fields. Do not synthesize generic walk flags on flying, mounted or stationary units.

User confirmed both characters arrived at Golden Ruins successfully through the new warm-before-teleport path.

Update: user teleported to a new location and both characters became visible on both clients. Visibility recovery confirmed; cause still unresolved. Movement checker correction passed all 7,624 unit tests, including concurrent player and mount sampling/reset stress tests. Deployment pending.

Deployment: movement-checker correction installed on 16 October 2026 with original matching assemblies backed up locally. Full suite passed 7,624 tests; World build succeeded with zero errors and 217 warnings. Server restarted successfully; starting zones 129/181 reported readiness. Live visibility and guard/animation findings remain unverified.
