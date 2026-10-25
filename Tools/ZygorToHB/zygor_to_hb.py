#!/usr/bin/env python3
"""Zygor Guides (3.3.5a remaster)  ->  CopilotBuddy / Honorbuddy questing profiles
==================================================================================

  python zygor_to_hb.py [--zygor DIR] [--azerothcore DIR] [--dbc DIR] [--out DIR]

Parses the Zygor guides with the addon's own Parser.lua (needs `pip install lupa`),
then rebuilds every guide as an <HBProfile> in the Profile v3 layout:

- quest givers, turn-in NPCs, objectives, drops and spawn points come from the
  AzerothCore world dumps (`data/sql/base/db_world`); the guide supplies the
  quest order and, through its map coordinates, which spawn group to use;
- a quest whose objectives all map onto <Objective> nodes (KillMob, CollectItem,
  UseObject) is turned in plainly; any quest that needs a custom behavior or
  could not be resolved gets the guarded turn-in plus AbandonQuest, so the
  profile always moves on instead of looping on the turn-in NPC;
- pickups carry the race, class, level, previous-quest, exclusive-group and
  reputation requirements of the quest, because a quest giver that does not
  offer the quest stops the bot after the pickup timeout;
- daily quests are declared with <Quest Id=".." Daily="True" />: the bot then
  treats them as completed only while they are in today's daily slots.

Output: <out>/<Faction>/<Category>_<guide>.xml, one profile per guide, chained
with LoadProfile along the guide's `next` header.
"""
import argparse
from collections import Counter, defaultdict
from dataclasses import dataclass, field
import math
from pathlib import Path
import re
import sys
from xml.sax.saxutils import escape, quoteattr

from dbc import FactionTemplates, Zones
from world_db import World
from zygor_guides import guide_faction, load_guides

REPO = Path(__file__).resolve().parents[2]
GAMES = REPO.parent.parent

# (category, guide files relative to Guides/) per faction. Dungeons, professions,
# gold, pets/mounts, titles and seasonal events are left out: they need a group,
# a profession or a running holiday, which a questing profile cannot check.
GUIDE_SETS = {
    "Alliance": [
        ("Dailies", ["Dailies/ZygorDailiesAlliance.lua", "Dailies/AresDailiesAlliance.lua",
                     "Retail/Dailies/ZygorDailiesAllianceCLASSIC.lua", "Retail/Dailies/ZygorDailiesCommonCLASSIC.lua"]),
        ("Reputations", ["Retail/Reputations/ZygorReputationsAllianceCLASSIC.lua",
                         "Reputations/AresReputationsAlliance.lua", "Reputations/AresReputationsNorthrend.lua"]),
        ("Loremaster", ["Achievements/A-Loremaster-EasternKingdoms.lua", "Achievements/A-Loremaster-Kalimdor.lua",
                        "Achievements/A-Loremaster-BurningCrusade.lua", "Achievements/A-Loremaster-Northrend.lua"]),
        ("Leveling", ["Leveling/ZygorGuidesAlliance.lua"]),
        ("Ding80", ["Leveling/Ding80GuidesAlliance.lua", "Leveling/Ding80GuidesAllianceTBCsolo.lua",
                    "Leveling/Ding80GuidesAllianceTBCduo.lua", "Leveling/BloodmystIsleTBC.lua"]),
        ("Legendary", ["Legendary/AresLegendariesWotLK.lua"]),
    ],
    "Horde": [
        ("Dailies", ["Dailies/ZygorDailiesHorde.lua", "Retail/Dailies/ZygorDailiesHordeCLASSIC.lua",
                     "Retail/Dailies/ZygorDailiesCommonCLASSIC.lua"]),
        ("Reputations", ["Retail/Reputations/ZygorReputationsHordeCLASSIC.lua", "Reputations/AresReputationsNorthrend.lua"]),
        ("Loremaster", ["Achievements/H-Loremaster-Northrend.lua"]),
        ("Leveling", ["Leveling/ZygorGuidesHorde.lua"]),
        ("Legendary", ["Legendary/AresLegendariesWotLK.lua"]),
    ],
}
# Retail guides #include shared snippets (compat stubs first); the Ares guides include from the root files.
INCLUDES = (["Retail/RetailCompat.lua"]
            + [f"Retail/Includes/General/{p}_{k}_Includes.lua" for k in ("General", "Quest") for p in "NAH"]
            + ["ZygorIncludesAlliance.lua", "ZygorIncludesHorde.lua"])
# Guides other guides copy steps from (`leechsteps`); loaded, not exported.
LEECH_SOURCES = {side: [f"Retail/Leveling/ZygorLeveling{side}CLASSIC.lua", f"Retail/Leveling/ZygorLeveling{side}WOTLK.lua",
                        f"Dailies/ZygorDailies{side}.lua"] for side in ("Alliance", "Horde")}

