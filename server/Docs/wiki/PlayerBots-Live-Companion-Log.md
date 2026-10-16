# Live companion activity log

Install the matching Game server source and Manager dashboard update. In Companion Manager, open Activity Log. The existing signed-in companion access rules apply to the read-only activitylog endpoint.

The dashboard refreshes every five seconds while visible. Gathering requests, route selection (route length, direct distance and destination), hostile replans, walking progress, cancellation, native action start and observed completion appear with server UTC times and positions. Walking samples are recorded every ten seconds, including when stationary. Completion distinguishes an observed inventory change from an action ending without an observed reward.

Download Log exports the selected companion's latest activity plus gathering and movement state to JSON. It includes no API key or login credentials. This allows diagnosis without transferring the full Server.log. Events before installation are not imported.

The server keeps up to 200 events per companion for up to 256 recently recorded companions, in memory. Older events and inactive companion buffers are evicted; server restart clears them. No server log file polling or disk writes are added to the world tick. This is a diagnostic change and does not alter path selection.
