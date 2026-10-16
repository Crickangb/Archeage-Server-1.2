# Companion Manager installation and updates

The preserved guides describe the separate Node Companion Manager project. They are reference documentation; this import does not replace the confirmed archive with that project's newer server overlays, distribute private bot registries, or include every earlier package attachment.

## Install sequence

1. Match the AAEmu host/client version and obtain the corresponding full Node application and server source overlay. Use Node.js 20+ and the .NET SDK required by that host. Confirm the overlay paths exist before merging.
2. Back up Manager files, Login/Game source, deployed build sets, databases, owner mappings and private runtime registries.
3. Apply matching Login and Game source updates and rebuild both where required. A Node-only update cannot add server endpoints. Preserve actual private configuration and data.
4. Install Node dependencies using the application's package lock. Start using its supplied Start-Manager/Start-PlayerBots launcher. Configure Game and Login bridge addresses/keys privately, with exact allowed client IPs for the agreed LAN setup.
5. Default roles: browser/local Manager port 17841, Game bridge 17840, Login account bridge 17842. Keep bridge endpoints and MySQL out of public routing. Sign in through the Manager and verify owner/admin access separately.
6. Test roster loading, offline/online inspection, owner permissions and review/confirmation flows before account creation or deletion. New companions start at level 1. Dedicated bot accounts and player-owner accounts serve different roles.
7. Optional public gateway work requires the existing compatible Wiki application and HTTPS/tunnel configuration. The gateway documentation describes a prepared patch, not proof of live public deployment. Preserve gateway secrets locally.

## Guide index

- [Original installation milestone](history/manager/INSTALL.md)
- [LAN setup](history/manager/LAN-SETUP.md)
- [Admin Manager](history/manager/ADMIN-INSTALL.md)
- [Accounts, icons, ownership, level-1 creation and recovery](history/manager/ADMIN-ACCOUNTS-INSTALL.md)
- [Manager change notes](history/manager/CHANGES.md)
- [Public website/gateway routing](history/manager/PUBLIC-WEBSITE-INSTALL.md)
- [Historical movement, gathering, crafting and skill updates](HISTORY-INDEX.md)

The public records use placeholders instead of real LAN addresses and account/character IDs where generalized. Create private settings for the actual environment. Earlier package layouts such as SERVER/, NODE/, ROLLBACK/ and GATEWAY/ refer to those historical packages and are not promised as directories in this source import.

Back up the encrypted generated-account vault together with its key. Reverting code does not undo confirmed account/character creation or ownership changes. Hair/face customization remains planned. Offline stats are last saved data, and recorded combat events are sampled rather than an exhaustive server log.