FACTION_RACES = {"Alliance": (1, 3, 4, 7, 11), "Horde": (2, 5, 6, 8, 10)}
RACE_NAMES = {1: "Human", 2: "Orc", 3: "Dwarf", 4: "NightElf", 5: "Undead", 6: "Tauren", 7: "Gnome", 8: "Troll",
              10: "BloodElf", 11: "Draenei"}
CLASS_NAMES = {1: "Warrior", 2: "Paladin", 3: "Hunter", 4: "Rogue", 5: "Priest", 6: "DeathKnight", 7: "Shaman",
               8: "Mage", 9: "Warlock", 11: "Druid"}
ZYGOR_RACES = {"HUMAN": 1, "ORC": 2, "DWARF": 3, "NIGHTELF": 4, "SCOURGE": 5, "UNDEAD": 5, "TAUREN": 6, "GNOME": 7,
               "TROLL": 8, "BLOODELF": 10, "DRAENEI": 11}
ZYGOR_CLASSES = {name.upper(): cid for cid, name in CLASS_NAMES.items()}

# Map 530 also holds the Blood Elf and Draenei starting isles, which are not Outland.
NOT_OUTLAND = {"Eversong Woods", "Ghostlands", "Silvermoon City", "Isle of Quel'Danas", "Azuremyst Isle",
               "Bloodmyst Isle", "The Exodar"}
TRAVEL = {0: "To Eastern Kingdoms", 1: "To Kalimdor", 530: "To Outland", 571: "To Northrend"}

SPAWN_RADIUS = 300.0      # spawns this close to the guide's coordinates belong to the step
SPAWN_FALLBACK = 1500.0   # nearest spawns still accepted when none is in the radius
HOTSPOT_SPACING = 40.0
MAX_HOTSPOTS = 8

UNIT_FLAG_NOT_ATTACKABLE = 0x2 | 0x100 | 0x2000000   # NON_ATTACKABLE, IMMUNE_TO_PC, NOT_SELECTABLE


def fmt(value):
    text = f"{value:.4f}".rstrip("0").rstrip(".")
    return "0" if text in ("-0", "") else text


def xyz(point):
    return f'X="{fmt(point[1])}" Y="{fmt(point[2])}" Z="{fmt(point[3])}"'


def attr(value):
    return quoteattr(str(value))


def dist(a, b):
    return math.hypot(a[1] - b[1], a[2] - b[2])


def file_name(text):
    return re.sub(r'[\\/:*?"<>|]+', "", text).strip()


@dataclass
class Plan:
    """What the profile does for one quest."""
    quest: object
    review: list = field(default_factory=list)   # reasons the quest is not fully automated
    behaviors: bool = False                      # a custom behavior (not an Objective node) does part of it
    done: set = field(default_factory=set)       # objective keys already emitted
    blocks: list = field(default_factory=list)   # <Quest> block objective elements

    @property
    def automated(self):
        return not self.review and not self.behaviors


