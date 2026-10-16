> Historical project guide preserved on 16 October 2026. It may refer to separately supplied packages or a different checkout; deployment and test claims below are historical. Private local addresses are generalized. Read ../../CHANGES-AND-STATUS.md and ../../HISTORY-INDEX.md for current import scope.

# Website and Companion Manager on one domain

This patch is prepared for your v29.3.1 Node Wiki, currently served on port 8787, and your Manager on port 17841. It includes the latest player Overview roster UI. No AAEmu server rebuild is needed for public routing; keep the Game patch supplied with the Overview roster installed.

Skills & Build now displays real icons on active and passive skill cards. Unlearned and level-locked cards also show their icons. Missing images retain the existing symbol, name and requirements. The skill icon mapping and bundled images are included. Admin skill lists keep their existing icons.

Public addresses:

- https://archeagegl.cityofheroesunity.com/companionmanager
- https://archeagegl.cityofheroesunity.com/companionmanager/admin

## Install

1. Stop the Manager and Wiki. Back up the files you replace.
2. Copy the files from `MANAGER` into your existing `C:\ArcheAge-PlayerBots` folder. Keep your existing `config.json`, icons, private files and logs. This is an update, not a complete fresh installation.
3. Copy `WEBSITE\app\page.tsx` into `F:\AAEmu-Discord-Wiki-v29.3.1-COMPLETE-WINDOWS\app\page.tsx`. This adds a **Companion Manager** button alongside the website navigation. Run `REBUILD-WIKI.bat`, then start the Wiki normally. The website continues to run on port 8787.
4. Copy the `GATEWAY` folder into the website directory. The Wiki, gateway and Manager must run on the same computer. AAEmu Login and Game may remain on YOUR_LAN_IP. Keep the working private bridge URLs and API keys in the Manager's config.
5. Open a terminal in that GATEWAY folder and run:

   ```powershell
   node setup-companion-gateway.mjs C:\ArcheAge-PlayerBots
   ```

   This adds the public origin, `/companionmanager` path and a generated gateway key to the existing Manager config. It keeps a `config.json.before-public-backup`. It creates `companion-gateway.private.json` beside the gateway script. Keep these files private. Existing account passwords and bridge keys are unchanged.

6. Restart the Manager with your existing launcher. Start `GATEWAY\START-COMPANION-GATEWAY.bat`. Keep both running, along with the Wiki.
7. In the existing Cloudflare Tunnel route for **archeagegl.cityofheroesunity.com**, change only the HTTP service destination from `localhost:8787` to `localhost:8788`. Keep the current hostname and HTTPS setup.
8. Open the public Companion Manager URL and sign in with your ArcheAge account. The admin page requires an administrator account. The Wiki account service and Manager sign-in remain separate.

The gateway sends `/companionmanager` and its child paths to the Manager. All other paths go to the existing Wiki. No public routes are added for ports 17840, 17842 or MySQL. The Manager accepts gateway requests only on loopback with the shared key and exact public host. Browser sessions use Secure, HttpOnly cookies limited to `/companionmanager`, and actions retain their existing CSRF and authorization checks.

After public mode is enabled, use the HTTPS address shown in the Manager console. The old direct `127.0.0.1:17841` address no longer accepts browser requests. A local HTTP gateway page cannot sign in because its session cookie requires HTTPS.

## Checks and limits

Local tests cover existing Wiki routing, the mounted assets, account sign-in, administrator restrictions, wrong-origin rejection, scoped secure cookies, logout, player rosters and deletion history. Public Cloudflare routing and the live server have not been tested or changed by this patch. The gateway handles HTTP requests; it does not proxy WebSocket upgrades.

## Roll back

Point the tunnel back at `localhost:8787`, stop the gateway, and restore the Manager config from `config.json.before-public-backup`. Restore your saved website page and rebuild the Wiki if you want to remove the navigation button.

## Bridge movement issue

This website patch does not change bot movement. Current bot movement samples terrain height and can reject the water or terrain drop below a bridge deck. Correct bridge support also needs the walkable deck surface and a route through the entrance; disabling water/drop checks alone would not provide that. The failing bridge's location and behavior are needed to identify the relevant data and verify a movement fix.
