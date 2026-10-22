"""A skill bound to a key this project spends on something else can never fire.

livewalk.js skillFor:
    if (this.keyFor(e.key)) return null;      // whatever owns the key wins
    const skillId = this.skillKeys.get(key);

keyFor now binds ONLY the four arrow keys (the reference's set,
clean-client `Keyboard.cpp:78-81`), plus Space for jump and Ctrl/Z for attack.
WASD was this project's own addition, never declared as one, and has been
removed; Z remains and is declared at the call site.

MEASURED before the fix: the server accepted A, D, W, S and Z with `rejected: []`
and the panel reported them bound. None of them could ever cast.
"""
import json, os, urllib.parse, urllib.request
try:
    from archivelib import archive_id
except ImportError:            # a missing module must not read as a pass
    print(">>> RED: archivelib.py is missing next to this suite")
    raise SystemExit(1)

BASE = os.environ.get("GATE_BASE") or "http://127.0.0.1:5591"

_MAP_ID = archive_id("Map.nx", BASE)
MAP = f"{_MAP_ID}/Map/Map1/100000004.img"

# Only Z, after WASD was removed.
MOVEMENT = ["Z"]
# A/W/D/S must bind again -- they are the reference's skill letters and this
# project no longer spends them.
FREED = ["A", "D", "W", "S"]
FREE = ["F", "G", "H", "J", "K", "L"]
bad = 0


def post(path, body):
    req = urllib.request.Request(f"{BASE}{path}", data=json.dumps(body).encode("utf-8"),
                                 headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=600) as r:
        return json.loads(r.read().decode("utf-8"))


def get(path, **q):
    with urllib.request.urlopen(f"{BASE}{path}?" + urllib.parse.urlencode(q),
                               timeout=600) as r:
        return json.loads(r.read().decode("utf-8"))


skills = get("/api/skill-cast/castable")["skills"]
print(f"the server offers {len(skills)} castable skill(s)")
skill = skills[0]["skillId"]


def bind(key):
    s = post("/api/simulate/session",
             {"path": MAP, "startX": 1710, "startY": -25,
              "weapon": "01302000.img", "invulnerable": True})
    r = post(f"/api/simulate/session/{s['sessionId']}/skills",
             {"bindings": [{"skillId": skill, "key": key}]})
    return r.get("count"), r.get("rejected") or []


print("\n=== the five keys movement owns must be REFUSED, with a reason ===")
for key in MOVEMENT:
    count, rejected = bind(key)
    print(f"  {key}: bound={count} rejected={rejected}")
    if count:
        print(f"  >>> {key} was accepted and can never be pressed")
        bad += 1
    if not rejected or "movement or attack" not in " ".join(rejected):
        print(f"  >>> {key} was refused without saying WHY")
        bad += 1

print("\n=== the rest of the alphabet must still bind ===")
for key in FREED + FREE:
    count, rejected = bind(key)
    tag = "(freed by removing WASD)" if key in FREED else ""
    print(f"  {key}: bound={count} rejected={rejected} {tag}")
    if count != 1:
        print(f"  >>> {key} did not bind, but nothing owns it")
        bad += 1

print("\n=== a non-letter is still refused by the reference's own rule ===")
count, rejected = bind("Space")
print(f"  Space: bound={count} rejected={rejected}")
if count:
    print("  >>> a key the reference does not offer was accepted")
    bad += 1

print(f"\nBAD={bad}")