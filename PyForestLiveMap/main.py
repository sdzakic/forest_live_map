import json
import sys
import time
from enum import Enum

import pygame
from pygame.locals import *
from pygame.math import Vector2

from twisted.internet.task import LoopingCall
from twisted.internet import reactor, protocol
from twisted.protocols.basic import LineReceiver

pygame.init()

# TCP port the mod connects to (see LiveMapMod/LiveMap.cfg); override with the first command line argument
LISTEN_PORT = int(sys.argv[1]) if len(sys.argv) > 1 else 9999

# square window: 1000 x 1000
WINDOW_SIZE = 1000

# dimensions of the map image PNG files: 4096 x 4096
MAP_IMAGE_SIZE = 4096

MAP_IMAGE_OFFSET_X = 2.5
MAP_IMAGE_OFFSET_Y = 0

# how much the map image is bigger than the actual in game world (1750x2 -> 4096)
WORLD_TO_MAP_IMAGE_SCALE_X = 1.17269076305
WORLD_TO_MAP_IMAGE_SCALE_Y = 1.168

FPS = 30.0
MAX_ZOOM_FACTOR = 5.0
MIN_ZOOM_FACTOR = 0.75

# players and enemies are re-sent by the mod every second; if we haven't heard about one for this long
# it left the game / despawned and is removed from the map. Static objects (caves, pickups) never expire.
DYNAMIC_OBJECT_TTL_SEC = 5.0

MARKER_SZE = 7.0
ICON_SIZE = 64
LABEL_FONT_SIZE = 13
HUD_FONT_SIZE = 18
ICONS = {}
FONTS = {}

TYPE_PLAYER = 0
TYPE_ENEMY = 1
TYPE_CAVE_ENTRANCE = 2
TYPE_PICKUP = 3

DYNAMIC_TYPES = (TYPE_PLAYER, TYPE_ENEMY)

LOCAL_PLAYER_COLOR = 'green'
# colors handed out to other players in the session, in order of appearance
REMOTE_PLAYER_COLORS = ['cyan', 'yellow', 'magenta', 'orange', 'deepskyblue', 'violet', 'springgreen', 'gold']


class Item(Enum):
    CLOTH = 33
    MEDS = 49
    FLASHLIGHT = 51
    ROPE = 54
    OLD_POT = 142
    AIR_CANISTER = 144
    DYNAMITE = 175

    @classmethod
    def parse(cls, item_id: int):
        for item in Item:
            if item_id == item.value:
                return item
        return None


# convert in-game world coordinates (center at 0,0) to map image coordinates (normalized to 4096)
def world_to_map_image(wx: float, wy: float):
    mx = -(wx * WORLD_TO_MAP_IMAGE_SCALE_X) + MAP_IMAGE_SIZE / 2 - MAP_IMAGE_OFFSET_X
    my = (wy * WORLD_TO_MAP_IMAGE_SCALE_Y) + MAP_IMAGE_SIZE / 2 + MAP_IMAGE_OFFSET_Y
    return mx, my


def load_icons():
    ICONS[TYPE_CAVE_ENTRANCE] = pygame.image.load("icons/entrance.png")
    ICONS[Item.CLOTH] = pygame.image.load("icons/cloth.png")
    ICONS[Item.MEDS] = pygame.image.load("icons/meds.png")
    ICONS[Item.FLASHLIGHT] = pygame.image.load("icons/flashlight.png")
    ICONS[Item.ROPE] = pygame.image.load("icons/rope_pickup.png")
    ICONS[Item.OLD_POT] = pygame.image.load("icons/old_pot.png")
    ICONS[Item.AIR_CANISTER] = pygame.image.load("icons/air_canister.png")
    ICONS[Item.DYNAMITE] = pygame.image.load("icons/dynamite_64.png")


def get_font(size: int):
    size = max(int(size), 8)
    if size not in FONTS:
        FONTS[size] = pygame.font.SysFont("arial", size, bold=True)
    return FONTS[size]


