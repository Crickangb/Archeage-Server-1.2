# Troubleshooting

| Symptom | Checks and next action |
| --- | --- |
| Build cannot find assets/config/content | This is a source-only import. Restore your compatible runtime world/client data privately; reconstruct private settings from source models. Verify SDK and project paths. |
| Source says net10.0 but client fails | .NET version is separate from ArcheAge version. Use the matching ArcheAge 1.2 r208022 client and content. |
| Formula parsing error or missing formula key | Capture the formula ID and schema/client version locally. Earlier errors followed changing game_pak/database versions. Verify data compatibility before adding arbitrary formulas or suppressing missing-key checks. |
| Login works, world entry fails | Check Game registration, shared key, advertised endpoint, schema and content readiness. |
| Manager reports fetch failed | Confirm Login/Game are running, the matching bridge patches are installed and the configured private endpoints are reachable. Inspect local startup errors before changing firewall rules. |
| Top-level JSON element must be an object | Historical Game loader tried loading PlayerBotsKnown.json list as host settings. Matching Program.cs fix excludes bot runtime documents; preserve those registries, rebuild and deploy Game. |
| Manager LAN sign-in fails | Check exact allowed IPs, matching bridge keys and configured server URLs. Outside the agreed trusted LAN use the matching HTTPS setup. |
| Public Manager sign-in fails on local HTTP | Public mode uses HTTPS and Secure scoped cookies; use the configured HTTPS origin and path. Check gateway key/host and routing. |
| Bot appears offline or history incomplete | Offline details are last saved data; combat history captures inspected events. This alone does not establish data loss. |

Keep credentials, raw crash dumps and logs out of public issues and Git. Share redacted error details, version/hash, reproduction steps and expected/actual behavior. A previously reported community bug is a test lead until reproduced on the current build.
