# The Forest Live Map
This is a mod for [The Forest game](https://en.wikipedia.org/wiki/The_Forest_(video_game)) based on the [Mod API](https://modapi.survivetheforest.net/). It is similar to and based on https://theforestmap.com/.

This is a fork of [igorrogov/forest_live_map](https://github.com/igorrogov/forest_live_map) by Igor Rogov that adds co-op multiplayer support. See [Credits](#credits).

Supported features:
 * World map in a separate window (useful for multi-monitor configurations).
 * Locations of the local player and all enemies.
 * Multiplayer (co-op) support: every player in the session is shown with their name, each in their own color.
   Works both as the host and as a client. Press TAB in the map window to follow another player, SPACE to follow yourself.
 * Zoomable map with mouse wheel scroll.
 * Map follows the player automatically.
 * Actual locations of certain game objects such as cave entrances, artifacts, etc. Work in progress.

## Compatibility

| Component | Version |
|---|---|
| The Forest (Steam) | 1.11b |
| ModAPI | v2 (checked against 2.0.9623) |
| Python | 3.13 with pygame 2.6.1 and Twisted |

The mod compiles against the assemblies of The Forest 1.11b, and the viewer has been tested with simulated connections. The multiplayer features have not been tested in a live co-op session yet, so please report what you find.

## Installation

You need Windows, The Forest on Steam, [Visual Studio](https://visualstudio.microsoft.com/) (not VS Code) and [Python 3](https://www.python.org/downloads/).
There is no prebuilt `.mod` file, so the mod is built from source once using ModAPI.

### 1. Install ModAPI

1. Go to the [ModAPI site](https://modapi.survivetheforest.net/), choose The Forest and download the ModAPI **v2** package. v1 is deprecated. Do not extract v2 over an existing v1 folder.
2. Extract the zip to a folder your user account controls. Do not extract it inside the Steam game folder.
3. Run `ModAPI.exe` and let it find your game.

### 2. Build the mod

1. In ModAPI, open the **Development** tab, click **Create new project** and use `LiveMap` as the ID.
2. Close ModAPI. It rewrites `ModInfo.xml` whenever it saves a project, so a copy made while it is open can be overwritten.
3. Copy `LiveMapMod/LiveMap.cs` and `LiveMapMod/ModInfo.xml` into `<ModAPI>/projects/TheForest/LiveMap/`, replacing the generated `ModInfo.xml`.
4. Open the `.sln` file from that folder in Visual Studio. Add `LiveMap.cs` to the project if it is not listed, and add a reference to `System` under **References**, because ModAPI's generated project leaves it out.
5. Build the solution with Ctrl+Shift+B.
6. Start ModAPI again, select the project and click **Create Mod** in the lower right corner. It should report that the mod was created.

If you change the code and create the mod again, increase `Version` in `ModInfo.xml` or delete the old file from `<ModAPI>/mods/TheForest/`.

Troubleshooting:
 * If the build cannot find `TcpClient` or `Queue`, the `System` reference is missing. ModAPI can remove it when it regenerates the project, so add it again.
 * If the F7 key is missing, add an assignable key with the ID `Update` to the project in the Development tab.
 * If ModAPI says to create the Mod Library first, or the build cannot find the game's classes, create the Mod Library in the Development tab and reopen the project. The project's references to the game, including `bolt.dll` and `bolt.user.dll`, depend on it.

### 3. Enable the mod

Open the **Mods** tab in ModAPI, enable Live Map and start the game from there.
To remove all mods later, use "Verify integrity of game files" in Steam.

### 4. Run the map viewer

```bash
pip install pygame twisted
cd PyForestLiveMap
python main.py
```

Always start it from the `PyForestLiveMap` folder, because it loads the map images from there. The viewer and the game can be started in either order, since the mod keeps trying to connect.

### 5. Use it

 * The map shows players and enemies as soon as you are in a game.
 * Press **F7** in the game to load cave entrances and pickups. They are not sent until you do.
 * Scroll the mouse wheel to zoom. In a co-op game, press TAB to follow another player and SPACE to follow yourself.
 * If nothing appears, check the ModAPI log for "Cannot send to viewer".

ModAPI is built for single player and its [FAQ](https://modapi.survivetheforest.net/faq/) says multiplayer use is at your own risk. Only use this mod in sessions you or your friends run, because mods in other people's sessions can get you banned from them.

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
   right at cave entrances. Enemies seen by a multiplayer client use the same estimate.
 * Cave entrances and pickups are shown on both the overworld and the caves map.
 * The viewer has no authentication. Anyone who can reach its port can send markers to it, so only open the port to people you trust and do not expose it to the internet.

## Project structure

The project consists of two parts:
 * The client side written in C# which interacts with the game and retrieves the required information. This is based on the [Mod API](https://modapi.survivetheforest.net/).
 * The server side written in Python ([pygame](https://www.pygame.org)) which receives data from the mod and displays objects on the screen.

The viewer port can be changed with the first command line argument: `python main.py 9999`.

### Configuration

The mod reads `LiveMap.cfg` from the game folder, next to `TheForest.exe`. Without the file it uses the defaults.

| Setting | Default | Meaning |
|---|---|---|
| `host` | `127.0.0.1` | Address of the PC running the viewer |
| `port` | `9999` | TCP port the viewer listens on |

### Data format

The mod sends one JSON object per line over TCP. Each object has these fields:

| Field | Meaning |
|---|---|
| `type` | 0 player, 1 enemy, 2 cave entrance, 3 pickup |
| `id` | Unity instance ID, unique within one running game |
| `itemID` | Item ID for pickups, otherwise -1 |
| `name` | Player name or game object name |
| `x`, `y` | Horizontal world position |
| `z` | Height |
| `rotZ` | Heading in degrees |
| `inCave` | Whether the object is in the caves |
| `isLocal` | Whether this is the player running the mod |

An object containing only `actionType` is a control message.

### Automated builds

The mod has to be built on a PC with The Forest installed. It compiles against the game's own DLLs, which cannot be redistributed in this repository, and the final **Create Mod** step only exists in ModAPI's user interface. A GitHub Actions hosted runner has neither, so there is no prebuilt download.

TODO:
 * Add an option to explore the map with the mouse drag.
 * Add more game objects to the map.
 * Add UI for configuring what to display on the map.
 * Add an option to show game objects that are on the player level only (z axis). Currently the map shows all game objects even those that are located 100 meters above or below the player.

## Credits

 * [Igor Rogov](https://github.com/igorrogov) created the original [Forest Live Map](https://github.com/igorrogov/forest_live_map) mod and viewer.
 * [sdzakic](https://github.com/sdzakic) maintains this fork, which adds multiplayer support.
 * [theforestmap.com](https://theforestmap.com/) inspired the original project.
 * Built on [ModAPI](https://modapi.survivetheforest.net/), [pygame](https://www.pygame.org) and [Twisted](https://twisted.org/).

## License

MIT, see [LICENSE](LICENSE). The original copyright notice by Igor Rogov is retained.
