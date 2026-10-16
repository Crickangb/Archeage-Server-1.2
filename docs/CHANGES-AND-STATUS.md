# Changes and development status — 16 October 2026

This record combines available local source documentation and historical reports. Historical test counts are quoted from existing records; they were not rerun as proof of this newly imported ZIP. User-observed behavior, automated checks, installed changes and planned work have different meanings.

## This source import

- Imports the user-confirmed latest archive's eligible source without substituting an older workspace tree.
- Preserves AAEmu licenses, solution/project files, tests and bundled PlayerBots source/documentation.
- Excludes secrets, deployment configuration, database content, binaries, generated output and large game data.
- Adds installation, compatibility, troubleshooting, maintenance and historical update guides.
- Extends the destination's original README commit; no history rewrite is intended.

## Companion Manager work documented in this project

The v0.17.8.6 dashboard reference has eight personal views: Overview, Skills & Build, Companion Control, Gathering, Crafting, Equipment, Inventory and Activity Log. Later local work adds shared ArcheAge account sign-in, owner-scoped personal access and administrator-only management. Admin features include online/offline roster, detail inspection, ownership assignment, reviewed creation/deletion and bot-account capacity/provisioning with encrypted private credential storage and recovery.

Other documented changes include item/skill icons, skill details and priorities, Overview character roster, resource maps, gathering/crafting controls, bridge/follow/swimming recovery work and optional HTTPS website gateway routing. Full appearance selection is planned; new-bot creation is level 1. Offline data is last saved state and combat histories are sampled observations. These later packages require matching server overlays and Node files; inclusion of their guides does not claim all features exist in the confirmed source ZIP or work with R575.

## Separate R575 work

- Updated the R575 base through upstream d2d0b3a while retaining local GM, outdoor-zone, boat-passenger, naming and Guild Mission changes.
- Outdoor startup waits for zone readiness; cold-zone waits, moving destinations and teleport entry received corrections. Both test characters later reached Golden Ruins; Lilyut travel was user verified.
- Inventory seed purchase, split/merge and persistence were user verified. Guild Mission completion, increased guild XP and reward-box delivery were observed for one character; daily reset and wider regression remain pending.
- Community notification-list guard passed seven recorded cases; one character's live retest passed. The other retest remains pending.
- Movement audit dictionary synchronization passed a recorded 7,624-test suite and was installed; visibility cause, guard behavior and NPC animation remain open.
- Removed two obsolete Golden Ruins template 12290 workbench spawns after reproducible crashes. Specialty crafting remains disabled; full live confirmation was pending in the available record.
- Client work covers login visibility, locale handling, Normal/GM launcher, English GM tools, quest labels, Mail/Roster, Patron labels/expiry, achievements and ArchePass review. Twelve GM Quick Tools were user verified. Translation and launcher installation records do not establish complete client translation.

## Open or deferred

R575 login-failure reproduction, player visibility, NPC sliding/guard behavior, Lucky Supplies interaction, repair/combat/dungeon regression, full Patron purchases/expiry and disabled specialty crafting remain unresolved or incomplete. Background changes and QR promotion replacement are on hold. Some welcome-branding records conflict about deployment; treat that change as staged until live verification is recorded. Website/wiki/Discord bot and R575 PlayerBots integration need their own implementation/validation; older Manager work is not proof of R575 support.

Use the historical guide index for detailed steps and recorded limits. When a historical guide disagrees with a later dated record, investigate the deployment receipts rather than assuming every checked item is current.
