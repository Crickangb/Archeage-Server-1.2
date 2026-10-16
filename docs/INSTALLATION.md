# Install the confirmed archive source

## Requirements and files you must supply

Use Windows with Git, the .NET 10 SDK compatible with `server/global.json`, MySQL compatible with the selected AAEmu schema, and the matching ArcheAge 1.2 r208022 client/content identified by the packet tables. Node.js 20 or newer is required only for the separate Companion Manager. Do not select a game client merely because the build target says net10.0.

Full game archives, compact/content databases, private configuration and deployed executables are intentionally absent. Runtime world data under AAEmu.Game/Data is also omitted. Supply those from your own compatible installation, preserving the layout expected by the source. The source guide expects compact.sqlite3 under AAEmu.Game/Data; put compatible game_pak content under ClientData or set ClientData.Sources in your private override. A 22 GB or 66 GB game_pak file size does not establish compatibility; renaming game_decrypted.sqlite3 to compact.sqlite3 does not convert its schema or game version.

## 1. Obtain and build

```powershell
git clone https://github.com/Crickangb/Archeage-Server-1.2.git
cd Archeage-Server-1.2
dotnet restore server/AAEmu.slnx
dotnet build server/AAEmu.Login/AAEmu.Login.csproj --configuration Release
dotnet build server/AAEmu.Game/AAEmu.Game.csproj --configuration Release
```

Build output belongs in ignored bin/obj/artifacts folders. Preserve the complete matching output when deploying; replacing one assembly can leave incompatible dependencies. See the validation record for checks actually performed on this snapshot.

## 2. Prepare databases

Back up any existing databases first. Use fresh isolated schemas for first installation and compatibility testing. Review `server/SQL/` and the source's migration/bootstrap mechanism before running SQL; some updates assume specific existing schemas. For a new isolated installation the supplied base schemas are server/SQL/aaemu_login.sql and server/SQL/aaemu_game.sql. Review their CREATE/USE statements before importing into your intended schemas; then apply only appropriate updates. Game server listing is configured through Login GameServers, not rows inserted into a game_servers table. Schema definitions and migrations are not a live database backup. Sample seeded-account SQL is excluded. Create accounts through the matching supported account flow and keep credentials private.

## 3. Configure privately

The original Config.json and local/example deployment configurations are omitted from the public import. Reviewed Config.public-example.json templates contain placeholders and loopback endpoints only. Copy the public templates to ignored private settings before building/deploying, then edit the private copies:

```powershell
Copy-Item server/AAEmu.Login/Config.public-example.json server/AAEmu.Login/Config.Local.json
Copy-Item server/AAEmu.Game/Config.public-example.json server/AAEmu.Game/Config.Local.json
```

Replace database users/passwords and set the same freshly generated shared SecretKey in both. The Login template disables automatic account creation; enable only your chosen account-registration mechanism after reviewing its behavior. Start with loopback for same-PC validation and deliberately set the advertised client-reachable Game address for LAN/remote use. Rebuild after adding private settings, or install them beside each deployed executable.

Use `server/AAEmu.Login/Models/AppConfiguration.cs`, `server/AAEmu.Game/Models/AppConfiguration.cs`, their service setup code and original project README files to reconstruct the required settings for your installation. The repository does not include a tested one-command configuration/bootstrap installer.

Set MySQL host/port/user/password/schema independently for each service. Use a dedicated database user and set the same newly generated shared SecretKey in Login and Game. Configure a GameServers entry in Login for the intended Game ID and address reachable by your clients. Set Game's LoginNetwork to Login's internal listener. Keep game IDs and shared keys consistent. Check required configuration validation at startup.

Store settings only in ignored local files or .NET user secrets as supported by the project. Never upload actual values. The source's project README explains its configuration and User Secrets mapping; example passwords there are illustrative, not deployment credentials.

## 4. Start and connect

Start MySQL, then Login, then Game. Check readiness and database-loading errors before starting the matching client. Common project ports are Login client 1237, Login internal 1234, Game 1239 and streaming 1250; confirm your actual configuration. Keep service-only endpoints private. Test account login, character selection, world entry, movement, saving and re-entry on an isolated installation before switching an existing server.


## 5. Optional PlayerBots and Manager

The archive contains PlayerBots source and its own module installation/compatibility guides. Match their required host version and migrations first. The Companion Manager guides describe separately developed packages; their documentation is included for continuity, but the Node application and every later server overlay are not silently merged into this archive. See COMPANION-MANAGER.md for prerequisites and update order.

## 6. Update or roll back

Back up source, complete deployed builds, databases and private configuration as separate items. Save/disconnect players before stopping services. Deploy a matching build set, restart in order, and verify world entry and persistence. To roll back, restore compatible build/configuration and database state where migrations require it. A code rollback does not undo bot/account creation or ownership changes.
