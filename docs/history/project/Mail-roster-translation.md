> Historical project guide preserved on 16 October 2026. It may refer to separately supplied packages or a different checkout; deployment and test claims below are historical. Private local addresses are generalized. Read ../../CHANGES-AND-STATUS.md and ../../HISTORY-INDEX.md for current import scope.

# Mail and roster messages — installed

Two literal Chinese strings in the compiled client UI were translated and installed after the user closed the client. Both edited Lua chunks reparse successfully. All instruction words, function flags, debug data, and other constants are unchanged. The roster `%d` placeholder is preserved. Mail, rewards, expiry rules, roster cooldowns, and server data are unchanged.

## Mail

Where: the message-reading UI, in the item/commercial-mail notice area when that warning is displayed. This is a warning about expiry and unclaimed items, not the mail contents written by players.

English:

> Expired mail is deleted automatically.
> Unclaimed items are permanently lost. Collect them in time.

The two-line layout keeps the notice shorter horizontally; in-game layout confirmation is pending.

Actual script: `F:\AAEmu-R575-client\game\scriptsbin64\x2ui\mailbox\common.alb`. The localized notice is in the compiled `CreateContentSectionOfReadMail` code, which differs from the loose source. An unrelated Chinese URL-validation diagnostic in that file was left unchanged.

## Roster

What: a saved member list, used by raid management and also group mail. It is separate from guild membership.

Where: the roster/settings button near the top-right of Raid Management opens the roster window. Group Mail can also open it. The translated system message appears when trying to save another roster while the save cooldown is active.

English:

> Please wait %d seconds before saving another roster.

The client replaces `%d` with the remaining seconds. Actual script: `F:\AAEmu-R575-client\game\scriptsbin64\x2ui\roster\roster_manager.alb`.

## Backup and verification

Original scripts are backed up under `F:\AAEmu-R575-compiled\Translation\MailRoster\2026-10-14`. Verified replacements, hashes, and installation receipt are under `output/mail-roster-translation`. Installed hashes match the prepared copies. Only the client was relaunched; Login, World, and zone hosts were left running. In-game confirmation remains pending.

GM console, mentioned in the scan, is the client's administrator/developer panel for health, mana, factions, and related controls. Its translation was not included in this two-message batch.
