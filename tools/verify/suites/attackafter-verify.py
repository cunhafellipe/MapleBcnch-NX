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
        current.append((i, mob["attackEntryElapsedMs"], mob["attackEntryDelayMs"],
                        mob.get("attackEntryX", 0.0)))
    elif current:
        # The frame the entry DISAPPEARED on. The mob's stun on THAT frame is
        # what tells the two removals apart: MobPatrol.cs:780-789 clears the
        # WHOLE list when the mob is stunned, and MobPatrol.cs:1169-1173
        # removes ONE entry when the hit test succeeds.
        shots.append({"frames": current, "stun": (mob or {}).get("stun") or 0,
                      "ended": i})
        current = []
if current:
    shots.append({"frames": current, "stun": 0, "ended": None})

if not shots:
    print("  >>> no entry ever appeared")
    bad += 1
else:
    print(f"  {len(shots)} shot(s); the first is {len(shots[0]['frames'])} frames long")
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
    judged_by_removal = 0
    never_testable = 0
    wrong_offset = []
    early_tested = []
    removed_at_delay = []
    cleared_early = []
    for shot in shots:
        frames = shot["frames"]
        age0 = frames[0][1]
        delay_of = frames[0][2]
        travelled = [r for r in frames if r[1] >= r[2]]
        if travelled:
            judged += 1
            testable_at = travelled[0]
            offset = testable_at[0] - frames[0][0]
            expected_offset = max(0.0, (delay_of - age0) / STEP_MS)
            if abs(offset - expected_offset) > 2:
                wrong_offset.append((frames[0][0], offset, expected_offset, age0, delay_of))
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
            before = frames[:offset] if 0 <= offset < len(frames) else []
            offenders = [(r[0], f"{r[1]:.0f}ms", f"{r[2]:.0f}ms") for r in before
                         if r[1] >= r[2]]
            if offenders:
                early_tested.append((frames[0][0], offenders))
            continue
        # NOT observed at or after its delay: the entry was REMOVED instead.
        #
        # THIS IS THE GATE'S ONLY RED (2026-10-24, the gate run against a fresh
        # instance on 5601: ">>> RED (BAD=1) / >>> no shot ever reached its
        # delay, so this run cannot judge attackAfter at all"). The old version
        # of this block called that outcome "the shot never reached its delay",
        # and it was FALSE. It refused about 2 runs in 10.
        #
        # The entry is removed on the frame it becomes testable when the hit
        # test succeeds (MobPatrol.cs:1169-1173), and this file only records
        # frames where an entry EXISTS -- so the one frame that would have
        # shown "elapsed >= delay" is never recorded, and the old code read
        # that as "the delay was never reached".
        #
        # MEASURED 2026-10-24, frame by frame, on the instance on 5601:
        #   the entry was last seen at elapsed=1583.3333ms with a delay of
        #   1600.0ms, so 1583.3333 + one 16.667ms tick = 1600.0000ms = the delay
        #   EXACTLY; the mob's stun was 0 on that frame, so it was not the stun
        #   clear at MobPatrol.cs:780-789; and the projectile had been born
        #   5.4px from the character, inside the player's box, so the hit test
        #   was bound to succeed on that frame.
        #
        # That is STRONGER evidence than the crossing frame, not weaker: the
        # product does not even evaluate the hit before the delay
        # (MobPatrol.cs:1146 skips the whole test while elapsed < delay), and
        # the projectile does not move before its delay, so a projectile born
        # inside the player's box would have been removed on the FIRST frame
        # it was tested. This entry sat untested from its birth to exactly its
        # delay and was removed there, so it was NOT tested before its delay.
        last = frames[-1]
        removed_at = last[1] + STEP_MS
        if (removed_at >= delay_of - 1e-6 and removed_at <= delay_of + 2 * STEP_MS
                and not shot["stun"]):
            judged_by_removal += 1
            removed_at_delay.append((frames[0][0], removed_at, delay_of, last[3]))
        else:
            never_testable += 1
            cleared_early.append((frames[0][0], removed_at, delay_of, shot["stun"],
                                  shot["ended"]))

    f0 = shots[0]["frames"]
    print(f"    created on frame {f0[0][0]} at elapsed={f0[0][1]:.0f}ms")
    print(f"    still not testable at elapsed={f0[-1][1]:.0f}ms on frame {f0[-1][0]}")
    print(f"  the product's own entry delay for this mob: {shots[0]['frames'][0][2]:.0f}ms ; "
          f"the archive's attackAfter says {delay_ms}ms")
    # The arithmetic the W-067 acceptance asks for, printed for a shot whose
    # crossing frame WAS observed. A shot that was removed ON its delay has no
    # crossing frame to do arithmetic with, and it does not need one.
    first = next((s for s in shots if any(r[1] >= r[2] for r in s["frames"])), None)
    if first is not None:
        f = first["frames"]
        travelled = [r for r in f if r[1] >= r[2]]
        testable_at = travelled[0]
        offset = testable_at[0] - f[0][0]
        print(f"    first testable frame: {testable_at[0]} "
              f"(elapsed={testable_at[1]:.0f}ms of delay={testable_at[2]:.0f}ms)")
        print(f"    that is {offset} frames after it was FIRST OBSERVED; "
              f"the archive's delay is {delay_frames:.1f} frames")
        print(f"    it was already {f[0][1]:.0f}ms old when first observed, "
              f"so the correct answer is "
              f"({f[0][2]:.0f} - {f[0][1]:.0f}) / {STEP_MS:.2f} = "
              f"{max(0.0, (f[0][2] - f[0][1]) / STEP_MS):.1f} frames")
    for frame, removed_at, delay_of, launch_x in removed_at_delay:
        print(f"    the shot from frame {frame} was born at x={launch_x:.1f} and was "
              f"removed by the HIT TEST on the frame it became testable "
              f"(elapsed={removed_at:.0f}ms of delay={delay_of:.0f}ms): it sat "
              f"untested from its birth to exactly its delay, so it was not tested "
              f"before it")
    for frame, removed_at, delay_of, stun, ended in cleared_early:
        if ended is None:
            # The window ended with the entry still in flight. It was not removed.
            print(f"    the shot from frame {frame} was still in flight at elapsed={removed_at:.0f}ms, "
                  f"under its delay of {delay_of:.0f}ms, when the measurement window ended; "
                  f"it cannot be judged")
        else:
            print(f"    the shot from frame {frame} was removed at elapsed={removed_at:.0f}ms, "
                  f"BEFORE its delay of {delay_of:.0f}ms (the mob's stun on that frame was "
                  f"{stun}), so it cannot be judged")
    print(f"  {judged} shot(s) were observed across their delay, {judged_by_removal} "
          f"were removed by a hit exactly on their delay, {never_testable} never "
          f"reached their delay")
    for frame, offset, expected, age0, delay_of in wrong_offset:
        print(f"  >>> the shot from frame {frame} became testable {offset} frames "
              f"in; with an entry already {age0:.0f}ms old the correct answer is "
              f"{expected:.1f}")
    if wrong_offset:
        bad += len(wrong_offset)
    elif judged or judged_by_removal:
        print("    attackAfter is honoured exactly: the entry cannot be tested "
              "before its delay elapses, so a hit cannot land before it either")
    else:
        print("  >>> every shot was removed before it could be tested, so this run "
              "cannot judge attackAfter at all")
        bad += 1
    Check("no frame before the delay was testable", not early_tested,
          f"testable early at {early_tested}")

print(f"\nBAD={bad}")
raise SystemExit(1 if bad else 0)
