"""3.3.5a client DBC data the converter needs.

Zones: Zygor map coordinates (zone name, x%, y%) to world coordinates, from
WorldMapArea.dbc (zone rectangle in the zone's own map) and AreaTable.dbc
(enUS zone names, as Zygor writes them).
FactionTemplates: whether a creature is hostile to Alliance or Horde players.
"""
from pathlib import Path
import struct

# Zygor names that differ from the AreaTable / WorldMapArea names.
ALIASES = {
    "Eastern Kingdoms": "Azeroth",
    "Stormwind": "Stormwind City",
}


def _read_dbc(path: Path, fmt: str):
    data = path.read_bytes()
    magic, count, _fields, size, _strings = struct.unpack_from("<4s4I", data)
    if magic != b"WDBC" or size != struct.calcsize(fmt):
        raise SystemExit(f"unexpected DBC layout: {path}")
    table = data[20 + count * size:]

    def string(offset):
        return table[offset:table.index(b"\0", offset)].decode("utf-8")
    return [struct.unpack_from(fmt, data, 20 + i * size) for i in range(count)], string


class Zones:
    def __init__(self, dbc: Path):
        areas, area_string = _read_dbc(dbc / "AreaTable.dbc", "<36I")
        names = {row[0]: area_string(row[11]) for row in areas}
        self.area_names = names
        rows, wma_string = _read_dbc(dbc / "WorldMapArea.dbc", "<4I4f3i")
        self.bounds = {}
        self.zone_area = {}
        # Bounds are in the coordinates of the row's own map, also for zones the world
        # map shows on another continent (Eversong Woods, Azuremyst Isle: map 530).
        for _, map_id, area_id, internal, left, right, top, bottom, _, _, _ in rows:
            name = names.get(area_id) if area_id else wma_string(internal)
            if not name or name in self.bounds:
                continue
            self.bounds[name] = (map_id, left, right, top, bottom)
            self.zone_area[name] = area_id
        for alias, name in ALIASES.items():
            if name in self.bounds:
                self.bounds.setdefault(alias, self.bounds[name])
        self.missing = set()

    def to_world(self, zone, x, y):
        """(map id, world x, world y) or None. x% runs left to right (world y), y% top to bottom (world x)."""
        if zone is None or x is None or y is None:
            return None
        zone = zone.split("/")[0]  # "Dragonblight/0": map floor
        bounds = self.bounds.get(zone)
        if bounds is None:
            self.missing.add(zone)
            return None
        map_id, left, right, top, bottom = bounds
        return map_id, top + (bottom - top) * y / 100.0, left + (right - left) * x / 100.0


# Player faction group bits and the player factions of FactionTemplate.dbc.
PLAYER_GROUP = {"Alliance": 2, "Horde": 4}
PLAYER_FACTION = {"Alliance": 469, "Horde": 67}


class FactionTemplates:
    def __init__(self, dbc: Path):
        rows, _ = _read_dbc(dbc / "FactionTemplate.dbc", "<14I")
        # id, faction, flags, our group, friend group, enemy group, enemies[4], friends[4]
        self.rows = {row[0]: row for row in rows}

    def hostile_to(self, template_id, side):
        row = self.rows.get(template_id)
        if row is None:
            return False
        if PLAYER_FACTION[side] in row[10:14]:
            return False
        return bool(row[5] & PLAYER_GROUP[side]) or PLAYER_FACTION[side] in row[6:10]


class Locks:
    """Lock.dbc: the items a lock asks for (a key opening a cage, a chest or a switch)."""

    LOCK_KEY_ITEM = 1

    def __init__(self, dbc: Path):
        rows, _ = _read_dbc(dbc / "Lock.dbc", "<33I")
        # id, type[8], index[8], skill[8], action[8]
        self.items = {row[0]: [row[9 + k] for k in range(8) if row[1 + k] == self.LOCK_KEY_ITEM and row[9 + k]]
                      for row in rows}

    def items_for(self, lock_id):
        return self.items.get(lock_id, [])
