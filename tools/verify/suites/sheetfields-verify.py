"""Verify the fields renderSheet() reads are actually on the frame.

This is NOT proof the row looks right — that is the user's call (studio rule
2.1). It proves the client will not be drawing blanks for a field the server
never sends, which is checkable from here.
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
MAP = f"{_MAP_ID}/Map/Map1/100000004.img"

# Exactly what walkview.js renderSheet() reads. A field added to the renderer
# without adding it here is a field this check cannot see.
FIELDS = ["hp", "maxHp", "mp", "maxMp", "strength", "dexterity", "intelligence",
          "luck", "watk", "accuracy", "minDamage", "maxDamage",
          "physicalDefence", "magicalDefence", "level", "moveSpeed",
          "jumpPower", "exp"]


def post(path, body):
    req = urllib.request.Request(f"{BASE}{path}", data=json.dumps(body).encode("utf-8"),
                                 headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=600) as r:
        return json.loads(r.read().decode("utf-8"))


bad = 0
s = post("/api/simulate/session", {"path": MAP, "startX": 1710, "startY": -25,
                                   "weapon": "01302000.img", "invulnerable": True})
sid = s["sessionId"]
f = post(f"/api/simulate/session/{sid}/frames", {"count": 1, "first": {}, "then": {}})
st = f.get("status") or {}

print(f"session {sid}  status present={bool(st)}")
print("\nfield            on-wire   value")
for name in FIELDS:
    if name in st:
        print(f"  {name:16s} yes       {st[name]}")
    else:
        print(f"  {name:16s} ABSENT    (renderSheet prints a dash, or skips)")
        # `exp`, `level`, `movementSpeed`, `jumpPower` are optional in the
        # renderer; the rest would render a dash. A dash for a stat the
        # reference states outright is a defect, so only those four may be absent.
        if name not in ("exp",):
            bad += 1

print("\n--- what the sheet will draw ---")
dmg = ("no weapon" if st.get("minDamage") == 0 and st.get("maxDamage") == 0
       else f"{st.get('minDamage')}-{st.get('maxDamage')}")
print(f"  HP {st.get('hp')}/{st.get('maxHp')}   MP {st.get('mp')}/{st.get('maxMp')}")
print(f"  STR {st.get('strength')} DEX {st.get('dexterity')} "
      f"INT {st.get('intelligence')} LUK {st.get('luck')}")
print(f"  ATK {st.get('watk')}  ACC {st.get('accuracy')}  Damage {dmg}  "
      f"DEF {st.get('physicalDefence')}/{st.get('magicalDefence')}")

# And with a weapon, because a sheet that does not move when the sheet's inputs
# move is the thing W-030 exists to prove.
post(f"/api/simulate/session/{sid}/weapon", {"equippedWeapon": "1302000"})
f2 = post(f"/api/simulate/session/{sid}/frames", {"count": 1, "first": {}, "then": {}})
st2 = f2.get("status") or {}
print(f"\n  after equipping 1302000: ATK {st.get('watk')} -> {st2.get('watk')}   "
      f"Damage {st.get('minDamage')}-{st.get('maxDamage')} -> "
      f"{st2.get('minDamage')}-{st2.get('maxDamage')}   "
      f"ACC {st.get('accuracy')} -> {st2.get('accuracy')}")
if st2.get("watk") == st.get("watk"):
    print("  >>> the sheet did not move when the weapon changed")
    bad += 1

print(f"\nBAD={bad}")