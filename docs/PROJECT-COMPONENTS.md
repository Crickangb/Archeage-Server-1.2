# ArcheAge 1.2 project components

This repository covers ArcheAge 1.2 and its server, Discord bot, PlayerBots/Companion Manager, wiki/website, launcher and related administration tools. Each integration must use the matching 1.2 host, client data, schema and private configuration.

| Component | Available here | Starting point |
| --- | --- | --- |
| AAEmu 1.2 server | Login/Game/Commons source, tests, solution and SQL schema/migrations | [Server installation](INSTALLATION.md) |
| PlayerBots | Integrated source and bundled module source/documentation | [PlayerBots](../server/modules/archeage-playerbots/README.md) |
| Companion Manager | Account, administration, LAN, website integration and update guides; the full Node application is supplied separately | [Manager guides](COMPANION-MANAGER.md) |
| Discord bot | Game-service integration is present; independent community-bot applications/configuration must be supplied from their own matching package | [Discord service source](../server/AAEmu.Game/Services/DiscordBotService.cs) |
| Wiki and website | Existing website/gateway integration guide; full wiki application is supplied separately | [Website integration](history/manager/PUBLIC-WEBSITE-INSTALL.md) |
| Launcher | Client connection/setup instructions; use the launcher package matching the 1.2 client | [Server/client setup](INSTALLATION.md) |

An installation guide may refer to package folders such as SERVER, NODE, GATEWAY or ROLLBACK. Those refer to the relevant distributed package, not additional bundled applications in this server-source snapshot. Check the component's actual files before installation.

Record each 1.2 update with its affected component, source/build version, installation steps, tests, deployment status and rollback method. Keep private API keys, bot tokens, account mappings and live databases outside Git.
