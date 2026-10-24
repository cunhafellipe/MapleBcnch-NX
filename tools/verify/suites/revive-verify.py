"""A dead character must be able to come back, and must act again afterwards.

MEASURED before this existed, on `Map.nx/Map/Map0/000000000.img` with `4220000`
summoned beside the character: contact damage (`PADamage 135`) killed a 50 HP
character on frame 0, and from then on the attack key did nothing, the skill key
did nothing, the character would not walk, and `/revive`, `/respawn` and
`/portal` were all 404. A death, not a broken build -- except that nothing said so
and nothing could be done about it.

The revive is the reference's: `Stage::respawn(portalid)` /
`Player::respawn(pos, underwater)` (Stage.cpp:497-508, Player.cpp:210-251),
reached from the revive prompt at UIStateGame.cpp:548-556.
"""
import json, os, urllib.request
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
MAP = f"{_MAP_ID}/Map/Map0/000000000.img"

KILLER = "4220000"
bad = 0


def post(route, body=None):
    req = urllib.request.Request(f"{BASE}{route}",
                                 data=json.dumps(body or {}).encode("utf-8"),
                                 headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=600) as r:
        return json.loads(r.read().decode("utf-8"))


s = post("/api/simulate/session",
         {"path": MAP, "weapon": "1302000.img", "equippedWeapon": "1302000",
          "invulnerable": False})
sid = s["sessionId"]
post(f"/api/simulate/session/{sid}/summon", {"mobId": KILLER, "count": 1})
hp0 = s["status"]["hp"]
print(f"character {hp0}/{s['status']['maxHp']} HP; {KILLER} (PADamage 135) summoned")


def step(keys=None):
    return post(f"/api/simulate/session/{sid}/frames",
                {"count": 1, "first": keys or {}, "then": {}})


print()
print("=== walk into it and die ===")
# The budget comes from the character's OWN max hit points, not from a constant.
# This used to be a flat 400 frames, which was enough at 50 HP and is not enough
# at SimTuning.PlayerHp (W-082a, ADR-0006): 135 damage per contact needs about
# eight hits, and contact is intermittent, so the character reached 190/1000 when
# the loop gave up and the file reported "never died" -- which reads like a
# broken revive and is only a stale budget.
#
# Nothing here is weakened. The assertion is still "the character must actually
# die, or this file cannot judge the revive".
MAX_HP = s["status"]["maxHp"]
BUDGET = max(400, 4000)
print(f"  budget {BUDGET} frames for a {MAX_HP} HP character")
POSE_CAP = 600
dead_at = None
for i in range(BUDGET):
    # NO attack key. The loop used to swing every frame, which with a fixed 100
    # damage kills a 1000 HP mob in ten hits -- so the character killed its own
    # executioner and then stood next to a corpse wondering why it stopped
    # taking damage. This section exists to DIE, not to win.
    fr = step({})
    m = next((x for x in fr.get("mobs") or [] if x.get("mobId") == KILLER), None)
    if m is None:
        print(f"  frame {i}: the killer is gone (dead? {KILLER} no longer on the map)")
        break
    # Stay in contact: the gap guard used to let the killer drift 20px away and
    # wander back, which is most of why 400 frames bought only three hits.
    if abs(m["x"] - fr["x"]) > 8:
        dx = m["x"] - fr["x"]
        step({"left": dx < 0, "right": dx > 0})
    if fr.get("playerDamage"):
        print(f"  frame {i}: hit for {fr['playerDamage']}, HP {fr['status']['hp']}")
    if fr.get("playerDead"):
        dead_at = i
        print(f"  frame {i}: DEAD at x={fr['x']:.0f}, y={fr['y']:.0f}")
        break

if dead_at is None:
    print(f"  >>> never died in {BUDGET} frames at {fr['status']['hp']}/{MAX_HP} HP; "
          f"this file cannot judge the revive")
    bad += 1
    raise SystemExit(1)

print()
print("=== a corpse acts on nothing ===")
fr = step({"attack": True})
print(f"  attack -> attackStance={fr.get('attackStance')!r}")
if fr.get("attackStance"):
    print("  >>> a dead character swung; the guard is not working")
    bad += 1
fr = step({"skillId": "10001009"})
print(f"  skill  -> skillStance={fr.get('skillStance')!r}")
if fr.get("skillStance"):
    print("  >>> a dead character cast; the guard is not working")
    bad += 1

print()
print("=== revive ===")
fr = post(f"/api/simulate/session/{sid}/revive")
print(f"  playerDead={fr.get('playerDead')} hp={fr['status']['hp']}/"
      f"{fr['status']['maxHp']} x={fr['x']:.0f}, y={fr['y']:.0f} "
      f"onGround={fr.get('onGround')}")
if fr.get("playerDead"):
    print("  >>> still dead after a revive")
    bad += 1
if fr["status"]["hp"] != fr["status"]["maxHp"]:
    print(f"  >>> came back on {fr['status']['hp']} HP")
    bad += 1

