"""Loads Zygor guides with the addon's own Parser.lua (Lua 5.1 through `lupa`).

Every guide comes back as plain Python data: the header (type, faction, next,
startlevel, ...) and the parsed steps, each with its goals as Parser.lua builds
them (action, questid, objnum, count, target, npcid, itemid, map, x, y, ...).
Compiled conditions are functions and are dropped; their source stays in the
`*_raw` fields.
"""
from pathlib import Path
import re

import lupa.lua51 as lupa

# Stubs follow the addon's offline harness, tools/parser/test_only_if_race_class.lua.
PRELUDE = r"""
tinsert, tremove = table.insert, table.remove
function TableKeys(tbl) local keys = {} for key in pairs(tbl or {}) do keys[#keys + 1] = key end return keys end
function ZygorGuidesViewer_L() return setmetatable({ coords = "%s,%s" }, { __index = function(_, key) return key end }) end
-- In game Parser.lua reads bare coordinates in the player's zone; load_guides sets FALLBACK_ZONE.
GetRealZoneText = function() return FALLBACK_ZONE or "" end
UnitRace = function() return "Human", "Human" end
UnitClass = function() return "WARRIOR", "WARRIOR" end
UnitLevel = function() return 1 end
UnitFactionGroup = function() return EXPORT_FACTION end
GetLocale = function() return "enUS" end

ZygorGuidesViewer = {
    BZL = {}, BSZL = {}, BFL = {}, StandingNamesEngRev = {}, dailyQuests = {}, instantQuests = {}, completedQuests = {},
    registeredguides = {}, registered_includes = {}, StepProto_mt = {}, GoalProto_mt = {}, db = { profile = {}, char = {} },
}
ZGV = ZygorGuidesViewer
local ZGV = ZygorGuidesViewer
function ZGV:RaceClassMatch() return true end
function ZGV:GetQuestName() return nil end
function ZGV:GetGuideByTitle(title)
    for _, guide in ipairs(self.registeredguides) do
        if guide.title == title then return guide end
    end
end
-- leechsteps copies steps out of another registered guide.
function ZGV:EnsureGuideParsed(guide)
    if guide.steps then return guide, true end
    guide._parsing = true
    local ok, parsed = pcall(self.ParseEntry, self, guide.data)
    guide._parsing = nil
    if not ok or type(parsed) ~= "table" then return guide, false end
    guide.steps = parsed.steps
    return guide, true
end
function ZGV:RegisterGuide(title, data, extra)
    if type(data) == "table" then data, extra = extra, data end
    if type(data) ~= "string" then return end
    self.registeredguides[#self.registeredguides + 1] = { title = title, data = data, extra = extra }
end
function ZGV:RegisterInclude(name, text)
    self.registered_includes[name] = text
end
function ZGV.BETASTART() end
function ZGV.BETAEND() end

local skip = { parentStep = true, parentGuide = true, parent = true }

-- Plain-data copy: drops functions, parent links and metatables.
function ExportCopy(value, depth, seen)
    local kind = type(value)
    if kind == "string" or kind == "number" or kind == "boolean" then
        return value
    end
    if kind ~= "table" or seen[value] or depth > 8 then
        return nil
    end
    seen[value] = true
    local out = {}
    for key, item in pairs(value) do
        if (type(key) == "string" or type(key) == "number") and not skip[key] then
            out[key] = ExportCopy(item, depth + 1, seen)
        end
    end
    seen[value] = nil
    return out
end

function ExportGuide(entry)
    local header = ZGV:ParseHeader(entry.data) or {}
    local ok, parsed, err = pcall(ZGV.ParseEntry, ZGV, entry.data)
    if not ok or type(parsed) ~= "table" then
        return nil, tostring(parsed) .. " " .. tostring(err)
    end
    local steps = {}
    for index, step in ipairs(parsed.steps or {}) do
        local copy = ExportCopy(step, 0, {})
        copy.goals = {}
        for goalIndex, goal in ipairs(step.goals or {}) do
            copy.goals[goalIndex] = ExportCopy(goal, 0, {})
        end
        steps[index] = copy
    end
    return { title = entry.title, header = ExportCopy(header, 0, {}), steps = steps }
end
"""


