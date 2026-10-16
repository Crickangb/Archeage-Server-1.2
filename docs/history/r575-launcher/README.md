> Historical project guide preserved on 16 October 2026. It may refer to separately supplied packages or a different checkout; deployment and test claims below are historical. Private local addresses are generalized. Read ../../CHANGES-AND-STATUS.md and ../../HISTORY-INDEX.md for current import scope.

# ArcheAge launcher

Installed at `F:\AAEmu-R575-client\ArcheAge-Launcher.exe`.

Choose Normal play or GM tools, then Play. The launcher stays open so you can launch another client using the other mode. Use different game accounts. GM tools selects the working development client; your account still needs GM permission. Normal play is selected when the launcher opens.

Server status checks whether both the login and world connections accept TCP connections. It does not guarantee that a particular zone is loaded. Status refreshes every 15 seconds or when Refresh is clicked.

No game credentials are collected or stored. Current connection and window size are in Launcher.settings.json. The existing batch launch files remain unchanged. This version does not start or stop the server and does not update client files.

Validation: compiled Windows GUI, 11 checks passed (mode flags, stable client options, locale/gamma, endpoints, absent credentials, rejected invalid settings, listening/stopped endpoint status and form creation). Preview rendered and visually checked. Actual game launch through the new launcher still needs user confirmation; it uses the same arguments as the previously working batch files.

Editable source is in src/Launcher.cs. Built with the installed Windows .NET Framework C# compiler; no additional runtime or download required on this PC.