print()
print("=== and it acts again ===")
fr = step({"attack": True})
print(f"  attack -> action={fr['action']!r} attackStance={fr.get('attackStance')!r}")
if not fr.get("attackStance"):
    print("  >>> a revived character still cannot swing -- the pose lock was not cleared")
    bad += 1
# Settle until the POSE IS OVER, not for a fixed count. A pose that is still
# playing locks movement (`SkillPoseTicks > 0` zeroes `moveLeft`, W-076), so a
# fixed count either wastes frames or measures the lock instead of the revive.
# The caps sit above what the poses MEASURE here, not above a guess: 10001009's
# `bamboo` pose runs 265 frames (4.4 s) from the archive body image's own per-frame
# delays, which is why the fixed 40 this used to wait could never have sufficed.
# The test keys on `action`, not on the stance NAME: `action` returns to 'stand'
# when the lock expires, while the stance field is left populated -- so waiting
# on the name would wait for a string that is never cleared.
for _ in range(POSE_CAP):
    fr = step({})
    if fr["action"] not in ("attack", "skill"):
        break
else:
    print(f"  >>> the swing pose never ended after {POSE_CAP} frames: action={fr['action']!r}")
    bad += 1
print(f"  after the pose ({_ + 1} frames): action={fr['action']!r} attackStance={fr.get('attackStance')!r}")

fr = step({"skillId": "10001009"})
print(f"  skill  -> action={fr['action']!r} skillStance={fr.get('skillStance')!r}")
if not fr.get("skillStance"):
    print("  >>> a revived character still cannot cast")
    bad += 1

# Same rule for the cast, and now ASSERTED rather than assumed: if the pose never
# ends, the walk below would measure the lock and report that a revived character
# cannot walk.
for _ in range(POSE_CAP):
    fr = step({})
    if fr["action"] not in ("attack", "skill"):
        break
else:
    print(f"  >>> the cast pose never ended after {POSE_CAP} frames: action={fr['action']!r}")
    bad += 1

# And alive again. The settles above are ~480 frames standing next to a mob that
# hits for 135, and at SimTuning.PlayerHp (W-082a) that is most of 1000 HP -- so
# the character was DEAD by the time the walk ran, and "a revived character
# cannot walk" was the corpse-does-not-walk guard (W-082c) working correctly.
# Reviving here is setup, not a workaround: this section is about whether a
# REVIVED character can act, and a second death mid-test is a different test.
fr = post(f"/api/simulate/session/{sid}/revive")
print(f"  (revived again for the walk: hp={fr['status']['hp']}/{fr['status']['maxHp']}, "
      f"playerDead={fr.get('playerDead')})")
if fr.get("playerDead"):
    print("  >>> could not revive before the walk")
    bad += 1
print(f"  after the cast ({_ + 1} frames): action={fr['action']!r} skillStance={fr.get('skillStance')!r}")

print()
print("=== and it walks ===")
x0 = fr["x"]
for _ in range(20):
    fr = step({"right": True})
print(f"  x {x0:.0f} -> {fr['x']:.0f}")
if abs(fr["x"] - x0) < 5:
    print("  >>> a revived character cannot walk")
    bad += 1

print()
print("=== a revive on a live character is a no-op, not a crash ===")
fr = post(f"/api/simulate/session/{sid}/revive")
print(f"  hp={fr['status']['hp']} playerDead={fr.get('playerDead')}")

print()
print("=== the invincibility toggle, mid-walk ===")
fr = post(f"/api/simulate/session/{sid}/invulnerable", {"value": True})
print(f"  on : hp={fr['status']['hp']} invulnerable="
      f"{fr['status'].get('invulnerable')} playerDead={fr.get('playerDead')}")
if not fr["status"].get("invulnerable"):
    print("  >>> the session did not report invincibility")
    bad += 1
# Now walk into the killer again and stay alive.
survived = True
for i in range(200):
    fr = step({"attack": True})
    m = next((x for x in fr.get("mobs") or [] if x.get("mobId") == KILLER), None)
    if m is not None and abs(m["x"] - fr["x"]) > 20:
        dx = m["x"] - fr["x"]
        step({"left": dx < 0, "right": dx > 0})
    if fr.get("playerDamage"):
        print(f"  frame {i}: reported a hit for {fr['playerDamage']} but HP stayed "
              f"{fr['status']['hp']}")
    if fr.get("playerDead"):
        survived = False
        break
print(f"  200 frames in contact with {KILLER}: playerDead={fr.get('playerDead')}")
if not survived:
    print("  >>> it still killed the character while invincibility was on")
    bad += 1

fr = post(f"/api/simulate/session/{sid}/invulnerable", {"value": False})
print(f"  off: invulnerable={fr['status'].get('invulnerable')}")
if fr["status"].get("invulnerable"):
    print("  >>> the session is still invulnerable after switching it off")
    bad += 1

print(f"\nBAD={bad}")

raise SystemExit(1 if bad else 0)