def _to_python(value):
    if lupa.lua_type(value) == "table":
        keys = list(value.keys())
        if keys and all(isinstance(k, int) for k in keys) and sorted(keys) == list(range(1, len(keys) + 1)):
            return [_to_python(value[k]) for k in sorted(keys)]
        return {str(k): _to_python(value[k]) for k in keys}
    if isinstance(value, bytes):
        return value.decode("utf-8", "replace")
    return value


def _read(path: Path) -> str:
    return path.read_text(encoding="utf-8-sig", errors="replace")


_INCLUDE = re.compile(r'#include\s+"([^"]+)"')
_QUEST_REF = re.compile(r'\b(?:accept|turnin)\b[^\n#]*##(\d+)|\|q\s+(\d+)')


def load_guides(addon: Path, files, faction: str, includes=(), home_zone=None):
    """Registers and parses every guide of `files` (paths relative to Guides/).

    `faction` ("Alliance"/"Horde") is what UnitFactionGroup returns while the
    guides register: several files only register for the matching faction.
    `includes` are loaded first (Retail guides `#include` shared snippets).
    `home_zone(quest_ids)` names the zone of a guide's quests: a guide whose bare
    coordinates have no map before them (Parser.lua then uses the player's zone)
    is parsed again as if the player stood there.
    Returns (guides, problems); each guide is a dict with title, file, header, steps.
    """
    runtime = lupa.LuaRuntime(unpack_returned_tuples=True, encoding="utf-8")
    runtime.globals().EXPORT_FACTION = faction
    runtime.execute(PRELUDE)
    runtime.execute(_read(addon / "Parser.lua"))
    zgv = runtime.globals().ZygorGuidesViewer
    problems = []
    for relative in includes:
        try:
            runtime.execute(_read(addon / "Guides" / relative))
        except lupa.LuaError as error:
            problems.append(f"{relative}: {error}")
    first = len(zgv.registeredguides)  # guides registered by include files are not exported
    owners = []
    for relative in files:
        before = len(zgv.registeredguides)
        try:
            runtime.execute(_read(addon / "Guides" / relative))
        except lupa.LuaError as error:
            problems.append(f"{relative}: {error}")
        owners.extend([relative] * (len(zgv.registeredguides) - before))
    guides = []
    count = len(zgv.registeredguides)
    for index, entry in enumerate(zgv.registeredguides[i] for i in range(first + 1, count + 1)):
        result = runtime.globals().ExportGuide(entry)
        guide, problem = result if isinstance(result, tuple) else (result, None)
        title = str(entry.title).replace("\\\\", "\\")
        if guide is None and home_zone and "has no map parameter" in str(problem):
            text = str(entry.data)
            text += "".join(str(zgv.registered_includes[name] or "") for name in _INCLUDE.findall(text))
            zone = home_zone([int(a or b) for a, b in _QUEST_REF.findall(text)])
            if zone:
                runtime.globals().FALLBACK_ZONE = zone
                result = runtime.globals().ExportGuide(entry)
                runtime.globals().FALLBACK_ZONE = None
                guide, problem = result if isinstance(result, tuple) else (result, None)
                if guide is not None:
                    problems.append(f"{title}: bare coordinates read in {zone}, the zone of its quests")
        if guide is None:
            problems.append(f"{title}: parse failed: {problem}")
            continue
        data = _to_python(guide)
        data["title"] = title
        data["file"] = owners[index] if index < len(owners) else None
        header = data.get("header") or {}
        if isinstance(header.get("next"), str):
            # The header sits in a [[long string]], so its `\\` separator stays doubled.
            header["next"] = header["next"].replace("\\\\", "\\")
        data["header"] = header
        guides.append(data)
    return guides, problems


def guide_faction(guide) -> str | None:
    """"Alliance", "Horde" or None (both) from the header or the title."""
    value = str((guide.get("header") or {}).get("faction") or "").lower()
    if value.startswith("a"):
        return "Alliance"
    if value.startswith("h"):
        return "Horde"
    title = guide["title"].lower()
    if re.search(r"\balliance\b", title):
        return "Alliance"
    if re.search(r"\bhorde\b", title):
        return "Horde"
    return None
