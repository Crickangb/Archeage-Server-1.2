# Separate ArcheAge 10 R575 work

The R575 project is a separate host/client integration. Its documented base is AAEmu upstream branch client_version/zone-10.0.2_r575 at d2d0b3a3ae5097d0654b5bd160f71ec691178875 plus local changes. Those source files are not substituted for this upload's confirmed ZIP.

## Setup outline for the matching R575 tree

Use its own .NET 10 solution, MySQL schemas, matching R575 native client/content, native ZoneHost executable and DLLs. The native client engine source and game assets are not supplied by this project. Configure GameContentRoot, ZoneGameDataRoot, native executable/working directory and dependencies in private World settings. Login/World shared secrets and account authentication must match that installation.

For that checkout, build Login plus AAEmu.WorldServer/AAEmu.World/AAEmu.World.csproj. Do not run that path against this archive's Login/Game layout. Start MySQL, Login, World, then wait for required zones. Historical ports are Login 1237, internal 1234, World 1239, zone link 1240 and stream 1250. A listening TCP port does not prove ZoneLoaded.

The outdoor supervisor uses on-demand startup and readiness waits with host limits and idle unloading. Historical local settings were eight hosts, a 120-second readiness timeout and 300-second idle unload; tune deliberately. Starting all native zones at once previously exhausted practical machine capacity. Native zone keys differ from SQLite zones.id. PreLoadTerrain caches terrain rather than starting every map.

The client startup workaround uses +locale en_us; the historical -lang en_us switch collided with Chromium and produced a locale startup failure. Normal/GM launcher modes do not grant GM permissions. Normal account mode resolved a recorded creation blockage; that does not establish every future naming/creation failure has the same cause.

## Detailed records

See [historical guides](HISTORY-INDEX.md) for source transitions, cold zones, GM/client translation, Guild Mission/Community changes, workbench and NPC investigation. Read CHANGES-AND-STATUS.md first: older checklists contain superseded observations, pending installation statements and contradictory branding status. Historical test counts concern those older checkouts and are not a new test run of this ZIP.

Keep original and modified client assets, databases and runtime configuration in private backups. Source version control, client-content backup and live server deployment are distinct operations.
