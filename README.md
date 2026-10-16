# Archeage-Server-1.2

Latest user-confirmed AAEmu source ZIP, Companion Manager documentation, installation guides and development history. The repository name and destination were explicitly confirmed on 16 October 2026. Existing repository history is retained.

## Start here

| Task | Guide |
| --- | --- |
| Understand which version this source represents | [Version and source provenance](docs/VERSION-AND-SOURCE.md) |
| Build and configure the uploaded Login/Game source | [Installation](docs/INSTALLATION.md) |
| Review changes and known limits | [Changes and status](docs/CHANGES-AND-STATUS.md) |
| Install or update Companion Manager | [Manager guide index](docs/COMPANION-MANAGER.md) |
| Read the separate ArcheAge 10 R575 work | [R575 history](docs/R575-HISTORY.md) |
| Diagnose startup or gameplay problems | [Troubleshooting](docs/TROUBLESHOOTING.md) |
| Update, test, deploy and roll back | [Maintenance](docs/MAINTENANCE.md) |
| Check exactly what was imported/excluded | [Import manifest](docs/IMPORT-MANIFEST.json) |

`server/` contains the selected archive source, solution, tests, module source, licenses and applicable SQL scripts. `docs/history/` preserves project guides available at import time, with private local addresses generalized. Historical guides refer to earlier packages and may require files supplied separately; they do not prove that a change exists in this source ZIP.

The archive identifies AAEmu **0.3.0.0-alpha**, targeting **.NET 10**. Its Login and Game packet tables explicitly identify **ArcheAge 1.2 r208022** (`client_12_r208022`). .NET 10 is the build platform. Its bundled PlayerBots guide identifies its public preview as **ArcheAge 1.2 r208022**, and discusses a separate **10.0.2.13 r575** prototype. The user confirmed committing this identified 1.2 source. Live client compatibility has not been tested during this import. See the version guide before choosing game data or deploying.

Secrets, actual configuration, databases, executable binaries, build output, logs, backups and large runtime data are excluded. Obtain matching client content and supply private settings separately. A clone alone cannot run the complete game. This upload does not change a running server.

Upstream attribution and licenses are retained in `server/`; see its original `readme.md` and license files. No claim is made to ownership of the original game or its assets.

Binary artwork is excluded. Login/Game project files make ApplicationIcon conditional on the icon file existing, so this source-only checkout can build without artwork. The two project-file adaptations and excluded files are recorded in IMPORT-MANIFEST.json.