class GameProtocol(LineReceiver):
    """One connection from one running copy of the mod.

    Several mods may connect at once (e.g. every player in a co-op session points their mod at
    the same viewer), so everything a connection sends is tagged with its connection id to keep
    the Unity instance IDs of different game processes from colliding.
    """

    delimiter = b"\n"
    MAX_LENGTH = 1024 * 1024
    next_conn_id = 1

    def connectionMade(self):
        self.conn_id = GameProtocol.next_conn_id
        GameProtocol.next_conn_id += 1
        peer = self.transport.getPeer()
        print(f"New incoming connection #{self.conn_id} from {peer.host}:{peer.port}")
        game.connection_made(self.conn_id)

    def connectionLost(self, reason=protocol.connectionDone):
        print(f"Connection #{self.conn_id} closed")
        game.connection_lost(self.conn_id)

    def lineReceived(self, line):
        try:
            game_object = json.loads(line)
        except ValueError as e:
            print(f"Ignoring malformed line from connection #{self.conn_id}: {e}")
            return
        game.received_object(self.conn_id, game_object)


class Game:

    def __init__(self):
        super().__init__()

        pygame.display.set_caption('Forest Live Map')

        self.clock = pygame.time.Clock()
        self.screen_res = [WINDOW_SIZE, WINDOW_SIZE]
        self.screen = pygame.display.set_mode(self.screen_res, pygame.HWSURFACE, 32)

        self.zoom_factor = 2.5

        self.bg_world_image = pygame.image.load("overworld.jpg")
        self.bg_caves_image = pygame.image.load("caves.jpg")

        load_icons()

        # in-game coordinates of the point of view (map center)
        # by default, it will follow the local player
        self.point_of_view_wx = 0.0
        self.point_of_view_wy = 0.0

        # all game objects that are shown on the map (dict: (connection ID, instance ID) -> json object)
        self.game_objects = {}
        # (connection ID, instance ID) -> time.time() of the last update, for players and enemies only
        self.last_update_time = {}

        # key of the player the map is centered on, or None to stay where we are
        self.followed_key = None
        # key -> color name, so a player keeps the same color for the whole session
        self.player_colors = {}

        # flag that tells whether the followed player is in the caves (selects the map image)
        self.is_in_caves = False
        self.is_running = True
        self.n_pickups = 0
        self.n_cave_entrances = 0
        self.n_connections = 0

        self.looping_call = LoopingCall(self.loop)
        self.looping_call.start(1.0 / FPS)

    # main loop method
    def loop(self):
        self.event_loop()
        self.evict_expired_game_objects()
        self.update_point_of_view()
        self.draw()

    # process input events from mouse and keyboard
    def event_loop(self):
        for event in pygame.event.get():
            if event.type == QUIT:
                self.is_running = False
                self.looping_call.stop()
                reactor.stop()
                pygame.quit()
                sys.exit()
            if event.type == MOUSEWHEEL:
                self.zoom_factor -= event.y*0.1
                self.zoom_factor = min(self.zoom_factor, MAX_ZOOM_FACTOR)
                self.zoom_factor = max(self.zoom_factor, MIN_ZOOM_FACTOR)
            if event.type == KEYDOWN:
                if event.key == K_TAB:
                    self.follow_next_player()
                if event.key == K_SPACE:
                    self.follow_local_player()

    # ------------------------------------------------------------------ connections

    def connection_made(self, conn_id: int):
        self.n_connections += 1

    def connection_lost(self, conn_id: int):
        self.n_connections = max(0, self.n_connections - 1)
        # everything that connection told us about is gone with it
        for key in [k for k in self.game_objects if k[0] == conn_id]:
            self.remove_object(key)

    # ------------------------------------------------------------------ game objects

    def remove_object(self, key):
        self.game_objects.pop(key, None)
        self.last_update_time.pop(key, None)
        if self.followed_key == key:
            self.followed_key = None

    def remove_enemies(self):
        for key, go in list(self.game_objects.items()):
            if go['type'] == TYPE_ENEMY:
                self.remove_object(key)

    def evict_expired_game_objects(self):
        now = time.time()
        for key, last in list(self.last_update_time.items()):
            if now - last > DYNAMIC_OBJECT_TTL_SEC:
                go = self.game_objects.get(key)
                if go is not None and go['type'] == TYPE_PLAYER:
                    print(f"Player left: {go.get('name', key)}")
                self.remove_object(key)

    def players(self):
        return {k: go for k, go in self.game_objects.items() if go['type'] == TYPE_PLAYER}

    def received_object(self, conn_id: int, game_object):
        if "actionType" in game_object:
            return

        key = (conn_id, game_object["id"])
        game_object.setdefault('inCave', False)
        game_object.setdefault('isLocal', False)
        game_object.setdefault('name', '')

        previous = self.game_objects.get(key)
        self.game_objects[key] = game_object

        if game_object['type'] in DYNAMIC_TYPES:
            self.last_update_time[key] = time.time()

        if game_object['type'] == TYPE_PLAYER:
            if previous is None:
                self.assign_player_color(key, game_object)
                print(f"Player joined: {game_object['name']}" + (" (local)" if game_object['isLocal'] else ""))
            # follow the first local player we hear about (ours), or whoever shows up first otherwise
            if self.followed_key is None or (game_object['isLocal'] and not self.game_objects.get(self.followed_key, {}).get('isLocal')):
                self.followed_key = key
        if game_object['type'] == TYPE_PICKUP:
            game_object['item'] = Item.parse(game_object['itemID'])
            self.n_pickups += 1
            print('Pickups: ' + str(self.n_pickups))
        if game_object['type'] == TYPE_CAVE_ENTRANCE:
            self.n_cave_entrances += 1
            print('Cave entrances: ' + str(self.n_cave_entrances))

    def assign_player_color(self, key, game_object):
        if game_object['isLocal']:
            self.player_colors[key] = LOCAL_PLAYER_COLOR
            return
        used = set(self.player_colors.values())
        for color in REMOTE_PLAYER_COLORS:
            if color not in used:
                self.player_colors[key] = color
                return
        self.player_colors[key] = REMOTE_PLAYER_COLORS[len(self.player_colors) % len(REMOTE_PLAYER_COLORS)]

    def player_color(self, key, game_object):
        if key not in self.player_colors:
            self.assign_player_color(key, game_object)
        return self.player_colors[key]

    # ------------------------------------------------------------------ following

    def follow_next_player(self):
        keys = sorted(self.players().keys())
        if not keys:
            return
        if self.followed_key in keys:
            self.followed_key = keys[(keys.index(self.followed_key) + 1) % len(keys)]
        else:
            self.followed_key = keys[0]
        print(f"Following: {self.game_objects[self.followed_key]['name']}")

    def follow_local_player(self):
        for key, go in self.players().items():
            if go['isLocal']:
                self.followed_key = key
                print(f"Following: {go['name']}")
                return

    def update_point_of_view(self):
        followed = self.game_objects.get(self.followed_key)
        if followed is None:
            return
        self.point_of_view_wx = followed['x']
        self.point_of_view_wy = followed['y']
        if self.is_in_caves != followed['inCave']:
            # the followed player entered or left a cave: switch map and drop the enemies of the other layer
            self.is_in_caves = followed['inCave']
            self.remove_enemies()

    # ------------------------------------------------------------------ drawing

    def draw(self):
        if self.is_in_caves:
            map_surface = self.bg_caves_image.copy()
        else:
            map_surface = self.bg_world_image.copy()

        # players last so they are drawn on top of everything else
        players = []
        for key, game_object in list(self.game_objects.items()):
            if game_object['type'] == TYPE_PLAYER:
                players.append((key, game_object))
            elif game_object['type'] == TYPE_ENEMY:
                if game_object['inCave'] == self.is_in_caves:
                    self.draw_marker(map_surface, game_object['x'], game_object['y'], 'red', False, game_object['rotZ'])
            elif game_object['type'] == TYPE_CAVE_ENTRANCE:
                self.draw_marker_icon(map_surface, game_object['x'], game_object['y'], ICONS[TYPE_CAVE_ENTRANCE])
            elif "item" in game_object and game_object['item'] in ICONS:
                self.draw_marker_icon(map_surface, game_object['x'], game_object['y'], ICONS[game_object['item']])

        for key, game_object in players:
            # only players on the same layer (overworld / caves) as the map we are showing
            if game_object['inCave'] != self.is_in_caves:
                continue
            color = self.player_color(key, game_object)
            self.draw_marker(map_surface, game_object['x'], game_object['y'], color, True, game_object['rotZ'])
            self.draw_label(map_surface, game_object['x'], game_object['y'], game_object['name'], color)

        self.blit_map_surface(map_surface)
        self.draw_hud(players)
        pygame.display.update()

    def draw_marker(self, map_surface, wx: float, wy: float, color: str, show_direction: bool, rot_z: float):
        mx, my = world_to_map_image(wx, wy)
        pygame.draw.circle(map_surface, pygame.color.Color(color), (mx, my), (MARKER_SZE - 1) * self.zoom_factor - 1)
        pygame.draw.circle(map_surface, pygame.color.Color('white'), (mx, my), MARKER_SZE * self.zoom_factor, width=1)

        if show_direction:
            v = Vector2(0, (MARKER_SZE + 5) * self.zoom_factor)
            v.rotate_ip(rot_z)
            arrow_cx, arrow_cy = (mx + v.x, my + v.y)
            v.scale_to_length((MARKER_SZE + 2) * self.zoom_factor)
            v.rotate_ip(-45)
            arrow_lx, arrow_ly = (mx + v.x, my + v.y)
            v.rotate_ip(90)
            arrow_rx, arrow_ry = (mx + v.x, my + v.y)
            pygame.draw.line(map_surface, pygame.color.Color('white'), (arrow_cx, arrow_cy), (arrow_lx, arrow_ly), 2)
            pygame.draw.line(map_surface, pygame.color.Color('white'), (arrow_cx, arrow_cy), (arrow_rx, arrow_ry), 2)

    def draw_label(self, map_surface, wx: float, wy: float, text: str, color: str):
        if not text:
            return
        mx, my = world_to_map_image(wx, wy)
        # the map surface is scaled down by zoom_factor when blitted, so scale the text up to compensate
        font = get_font(LABEL_FONT_SIZE * self.zoom_factor)
        shadow = font.render(text, True, pygame.color.Color('black'))
        label = font.render(text, True, pygame.color.Color(color))
        x = mx - label.get_width() / 2
        y = my + (MARKER_SZE + 3) * self.zoom_factor
        offset = max(1, int(self.zoom_factor))
        map_surface.blit(shadow, (x + offset, y + offset))
        map_surface.blit(label, (x, y))

    def draw_marker_icon(self, map_surface, wx: float, wy: float, marker_image):
        mx, my = world_to_map_image(wx, wy)
        scaled_icon_size = ICON_SIZE * self.zoom_factor / 4
        marker_image = pygame.transform.smoothscale(marker_image, (int(scaled_icon_size), int(scaled_icon_size)))
        map_surface.blit(marker_image, (mx - scaled_icon_size/2, my - scaled_icon_size/2))

    # list of players in the top left corner of the window, drawn directly on the screen (not scaled)
    def draw_hud(self, players):
        font = get_font(HUD_FONT_SIZE)
        x, y = 10, 10
        line_height = font.get_linesize() + 4

        if self.n_connections == 0:
            self.screen.blit(font.render(f"Waiting for the game mod on port {LISTEN_PORT}...", True, pygame.color.Color('white')), (x, y))
            return

        for key, go in sorted(players, key=lambda kv: (not kv[1]['isLocal'], kv[1]['name'])):
            color = self.player_color(key, go)
            text = go['name']
            if key == self.followed_key:
                text = "> " + text
            if go['inCave'] != self.is_in_caves:
                text += " (in caves)" if go['inCave'] else " (outside)"
            pygame.draw.circle(self.screen, pygame.color.Color(color), (x + 8, y + line_height // 2), 6)
            pygame.draw.circle(self.screen, pygame.color.Color('white'), (x + 8, y + line_height // 2), 7, width=1)
            self.screen.blit(font.render(text, True, pygame.color.Color('white')), (x + 22, y))
            y += line_height

        if len(players) > 1:
            self.screen.blit(font.render("TAB: follow next player   SPACE: follow yourself", True, pygame.color.Color('lightgray')), (x, y + 6))

    def blit_map_surface(self, map_surface):
        dst_size = (int(map_surface.get_rect().w / self.zoom_factor), int(map_surface.get_rect().h / self.zoom_factor))
        map_surface = pygame.transform.smoothscale(map_surface, dst_size)
        rect = map_surface.get_rect()
        rect.center = WINDOW_SIZE / 2, WINDOW_SIZE / 2

        pov_mx, pov_my = world_to_map_image(self.point_of_view_wx, self.point_of_view_wy)
        center_offset_x = (pov_mx - MAP_IMAGE_SIZE / 2) / self.zoom_factor
        center_offset_y = (pov_my - MAP_IMAGE_SIZE / 2) / self.zoom_factor
        rect.move_ip(-center_offset_x, -center_offset_y)

        self.screen.fill(pygame.color.Color('black'))
        self.screen.blit(map_surface, rect)


game = Game()

factory = protocol.Factory()
factory.protocol = GameProtocol
# listen on all interfaces so mods running on other PCs in the session can connect too
reactor.listenTCP(LISTEN_PORT, factory, interface="0.0.0.0")
print(f"Listening for the Live Map mod on port {LISTEN_PORT}")
reactor.run()
