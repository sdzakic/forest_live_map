# The Forest Live Map
This is a mod for [The Forest game](https://en.wikipedia.org/wiki/The_Forest_(video_game)) based on the [Mod API](https://modapi.survivetheforest.net/). It is similar to and based on https://theforestmap.com/.

Supported features:
 * World map in a separate window (useful for multi-monitor configurations).
 * Locations of the local player and all enemies.
 * Multiplayer (co-op) support: every player in the session is shown with their name, each in their own color.
   Works both as the host and as a client. Press TAB in the map window to follow another player, SPACE to follow yourself.
 * Zoomable map with mouse wheel scroll.
 * Map follows the player automatically.
 * Actual locations of certain game objects such as cave entrances, artifacts, etc. Work in progress.

## Multiplayer

The mod sends the positions of all players in the co-op session, not just yours, so a single mod + viewer is enough
to see your whole team. Other players' names are taken from the game and labeled next to their markers.

If a friend wants the viewer window on their own PC while you host the game, they can run the viewer there and point
*their* copy of the mod at it: copy `LiveMapMod/LiveMap.cfg` next to `TheForest.exe` and set `host`/`port`. Several mods
can connect to the same viewer at the same time; objects coming from different connections are kept apart.
The viewer listens on all network interfaces, so you may need to allow port 9999 through the Windows firewall.

Players and enemies that stop being reported (left the game, despawned) disappear from the map after 5 seconds.

Limitations:
 * As a multiplayer *client*, the game does not have the host's enemy lists, so enemies are found by scanning the
   scene for networked enemy AI components every 5 seconds. If the game's class names change, enemies may not show
   for clients (check the ModAPI log for "Client enemy type not found").
 * Whether a *remote* player is in a cave is estimated from their height below the terrain surface; it may be wrong
   right at cave entrances.

## Project structure

The project consists of two parts:
 * The client side written in C# which interacts with the game and retrieves the required information. This is based on the [Mod API](https://modapi.survivetheforest.net/).
 * The server side written in Python ([pygame](https://www.pygame.org)) which receives data from the mod and displays objects on the screen.

The viewer port can be changed with the first command line argument: `python main.py 9999`.

TODO:
 * Add an option to explore the map with the mouse drag.
 * Add more game objects to the map.
 * Add UI for configuring what to display on the map.
 * Add an option to show game objects that are on the player level only (z axis). Currently the map shows all game objects even those that are located 100 meters above or below the player.