class Converter:
    def __init__(self, world: World, zones: Zones, factions: FactionTemplates, side: str):
        self.world, self.zones, self.factions, self.side = world, zones, factions, side
        self.race_mask = sum(1 << (r - 1) for r in FACTION_RACES[side])
        self.stats = Counter()

    # ---------------------------------------------------------------- spawns

    def spawns_near(self, spawns, anchor):
        """Spawns of one entry around the guide coordinates, spread into hotspots."""
        if not spawns:
            return []
        if anchor is None:
            return spread(spawns, MAX_HOTSPOTS)
        same = sorted((s for s in spawns if s[0] == anchor[0]), key=lambda s: dist(s, anchor))
        if not same or dist(same[0], anchor) > SPAWN_FALLBACK:
            return []
        close = [s for s in same if dist(s, anchor) <= SPAWN_RADIUS] or same[:MAX_HOTSPOTS]
        return spread(close, MAX_HOTSPOTS)

    def creature_spawns(self, entries, anchor):
        points = []
        for entry in entries:
            points.extend(self.world.creature_spawns.get(entry, ()))
        return self.spawns_near(points, anchor)

    def object_spawns(self, entries, anchor):
        points = []
        for entry in entries:
            points.extend(self.world.object_spawns.get(entry, ()))
        return self.spawns_near(points, anchor)

    def nearest(self, spawns, anchor):
        if not spawns:
            return None
        if anchor is None:
            return spawns[0]
        same = [s for s in spawns if s[0] == anchor[0]] or spawns
        return min(same, key=lambda s: dist(s, anchor) if s[0] == anchor[0] else 1e9)

    def creature_name(self, entry):
        creature = self.world.creatures.get(entry)
        return creature.name if creature else str(entry)

    def object_name(self, entry):
        obj = self.world.objects.get(entry)
        return obj.name if obj else str(entry)

    def item_name(self, entry):
        item = self.world.items.get(entry)
        return item["name"] if item else str(entry)

    # ---------------------------------------------------------- requirements

    def available(self, quest):
        """False when the quest can never be offered to this faction."""
        return not quest.races or bool(quest.races & self.race_mask)

    def requirements(self, quest):
        """Condition terms that must hold for the giver to offer the quest."""
        terms = []
        if quest.races and (quest.races & self.race_mask) != self.race_mask:
            races = [r for r in FACTION_RACES[self.side] if quest.races & (1 << (r - 1))]
            terms.append(" || ".join(f"(Me.Race == WoWRace.{RACE_NAMES[r]})" for r in races))
        if quest.classes and (quest.classes & 1535) != 1535:
            classes = [c for c in CLASS_NAMES if quest.classes & (1 << (c - 1))]
            terms.append(" || ".join(f"(Me.Class == WoWClass.{CLASS_NAMES[c]})" for c in classes))
        if quest.min_level > 1:
            terms.append(f"Me.Level &gt;= {quest.min_level}")
        if quest.prev > 0:
            terms.append(f"IsQuestCompleted({quest.prev})")
        elif quest.prev < 0:
            terms.append(f"HasQuest({-quest.prev})")
        if quest.exclusive > 0:
            for other in self.world.quests.values():
                if other.exclusive == quest.exclusive and other.id != quest.id and self.available(other):
                    terms.append(f"!IsQuestCompleted({other.id})")
                    terms.append(f"!HasQuest({other.id})")
        if quest.rep_faction and quest.rep_value:
            terms.append(f"GetFactionReputation({quest.rep_faction}) &gt;= {quest.rep_value}")
        return [f"({t})" if " " in t else t for t in terms]

    # ------------------------------------------------------------- objectives

    @staticmethod
    def objective_keys(quest):
        """Leaderboard order: creature/gameobject objectives, then items."""
        keys = [("npc", i, entry, count) for i, (entry, count) in enumerate(quest.npc_or_go) if entry and count]
        keys += [("item", i, entry, count) for i, (entry, count) in enumerate(quest.items) if entry and count]
        return keys

    def match_objectives(self, goal, quest):
        keys = self.objective_keys(quest)
        target = goal.get("targetid") or goal.get("npcid")
        if target:
            hits = [k for k in keys if abs(k[2]) == target]
            if not hits:
                credit = self.world.creatures.get(target)
                hits = [k for k in keys if credit and k[0] == "npc" and k[2] in credit.kill_credit]
            if hits:
                return hits
        objnum = goal.get("objnum")
        if objnum and 1 <= objnum <= len(keys):
            return [keys[objnum - 1]]
        name = str(goal.get("target") or "").lower().rstrip("s")
        if name:
            for k in keys:
                if k[0] == "npc":
                    label = self.creature_name(k[2]) if k[2] > 0 else self.object_name(-k[2])
                else:
                    label = self.item_name(k[2])
                if label.lower().rstrip("s") == name:
                    return [k]
        return keys

    def objective_nodes(self, plan, key, goal, step_goals, anchor, out, indent):
        """Order nodes for one quest objective; records what could not be automated."""
        quest = plan.quest
        kind, _, entry, count = key
        plan.done.add(key[:2])
        qname = attr(quest.title)
        use_items = [g["useitemid"] for g in step_goals if g.get("action") == "use" and g.get("useitemid")]
        action = goal.get("action")
        pad = "\t" * indent
        if kind == "item":
            if entry == quest.start_item or any(entry == i for i, _ in quest.source_items):
                return  # handed out with the quest
            mobs = [(c, chance, q) for c, chance, q in self.world.item_from_creatures.get(entry, ())
                    if q or chance >= 5 or chance == 0]
            objs = [(o, chance) for o, chance in self.world.item_from_objects.get(entry, ())]
            mob_ids = [c for c, *_ in mobs if self.creature_spawns([c], anchor)]
            obj_ids = [o for o, _ in objs if self.object_spawns([o], anchor)]
            hotspots = self.creature_spawns(mob_ids, anchor) + self.object_spawns(obj_ids, anchor)
            if not hotspots:
                plan.review.append(f"item {entry} {self.item_name(entry)} has no drop or chest near the guide coordinates")
                return
            mob_ids, obj_ids = mob_ids[:6], obj_ids[:6]
            out.append(f'{pad}<Objective QuestName={qname} QuestId="{quest.id}" Type="CollectItem" ItemId="{entry}" '
                       f'CollectCount="{count}" />')
            block = [f'\t\t<Objective Type="CollectItem" ItemId="{entry}" CollectCount="{count}">', "\t\t\t<CollectFrom>"]
            block += [f'\t\t\t\t<Mob Name={attr(self.creature_name(m))} Id="{m}" />' for m in mob_ids]
            block += [f'\t\t\t\t<GameObject Name={attr(self.object_name(o))} Id="{o}" />' for o in obj_ids]
            block += ["\t\t\t</CollectFrom>"] + hotspot_lines(spread(hotspots, MAX_HOTSPOTS), 3) + ["\t\t</Objective>"]
            plan.blocks.append(block)
            return
        if entry < 0:
            obj = -entry
            spots = self.object_spawns([obj], anchor)
            if use_items and spots:
                plan.behaviors = True
                out.append(f'{pad}<CustomBehavior File="UseItemOn" QuestId="{quest.id}" ItemId="{use_items[0]}" '
                           f'MobId="{obj}" MobType="GameObject" NumOfTimes="{count}" Range="5" CollectionDistance="200" '
                           f'{xyz(spots[0])} />')
                return
            if not spots:
                plan.review.append(f"object {obj} {self.object_name(obj)} has no spawn near the guide coordinates")
                return
            out.append(f'{pad}<Objective QuestName={qname} QuestId="{quest.id}" Type="UseObject" ObjectId="{obj}" '
                       f'UseCount="{count}" />')
            plan.blocks.append([f'\t\t<Objective Type="UseObject" ObjectId="{obj}" UseCount="{count}">']
                               + hotspot_lines(spots, 3) + ["\t\t</Objective>"])
            return
        creature = self.world.creatures.get(entry)
        direct = self.creature_spawns([entry], anchor)
        sources = [c for c in self.world.credit_sources.get(entry, ()) if self.creature_spawns([c], anchor)]
        if use_items:
            targets = [entry] if direct else sources[:6]
            spots = direct or self.creature_spawns(targets, anchor)
            if not targets:
                plan.review.append(f"use item {use_items[0]}: target {entry} {self.creature_name(entry)} has no spawn "
                                   "(kill credit or scripted event)")
                return
            plan.behaviors = True
            mobs = " ".join(f'MobId{i + 1 if i else ""}="{m}"' for i, m in enumerate(targets))
            out.append(f'{pad}<CustomBehavior File="UseItemOn" QuestId="{quest.id}" ItemId="{use_items[0]}" {mobs} '
                       f'NumOfTimes="{count}" Range="5" CollectionDistance="200" {xyz(spots[0])} />')
            return
        if action in ("talk", "clicknpc", "click"):
            if not direct:
                plan.review.append(f"interact with {entry} {self.creature_name(entry)}: no spawn near the guide coordinates")
                return
            plan.behaviors = True
            gossip = ' GossipOptions="1"' if action == "talk" else ""
            out.append(f'{pad}<CustomBehavior File="InteractWith" QuestId="{quest.id}" MobId="{entry}"{gossip} '
                       f'NumOfTimes="{count}" CollectionDistance="200" {xyz(direct[0])} />')
            return
        attackable = creature is not None and not creature.unit_flags & UNIT_FLAG_NOT_ATTACKABLE
        if direct and attackable and (action == "kill" or not creature.npcflag & 3):
            out.append(f'{pad}<Objective QuestName={qname} QuestId="{quest.id}" Type="KillMob" MobId="{entry}" '
                       f'KillCount="{count}" />')
            plan.blocks.append([f'\t\t<Objective Type="KillMob" MobId="{entry}" KillCount="{count}">']
                               + hotspot_lines(direct, 3) + ["\t\t</Objective>"])
            return
        if action == "kill" and sources and len(self.objective_keys(quest)) == 1:
            # The credit is given by other creatures; kill them until the (single-objective) quest completes.
            plan.behaviors = True
            spots = self.creature_spawns(sources, anchor)
            mobs = " ".join(f'MobId{i + 1}="{m}"' for i, m in enumerate(sources[:6]))
            out.append(f'{pad}<CustomBehavior File="KillUntilComplete" QuestId="{quest.id}" {mobs} {xyz(spots[0])} />')
            return
        plan.review.append(f"{action or 'objective'} {entry} {self.creature_name(entry)} x{count}: "
                           + ("kill credit or scripted event" if not direct else "not attackable"))

    # --------------------------------------------------------------- givers

    def pick_npc(self, candidates, preferred, anchor):
        """(kind, id, location or None) of the giver/ender, preferring the NPC the guide talks to."""
        ordered = sorted(candidates, key=lambda c: (c[1] != preferred, c[0] != "npc"))
        for kind, entry in ordered:
            if kind == "item":
                return kind, entry, None
            spawns = (self.world.creature_spawns if kind == "npc" else self.world.object_spawns).get(entry, ())
            spot = self.nearest(spawns, anchor)
            if spot is not None:
                return kind, entry, spot
        return None

    # -------------------------------------------------------------- profile

    def convert(self, guide, category, file_of_title):
        header = guide["header"]
        plans = {}
        order = []
        anchor = None
        first_map = None
        hub_points = []

        def plan_for(quest):
            if quest.id not in plans:
                plans[quest.id] = Plan(quest)
            return plans[quest.id]

        for step in guide["steps"]:
            goals = step.get("goals") or []
            body = []
            talk_npc = None
            for goal in goals:
                if goal.get("x") is not None:
                    point = self.zones.to_world(goal.get("map") or step.get("map"), goal["x"], goal["y"])
                    if point is not None:
                        anchor = point
                        if first_map is None:
                            first_map = (point[0], goal.get("map") or step.get("map"))
                if goal.get("action") == "talk" and goal.get("npcid"):
                    talk_npc = goal["npcid"]
            for goal in goals:
                action = goal.get("action")
                quest = self.world.quests.get(goal.get("questid") or 0)
                if quest is None:
                    if goal.get("questid"):
                        body.append(f"<!-- quest {goal['questid']} is not in the 3.3.5a database, step skipped -->")
                    continue
                if not self.available(quest):
                    continue
                plan = plan_for(quest)
                if action == "accept":
                    if quest.group >= 3:
                        plan.review.append(f"group quest ({quest.group} players)")
                        body.append(f"<!-- SKIP Q{quest.id} {escape(quest.title)}: group quest "
                                    f"({quest.group} players) -->")
                        self.stats["group"] += 1
                        continue
                    nodes = self.pickup(plan, talk_npc, anchor)
                    if nodes and first_map is None and anchor is not None:
                        first_map = (anchor[0], step.get("map"))
                    body.extend(nodes)
                    if anchor is not None:
                        hub_points.append(anchor)
                elif action == "turnin":
                    body.extend(self.remaining(plan, goals, anchor))
                    body.extend(self.turnin(plan, talk_npc, anchor))
                    if anchor is not None:
                        hub_points.append(anchor)
                elif action in ("kill", "collect", "get", "goal", "click", "clicknpc", "talk", "use", "buy"):
                    for key in self.match_objectives(goal, quest):
                        if key[:2] not in plan.done:
                            self.objective_nodes(plan, key, goal, goals, anchor, body, 0)
                    if quest.areatriggers and ("explore", 0) not in plan.done:
                        body.extend(self.explore(plan, anchor))
            for goal in goals:
                # Items that start a quest, looted along the way (Zygor: collect/get the item, then accept).
                if goal.get("action") in ("collect", "get") and goal.get("targetid") and not goal.get("questid"):
                    body.extend(self.starter_item(goal["targetid"], anchor))
            if body:
                condition = step_condition(step)
                if condition:
                    order.append(f'<If Condition="{condition}">')
                    order.extend("\t" + line for line in body)
                    order.append("</If>")
                else:
                    order.extend(body)

        if not plans:
            return None
        daily = header.get("type") == "daily" or "daily" in header or any(p.quest.daily for p in plans.values())
        name = profile_title(guide["title"], category)
        lines = ["<HBProfile>", f"\t<Name>{escape(name)}</Name>", "\t<MinLevel>1</MinLevel>", "\t<MaxLevel>81</MaxLevel>",
                 "\t<MinDurability>0.2</MinDurability>", "\t<MinFreeBagSlots>2</MinFreeBagSlots>",
                 "\t<SellGrey>True</SellGrey>", "\t<SellWhite>True</SellWhite>", "\t<SellGreen>True</SellGreen>",
                 "\t<SellBlue>False</SellBlue>", "\t<SellPurple>False</SellPurple>", "\t<MailGrey>False</MailGrey>",
                 "\t<MailWhite>False</MailWhite>", "\t<MailGreen>True</MailGreen>", "\t<MailBlue>True</MailBlue>",
                 "\t<MailPurple>True</MailPurple>", ""]
        vendors = self.vendors(hub_points)
        if vendors:
            lines.append("\t<Vendors>")
            lines.extend(vendors)
            lines.append("\t</Vendors>")
        else:
            lines.append("\t<Vendors />")
        lines += ["", "\t<AvoidMobs />", "", "\t<Blackspots />", "", "\t<Mailboxes />", ""]
        for plan in plans.values():
            quest = plan.quest
            flag = ' Daily="True"' if quest.daily else ""
            if plan.blocks:
                lines.append(f'\t<Quest Id="{quest.id}" Name={attr(quest.title)}{flag}>')
                for block in plan.blocks:
                    lines.extend(block)
                lines.append("\t</Quest>")
            elif quest.daily:
                lines.append(f'\t<Quest Id="{quest.id}" Name={attr(quest.title)}{flag} />')
        lines.append("\t<QuestOrder>")
        lines.append('\t\t<CustomBehavior File="UserSettings" LootMobs="True" TrainNewSkills="True" '
                     'LearnFlightPaths="True" UseFlightPaths="True" PullDistance="25" />')
        lines.append(f"\t\t<!-- Generated by CopilotBuddy Tools/ZygorToHB from {escape(guide['file'] or '')}: "
                     f"{escape(guide['title'])} -->")
        if daily:
            lines.append("\t\t<!-- Daily quests count as done only while they are in today's daily slots; "
                         "load the profile again after the daily reset. -->")
        travel = self.travel(first_map, file_of_title[guide["title"]])
        lines.extend("\t\t" + line for line in travel)
        lines.extend("\t\t" + line for line in order)
        nxt = header.get("next")
        if isinstance(nxt, str) and nxt in file_of_title:
            lines.append(f'\t\t<CustomBehavior File="LoadProfile" ProfileName={attr(file_of_title[nxt])} />')
        elif daily:
            lines.append(f'\t\t<CustomBehavior File="Message" Text={attr(name + ": done for today")} LogColor="Green" />')
        lines += ["\t</QuestOrder>", "</HBProfile>", ""]
        for plan in plans.values():
            self.stats["quests"] += 1
            self.stats["automated" if plan.automated else "guarded"] += 1
        return "\n".join(lines)

    def pickup(self, plan, talk_npc, anchor):
        quest = plan.quest
        giver = self.pick_npc(quest.starters, talk_npc, anchor)
        if giver is None:
            plan.review.append("no quest giver in the database")
            return [f"<!-- SKIP pickup Q{quest.id} {escape(quest.title)}: no quest giver with a spawn in the database -->"]
        kind, entry, spot = giver
        terms = self.requirements(quest)
        qname = attr(quest.title)
        if kind == "item":
            node = (f'<PickUp QuestName={qname} QuestId="{quest.id}" GiverName={attr(self.item_name(entry))} '
                    f'GiverId="{entry}" GiverType="Item" />')
            terms = [f"HasItem({entry})", f"!HasQuest({quest.id})", f"!IsQuestCompleted({quest.id})"] + terms
        else:
            giver_name = self.creature_name(entry) if kind == "npc" else self.object_name(entry)
            typ = ' GiverType="Object"' if kind == "object" else ""
            node = (f'<PickUp QuestName={qname} QuestId="{quest.id}" GiverName={attr(giver_name)} GiverId="{entry}"'
                    f'{typ} {xyz(spot)} />')
        if not terms:
            return [node]
        return [f'<If Condition="{" &amp;&amp; ".join(terms)}">', "\t" + node, "</If>"]

    def remaining(self, plan, goals, anchor):
        """Objectives the guide never pointed at before the turn-in."""
        out = []
        for key in self.objective_keys(plan.quest):
            if key[:2] not in plan.done:
                self.objective_nodes(plan, key, {}, goals, anchor, out, 0)
        if plan.quest.areatriggers and ("explore", 0) not in plan.done:
            out.extend(self.explore(plan, anchor))
        return out

    def explore(self, plan, anchor):
        plan.done.add(("explore", 0))
        trigger = self.nearest(plan.quest.areatriggers, anchor)
        plan.behaviors = True
        return [f'<If Condition="((HasQuest({plan.quest.id})) &amp;&amp; (!IsQuestCompleted({plan.quest.id})))">',
                f'\t<RunTo {xyz(trigger)} />', "</If>"]

    def turnin(self, plan, talk_npc, anchor):
        quest = plan.quest
        ender = self.pick_npc(quest.enders, talk_npc, anchor)
        if ender is None or ender[0] == "item":
            return [f"<!-- SKIP turn-in Q{quest.id} {escape(quest.title)}: no turn-in NPC with a spawn in the database -->",
                    f'<If Condition="((HasQuest({quest.id})) &amp;&amp; (!IsQuestCompleted({quest.id})))">',
                    f'\t<CustomBehavior File="AbandonQuest" QuestId="{quest.id}" Type="Incomplete" />', "</If>"]
        kind, entry, spot = ender
        name = self.creature_name(entry) if kind == "npc" else self.object_name(entry)
        typ = ' TurnInType="Object"' if kind == "object" else ""
        node = (f'<TurnIn QuestName={attr(quest.title)} QuestId="{quest.id}" TurnInName={attr(name)} TurnInId="{entry}"'
                f'{typ} {xyz(spot)} />')
        event = bool(quest.special_flags & 2) and not quest.areatriggers
        if event:
            plan.review.append("completed by a scripted event")
        if plan.automated and not event:
            return [node]
        reasons = "; ".join(dict.fromkeys(plan.review)) or "objectives done by custom behaviors"
        return [f"<!-- REVIEW Q{quest.id} {escape(quest.title)}: {escape(reasons)}. Turn-in guarded, quest abandoned "
                "if still incomplete so the profile can continue. -->",
                f'<If Condition="((HasQuest({quest.id})) &amp;&amp; (IsQuestCompleted({quest.id})))">', "\t" + node, "</If>",
                f'<If Condition="((HasQuest({quest.id})) &amp;&amp; (!IsQuestCompleted({quest.id})))">',
                f'\t<CustomBehavior File="AbandonQuest" QuestId="{quest.id}" Type="Incomplete" />', "</If>"]

    def starter_item(self, item, anchor):
        quests = [q for q in self.world.quests.values() if ("item", item) in q.starters and self.available(q)]
        if not quests:
            return []
        quest = quests[0]
        mobs = [c for c, chance, _ in self.world.item_from_creatures.get(item, ()) if self.creature_spawns([c], anchor)]
        spots = self.creature_spawns(mobs, anchor)
        if not spots:
            return []
        ids = " ".join(f'MobId{i + 1 if i else ""}="{m}"' for i, m in enumerate(mobs[:6]))
        return ([f'<If Condition="!HasItem({item}) &amp;&amp; !HasQuest({quest.id}) &amp;&amp; !IsQuestCompleted({quest.id})">',
                 f'\t<CustomBehavior File="CollectThings" CollectItemId="{item}" CollectItemCount="1" '
                 f'HuntingGroundRadius="120" {ids} {xyz(spots[0])}>']
                + [f"\t\t<Hotspot {xyz(s)} />" for s in spots] + ["\t</CustomBehavior>", "</If>"])

    def travel(self, first_map, own_file):
        """Universal navigation snip (Questing-profiles/GoTo) when the guide starts on another continent."""
        if first_map is None:
            return []
        map_id, zone = first_map
        if map_id not in TRAVEL or (map_id == 530 and zone in NOT_OUTLAND):
            return []
        back = f"..\\Zygor\\{self.side}\\{own_file}"
        code = f'AppDomain.CurrentDomain.SetData("BackToProfile", @"{back}");'
        goto = "..\\..\\GoTo\\" + TRAVEL[map_id]
        return [f'<If Condition="Me.MapId != {map_id}">',
                f'\t<CustomBehavior File="RunCode" Code={attr(code)} />',
                f'\t<CustomBehavior File="LoadProfile" ProfileName={attr(goto)} />',
                "</If>"]

    def vendors(self, hubs):
        if not hubs:
            return []
        picked = {"Repair": [], "Food": []}
        for entry, creature in self.world.creatures.items():
            kind = None
            if creature.npcflag & 4096:
                kind = "Repair"
            elif creature.npcflag & 128 and any(
                    (it := self.world.items.get(i)) and it["class"] == 0 and it["subclass"] == 5
                    for i in self.world.vendors.get(entry, ())):
                kind = "Food"
            if kind is None or self.factions.hostile_to(creature.faction, self.side):
                continue
            for spawn in self.world.creature_spawns.get(entry, ()):
                d = min((dist(spawn, h) for h in hubs if h[0] == spawn[0]), default=None)
                if d is not None and d <= 150:
                    picked[kind].append((d, entry, creature.name, spawn))
                    break
        lines = []
        for kind in ("Food", "Repair"):
            for d, entry, name, spawn in sorted(picked[kind])[:5]:
                lines.append(f'\t\t<Vendor Name={attr(name)} Entry="{entry}" Type="{kind}" {xyz(spawn)} />')
        return lines


