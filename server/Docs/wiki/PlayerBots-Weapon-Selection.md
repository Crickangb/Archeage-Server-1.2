# Companion weapon selection

Server-controlled companions scan equipped weapons and bag weapons every two seconds when they are alive and not fighting, casting, gathering, crafting, jumping, falling, climbing, or attached to a vehicle/mount. This also applies to Farmers. Connected client actors retain native equipment control.

Learned combat skills determine the weapon profile. The highest-priority learned attack takes precedence; otherwise the largest learned attack family wins, with the starting ability resolving ties. Healing/support skills are used when no attack is learned. A healer role with learned Vitalism uses healing gear. Defense skills or a tank role favor a shield for a melee build.

Magic favors staves/scepters and Intelligence; healing favors clubs and Spirit; archery uses a bow plus melee backup and Dexterity; melee favors Strength weapons. These are preferences rather than skill prerequisites. The picker only uses existing items allowed by native equipment slots and level restrictions, scores their level and relevant attributes, and retains current gear on ties. If no suitable item exists, current gear remains. This does not buy items or change learned skills, skillsets, armor, or combat priorities.

Two-handed weapons occupy both hands. Equipping one uses the native inventory move to return an existing offhand to the bag. With insufficient bag space, the move fails without deleting items. A dashboard offhand request is rejected while a two-handed weapon is equipped; replace or unequip it first. Existing illegal two-handed/offhand pairs are repaired by returning the offhand to the bag when space is available.

Deployment: merge SERVER-SOURCE into the AAEmu source checkout, rebuild AAEmu.Game, deploy the rebuilt runtime and restart. No database or configuration change is required.
