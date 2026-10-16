> Historical project guide preserved on 16 October 2026. It may refer to separately supplied packages or a different checkout; deployment and test claims below are historical. Private local addresses are generalized. Read ../../CHANGES-AND-STATUS.md and ../../HISTORY-INDEX.md for current import scope.

# Companion native corner fix — October 10, 2026

The harbour log repeatedly reports terrain_drop with to_z=NaN after selecting native routes. This update corrects a reproduced movement bug: native waypoints could be skipped up to 0.75 metres before reaching the vertex, cutting tight corners outside the route surface. Native routes now reach each vertex within 0.01 metres before advancing, and approach the final vertex fully instead of braking at the generic arrival radius.

This is cumulative over the Upright Pose FIX, including previous bridge, neutral gathering and swimming source changes.

1. Stop AAEmu.Game and back up the matching source files.
2. Merge SERVER-SOURCE into your AAEmu-develop source directory, replacing matching files.
3. Rebuild AAEmu.Game using .NET 10. Deploy the rebuilt output to the server runtime and restart Game. Copying source into the runtime directory alone does not apply the fix.
4. Keep the existing bots. Test the same harbour streets, stairs and bridge in both directions, including stopping and waiting for companions.
5. If they stop again, send the new Server.log, especially navigation_hazard and follow_route_unavailable lines.

No database, dashboard or configuration changes are required.

Validation: the regression failed on the previous code. All ten movement tests pass after the change, including tight native turns, final arrival, raised routes and swimming. Complete suite results are recorded in VALIDATION.txt.

Live harbour/bridge crossing still needs confirmation. This corrects native route following; it does not add missing navigation surfaces, obstacle collision meshes or boat boarding.