def spread(points, limit):
    """Up to `limit` points at least HOTSPOT_SPACING apart, in walking order."""
    chosen = []
    for p in points:
        if all(dist(p, c) >= HOTSPOT_SPACING for c in chosen):
            chosen.append(p)
        if len(chosen) >= limit:
            break
    if len(chosen) <= 2:
        return chosen
    route = [chosen.pop(0)]
    while chosen:
        nxt = min(chosen, key=lambda c: dist(c, route[-1]))
        chosen.remove(nxt)
        route.append(nxt)
    return route


def hotspot_lines(points, indent):
    pad = "\t" * indent
    return [f"{pad}<Hotspots>"] + [f"{pad}\t<Hotspot {xyz(p)} />" for p in points] + [f"{pad}</Hotspots>"]


def step_condition(step):
    """Race/class requirement of a Zygor step (`only Human`, `only !Draenei`, `only NightElf Druid`)."""
    requirement = step.get("requirement")
    if not requirement:
        return None
    if isinstance(requirement, str):
        requirement = [requirement]
    terms = []
    for item in requirement:
        if not isinstance(item, str):
            return None
        negate = item.startswith("!")
        parts = []
        for word in item.lstrip("!").upper().split():
            if word in ZYGOR_RACES:
                parts.append(f"Me.Race == WoWRace.{RACE_NAMES[ZYGOR_RACES[word]]}")
            elif word in ZYGOR_CLASSES:
                parts.append(f"Me.Class == WoWClass.{CLASS_NAMES[ZYGOR_CLASSES[word]]}")
            else:
                return None
        term = " &amp;&amp; ".join(f"({p})" for p in parts)
        terms.append((negate, f"({term})" if len(parts) > 1 else term))
    positive = [t for neg, t in terms if not neg]
    negative = [f"!({t})" for neg, t in terms if neg]
    joined = []
    if positive:
        joined.append(" || ".join(f"({t})" for t in positive))
    joined.extend(negative)
    return " &amp;&amp; ".join(f"({t})" for t in joined) if len(joined) > 1 else joined[0]


