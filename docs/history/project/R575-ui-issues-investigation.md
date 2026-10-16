> Historical project guide preserved on 16 October 2026. It may refer to separately supplied packages or a different checkout; deployment and test claims below are historical. Private local addresses are generalized. Read ../../CHANGES-AND-STATUS.md and ../../HISTORY-INDEX.md for current import scope.

# UI investigation — October 14

The screenshots confirm Testelf has entered Gweonid Forest. This investigation does not change achievement visibility, event activation, reward eligibility, subscription grants, or quest progress.

## Seasonal achievements

The installed client database includes category 7 (`Season`) and subcategories 31–34 (`Spring`, `Summer`, `Autumn`, `Winter`). Their completion achievements 3993–3996 are named `Spring`, `Summer`, `Fall`, `Winter`. Thus Autumn/Fall is an existing naming inconsistency, not a missing translation.

Excluding those four completion records:

| Subcategory | Achievement records | Not hidden | Not hidden and season not switched off |
|---|---:|---:|---:|
| Spring | 94 | 0 | 0 |
| Summer | 105 | 7 | 6 |
| Autumn/Fall | 144 | 0 | 0 |
| Winter | 102 | 6 | 6 |

Spring and Autumn have all their ordinary achievement entries hidden in this client dataset. Winter still has six unhidden entries whose `season_off` flag is false, so its absence cannot be explained by those flags alone. The Seasonal window itself was not among the four screenshots; its current filters and native category enumeration still need checking. Do not enable every seasonal row: that changes eligibility and may expose development or retired content.

The Lua category builder calls `X2Achievement:GetCategories(kind)` and displays its returned subcategories. Its display filters include All, Completed, and Incomplete. These facts do not prove which native filter suppressed a particular subcategory during the user's test.

Wording choices: **Autumn** fits the installed subcategory label; **Fall** fits the existing completion-achievement label and US English. Recommend Autumn consistently if the user selects it; leave current English untouched until selected.

## Follow-for-rewards tab

The Chinese tab is the publisher's follow/community promotion. The compiled `eventcenter/followme.alb` creates a drawable using `TEXTURE_PATH.EVENT_CENTER_FOLLOWME`, with image key `followme`; the container has picking disabled. The Chinese text and QR code are part of the texture. There is no clickable claim handler or server reward transaction in that script.

It can be replaced with our community or rewards page, but a functioning claim needs our destination URL, eligibility rules, and a server-side claim implementation. Relabeling or replacing the QR image alone would not award items. Choices for the future tab: **Community Rewards** for an actual reward flow, **Community Hub** for links/news, or **Events** for promotions.

## Register / Change Pass

This window registers or switches an **ArchePass**, not an account password. The list calls `X2ArchePass:GetCategories()` and reads each pass's name, description, end date, price, and rewards. The Chinese displayed pass name and description are separate from achievements, which was the only database section translated and installed.

The client `arche_passes` data includes permanent entries with all five end-date components zero. The registration UI formats these values directly, explaining displays such as `0. 00. 00 00:00` rather than a friendly unlimited-duration label. This is a display issue; do not invent an expiry date to correct it. Registration, point progress, and reward claims require their own server tests before being called functional.

## Patron and malformed expiry

The test account's actual payment row is account 2, payment method 1, start `2026-10-12 15:02:21`, end `9999-12-31 23:59:59`, buy count 0. The server's `AccountPayment.NoExpiryTime` explicitly uses that year-9999 sentinel for a grant without expiry. The screenshot's `1899-0--1 -1:-1` is not the stored subscription date. It is consistent with the native date conversion failing to represent that sentinel; the precise native conversion has not been traced here.

`premium_service_view.lua` formats `endTime.payEnd` without validating the date fields. A fix should show **No expiry** for the server's verified unlimited subscription state and a neutral **Expiry unavailable** for other invalid values, while retaining ordinary valid expiries. A guard must not falsely label every malformed date as an unlimited entitlement.

Patron product names are obtained by `PremiumServiceCatalog` from the **server** item templates/localization. Changing achievements in the client cannot translate them. Some products are named as test rows.

Purchase failure is explicit server behavior: `CSPremiumServiceBuyPacket` rejects requests with the message `Patron is granted, not sold`, sends `PremiumServiceBuyFail`, and refreshes the catalog. Runtime logs confirm these rejections at 18:20:17 and 18:20:23. A real purchase flow requires implementation and a choice of economy; it is not repaired by translating the Purchase button.

## Quest question marks

`questcontext/quest_context_common.lua` deliberately replaces main-quest titles with `N. ???` and chapter subtitles with `???` when `item.status == 0`. This is the client hiding the story before the quest begins. The screenshot also says “The story hasn't begun.” The quest list disables Map and Pass for unstarted or completed quests.

This screenshot therefore does not establish missing translation or corrupted quest names. Keep the intended spoiler/progress behavior. If an accepted/current quest also shows question marks, check its quest ID and actual client/server status separately.

## Next small steps

1. Get the Seasonal achievement window and its selected filter to narrow down Winter's absence.
2. Agree Autumn or Fall before making the small naming change.
3. Correct the Patron expiry display without changing the account's entitlement.
4. Translate the visible ArchePass and Patron product text as separate, backed-up sections.
5. Implement our community reward page when its destination and reward rules are defined.
