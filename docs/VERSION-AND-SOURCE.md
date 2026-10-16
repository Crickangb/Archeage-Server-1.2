# Version and source provenance

Import date: 16 October 2026. Target explicitly confirmed by the user: `Crickangb/Archeage-Server-1.2`, branch `main`.

The earlier conversation called the attachment `AAEmu-develop(1).zip`. That path is unavailable in this Windows session. The user explicitly confirmed using the accessible Downloads file `AAEmu-develop.zip` as the latest source instead.

| Evidence | Observed value |
| --- | --- |
| Archive size | 69,688,296 bytes |
| SHA-256 | `206f86dc21c7dfaf44acae7cde196a134d746858f905f78d9365b8b1e3cfe28a` |
| Archive root | `AAEmu-develop/` |
| `server/Directory.Build.props` | `TargetFramework=net10.0`, `VersionPrefix=0.3.0.0`, `VersionSuffix=alpha` |
| `server/global.json` | SDK 10.0.0, latest-major roll forward; Microsoft.Testing.Platform runner |
| Bundled PlayerBots README | Public preview described as ArcheAge 1.2 r208022, with separate 10.x prototype |
| Login/Game packet opcode tables | Explicitly target `client_12_r208022` (ArcheAge 1.2 r208022) |
| Archive layout | AAEmu.Login, AAEmu.Game, Commons, tests, modules; separate R575 WorldServer tree is absent |

The user states their server has always used AAEmu 10. The ZIP's .NET 10 requirement confirms its compiler/runtime target, not an ArcheAge 10 protocol implementation. The archive's AAEmu application version, framework version, PlayerBots release version and game version are separate identifiers. The Login and Game opcode-table comments identify the 1.2 r208022 protocol. After this finding the user explicitly confirmed committing this identified 1.2 source and all guides to the named 1.2 repository. No game compatibility test was run during this documentation/import work.

The workspace also contains a separate R575 development checkout based on upstream `d2d0b3a3ae5097d0654b5bd160f71ec691178875`, plus local changes. It is not substituted for this confirmed archive. Its history is documented separately, without copying its runtime files into this upload.

Source files are imported without intentional code edits. IMPORT-MANIFEST.json records archive hashes and staged Git hashes separately because Git may normalize line endings. An uploaded source snapshot has no embedded upstream Git history unless separately provided; it extends this destination repository's existing history. SQL scripts and module documentation retain their own compatibility constraints.

Binary artwork is excluded. Login/Game project files make ApplicationIcon conditional on the icon file existing, so this source-only checkout can build without artwork. The two project-file adaptations and excluded files are recorded in IMPORT-MANIFEST.json.
