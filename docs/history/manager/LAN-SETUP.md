> Historical project guide preserved on 16 October 2026. It may refer to separately supplied packages or a different checkout; deployment and test claims below are historical. Private local addresses are generalized. Read ../../CHANGES-AND-STATUS.md and ../../HISTORY-INDEX.md for current import scope.

# LAN test setup: server .16, Manager .30

This patch extends the account-access update for AAEmu on **YOUR_LAN_IP** and the Node Manager on **YOUR_LAN_IP**. It supplies source files; Login must be rebuilt. It does not alter your SQL database. Live connectivity between your PCs has not been verified here.

## 1. Install the patch files

Stop the Manager. Copy `NODE/ArcheAge-PlayerBots/src/account-auth.js` and `NODE/ArcheAge-PlayerBots/Start-Manager.cjs` over those files in your installed PlayerBots folder on .30. Preserve your configuration.

On .16, copy `SERVER/AAEmu.Login/Program.cs` and `SERVER/AAEmu.Login/Core/Services/ManagerAccounts/ManagerAccountEndpoints.cs` into the matching AAEmu source locations. Keep the account-service files and Game bridge from the earlier account-access update. Rebuild/deploy the complete Login build, then restart Login. The included UnitTests and wiki files are optional development material, not runtime files.

## 2. Login configuration on YOUR_LAN_IP

Merge into the private Login configuration. Keep your existing key in place of the placeholder:

```json
"PlayerBotsManager": {
  "Enabled": true,
  "Host": "YOUR_LAN_IP",
  "Port": 17842,
  "AllowedClientIps": ["YOUR_LAN_IP"],
  "Key": "KEEP_YOUR_EXISTING_KEY"
}
```

Login now binds this account HTTP service to .16, accepts .30 or local requests, and still requires the matching key. It checks the socket peer IP and rejects browser Origin headers. Without Host/AllowedClientIps, the default stays loopback-only.

## 3. PlayerBots configuration on YOUR_LAN_IP

Merge these fields into `config.json` and keep `apiKey`, `requestTimeoutMs`, and your other existing fields:

```json
"serverUrl": "http://YOUR_LAN_IP:17840",
"authMode": "archeage",
"accountServerUrl": "http://YOUR_LAN_IP:17842",
"trustedLanServerIp": "YOUR_LAN_IP",
"accountApiKey": "KEEP_THE_SAME_KEY_AS_LOGIN"
```

Node allows LAN HTTP only to that explicitly configured private server IP; another LAN host/public address is refused. This LAN test option sends credentials over HTTP between your PCs. Public hosting continues to require HTTPS.

## 4. Check Game bridge and server firewall

If the existing Game bridge already works from .30 on port 17840, keep its current settings. Otherwise start Game with `AAEMU_PLAYERBOTS_REMOTE_PREFIX` set to `http://YOUR_LAN_IP:17840/` in its process environment (keep its existing enable/key settings). The Game bridge reads this environment variable, not a new Login config field. Restart Game after changing its environment.

If Game reports HttpListener access denied, the server needs a Windows URL reservation for that exact prefix and the Windows user running Game. Do not change URL reservations unless that error occurs.

Allow inbound server TCP ports 17840 (Game bridge) and 17842 (Login account service) from **YOUR_LAN_IP**, on the private network firewall profile if those ports are blocked. Do not change the regular game/Login client ports or forward these manager ports on the router.

On .30 you can check each connection in PowerShell:

```powershell
Test-NetConnection YOUR_LAN_IP -Port 17842
Test-NetConnection YOUR_LAN_IP -Port 17840
```

Both should report `TcpTestSucceeded: True`. This confirms a TCP listener is reachable, not that credentials/permissions are valid.

## 5. Start the Manager on your PC

Run `Start-Manager.bat` on .30. Open **http://127.0.0.1:17841** on .30 and sign in with an existing ArcheAge account. The browser-to-Manager connection remains local; Node contacts .16 for account and bot data.

If the controller is normally on .30, its existing `Start-PlayerBots.bat` can still be used there. Keep any controller running on .16 in its usual location rather than starting a second copy.

## Revert

Restore the account-auth.js, Start-Manager.cjs, Program.cs, and ManagerAccountEndpoints.cs backed up before this patch. Restore prior config values and rebuild Login. No SQL rollback is needed.
