"""A mob that is hit recoils. Mob.cpp:595-598, KBSPEED = 130.0 (Mob.h:467).

    else if (canmove && stance_entered)
    {   phobj.vx = flip ? -KBSPEED : KBSPEED; }

`stance_entered` means ONCE, on the frame the mob enters the hit pose. The
expected first-frame displacement is 130 px/s * (1000/60) ms = 2.17px, away from
the side the mob is facing.

Calibration first: a mob that is NOT hit must not move, or "it moved" proves
nothing.
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

MOB = "2220100"
STEP_MS = 1000 / 60
EXPECT = 130.0 * STEP_MS / 1000.0 * 1000.0 / 1000.0   # see below
EXPECT_PX = 130.0 / 1000.0 * STEP_MS                  # 2.1667 px
bad = 0


def post(path, body):
    req = urllib.request.Request(f"{BASE}{path}", data=json.dumps(body).encode("utf-8"),
                                 headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=600) as r:
        return json.loads(r.read().decode("utf-8"))


def step(sid, keys=None):
    return post(f"/api/simulate/session/{sid}/frames",
                {"count": 1, "first": keys or {}, "then": {}, "manual": True})


# W-089. Every session this suite opens is DELETED before it exits -- see the
# note in ballart-verify.py: a leaked session is background load for the NEXT
# suite, and 40 of them push the request latency from 1.6 ms to 143.9 ms.
_OPENED = []


def _drop(session_id):
    try:
        req = urllib.request.Request(f"{BASE}/api/simulate/session/{session_id}", method="DELETE")
        with urllib.request.urlopen(req, timeout=30) as r:
            r.read()
    except Exception:
        pass


def new():
    s = post("/api/simulate/session",
             {"path": MAP, "startX": 1710, "startY": -25,
              "weapon": "01302000.img", "invulnerable": True})
    sid = s["sessionId"]
    _OPENED.append(sid)
    post(f"/api/simulate/session/{sid}/weapon", {"equippedWeapon": "1302000"})
    for _ in range(200):
        f = step(sid)
        # W-064. The INDEX, not the id.
        #
        # This test map carries ELEVEN instances of 2220100 (measured through the
        # live frame: x = 2091, 2221, 2767, 2553, 2681, 2847, 3043, 2006, 2210,
        # 2451, 2435). Selecting by mobId therefore does not identify a mob --
        # only an ordinal does. This function used to return the mob NEAREST the
        # player and both sections below then re-found "the mob" as `ms[0]`, the
        # FIRST one with that id, which is a different animal.
        #
        # That is where 194.85px came from: a gap reported as "single-frame
        # drift" that was really the distance between instance 0 at x=2091 and
        # the instance at x=2221 -- and 194.85px in one frame is impossible
        # movement, since MaxWalkSpeed is 2.5px/frame. Which instance the setup
        # landed on decided whether the suite was green, which is why it passed
        # standalone three times and went red inside the gate.
        #
        # mob-attack-verify.ps1 already pins the index (`$sum.indexes[0]`) and has
        # never had this problem. Same fix, same reason.
        best, best_d, best_i = None, None, None
        for i, mob in enumerate(f.get("mobs") or []):
            if mob["mobId"] != MOB or not mob.get("onGround"):
                continue
            if abs(mob["y"] - f["y"]) > 100:
                continue
            d = abs(mob["x"] - f["x"])
            if best_d is None or d < best_d:
                best, best_d, best_i = mob, d, i
        if best is not None:
            dx = best["x"] - f["x"]
            if abs(dx) < 12:
                return sid, f, best_i
            step(sid, {"right": True} if dx > 0 else {"left": True})
    return sid, None, None


print("=== calibration: an UNHIT mob must get NO impulse ===")
# W-104 (2026-10-25, the gate's only red). The old control was "an UNHIT mob
# must not drift", and its premise was FALSE. The plain mob PATROLS its own
# foothold chain -- MapleBench/Api/SimulatorEndpoints.cs:222-227, "Mobs and NPCs
# patrol their own foothold chain. Seeded, so a map's answer is reproducible."
# -- so the control demanded the impossible, and it made the gate's verdict depend
# on which suites ran before it.
#
# It was also measuring the wrong channel. The assertion below reads the frame's
# own knockbackPx, the impulse the product computed (Mob.cpp:595-598), NOT the
# mob's x: a struck mob also walks toward its attacker (Mob.cs:434-460) and that
# walk is thousands of pixels, which is why the first version of this file could
# not see it. A control that says "the mob did not move" therefore guarded a
# quantity the assertion does not use.
#
# The control now says what the assertion needs: while NOTHING has been hit, NO
# mob reports an impulse. MEASURED 2026-10-25 over 20 phases x 40 idle frames
# (800 frames), the largest impulse reported was 0.000px, while those same frames
# gave idle drift of 0.000px in 19 phases and 420.609px in the twentieth -- a mob
# that leaves the map shifts every later index, so that drift was the distance
# between two different instances, not movement. The impulse channel is stable;
# the drift channel is not.
sid, f, idx = new()
before = f["mobs"][idx]["x"]
drift = 0.0
last = before
max_kb = 0.0
for _ in range(40):
    fr = step(sid)
    m = fr["mobs"][idx]
    drift = max(drift, abs(m["x"] - last))
    last = m["x"]
    for other in fr["mobs"]:
        kb = abs(other.get("knockbackPx") or 0.0)
        if kb > max_kb:
            max_kb = kb
print(f"  instance #{idx} at x={before:.2f}, largest single-frame drift over 40 idle frames: {drift:.3f}px")
print(f"  the largest knockback impulse ANY mob reported over those 40 frames: {max_kb:.3f}px")
if drift > 0.01:
    print("  (the mob PATROLLED, or the frame's mob list shifted under the measurement -")
    print("   the plain mob walks its own foothold chain, and a mob that leaves the map shifts")
    print("   every later index. Neither is a defect in the product, and the assertion below")
    print("   does not read the mob's x: it reads the frame's own knockbackPx.)")
if max_kb > 0.001:
    print(f"  >>> an UNHIT mob reported a {max_kb:.3f}px impulse, so 'it was knocked back' would prove nothing")
    bad += 1
else:
    print("  control holds: while nothing was hit, NO mob reported an impulse")

# THE POSITIVE CONTROL ARM. The check above ("no impulse while nothing was hit")
# would pass for a product that never reports an impulse at all, so the SAME
# reader, on the SAME channel, must go NON-ZERO the moment a mob is hit. This arm
# is where the control is ALLOWED to fail, and it must. If it does not, the
# calibration above is vacuous and that is a RED.
probe = 0.0
probe_at = -1
for attempt in range(4):
    sid2, f2, idx2 = new()
    if f2 is None:
        continue
    for k in range(45):
        fr2 = step(sid2, {"attack": True})
        hit = 0.0
        for other in fr2["mobs"]:
            kb = abs(other.get("knockbackPx") or 0.0)
            if kb > hit:
                hit = kb
        if hit > 0.001:
            probe = hit
            probe_at = k
            break
    if probe > 0.001:
        break
if probe > 0.001:
    print(f"  positive control: the same channel reads {probe:+.3f}px on a mob that WAS hit "
          f"(frame {probe_at}), so the control above would have fired on it")
else:
    print("  >>> the impulse channel reads 0.000 even on a mob that was hit, so the "
          "control above can never fail")
    bad += 1

print()
print("=== the hit recoil ===")
print(f"  reference: KBSPEED 130 px/s * {STEP_MS:.2f}ms = {EXPECT_PX:.3f}px, once, "
      f"sign = -KBSPEED when the mob faces right (Mob.cpp:597)")
print("  read from the frame's own `knockbackPx`, NOT from the mob's x: a struck mob")
print("  also walks toward its attacker (Mob.cs:434-460) and that walk is thousands")
print("  of pixels, which is why the first version of this file could not see it.")
for attempt in range(4):
    sid, f, idx = new()
    facing = f.get("facing")
    x0 = f["mobs"][idx]["x"]
    step(sid, {"attack": True})
    impulses = []
    for k in range(45):
        fr = step(sid)
        if idx >= len(fr["mobs"]):
            break
        m = fr["mobs"][idx]
        kb = m.get("knockbackPx") or 0.0
        # The sign is checked against the mob's OWN facing ON THE IMPULSE FRAME,
        # not against the facing before the swing: the struck mob is redirected
        # toward its attacker (Mob.cs:434-460), so the facing at TakeHit time is
        # not the facing the probe read a moment earlier.
        if abs(kb) > 0.001:
            impulses.append((k, kb, m.get("facing"), m.get("knockbackFacing"),
                             m.get("direction"), m["hp"]))
    if not impulses:
        continue
    k, kb, mface, mknockfacing, mdir, hp = impulses[0]
    print(f"  attempt {attempt}: instance #{idx} (x0={x0:.1f}), impulse on frame {k}: {kb:+.3f}px, "
          f"knockbackFacing={mknockfacing} mob facing now {mface} hp={hp}")
    print(f"    impulses seen in 45 frames: {len(impulses)}  (Mob.cpp:595 gates on "
          f"stance_entered, so exactly ONE per hit is the expectation)")
    # The SIGN is checked against the facing the impulse was COMPUTED from, which
    # the mob carries on the frame: the struck mob is redirected toward its
    # attacker (Mob.cs:434-460) on the same hit, so the facing reported a moment
    # later is not the one Mob.cpp:597 used. Asserting against the later facing is
    # asserting against a different value than the code used.
    sign_ok = (kb < 0) if mknockfacing == 1 else (kb > 0)
    print(f"    knockbackFacing={mknockfacing} (the facing the impulse used), "
          f"mob facing now {mface}")
    print(f"    away from the side it faced: {sign_ok}   (Mob.cpp:597)")
    if not sign_ok:
        print("    >>> the recoil went TOWARDS the attacker"); bad += 1
    if abs(abs(kb) - EXPECT_PX) > 0.05:
        print(f"    >>> {abs(kb):.3f}px vs the reference's {EXPECT_PX:.3f}px"); bad += 1
    if len(impulses) != 1:
        print(f"    >>> {len(impulses)} impulses for one hit"); bad += 1
    break
else:
    print("  >>> no impulse in 45 frames across 4 attempts")
    bad += 1

for _sid in _OPENED:
    _drop(_sid)

print(f"\nBAD={bad}")
raise SystemExit(1 if bad else 0)
