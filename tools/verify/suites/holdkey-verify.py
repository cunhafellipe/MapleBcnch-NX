"""HOLDING the attack key must deal damage.

`Player::can_attack()` (Player.cpp:1250-1253) ends in `&& !attacking`: the
reference cannot BEGIN a second swing while one is playing, and the hit of the
first lands on its own schedule -- `Combat.cpp:1794` asks for
`get_attackdelay(0)` and searches the map on a later tick, not on the keypress.

The port armed the swing and then immediately decremented its own countdown, every
frame, for as long as the key was down. `PendingSwingHitTicks` was rewritten to ~16
and decremented to ~15 on the same frame, sixty times a second, so it could never
reach zero: the swing never resolved and a player who HELD the button -- which is
what a player does -- hit nothing forever.

Tapping the key worked, which is why this survived: every probe up to here tapped.
The suite therefore holds the key down for a long, unbroken run and counts the
damage numbers, which is the case nobody had measured.
"""
import json, os, urllib.parse, urllib.request
try:
    from archivelib import archive_id, require_gate_base
except ImportError:            # a missing module must not read as a pass
    print(">>> RED: archivelib.py is missing next to this suite")
    raise SystemExit(1)
except SyntaxError as exc:    # a helper that does not PARSE is not missing
    print(f">>> RED: archivelib.py next to this suite does not parse: {exc}")
    raise SystemExit(1)

BASE = require_gate_base()

_MAP_ID = archive_id("Map.nx", BASE)
_MOB_ID = archive_id("Mob.nx", BASE)
MAP = f"{_MAP_ID}/Map/Map0/000000000.img"

bad = 0


def get_node(path):
    with urllib.request.urlopen(
            f"{BASE}/api/inspect?" + urllib.parse.urlencode({"path": path}), timeout=300) as r:
        return json.loads(r.read().decode("utf-8"))


def post(route, body):
    req = urllib.request.Request(f"{BASE}{route}", data=json.dumps(body).encode("utf-8"),
                                 headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=600) as r:
        return json.loads(r.read().decode("utf-8"))


print("=== an empty map, a weapon, and a summoned mob ===")
# MEASURED: Map.nx/Map/Map0/000000000.img starts the character at (0,95) with no mobs
# and a foothold underfoot. A map full of mobs kills a 50 HP character before the
# question can be asked.
#
# The character is invulnerable, and has to be: standing 27px from 9300104 is
# contact range, and its `info/PADamage` is 200 -- MEASURED, the character was dead
# within a frame of arriving and `Swing` then correctly refuses, which reads as
# "a held key hits nothing" for the second time. Invulnerability does not touch
# what this file measures: the player's damage NUMBER comes from
# `mobPatrol.TakeHit`, never from `PlayerStatus.Hurt`.
s = post("/api/simulate/session",
         {"path": MAP,
          # `weapon` is the swing BOX; `equippedWeapon` is the WZ image whose
          # attack1..N actions exist. Without the second, `AttackStancesFor` is
          # empty and `Swing` arms nothing -- a no-op that reads like a dead swing.
          "weapon": "1302000.img", "equippedWeapon": "1302000",
          "invulnerable": True})
sid = s["sessionId"]

# The mob must have a REAL collision box. `Frame::get_bounds` (Animation.cpp:58-61,
# `bounds = src`) reads `lt` and `rb` and nothing else, so a frame without both is
# a POINT at its own feet -- the player's swing box sits ~15px above its own feet
# and never contains it, so the suite reports "a held key hits nothing" for a mob
# that was never standing there. MEASURED over the open Mob.nx: 108 of 2,053 mobs
# are in that state.
CANDIDATES = ("9500302", "9500195", "2110200", "2220100", "9400100")
mid = None
for cand in CANDIDATES:
    try:
        node = get_node(f"{_MOB_ID}/{cand}.img/stand/0")
    except Exception:
        continue
    kids = {c["name"] for c in node.get("children", [])}
    if "lt" in kids and "rb" in kids:
        mid = cand
        box = {c["name"]: c.get("value") for c in node.get("children", []) if c["name"] in ("lt", "rb")}
        print(f"  {cand}: stand/0 carries lt={box.get('lt')} rb={box.get('rb')}")
        break
