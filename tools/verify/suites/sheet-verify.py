"""End-to-end: does equipping a weapon actually move the sheet and the damage?

Chain under test, all cited:
  PlayerStatus.ApplyEquip      Character.cs:18-21 base + the item's own inc* nodes
  PlayerStatus.MaxDamage       CharStats.cpp:118  ((4*str+dex)/100) * (pad * 1.2)
  PlayerStatus.MinDamage       CharStats.cpp:172  max(1, (int)(max*0.20 + 0.5))
  DamageRoll.Roll              Mob.cpp:177-186 / :1590-1601 / :1790
  CombatService.MobDefence     Mob.cpp:62  info/PDDamage
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
MAP = f"{_MAP_ID}/Map/Map1/100000004.img"

bad = 0


def api(path, **q):
    url = f"{BASE}{path}?" + urllib.parse.urlencode(q)
    with urllib.request.urlopen(url, timeout=600) as r:
        return json.loads(r.read().decode("utf-8"))


def post(path, body):
    req = urllib.request.Request(f"{BASE}{path}", data=json.dumps(body).encode("utf-8"),
                                 headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=600) as r:
        return json.loads(r.read().decode("utf-8"))


# ---------------------------------------------------------------- the sheet math
def expect(str_, dex_, pad):
    calc = ((4.0 * str_ + dex_) / 100.0) * (pad * 1.2)
    mx = int(calc + 0.5) if calc > 0 else 0
    mn = max(1, int(mx * 0.20 + 0.5)) if mx > 0 else 0
    return mx, mn


print("=== the sheet, computed from the reference's formulas ===")
for name, str_, dex_, pad in [("unarmed", 12, 5, 1), ("Str sword 01302000", 12, 5, 17)]:
    mx, mn = expect(str_, dex_, pad)
    print(f"  {name:22s} str={str_} dex={dex_} pad={pad}  -> max={mx} min={mn}")

# ---------------------------------------------------------------- the live chain
s = post("/api/simulate/session", {"path": MAP, "startX": 1710, "startY": -25,
                                   # `weapon` is the SWING BOX and `equippedWeapon`
                                   # is the sheet -- two different fields, and
                                   # MapSimSessionStore.ResolveSwingHit reads
                                   # session.Weapon. Starting without it makes every
                                   # swing resolve to an empty box and land nothing,
                                   # which is indistinguishable from a broken roll.
                                   #
                                   # The session is started UNARMED deliberately: it
                                   # used to pass `equippedWeapon` here and then
                                   # assert the sheet was 1/1, which stopped being true
                                   # the moment `Start` began honouring the weapon it
                                   # is given. Measuring the unarmed sheet by asking
                                   # for the unarmed character is the honest way.
                                   "weapon": "01302000.img",
                                   "invulnerable": True})
sid = s["sessionId"]
print(f"\nsession {sid}  x={s['x']}  mobs={len(s.get('mobs') or [])}")
# The nearest GROUND-STANDING mob, the same selection attack-test.ps1:42-45 makes.
# The nearest mob on this map stands at x=2091, 381px away -- a 90-frame walk at
# 2.5px/frame stops at 1840 and never arrives, which reads as "no hits" and is
# really "never got there".
# The nearest ground-standing mob ON THE SAME LEDGE. Selecting by x alone picks
# the mob at x=1867 y=-535 — 510px above the spawn — and walking toward its x
# never reaches it. That is the fourth recorded instance of the same trap.
targets = [m for m in (s.get("mobs") or [])
           if m.get("onGround") and abs(m["y"] - s["y"]) <= 100]
target = min(targets, key=lambda m: abs(m["x"] - s["x"])) if targets else None
print(f"  target mob at x={target and target['x']} y={target and target['y']} "
      f"hp={target and target.get('hp')}")

def sheet(label):
    f = post(f"/api/simulate/session/{sid}/frames", {"count": 1, "first": {}, "then": {}})
    st = f["status"]
    print(f"  [{label}] str={st['strength']} dex={st['dexterity']} int={st['intelligence']} "
          f"luk={st['luck']} hp={st['hp']}/{st['maxHp']} mp={st['mp']}/{st['maxMp']} "
          f"atk={st['watk']} acc={st['accuracy']} max={st['maxDamage']} min={st['minDamage']}")
    return st

bare = sheet("no weapon")
if (bare["strength"], bare["dexterity"], bare["intelligence"], bare["luck"]) != (12, 5, 4, 4):
    print(">>> base attributes are not the reference's level-1 character"); bad += 1
# ADR-0006 / W-082a: the base HP pool is SimTuning.PlayerHp (1000), not the
# reference's 50. It was 50 while a level-20 mob's contact hit for 150, so one
# touch killed the character and no fight could be read at all. MP is untouched
# and is still the reference's 10.
if (bare["maxHp"], bare["maxMp"]) != (1000, 10):
    print(f">>> base pools are not 1000/10 (SimTuning.PlayerHp / the reference's MaxMP): "
          f"{bare['maxHp']}/{bare['maxMp']}"); bad += 1
if bare["maxDamage"] != 1 or bare["minDamage"] != 1:
    print(f">>> unarmed damage is not 1/1: {bare['maxDamage']}/{bare['minDamage']}"); bad += 1

post(f"/api/simulate/session/{sid}/weapon", {"equippedWeapon": "1302000"})
armed = sheet("sword 1302000")
want_max, want_min = expect(12, 5, 17)
if armed["maxDamage"] != want_max or armed["minDamage"] != want_min:
    print(f">>> sword damage is {armed['maxDamage']}/{armed['minDamage']}, "
          f"reference says {want_max}/{want_min}"); bad += 1
if armed["watk"] != 17:
    print(f">>> incPAD not applied: watk={armed['watk']}"); bad += 1
if armed["maxDamage"] <= bare["maxDamage"]:
    print(">>> equipping a weapon did not raise the damage"); bad += 1

# ---------------------------------------------------------------- the roll
# Walk toward the nearest mob and swing, collecting the damage numbers the frame
# actually reports. 0 hits from a fixed walk means "never got adjacent", not
# "the roll is broken" -- so the direction comes from the map's own mob list.
def step(body):
    return post(f"/api/simulate/session/{sid}/frames",
                {"count": 1, "first": body, "then": {}})


hits = []
last = None
swings = 0

# PHASE 1 -- close in, then STOP chasing. The mob patrols; a loop that keeps
# re-picking "the nearest mob" walks the character off the ledge chasing it (it
# ended at x=2302 y=240, 265px above the ground). Close once, then stand still.
for i in range(400):
    last = step({})
    px, py = last["x"], last["y"]
    hits += [d.get("value") for d in (last.get("damage") or [])]
    near = [m for m in (last.get("mobs") or [])
            if m.get("onGround") and abs(m["y"] - py) <= 100]
    if not near:
        continue
    mob = min(near, key=lambda m: abs(m["x"] - px))
    dx = mob["x"] - px
    # Walk all the way IN, not to within reach: attack-test.ps1:48-51 walks to the
    # mob's own x and lands 8 of 8. A "close enough" threshold of 45 stops 40px
    # short and every swing misses.
    if abs(dx) < 10:
        break
    step({"right": True} if dx > 0 else {"left": True})

# PHASE 2 -- swing from here, each followed by enough idle frames for the hit.
# The hit is DELAYED (swingDelayTicks measured 15-20 on this weapon) and swinging
# again on the next frame RESETS the countdown, so a loop that presses attack
# every frame resolves nothing at all. Idle frames are part of measuring a
# one-shot event.
for s in range(8):
    # Re-close every swing. The mob patrols and 45 idle frames is enough time to
    # walk out of reach, so a swing thrown at a stale position misses on geometry.
    for _ in range(120):
        probe = step({})
        hits += [d.get("value") for d in (probe.get("damage") or [])]
        near = [m for m in (probe.get("mobs") or [])
                if m.get("onGround") and abs(m["y"] - probe["y"]) <= 100]
        if not near:
            break
        mob = min(near, key=lambda m: abs(m["x"] - probe["x"]))
        if abs(mob["x"] - probe["x"]) < 10:
            break
        step({"right": True} if mob["x"] > probe["x"] else {"left": True})

    swung = step({"attack": True})
    swings += 1
    print(f"    swing {s}: x={swung['x']:.0f} stance={swung.get('attackStance')!r} "
          f"facing={swung.get('facing')} damage={swung.get('damage')}")
    for k in range(60):
        idled = step({})
        got = [d.get("value") for d in (idled.get("damage") or [])]
        hits += got
        if got:
            print(f"      -> hit on idle frame {k + 1}: {got}")
            break
        if k % 10 == 9:
            live = [m for m in (idled.get("mobs") or []) if m.get("onGround")]
            near = min(live, key=lambda m: abs(m["x"] - idled["x"]), default=None)
            print(f"      ..idle {k + 1}: x={idled['x']:.0f} mobx={near and round(near['x'])} "
                  f"mobhp={near and near.get('hp')}")

print(f"  closed to x={last['x']:.0f} y={last['y']:.0f}, {swings} swings, "
      f"collected {len(hits)} damage numbers: {hits[:14]}")
print(f"  sheet max={armed['maxDamage']} min={armed['minDamage']}")
# ADR-0006 (2026-10-17): the simulator deals a fixed SimTuning.PlayerDamage and
# every mob spawns at SimTuning.MobHp, so a swing is no longer expected to fall
# inside the sheet's weapon range -- that formula is still computed and still
# asserted above, it is just not what the simulator deals any more. This used to
# be "a swing for 100 exceeded the sheet's max 11", which is the suite reporting
# a decision as a defect.
TEST_DAMAGE, TEST_HP = 100, 1000
if hits:
    lo, hi = min(hits), max(hits)
    print(f"  range [{lo}..{hi}]")
    if lo != TEST_DAMAGE or hi != TEST_DAMAGE:
        print(f">>> swings were not the fixed test damage {TEST_DAMAGE}: [{lo}..{hi}]"); bad += 1
    else:
        print(f"  every connected swing was exactly the test damage {TEST_DAMAGE}")
else:
    print("  >>> no swing ever connected — the roll was never exercised")
    bad += 1

print(f"\nBAD={bad}")
raise SystemExit(1 if bad else 0)
