# Archeage-Server-1.2

Source and documentation for **ArcheAge 1.2 r208022** and its related Discord bot, PlayerBots/Companion Manager, wiki/website, launcher and administration tools.

The AAEmu source targets **.NET 10** and identifies its application build as **0.3.0.0-alpha**. The game version is **ArcheAge 1.2**.

## Guides

| Task | Guide |
| --- | --- |
| Find the server and related tools | [Project components](docs/PROJECT-COMPONENTS.md) |
| Install and configure the server | [Installation](docs/INSTALLATION.md) |
| Install or update Companion Manager | [Companion Manager](docs/COMPANION-MANAGER.md) |
| Review changes and implementation limits | [Changes and status](docs/CHANGES-AND-STATUS.md) |
| Find earlier PlayerBots update/install guides | [Guide index](docs/HISTORY-INDEX.md) |
| Diagnose problems | [Troubleshooting](docs/TROUBLESHOOTING.md) |
| Update, deploy, back up or roll back | [Maintenance](docs/MAINTENANCE.md) |
| Check source version and validation | [Source provenance](docs/VERSION-AND-SOURCE.md), [validation](docs/VALIDATION.md) |

## Contents

`server/` contains the confirmed AAEmu source, solution, tests, bundled PlayerBots module, licenses and SQL schema/migrations. `docs/` contains the project's installation and update documentation. The component guide identifies which related applications have source here and which guides require separately supplied packages.

Credentials, actual private settings, game databases/content, binary artwork, executable builds, logs, backups and large runtime data are excluded. Reviewed public configuration templates use placeholders. Supply matching client content and private settings separately. Application icons are optional so the source-only checkout can build without binary artwork.

Upstream attribution and licenses are retained in `server/`; see its original readme and license files. Existing Git history is preserved. See [import provenance](docs/IMPORT-MANIFEST.json) for archive hashes, exclusions and documented adaptations.
