"""W-040: where does `attackAfter = 1200ms` land?

The report from 2026-10-14 was that a hit landed on frame 7 while the archive said
1200ms, which is frame 72. MEASURED here, on the current build, for every mob in a
stride of the catalog that has exactly one type-2 attack with real power.

The rule being checked is `MobAttack.cpp:199` —

    if (entry->elapsed < entry->delay) { ++entry; continue; }

— with `entry->delay = data.delay` (:146), `data.delay = max(0, data["attackAfter"])`
(`Mob.cpp:153`) and `entry->elapsed` advanced by one TIMESTEP per tick (:37).
TIMESTEP is 16.667ms, so 1200ms is frame 72.0 and a hit cannot be tested before it.
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
MAP = f"{_MAP_ID}/Map/Map0/000000000.img"

STEP_MS = 1000 / 60
bad = 0

# W-067. A judge that cannot report red protects nothing, and one that reports
# red for the wrong reason is worse (pm/GATE.md:78-83). Both live here now.
def Check(name, ok, detail=""):
    global bad
    if ok:
        print(f"    ok    {name}")
    else:
        bad += 1
        print(f"  FAIL  {name} -- {detail}")
        print(f">>> RED: attack-after -- {name} ({detail})")


def get(route, **q):
    with urllib.request.urlopen(f"{BASE}{route}?" + urllib.parse.urlencode(q),
                               timeout=300) as r:
        return json.loads(r.read().decode("utf-8"))


def post(route, body):
    req = urllib.request.Request(f"{BASE}{route}", data=json.dumps(body).encode("utf-8"),
                                 headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=600) as r:
        return json.loads(r.read().decode("utf-8"))


def step(sid, keys=None):
    return post(f"/api/simulate/session/{sid}/frames",
                {"count": 1, "first": keys or {}, "then": {}})


print("=== which mobs declare attackAfter, and how long is it? ===")
cat = get("/api/simulate/mob-catalog")["mobs"]
ids = [m["id"] for m in cat]
sample = ids[::max(1, len(ids) // 80)][:80]
delays = []
for mid in sample:
    for a in get("/api/simulate/mob-attacks", mobId=mid)["attacks"]:
        if a.get("type") == 2 and a.get("radius", 0) > 0:
            delays.append((mid, a["action"], a["delayMs"], a.get("power"), a.get("bulletSpeed")))
            break

hist = {}
for _, _, d, _, _ in delays:
    hist[d] = hist.get(d, 0) + 1
print(f"  {len(delays)} type-2 attacks in {len(sample)} sampled mobs")
print(f"  attackAfter histogram: {dict(sorted(hist.items()))}")

# The reported mob is the one with 1200ms if there is one; otherwise take the
# closest, and say which.
target = None
for row in delays:
    if row[2] == 1200:
        target = row
        break
if target is None and delays:
    target = min(delays, key=lambda r: abs(r[2] - 1200))
print()
if target is None:
    print("  >>> no type-2 attack in this sample")
    raise SystemExit(1)
mid, action, delay_ms, power, bullet = target
delay_frames = delay_ms / STEP_MS
life = max(1, (get("/api/simulate/mob-attacks", mobId=mid)["attacks"][0]["radius"] or 1)
           * 600.0 / (bullet or 100))
print(f"  fighting {mid} {action}: attackAfter={delay_ms}ms = {delay_frames:.1f} "
      f"frames, flight {life:.0f}ms = {life / STEP_MS:.1f} frames")

print()
print("=== when the entry becomes testable ===")
s = post("/api/simulate/session",
         {"path": MAP, "weapon": "1302000.img", "equippedWeapon": "1302000",
          "invulnerable": True})
sid = s["sessionId"]
post(f"/api/simulate/session/{sid}/summon", {"mobId": mid, "count": 1})


def mob_of(fr):
    return next((m for m in fr.get("mobs") or [] if m.get("mobId") == mid), None)


fr = step(sid)
mob = mob_of(fr)
if mob is None:
    print("  >>> the mob did not spawn")
    raise SystemExit(1)

# Wake it, back off, let any shot in flight finish -- all as in projectile-verify,
# because a shot born inside the character resolves the instant its delay expires
# and cannot be timed.
woken = False
for _ in range(400):
    fr = step(sid, {"attack": True})
    mob = mob_of(fr)
    if mob is None:
        break
    if mob["hp"] < mob["maxHp"]:
        woken = True
        break
    dx = mob["x"] - fr["x"]
    if abs(dx) > 8:
        step(sid, {"left": dx < 0, "right": dx > 0})
print(f"  woken by a landed hit: {woken}")

fr = step(sid)
mob = mob_of(fr)
away = fr["x"] <= mob["x"]
for _ in range(90):
    fr = step(sid, {"right": away, "left": not away})
    mob = mob_of(fr)
    if mob is None or abs(mob["x"] - fr["x"]) >= 130 or not fr["onGround"]:
        break
for _ in range(200):
    fr = step(sid)
    mob = mob_of(fr)
    if mob is None or mob.get("attackEntryCount", 0) == 0:
        break

shots = []
current = []
for i in range(900):
    fr = step(sid)
    mob = mob_of(fr)
    if mob is not None and mob.get("attackEntryCount", 0) > 0:
        current.append((i, mob["attackEntryElapsedMs"], mob["attackEntryDelayMs"]))
    elif current:
        shots.append(current)
        current = []
if current:
    shots.append(current)

if not shots:
    print("  >>> no entry ever appeared")
    bad += 1
else:
    print(f"  {len(shots)} shot(s); the first is {len(shots[0])} frames long")
    # EVERY shot is judged, and the verdict is a COUNT over them.
    #
    # W-067 recorded that the first version compared a stale expectation
    # against the first frame the probe OBSERVED, and called a correct product
    # wrong. The expectation is right; what it did was apply it to ONE shot.
    # The shot it happened to be holding can be one that was already old when
    # the probe saw it, or one that never reached its delay at all -- and then
    # the whole run is decided by WHICH shot came first.
    #
    # The expectation, stated once:
    #   the entry becomes testable when elapsed reaches the delay, so measured
    #   from the first observation the answer is (delay - age) / frame.
    judged = 0
    never_testable = 0
    wrong_offset = []
    early_tested = []
    for shot in shots:
        age0 = shot[0][1]
        delay_of = shot[0][2]
        travelled = [r for r in shot if r[1] >= r[2]]
        if not travelled:
            never_testable += 1
            continue
        judged += 1
        testable_at = travelled[0]
        offset = testable_at[0] - shot[0][0]
        expected_offset = max(0.0, (delay_of - age0) / STEP_MS)
        if abs(offset - expected_offset) > 2:
            wrong_offset.append((shot[0][0], offset, expected_offset, age0, delay_of))
        # The invariant that actually matters, stated once and checked directly
        # from the frame data rather than inferred from an index: EVERY frame
        # before the testable one was still under its own delay.
        #
        # The old version of this block printed "not testable, so nothing could
        # have landed" and then, on the very same line, counted a failure because
        # the entry WAS testable at frame 7 -- i.e. it translated "my expectation
        # is stale" into "the product is wrong". That is the worst failure mode a
        # judge has (pm/GATE.md:78-83): it cries wolf, and a gate that cries wolf
        # gets ignored, which means it protects nothing.
        before = shot[:offset] if 0 <= offset < len(shot) else []
        offenders = [(r[0], f"{r[1]:.0f}ms", f"{r[2]:.0f}ms") for r in before
                     if r[1] >= r[2]]
        if offenders:
            early_tested.append((shot[0][0], offenders))

    first = shots[0]
    print(f"    created on frame {first[0][0]} at elapsed={first[0][1]:.0f}ms")
    print(f"    still not testable at elapsed="
          f"{first[-1][1]:.0f}ms on frame {first[-1][0]}")
    print(f"  {judged} of {len(shots)} shot(s) could be judged; {never_testable} "
          f"never reached its delay")
    if judged:
        travelled = [r for r in first if r[1] >= r[2]]
        if travelled:
            testable_at = travelled[0]
            offset = testable_at[0] - first[0][0]
            print(f"    first testable frame: {testable_at[0]} "
                  f"(elapsed={testable_at[1]:.0f}ms of delay={testable_at[2]:.0f}ms)")
            print(f"    that is {offset} frames after it was FIRST OBSERVED; "
                  f"the archive's delay is {delay_frames:.1f} frames")
            print(f"    it was already {first[0][1]:.0f}ms old when first observed, "
                  f"so the correct answer is "
                  f"({first[0][2]:.0f} - {first[0][1]:.0f}) / {STEP_MS:.2f} = "
                  f"{max(0.0, (first[0][2] - first[0][1]) / STEP_MS):.1f} frames")
    for frame, offset, expected, age0, delay_of in wrong_offset:
        print(f"  >>> the shot from frame {frame} became testable {offset} frames "
              f"in; with an entry already {age0:.0f}ms old the correct answer is "
              f"{expected:.1f}")
    if wrong_offset:
        bad += len(wrong_offset)
    elif judged:
        print("    attackAfter is honoured exactly: the entry cannot be tested "
              "before its delay elapses, so a hit cannot land before it either")
    else:
        print("  >>> no shot ever reached its delay, so this run cannot judge "
              "attackAfter at all")
        bad += 1
    Check("no frame before the delay was testable", not early_tested,
          f"testable early at {early_tested}")

print(f"\nBAD={bad}")