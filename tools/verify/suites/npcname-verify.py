"""Every NPC on a map must be NAMED, and the naming rule is the reference's.

`MapleNecrocer/Extension.cs:47-50` --

    public static string IntID(this String Number)
    {
        return Number.ToInt().ToString();
    }

-- and `Npc.cs:67` builds its string path from that:

    npcStringPath = Wz.HasHardCodedStrings ? "Npc/" + ID + ".img/info"
                                          : "String/Npc.img/" + ID.IntID();

So the pool is keyed by the id with its leading zeros STRIPPED. That is not a width
sweep and the archive says so: on `Map.nx/Map/Map0/000000002.img`, `0002000` is
`String.nx/Npc.img/2000` and `9101002` is `9101002` -- the same number both times.

MEASURED before the fix: `CombatService.NpcText` built the pool path from the
verbatim map id, so four of that map's six NPCs had no name at all and the wire
read `id=0002000 name=''`.
"""
import json, os, urllib.parse, urllib.request
try:
    from archivelib import archive_id
except ImportError:            # a missing module must not read as a pass
    print(">>> RED: archivelib.py is missing next to this suite")
    raise SystemExit(1)
except SyntaxError as exc:    # a helper that does not PARSE is not missing
    print(f">>> RED: archivelib.py next to this suite does not parse: {exc}")
    raise SystemExit(1)

BASE = os.environ.get("GATE_BASE") or "http://127.0.0.1:5591"

_MAP_ID = archive_id("Map.nx", BASE)
_STRING_ID = archive_id("String.nx", BASE)
MAP = f"{_MAP_ID}/Map/Map0/000000002.img"

bad = 0


def post(route, body):
    req = urllib.request.Request(f"{BASE}{route}", data=json.dumps(body).encode("utf-8"),
                                 headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=600) as r:
        return json.loads(r.read().decode("utf-8"))


def get(route, **q):
    with urllib.request.urlopen(f"{BASE}{route}?" + urllib.parse.urlencode(q), timeout=300) as r:
        return json.loads(r.read().decode("utf-8"))


print("=== the rule, read straight off the archive ===")
# For each NPC the map places, ask the pool at BOTH spellings and report which
# answers. If neither answers, the NPC is nameless for a reason that is not this.
s = post("/api/simulate/session",
         {"path": MAP, "weapon": "1302000.img", "equippedWeapon": "1302000",
          "invulnerable": True})
sid = s["sessionId"]
fr = post(f"/api/simulate/session/{sid}/frames", {"count": 1, "first": {}, "then": {}})
npcs = [m for m in fr.get("mobs") or [] if m.get("kind") == "n"]
print(f"  {MAP} places {len(npcs)} NPCs")

expected = {}
for m in npcs:
    npc_id = m["mobId"]
    stripped = str(int(npc_id)) if npc_id.isdigit() else npc_id
    answers = {}
    for form in dict.fromkeys([npc_id, stripped]):
        try:
            node = get("/api/inspect", path=f"{_STRING_ID}/Npc.img/{form}/name")
            answers[form] = (node.get("node") or {}).get("value") or ""
        except Exception:
            answers[form] = None
    expected[npc_id] = next((v for v in answers.values() if v), "")
    shown = ", ".join(f"{k}->{v!r}" if v else f"{k}->MISSING" for k, v in answers.items())
    print(f"    {npc_id}: {shown}")

print()
print("=== what the simulation actually shows ===")
print(f"  {'id':<9} {'name':<12} {'func':<12} expected")
for m in npcs:
    print(f"  {m['mobId']:<9} {m['npcName']:<12} {m['npcFunc']:<12} {expected[m['mobId']]!r}")

print()
for m in npcs:
    want = expected[m["mobId"]]
    if want and m["npcName"] != want:
        print(f"  >>> {m['mobId']} shows {m['npcName']!r}, the archive says {want!r}")
        bad += 1
    if not want and m["npcName"]:
        print(f"  >>> {m['mobId']} shows {m['npcName']!r} but the archive has no name "
              f"for it at either spelling")
        bad += 1

named = sum(1 for m in npcs if m["npcName"])
print()
print(f"  {named}/{len(npcs)} NPCs carry a name")
if named == 0 and npcs:
    print(f"  >>> not one NPC on this map is named")
    bad += 1

# The stripped form has to be the one that works, or the suite proves nothing:
# if the verbatim id alone answered everywhere, both readings would pass.
stripped_only = [m["mobId"] for m in npcs
                 if expected[m["mobId"]]
                 and m["mobId"].isdigit()
                 and str(int(m["mobId"])) != m["mobId"]]
print(f"  {len(stripped_only)} of them are nameless unless the zeros are stripped: "
      f"{stripped_only}")
if not stripped_only:
    print("  >>> this map cannot distinguish the two spellings; the suite proves nothing")
    bad += 1

print(f"\nBAD={bad}")