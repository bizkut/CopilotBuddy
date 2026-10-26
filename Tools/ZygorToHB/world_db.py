"""Reads the 3.3.5a world data the converter needs.

Two sources, same AzerothCore schema:
- the server's live world database: the needed tables are exported with
  mysqldump into `.cache/<database>/` (World.from_server);
- AzerothCore's SQL dumps (`data/sql/base/db_world/*.sql` in an
  azerothcore-wotlk checkout), parsed directly (World.from_azerothcore).
The parsed tables are pickled under `.cache/` and reused while the dump files
are unchanged.
"""
from collections import defaultdict
from dataclasses import dataclass, field
from pathlib import Path
import hashlib
import pickle
import re
import subprocess

TABLES = ["quest_template", "quest_template_addon", "creature_queststarter", "creature_questender",
          "gameobject_queststarter", "gameobject_questender", "creature_template", "creature", "gameobject_template",
          "gameobject", "item_template", "reference_loot_template", "creature_loot_template",
          "gameobject_loot_template", "npc_vendor", "areatrigger", "areatrigger_involvedrelation", "smart_scripts"]

CACHE_DIR = Path(__file__).resolve().parent / ".cache"
CACHE_VERSION = 8

_COLUMN = re.compile(r"^\s*`(\w+)`", re.M)
_VALUE = re.compile(r"'((?:[^'\\]|\\.|'')*)'|(NULL)|(-?[0-9.eE+-]+)")
_ESCAPES = {"n": "\n", "r": "\r", "t": "\t", "0": "\0", "Z": "\x1a"}


def _unescape(text):
    text = text.replace("''", "'")
    return re.sub(r"\\(.)", lambda m: _ESCAPES.get(m.group(1), m.group(1)), text)


def _rows(path: Path, wanted):
    """Yields dicts with the `wanted` columns of every row of the dump's INSERTs."""
    text = path.read_text(encoding="utf-8", errors="replace")
    create = text[text.index("CREATE TABLE"):]
    create = create[:create.index(") ENGINE")]
    columns = _COLUMN.findall(create)
    index = [columns.index(name) for name in wanted]
    for insert in re.finditer(r"INSERT INTO `\w+` VALUES\s*", text):
        position = insert.end()
        while position < len(text) and text[position] == "(":
            position += 1
            values = []
            while True:
                match = _VALUE.match(text, position)
                if not match:
                    raise ValueError(f"{path.name}: cannot parse value at {position}: {text[position:position + 60]!r}")
                quoted, null, number = match.groups()
                if quoted is not None:
                    values.append(_unescape(quoted))
                elif null:
                    values.append(None)
                else:
                    values.append(float(number) if any(c in number for c in ".eE") else int(number))
                position = match.end()
                if text[position] == ",":
                    position += 1
                    continue
                if text[position] == ")":
                    position += 1
                    break
                raise ValueError(f"{path.name}: unexpected {text[position]!r} at {position}")
            yield {name: values[i] for name, i in zip(wanted, index)}
            while position < len(text) and text[position] in ",\r\n \t":
                position += 1


@dataclass
class Quest:
    id: int
    title: str
    level: int
    min_level: int
    flags: int
    races: int
    classes: int = 0
    sort: int = 0
    group: int = 0
    start_item: int = 0
    npc_or_go: list = field(default_factory=list)     # [(id, count)], id < 0 = gameobject
    items: list = field(default_factory=list)         # [(item, count)]
    objective_texts: list = field(default_factory=list)
    prev: int = 0
    next: int = 0
    exclusive: int = 0
    breadcrumb_for: int = 0
    special_flags: int = 0
    skill: int = 0
    skill_points: int = 0
    rep_faction: int = 0
    rep_value: int = 0
    max_level: int = 0
    starters: list = field(default_factory=list)      # [("npc"|"object"|"item", id)]
    enders: list = field(default_factory=list)
    areatriggers: list = field(default_factory=list)  # [(map, x, y, z)] exploration objectives

    @property
    def daily(self):
        return bool(self.flags & 0x1000)

    @property
    def weekly(self):
        return bool(self.flags & 0x8000)

    @property
    def repeatable(self):
        return bool(self.special_flags & 1) or self.daily or self.weekly


@dataclass
class Creature:
    id: int
    name: str
    faction: int
    npcflag: int
    unit_flags: int
    min_level: int
    max_level: int
    rank: int
    kill_credit: tuple
    vehicle: int
    type_flags: int


@dataclass
class GameObject:
    id: int
    name: str
    type: int
    data0: int
    data1: int


