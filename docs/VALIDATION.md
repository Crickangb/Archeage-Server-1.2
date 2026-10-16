# Import validation — 16 October 2026

- Source archive: user-confirmed AAEmu-develop.zip; SHA-256 and per-file provenance are in IMPORT-MANIFEST.json.
- Version evidence: Login/Game opcode tables identify ArcheAge 1.2 r208022. AAEmu application metadata is 0.3.0.0-alpha; build platform is .NET 10. User explicitly confirmed this version and destination before committing.
- Dependency restore succeeded for the solution.
- Full Release solution build succeeded with binary artwork excluded and a sanitized integration-test example configuration: **0 errors, 31 existing analyzer warnings** on the final build. No gameplay source code was changed to suppress warnings.
- TUnit unit tests: **1,197 passed, 0 failed, 0 skipped**. No database integration tests or live game server were started.
- Staged content was scanned for forbidden files and common credential patterns. Original source hashes and staged Git hashes are recorded separately to account for normal Git line-ending normalization.
- Newly written documentation passed the whitespace check. Archive source contains existing trailing whitespace, retained to avoid unrelated source edits.

Executable builds, test output and raw review logs remain outside the uploaded source. Live client compatibility, complete fresh installation, Manager overlays and historical gameplay fixes require their own environment-specific checks.

Binary artwork is excluded. Login/Game project files make ApplicationIcon conditional on the icon file existing, so this source-only checkout can build without artwork. The two project-file adaptations and excluded files are recorded in IMPORT-MANIFEST.json.
