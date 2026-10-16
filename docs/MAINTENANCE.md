# Updating, testing, deployment and backup

## Source and history

Work from the intended checkout and inspect the diff before committing. Preserve AAEmu/module attribution and licenses. Compare upstream updates with local modifications; open upstream PRs are not automatically installed. Never overwrite the confirmed ZIP's source with a separate workspace checkout while claiming the archive was imported unchanged.

Use the import manifest to identify the original archive and retained file hashes. Later edits should be explicit commits with their own validation and status updates. Fetch/pull before pushing; preserve concurrent changes and avoid force pushes. GitHub source storage does not automatically save changes made elsewhere.

## Verification

Run restore and builds for the changed projects. Follow global.json and the actual test project: this archive selects Microsoft.Testing.Platform. Inspect project test SDK/framework settings before choosing runner options; historical guides may describe older runners. Use isolated schemas for integration tests. Record exact command, result and unresolved warnings. Build success does not prove client protocol compatibility or gameplay correctness.

Live smoke test: sign in, select/create a character through the intended flow, enter the world, move, inspect inventory, save/logout, restart/re-enter and confirm persistence. For multiplayer changes use two accounts. Manager tests must exercise player-owner and admin access independently, including denied actions.

## Deploy and restore

Maintain separate backups for source/history; private database/configuration and generated-account vault/key; and original/modified client content. Store complete pre-deployment assembly sets and a local installation receipt. Save/disconnect players, stop affected services, deploy matching output, restart in order and verify readiness plus gameplay. Reverting code does not undo state changes; restore database state only through a deliberate compatible rollback plan.

## Public import exclusions

The upload excludes .env files, private/runtime configuration, account/owner registries, credentials/keys, executable binaries, dependencies, build outputs, logs/dumps, backups, game databases/archives and large data. Keep these out of future commits even if a file is small. Source resource images and reviewed schema/migration SQL may remain. An ignore file is an aid; inspect staged files and scan their contents before every public upload.
