> Historical project guide preserved on 16 October 2026. It may refer to separately supplied packages or a different checkout; deployment and test claims below are historical. Private local addresses are generalized. Read ../../CHANGES-AND-STATUS.md and ../../HISTORY-INDEX.md for current import scope.

# Saving, updating and deploying

GitHub stores reviewed source changes and documentation. It does not back up ignored files or automatically save edits made only in another folder.

The current working server source remains `inspection/aaemu-r575-update` in the local project workspace. `inspection/aaemu-r575` is the older backup. This repository imports a copy into `server/`; do not build the old backup or overwrite newer source with the imported snapshot. Choose one working checkout deliberately for subsequent work and commit reviewed changes here.

For each change: inspect the diff, run relevant tests, update status/docs, commit, and push. Keep uninstalled work identified as pending. Never claim a server fix is live because a commit was pushed.

Retain upstream remote/reference to AAEmu/AAEmu and compare its R575 branch before updating. Review overlapping local fixes; run tests and build before installation. Preserve upstream attribution and licenses.

Before deploying, save characters, stop affected services, back up the test database plus changed runtime files, deploy the complete matching assembly set, restart, wait for zone readiness and verify in game. Keep a receipt and file hashes in local backup storage. Roll back assemblies and database together if a migration requires it.

Keep three separate backups: this source repository; private local/offline backups of runtime configuration and databases; and original/modified client assets. Do not add MySQL dumps, account tables, credentials, game archives or crash logs to Git, even in a private repository. Check ignored files with `git status --ignored` if a source file appears missing.