def profile_title(title, category):
    parts = title.split("\\")
    return "\\".join([f"Zygor {category}"] + parts[1:]) if len(parts) > 1 else f"Zygor {category}\\{title}"


def output_name(title, category):
    parts = [file_name(p) for p in title.split("\\")[1:]] or [file_name(title)]
    return "_".join([category] + parts)


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--zygor", type=Path, default=GAMES / "ZygorGuidesRemaster-3.3.5a_WOTLK")
    parser.add_argument("--server", type=Path, default=GAMES / "Private" / "wow_legends_repack",
                        help="server folder: world data from its live database (mysql/bin/mysqldump.exe), "
                             "dbc from data/dbc")
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=3306)
    parser.add_argument("--user", default="wowlegends")
    parser.add_argument("--password", default="wowlegends")
    parser.add_argument("--database", default="wl_world")
    parser.add_argument("--azerothcore", type=Path,
                        help="read the world data from an azerothcore-wotlk checkout's SQL dumps instead of the server")
    parser.add_argument("--dbc", type=Path, help="3.3.5a dbc folder (default: <server>/data/dbc)")
    parser.add_argument("--out", type=Path, default=REPO.parent / "Questing-profiles" / "Zygor")
    parser.add_argument("--faction", choices=sorted(GUIDE_SETS), help="only this faction")
    args = parser.parse_args()

    addon = args.zygor / "ZygorGuidesViewerRM"
    if args.azerothcore:
        world = World.from_azerothcore(args.azerothcore)
    else:
        world = World.from_server(args.server / "mysql" / "bin" / "mysqldump.exe", args.host, args.port,
                                  args.user, args.password, args.database)
    dbc = args.dbc or args.server / "data" / "dbc"
    zones = Zones(dbc)
    factions = FactionTemplates(dbc)
    for side, sets in GUIDE_SETS.items():
        if args.faction and side != args.faction:
            continue
        converter = Converter(world, zones, factions, side)
        guides = []
        seen = set()
        for category, files in sets:
            loaded, problems = load_guides(addon, files, side, INCLUDES + LEECH_SOURCES[side])
            for problem in problems:
                print(f"{side} {category}: {problem}", file=sys.stderr)
            for guide in loaded:
                faction = guide_faction(guide)
                if (faction and faction != side) or guide["title"] in seen:
                    continue
                seen.add(guide["title"])
                guides.append((category, guide))
        file_of_title = {g["title"]: output_name(g["title"], c) for c, g in guides}
        out_dir = args.out / side
        out_dir.mkdir(parents=True, exist_ok=True)
        written = 0
        for category, guide in guides:
            xml = converter.convert(guide, category, file_of_title)
            if xml is None:
                continue
            (out_dir / f"{file_of_title[guide['title']]}.xml").write_text(xml, encoding="utf-8", newline="\n")
            written += 1
        stats = converter.stats
        print(f"{side}: {written} profiles, {stats['quests']} quest entries, {stats['automated']} fully automated, "
              f"{stats['guarded']} guarded turn-ins, {stats['group']} group quests skipped", file=sys.stderr)
    if zones.missing:
        print(f"zones without WorldMapArea bounds: {', '.join(sorted(zones.missing))}", file=sys.stderr)
    return 0


if __name__ == "__main__":
    sys.exit(main())
