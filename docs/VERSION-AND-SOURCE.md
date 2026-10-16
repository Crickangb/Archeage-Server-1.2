# ArcheAge 1.2 source provenance

Target: `Crickangb/Archeage-Server-1.2`, branch `main`. Import date: 16 October 2026.

The user confirmed the local `AAEmu-develop.zip` as the latest source and explicitly confirmed committing its identified 1.2 version to this repository.

| Evidence | Value |
| --- | --- |
| Archive size | 69,688,296 bytes |
| Archive SHA-256 | `206f86dc21c7dfaf44acae7cde196a134d746858f905f78d9365b8b1e3cfe28a` |
| Archive root | `AAEmu-develop/` |
| Packet opcode tables | `client_12_r208022`: ArcheAge 1.2 r208022 |
| AAEmu application version | `0.3.0.0-alpha` |
| Build platform | `net10.0` (.NET 10) |
| SDK selection | See `server/global.json` |
| Source layout | AAEmu.Login, AAEmu.Game, Commons, tests and modules |

The game version, .NET build platform, AAEmu application version and individual module release versions are separate identifiers. The game scope of this repository is ArcheAge 1.2.

IMPORT-MANIFEST.json retains original archive hashes and staged Git hashes separately because Git may normalize line endings. Import adaptations include optional application icons and documentation scoped to this 1.2 repository. No gameplay source code changed during those adaptations.

The source snapshot extends the destination's existing history. Upstream attribution, licensing, schema requirements and module release constraints remain applicable. See VALIDATION.md for checks actually performed.
