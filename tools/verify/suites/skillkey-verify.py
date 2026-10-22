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
import json, os, sys, urllib.parse, urllib.request
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

# This suite binds a SKILL, so it reads the server's skill list, which is built
# from Skill.nx. It never declared that dependency, so on an instance without
# Skill.nx it went on to `skills[0]` with an empty list and died with
# `IndexError: list index out of range` -- a traceback that says nothing about
# the missing archive, while `gate.ps1:227` still turned it red. Rule 1: a
# prerequisite that is absent is a loud failure that NAMES itself.
# `archive_id` prints `>>> RED: Skill.nx is not open on this instance, so there
# is nothing to judge` and exits 1 when it is not open (archivelib.py:43-46).
_SKILL_ID = archive_id("Skill.nx", BASE)

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


castable = get("/api/skill-cast/castable")
if "skills" not in castable:
    # A missing field and an empty list are different things, and a missing one
    # used to arrive as a KeyError traceback that named nothing.
    print(f">>> RED: /api/skill-cast/castable carried no 'skills' field; it "
          f"returned {sorted(castable) if isinstance(castable, dict) else type(castable).__name__}")
    raise SystemExit(1)
skills = castable["skills"]
print(f"the server offers {len(skills)} castable skill(s)")
if not skills:
    # The prerequisite is empty, not the verdict. Skill.nx IS open here (it was
    # resolved by name above), so an empty list is a different failure from "the
    # archive is missing" and it must say so.
    print(f">>> RED: the server offers 0 castable skill(s) with Skill.nx open "
          f"as {_SKILL_ID}; an empty skill list is a missing prerequisite, "
          f"not a pass")
    raise SystemExit(1)
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
# Rule 14: the gate greps the text, every other caller reads the exit code.
# This file had no `sys.exit`, so `BAD=3` was exit 0 to the latter.
sys.exit(1 if bad else 0)
