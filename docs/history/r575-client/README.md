> Historical project guide preserved on 16 October 2026. It may refer to separately supplied packages or a different checkout; deployment and test claims below are historical. Private local addresses are generalized. Read ../../CHANGES-AND-STATUS.md and ../../HISTORY-INDEX.md for current import scope.

# Client modifications and tools

This folder preserves the Lua payloads, launcher source, reviewed translation change lists, and historical preparation scripts used for the R575 client. The original C++ client engine source is not available in this workspace. This is not a full client distribution.

## Launcher

`launcher/src/Launcher.cs` provides Normal/GM mode, Play, server TCP status and configurable local connection/window settings. It does not start the backend or patch/download client files. It stores no game login credentials. Build on Windows with the .NET Framework C# compiler and references to System.Windows.Forms, System.Drawing and System.Web.Extensions; use `launcher/src/launcher.manifest` for the application manifest. `LauncherTests.cs` contains the existing checks. Place the built launcher beside the client with its local settings. GM mode still requires an authorized GM account and the separately prepared development client.

## Translation workflow

Use the installed `game/db/compact.sqlite3` as the intended client database; similarly named archives and top-level copies may be Asian-language originals. Back up before editing, retain existing English and DO NOT TRANSLATE content, preserve formatting tokens and IDs, check SQLite integrity, and close the client before installation. Translate only reviewed display fields, never identifiers or gameplay relationships. Ambiguous achievement wording should be agreed before applying it.

The CSV change lists preserve reviewed database edits. Source tools in `inspection/` include Lua 5.1 chunk parsing, Mail/Roster strings, GM console and Quick Tools, Patron headings/expiry, chat tabs, achievements and ArchePass. Some scripts use fixed local F: paths and refer to other audit outputs. They are **historical preparation tools**, not an unattended installer: inspect paths and prerequisites, back up originals, and stage output first. They may require Python 3.12, Lua 5.1 via lupa and pefile. Dependency packages are not vendored.

Run tools that use `inspection/...` and `output/...` relative paths from this `client-mods` directory. Supply any referenced audit inputs locally. Never rerun a binary patch expecting original bytes against an already modified client. Compiled ALB files and native DLLs are intentionally excluded; preserve their originals and patched versions in local backup storage.

Full client database translation, native client source and a one-command client restoration pipeline are not provided by this first import. Do not confuse archived patch scripts with a verified universal installer.

## Community-window guard

`inspection/community-notifier.lua` handles a missing native quest-notifier list when Guild Mission UI is created. `prepare-community-notifier.py` reproduces the original nil error, checks seven cases on source and compiled output, verifies unrelated functions are unchanged, and stages a replacement for `x2ui/assignment/quest_assignment/component.alb`. Installation requires a closed client and an original-file hash check plus backup. This guard does not grant or complete quests. The underlying native reason for an absent list remains under investigation.
