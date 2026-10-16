# ArcheAge 1.2 changes and status

## Source import

- Imported the user-confirmed AAEmu source ZIP for ArcheAge 1.2 r208022, using .NET 10.
- Retained Login/Game/Commons source, solution files, tests, bundled PlayerBots source, SQL schema/migrations and license notices.
- Added setup, public configuration templates, source provenance, troubleshooting, maintenance and relevant project guides.
- Excluded secrets, private settings, runtime registries, databases/content, binaries, generated output and large data. Application icons are optional for source-only builds.
- Release build passed with zero errors and 31 existing analyzer warnings; all 1,197 unit tests passed. Live installation and gameplay require separate verification.

## PlayerBots and Companion Manager

The v0.17.8.6 dashboard reference has Overview, Skills & Build, Companion Control, Gathering, Crafting, Equipment, Inventory and Activity Log views. Documented updates cover shared ArcheAge sign-in, owner-scoped personal access, administrator permissions, online/offline bot roster and details, ownership assignment, reviewed creation/deletion and bot-account capacity/provisioning with encrypted private credentials and recovery.

Other documented work includes local item/skill icons, skill details/priorities, Overview character roster, resource maps, gathering/crafting controls, bridge/follow/swimming recovery and optional HTTPS website gateway routing. New-bot creation is level 1; detailed appearance selection remains planned. Offline data is last saved state, and combat histories are sampled observations.

Historical guides describe the matching packages and their installation limits. They do not establish that every later overlay or the complete Node application is bundled in this source snapshot. Use the [component index](PROJECT-COMPONENTS.md) and [guide index](HISTORY-INDEX.md) to select the appropriate 1.2 package.

## Discord bot, wiki and launcher

These are part of the 1.2 project scope. Existing Game Discord integration and client setup instructions remain with the source. The preserved website/gateway guide describes integration with the existing wiki application. Independent application packages and their private deployment settings must be supplied separately where the component index identifies them as external to this snapshot.

## Documentation scope correction

Repository guides now cover only ArcheAge 1.2 and its related components. Unrelated project history and installation records were removed from the current documentation. Gameplay source behavior is unchanged by this documentation correction.
