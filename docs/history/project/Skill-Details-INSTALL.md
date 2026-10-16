> Historical project guide preserved on 16 October 2026. It may refer to separately supplied packages or a different checkout; deployment and test claims below are historical. Private local addresses are generalized. Read ../../CHANGES-AND-STATUS.md and ../../HISTORY-INDEX.md for current import scope.

# Skill descriptions and combos — October 9, 2026

Manager-only update for the working Skill Priority dashboard. No AAEmu Game rebuild or database edits are needed.

1. Stop the Node Companion Manager and back up the matching files.
2. Copy MANAGER contents into your existing playerbots-manager folder, replacing matching files. Keep your settings, credentials and request history.
3. Restart the Node manager and refresh the browser with Ctrl+F5.
4. Open Skill Priority and click a skill name, or click a skill card in Skills & Build. Learned and locked skills can both be inspected.

The popup shows the skill icon, description, available combo/synergy tooltips, and base values. Learning an available skill is a separate Preview Learning button, followed by the existing confirmation. Selecting a skill for information does not cast, learn, reorder or reset it.

Descriptions and combo text come from the English localized_texts and skill_synergy_icons in your ArcheAge 1.2 compact.sqlite3 data, read without modifying the database. Passive descriptions use their linked buff entries. Dynamic values are shown as readable placeholders rather than invented damage/healing numbers. Base values are not a calculation of the companion's current stats. Missing descriptions or combos are identified clearly.

Antithesis includes its ally healing/enemy magic damage description and four synergy tooltips: Inspired, Conversion Shield, Resurgence and Charmed. Your saved skill priorities and cooldown skipping are preserved.

Validation: 27 dashboard tests passed, including description delivery, tooltip formatting, Antithesis' four combos, inspection without learning, and prevention of learning on a different companion after switching. Live browser/game testing remains after installation.
