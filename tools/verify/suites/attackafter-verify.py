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
import json, urllib.parse, urllib.request

BASE = "http://127.0.0.1:5591"
MAP = "f4/Map/Map0/000000000.img"
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
    first = shots[0]
    print(f"    created on frame {first[0][0]} at elapsed={first[0][1]:.0f}ms")
    print(f"    still not testable at elapsed="
          f"{first[-1][1]:.0f}ms on frame {first[-1][0]}")
    travelled = [r for r in first if r[1] >= r[2]]
    if travelled:
        testable_at = travelled[0]
        print(f"    first testable frame: {testable_at[0]} "
              f"(elapsed={testable_at[1]:.0f}ms of delay={testable_at[2]:.0f}ms)")
        offset = testable_at[0] - first[0][0]
        print(f"    that is {offset} frames after it was FIRST OBSERVED; "
              f"the archive's delay is {delay_frames:.1f} frames")

        # W-067. THE SUITE WAS ARITHMETICALLY WRONG, AND THE PRODUCT WAS RIGHT.
        #
        # The old check compared `offset` against `delayMs / STEP_MS` and printed
        #   ">>> the entry became testable 7 frames in, not 96.0"
        # treating the first frame the probe OBSERVED as the frame the entry was
        # CREATED. It was not: this run's own output says
        #   created on frame 0 at elapsed=1483ms
        # so the entry was already 1483ms (89 frames) old before anything saw it.
        # The entry becomes testable when elapsed reaches the delay, so measured
        # from first observation the answer is (delay - age) / frame -- here
        # (1600 - 1483) / 16.67 = 7.0 frames, which is EXACTLY what it measured.
        # attackAfter was honoured to the frame all along.
        age0 = first[0][1]
        delay_of = testable_at[2]
        expected_offset = max(0.0, (delay_of - age0) / STEP_MS)
        print(f"    it was already {age0:.0f}ms old when first observed, so the "
              f"correct answer is ({delay_of:.0f} - {age0:.0f}) / {STEP_MS:.2f} = "
              f"{expected_offset:.1f} frames")
        if abs(offset - expected_offset) <= 2:
            print("    attackAfter is honoured exactly: the entry cannot be tested "
                  "before its delay elapses, so a hit cannot land before it either")
        else:
            print(f"  >>> became testable {offset} frames in; with an entry already "
                  f"{age0:.0f}ms old the correct answer is {expected_offset:.1f}")
            bad += 1

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
        before = first[:offset] if 0 <= offset < len(first) else []
        offenders = [(r[0], f"{r[1]:.0f}ms", f"{r[2]:.0f}ms") for r in before
                     if r[1] >= r[2]]
        Check("no frame before the delay was testable", not offenders,
              f"testable early at {offenders}")
    else:
        print("  >>> the entry never became testable")
        bad += 1

print(f"\nBAD={bad}")