class World:
    def __init__(self):
        self.quests: dict[int, Quest] = {}
        self.creatures: dict[int, Creature] = {}
        self.objects: dict[int, GameObject] = {}
        self.items: dict[int, dict] = {}
        self.creature_spawns: dict[int, list] = defaultdict(list)   # entry -> [(map, x, y, z)]
        self.object_spawns: dict[int, list] = defaultdict(list)
        self.item_from_creatures: dict[int, list] = defaultdict(list)  # item -> [(creature, chance, questreq)]
        self.item_from_objects: dict[int, list] = defaultdict(list)    # item -> [(gameobject, chance)]
        self.credit_sources: dict[int, list] = defaultdict(list)       # kill credit entry -> creatures granting it
        self.vendors: dict[int, set] = defaultdict(set)                # creature -> items sold
        self.attackable_after_spell: set[int] = set()                   # creatures a spell hit makes attackable

    @staticmethod
    def from_azerothcore(azerothcore: Path) -> "World":
        dumps = azerothcore / "data" / "sql" / "base" / "db_world"
        if not dumps.is_dir():
            raise SystemExit(f"no AzerothCore world dumps at {dumps}")
        return World._load(dumps, CACHE_DIR / "azerothcore.pickle")

    @staticmethod
    def from_server(mysqldump: Path, host, port, user, password, database) -> "World":
        """Exports the needed tables of the live world database, then parses them."""
        out = CACHE_DIR / database
        out.mkdir(parents=True, exist_ok=True)
        for table in TABLES:
            with open(out / f"{table}.sql", "wb") as sink:
                subprocess.run([str(mysqldump), f"--host={host}", f"--port={port}", f"--user={user}",
                                f"--password={password}", "--skip-comments", "--no-tablespaces",
                                "--default-character-set=utf8mb4", database, table],
                               stdout=sink, stderr=subprocess.PIPE, check=True)
        return World._load(out, CACHE_DIR / f"{database}.pickle")

    @staticmethod
    def _load(dumps: Path, cache: Path) -> "World":
        stamp = (CACHE_VERSION, sorted((p.name, hashlib.sha1(p.read_bytes()).hexdigest())
                                       for p in dumps.glob("*.sql") if p.stem in TABLES))
        if cache.exists():
            try:
                cached_stamp, world = pickle.loads(cache.read_bytes())
                if cached_stamp == stamp:
                    return world
            except Exception:
                pass
        world = World()
        world._read(dumps)
        cache.parent.mkdir(exist_ok=True)
        cache.write_bytes(pickle.dumps((stamp, world), protocol=pickle.HIGHEST_PROTOCOL))
        return world

    def _read(self, dumps: Path):
        npc_go = [f"RequiredNpcOrGo{i}" for i in range(1, 5)] + [f"RequiredNpcOrGoCount{i}" for i in range(1, 5)]
        req_items = [f"RequiredItemId{i}" for i in range(1, 7)] + [f"RequiredItemCount{i}" for i in range(1, 7)]
        texts = [f"ObjectiveText{i}" for i in range(1, 5)]
        for row in _rows(dumps / "quest_template.sql",
                         ["ID", "LogTitle", "QuestLevel", "MinLevel", "Flags", "AllowableRaces", "QuestSortID",
                          "SuggestedGroupNum", "StartItem", *npc_go, *req_items, *texts]):
            quest = Quest(row["ID"], row["LogTitle"] or "", row["QuestLevel"], row["MinLevel"], row["Flags"],
                          row["AllowableRaces"], sort=row["QuestSortID"], group=row["SuggestedGroupNum"],
                          start_item=row["StartItem"])
            quest.npc_or_go = [(row[f"RequiredNpcOrGo{i}"], row[f"RequiredNpcOrGoCount{i}"]) for i in range(1, 5)]
            quest.items = [(row[f"RequiredItemId{i}"], row[f"RequiredItemCount{i}"]) for i in range(1, 7)]
            quest.objective_texts = [row[name] or "" for name in texts]
            self.quests[quest.id] = quest
        for row in _rows(dumps / "quest_template_addon.sql",
                         ["ID", "MaxLevel", "AllowableClasses", "PrevQuestID", "NextQuestID", "ExclusiveGroup",
                          "BreadcrumbForQuestId", "RequiredSkillID", "RequiredSkillPoints", "RequiredMinRepFaction",
                          "RequiredMinRepValue", "SpecialFlags"]):
            quest = self.quests.get(row["ID"])
            if quest:
                quest.max_level, quest.classes = row["MaxLevel"], row["AllowableClasses"]
                quest.prev, quest.next, quest.exclusive = row["PrevQuestID"], row["NextQuestID"], row["ExclusiveGroup"]
                quest.breadcrumb_for, quest.skill = row["BreadcrumbForQuestId"], row["RequiredSkillID"]
                quest.skill_points = row["RequiredSkillPoints"]
                quest.rep_faction, quest.rep_value = row["RequiredMinRepFaction"], row["RequiredMinRepValue"]
                quest.special_flags = row["SpecialFlags"]
        for table, kind, target in (("creature_queststarter", "npc", "starters"), ("creature_questender", "npc", "enders"),
                                    ("gameobject_queststarter", "object", "starters"),
                                    ("gameobject_questender", "object", "enders")):
            for row in _rows(dumps / f"{table}.sql", ["id", "quest"]):
                quest = self.quests.get(row["quest"])
                if quest:
                    getattr(quest, target).append((kind, row["id"]))
        for row in _rows(dumps / "creature_template.sql",
                         ["entry", "name", "faction", "npcflag", "unit_flags", "minlevel", "maxlevel", "rank",
                          "KillCredit1", "KillCredit2", "VehicleId", "type_flags"]):
            credit = tuple(c for c in (row["KillCredit1"], row["KillCredit2"]) if c)
            self.creatures[row["entry"]] = Creature(row["entry"], row["name"], row["faction"], row["npcflag"],
                                                    row["unit_flags"], row["minlevel"], row["maxlevel"], row["rank"],
                                                    credit, row["VehicleId"], row["type_flags"])
            for c in credit:
                self.credit_sources[c].append(row["entry"])
        # Every phase: quest NPCs of phased areas (Icecrown, Ebon Hold) only spawn outside phase 1, and
        # the spawn nearest to the guide coordinates is the one the converter picks.
        for row in _rows(dumps / "creature.sql", ["id1", "map", "position_x", "position_y", "position_z"]):
            self.creature_spawns[row["id1"]].append((row["map"], row["position_x"], row["position_y"], row["position_z"]))
        for row in _rows(dumps / "gameobject_template.sql", ["entry", "type", "name", "Data0", "Data1"]):
            self.objects[row["entry"]] = GameObject(row["entry"], row["name"], row["type"], row["Data0"], row["Data1"])
        for row in _rows(dumps / "gameobject.sql", ["id", "map", "position_x", "position_y", "position_z"]):
            self.object_spawns[row["id"]].append((row["map"], row["position_x"], row["position_y"], row["position_z"]))
        for row in _rows(dumps / "item_template.sql",
                         ["entry", "name", "class", "subclass", "startquest", "spellid_1", "Flags"]):
            self.items[row["entry"]] = row
            if row["startquest"] and row["startquest"] in self.quests:
                quest = self.quests[row["startquest"]]
                quest.starters.append(("item", row["entry"]))
        # Loot: resolve one level of references (reference_loot_template) for quest items.
        references = defaultdict(list)
        for row in _rows(dumps / "reference_loot_template.sql", ["Entry", "Item", "Reference", "Chance", "QuestRequired"]):
            if not row["Reference"]:
                references[row["Entry"]].append((row["Item"], row["Chance"], row["QuestRequired"]))
        creature_loot = defaultdict(list)
        for row in _rows(dumps / "creature_loot_template.sql", ["Entry", "Item", "Reference", "Chance", "QuestRequired"]):
            if row["Reference"]:
                creature_loot[row["Entry"]].extend(references.get(row["Reference"], ()))
            else:
                creature_loot[row["Entry"]].append((row["Item"], row["Chance"], row["QuestRequired"]))
        loot_owners = defaultdict(list)
        for row in _rows(dumps / "creature_template.sql", ["entry", "lootid"]):
            if row["lootid"]:
                loot_owners[row["lootid"]].append(row["entry"])
        for loot_id, entries in creature_loot.items():
            for item, chance, questreq in entries:
                for creature in loot_owners.get(loot_id, ()):
                    self.item_from_creatures[item].append((creature, chance, questreq))
        object_loot = defaultdict(list)
        for row in _rows(dumps / "gameobject_loot_template.sql", ["Entry", "Item", "Reference", "Chance", "QuestRequired"]):
            if row["Reference"]:
                object_loot[row["Entry"]].extend(references.get(row["Reference"], ()))
            else:
                object_loot[row["Entry"]].append((row["Item"], row["Chance"], row["QuestRequired"]))
        chests = defaultdict(list)
        for entry in self.objects.values():
            # Chest (3): Data1 = loot id; goober (10) and others carry no loot table.
            if entry.type == 3 and entry.data1:
                chests[entry.data1].append(entry.id)
        for loot_id, entries in object_loot.items():
            for item, chance, _ in entries:
                for obj in chests.get(loot_id, ()):
                    self.item_from_objects[item].append((obj, chance))
        for row in _rows(dumps / "npc_vendor.sql", ["entry", "item"]):
            if row["item"] > 0:
                self.vendors[row["entry"]].add(row["item"])
        triggers = {row["entry"]: (row["map"], row["x"], row["y"], row["z"])
                    for row in _rows(dumps / "areatrigger.sql", ["entry", "map", "x", "y", "z"])}
        for row in _rows(dumps / "areatrigger_involvedrelation.sql", ["id", "quest"]):
            quest = self.quests.get(row["quest"])
            if quest and row["id"] in triggers:
                quest.areatriggers.append(triggers[row["id"]])
        # SmartAI: on spell hit (event 8) remove unit flag (action 19) IMMUNE_TO_PC (0x100), as Leviroth
        # does when the Trident of Naz'jan hits it: the quest item opens a fight, it is not the credit.
        for row in _rows(dumps / "smart_scripts.sql", ["entryorguid", "source_type", "event_type", "action_type",
                                                       "action_param1"]):
            if row["source_type"] == 0 and row["entryorguid"] > 0 and row["event_type"] == 8 \
                    and row["action_type"] == 19 and row["action_param1"] & 0x100:
                self.attackable_after_spell.add(row["entryorguid"])