if mid is None:
    print(f"  >>> no candidate mob has a stand/0 collision box; this file cannot run")
    bad += 1
    raise SystemExit(1)

r = post(f"/api/simulate/session/{sid}/summon", {"mobId": mid, "count": 1})
print(f"  summoned {mid}: {r.get('spawned')} spawned, {len(r.get('rejected') or [])} rejected")


def step(keys):
    return post(f"/api/simulate/session/{sid}/frames",
                {"count": 1, "first": keys, "then": {}})


def mob(fr):
    return next((m for m in fr.get("mobs") or [] if m.get("mobId") == mid), None)


# 1. Close to arm's length. The swing box reaches roughly +20..+84px in FRONT of
#    the character, so walking until the mob is inside it is what makes the
#    question answerable rather than "it swung at empty air".
print()
print("=== walk into reach (the swing box does not reach 90px) ===")
hp_start = None
for _ in range(400):
    fr = step({})
    m = mob(fr)
    if m is None:
        break
    if hp_start is None:
        hp_start = m["maxHp"]
    if abs(m["x"] - fr["x"]) <= 30:
        print(f"  in reach: player x={fr['x']:.0f}, mob x={m['x']:.0f}, "
              f"{abs(m['x'] - fr['x']):.0f}px apart, mob HP {m['hp']}/{m['maxHp']}")
        break
    dx = m["x"] - fr["x"]
    step({"left": dx < 0, "right": dx > 0})
else:
    print(f"  >>> could not get within 30px of {mid}")
    bad += 1

# 2. HOLD THE KEY. No release frame, no edge, no idle gap: one continuous
#    `attack: true` on every frame for 600 frames.
print()
print("=== 600 frames with the attack key held down the whole time ===")
frames = 600
damage = []
hits = 0
hp0 = None
hp1 = None
for i in range(frames):
    fr = step({"attack": True})
    m = mob(fr)
    for d in fr.get("damage") or []:
        damage.append((i, d.get("value"), d.get("mobId")))
    if m is not None:
        if hp0 is None:
            hp0 = m["hp"]
        hp1 = m["hp"]

print(f"  mob HP {hp0} -> {hp1}")
print(f"  damage numbers produced: {len(damage)}")
if damage:
    print(f"    first: frame {damage[0][0]}, value {damage[0][1]}, mob {damage[0][2]}")
    print(f"    last : frame {damage[-1][0]}, value {damage[-1][1]}, mob {damage[-1][2]}")
    total = sum(int(d[1]) for d in damage)
    print(f"    total damage {total} across {len(damage)} hits")

if hp1 is not None and hp0 is not None and hp0 - hp1 > 0:
    print(f"  the mob lost {hp0 - hp1} HP to a key that was never released -- "
          f"the swing resolves while held")
elif damage:
    print(f"  damage numbers appeared ({len(damage)}) but the mob's HP did not move; "
          f"those numbers are not against it")
    bad += 1
else:
    print(f"  >>> 600 frames of a HELD attack key produced no damage at all. "
          f"`Swing` armed a countdown it reset on the same frame, 60 times a second.")
    bad += 1

# 3. The cadence sanity check that falls out of the same rule: one swing at a
#    time. A key held down must NOT deal a hit every frame -- the pose and the
#    hit delay space the swings out, and a "one hit per frame" reading would mean
#    the delay is being ignored rather than merely re-armed.
if damage:
    frames_with_damage = len({d[0] for d in damage})
    print()
    print(f"  swings that landed: {frames_with_damage} over {frames} frames of a held key")
    if frames_with_damage >= frames - 1:
        print(f"  >>> a hit on essentially every frame: the attack delay is not being "
              f"honoured, only its damage")
        bad += 1
    else:
        print(f"  they are spaced by the pose and the delay, not one per frame -- "
              f"the cadence is the reference's")

print(f"\nBAD={bad}")
raise SystemExit(1 if bad else 0)
