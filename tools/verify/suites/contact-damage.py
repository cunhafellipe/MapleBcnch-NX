"""contact-damage.py -- WHAT A MOB'S CONTACT HIT HURTS, judged as a distribution.

`attack-damage.py` judges the CHARACTER's swing: one attack, one number, one
expected value per slot. This file judges the other side of the same fight --
the mob's touch attack -- and it CANNOT be a per-value equality check, because
the reference has no single value there. It has a range.

THE REFERENCE'S RULES, transcribed from the clean-client tree (the authority).
This file reads no file of this repository at run time and opens no path outside
the tree: the rules are transcribed here, each with its file and line, so a
clone measures its own instance rather than a machine-local path.

  Mob.cpp:1850        if (!touchdamage) return MobAttack();
  Mob.cpp:1853          int32_t power     = watk + status_value(WATK);
  Mob.cpp:1854          int32_t maxattack = static_cast<int32_t>(power * attack_multiplier(false));
  Mob.cpp:1855          int32_t minattack = static_cast<int32_t>(maxattack * 0.8f);
  Mob.cpp:1856          int32_t attack    = randomizer.next_int(minattack, maxattack);
  Randomizer.h:72-73    if (from >= to) return from;
  Randomizer.h:75       std::uniform_int_distribution<T> range(from, to - 1);
  Mob.cpp:60          watk = info["PADamage"];
  node.cpp:155-157    if (!m_data) return def;      // an absent node is 0
  Mob.cpp:69          touchdamage = info["bodyAttack"].get_bool();

Two terms are the port's DECLARED differences and are NOT judged here, because
this port has no stat sheet and the lead's brief says not to touch them:
`status_value(WATK)` is 0 and `attack_multiplier(false)` is 1.0. With those,
`power == maxattack == watk`, so the reference's range for one hit is

    [ floor(0.8 * PADamage) , PADamage - 1 ]        (the upper bound is EXCLUSIVE)
    { floor(0.8 * PADamage) }                        when floor(0.8*PADamage) >= PADamage

which for PADamage = 0 is the single value 0, and for PADamage <= 5 is also a
single value -- the reference genuinely cannot vary those, so a judge that
demanded variation there would be demanding something the client cannot do.

WHAT IT ASSERTS:

  A1  every hit the port lands is INSIDE that range, where PADamage is read from
      the ARCHIVE (`/api/inspect` on `<mob>.img/info`), never from the port.
      This is the rule 15 obligation: an expected value taken from the subject
      under test cannot fail.
  A2  when the range holds two or more numbers, the hits are not all equal.
      This is not a second admissible answer -- it is what A1's range means. It
      is the half that a build which FREEZES the number cannot pass.
  A3  a mob whose PADamage is 0 lands 0 on every hit. The old build's
      `Math.Max(1, pad)` turned the archive's own 0 into a 1, so 25 of the 1,862
      mobs that carry `info/bodyAttack` hit for 1 point.

HOW A HIT IS RECOGNISED, which matters for A3: `playerDamage` is 0 both when no
mob touched and when a 0-damage mob touched, so it cannot see A3 on its own.
The session's own touch log (`/api/simulate/debug/mob-swing`) carries the
cooldown the toucher had BEFORE the hit (`cd=`), written before the roll, and
the toucher's own position. A touch frame whose entry names the mob under test
and whose `cd=0` is the frame the hit landed on. Both halves are needed: the
map carries its own mobs, `find_colliding` takes the FIRST overlapping one,
and a hit recorded without the position check can belong to a mob this judge
never summoned (measured: a touch attributed to a PADamage-0 mob was really
the map's PADamage-90 mob). That is what makes A3 a measurement rather than a
tautology.

IT OPENS NO ARCHIVE, SAVES NOTHING, AND WRITES NOTHING.
"""
import json
import os
import re
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
from concurrent.futures import ThreadPoolExecutor

try:
    from archivelib import archive_id, require_gate_base
except ImportError:            # a missing module must not read as a pass
    print(">>> RED: contact-damage -- archivelib.py is missing next to this suite")
    raise SystemExit(1)
except SyntaxError as exc:    # a helper that does not PARSE is not missing
    print(f">>> RED: contact-damage -- archivelib.py next to this suite does not parse: {exc}")
    raise SystemExit(1)

# The gate's own override, so a gate pointed at another instance reaches this
# judge too (the same two lines the other suites use).
BASE = require_gate_base()

# The backend serialises node reads behind one lock, so the census is a few
# thousand small reads and the thread count is small on purpose.
THREADS = 8

# How many hits to collect per mob. The cooldown is 60 frames
# (MobPatrol.AttackCooldown), so this is ~61 frames per hit.
HITS = 8

# How many PLACEMENTS to draw before the sample is called too small to judge.
#
# The mob under test is SUMMONED, and the summon endpoint takes no position --
# "N mobs at a random x within 100px of the character"
# (MapleBench/Api/SimulatorEndpoints.cs:451-455) -- so WHERE it lands decides
# how long the character can stay in contact with it, and that decides HOW
# MANY hits there are to judge.  MEASURED 2026-10-25 over 10 runs of this
# file against one instance, sample 1210103 (PADamage 80): 8 hits in 8 runs,
# 2 hits in one run, 3 hits in another.
#
# A2 asks "are the hits all one number?".  On a 2-hit sample that question is
# a coin flip -- the range holds 16 numbers, so two equal hits happen 1 time
# in 16 -- and the failure reports "a frozen build" for a build that is fine.
# The verdict then follows the DRAW rather than the build.
#
# => The sample size is CONTROLLED: a placement that cannot produce HITS hits
# is RE-DRAWN (a fresh session is a fresh placement), and a placement that
# still cannot is a NAMED failure about the placement -- never a verdict about
# the damage rule, and never A2 on a short sample.
PLACEMENT_ATTEMPTS = 6

# The map to fight on. Same one the other simulation suites use: it is the one
# this repository's own harnesses already know how to spawn on.
MAP_IMAGE = "Map/Map1/100000004.img"
MAP_START_X = 1710
MAP_START_Y = -25

asserts = 0
fails = 0


def Check(name, ok, detail=""):
    """One assertion. The red marker is printed on every failure, not once."""
    global asserts, fails
    asserts += 1
    if ok:
        print(f"  ok    {name}")
        return True
    fails += 1
    print(f"  FAIL  {name} -- {detail}")
    print(f">>> RED: contact-damage -- {name} ({detail})")
    return False


def Red(message):
    """A failure that is not an assertion: this judge cannot run."""
    print(f">>> RED: contact-damage -- {message}")
    print("BAD=1")
    raise SystemExit(1)


def _fatal(exc_type, exc, tb):
    """Rule 14: a suite that aborts without a marker reads as a pass.

    A traceback with no `>>> RED` and no `BAD=` in the output leaves the gate's
    grep with nothing to find, and the gate prints `ok` for a suite that never
    measured anything. So every uncaught exception becomes a red marker and a
    non-zero exit. MEASURED: a census of 2,100 images over 8 threads hit
    WinError 10055 (the connection was aborted) and the judge died with a
    traceback and no marker.
    """
    print(f">>> RED: contact-damage -- unexpected failure: "
          f"{exc_type.__name__}: {exc}")
    print("BAD=1")
    sys.exit(1)


sys.excepthook = _fatal


def _open(req, tries=4):
    """urlopen with a retry, because a busy instance aborts a connection.

    The census opens eight at once, and the instance behind it is a
    single-process server: MEASURED 2026-10-22, one of eight reads came back
    WinError 10055. A transient abort is not a measurement, so it is retried
    rather than reported; a persistent one is a red marker.
    """
    last = None
    for attempt in range(tries):
        try:
            with urllib.request.urlopen(req, timeout=600) as r:
                return r.read()
        except urllib.error.HTTPError:
            raise
        except (urllib.error.URLError, OSError) as exc:
            last = exc
            time.sleep(0.4 * (attempt + 1))
    raise last


def get(route, **q):
    url = BASE + route + ("?" + urllib.parse.urlencode(q) if q else "")
    try:
        return json.loads(_open(urllib.request.Request(url)).decode("utf-8"))
    except urllib.error.HTTPError as e:
        if e.code == 404:
            return None
        raise


def post(route, body):
    req = urllib.request.Request(
        BASE + route, data=json.dumps(body).encode("utf-8"),
        headers={"Content-Type": "application/json"})
    try:
        return json.loads(_open(req).decode("utf-8"))
    except urllib.error.HTTPError as e:
        detail = e.read().decode("utf-8", "replace")[:200]
        Red(f"POST {route} returned HTTP {e.code}: {detail}")


def need(obj, field, where):
    """Rule 9: a field that is not there and a field that is 0 look the same."""
    if not isinstance(obj, dict) or field not in obj:
        Red(f"{where} carried no {field!r} field, so this judge cannot read it")
    return obj[field]


# --------------------------------------------------------------------------- #
# the reference's value readers, transcribed
# --------------------------------------------------------------------------- #
def node_int(kind, value):
    """nl::node::get_integer -- vendor/NoLifeNx/nlnx/node.cpp:155-173.

    An absent node is 0, an integer is itself, a real is truncated, and a STRING
    is parsed with std::stoll -- so "85" is the number 85. A string that does
    not start with a number makes the reference THROW; 0 is returned for it
    here, which is the value such a node can carry anyway.
    """
    if kind in (None, "Null"):
        return 0
    if kind in ("Int", "Short", "Long", "Byte", "SByte", "UShort", "UInt",
                "ULong", "UByte"):
        try:
            return int(str(value).strip())
        except (TypeError, ValueError):
            return 0
    if kind in ("Double", "Float", "Single"):
        try:
            return int(float(str(value).strip()))
        except (TypeError, ValueError):
            return 0
    if kind == "String":
        m = re.match(r"\s*([+-]?[0-9]+)", str(value or ""))
        return int(m.group(1)) if m else 0
    return 0


def read_info(archive, image):
    """(bodyAttack, PADamage) for one mob image, read from the ARCHIVE."""
    doc = get("/api/inspect", path=f"{archive}/{image}/info")
    fields = {}
    for c in (doc or {}).get("children") or []:
        fields[c.get("name")] = (c.get("type"), c.get("value"))
    ba = fields.get("bodyAttack")
    pd = fields.get("PADamage")
    # Mob.cpp:69 `get_bool()` is false for anything that is not a non-zero
    # integer (node.cpp:228/231).
    body_attack = 1 if (ba and node_int(*ba) != 0) else 0
    return body_attack, (node_int(*pd) if pd else 0)


def reference_hits(pad):
    """The set of numbers the reference can produce for one hit.

    Returns (low, high, varies): the inclusive bounds of the range, and whether
    the range holds more than one number. `varies` is False when the reference
    itself cannot vary, so that A2 is never asked for something the client
    cannot do.
    """
    low = int(pad * 0.8)          # Mob.cpp:1855, static_cast<int32_t> truncates
    if low >= pad:                # Randomizer.h:72-73 -- returns `from`
        return low, low, False
    # `to - 1` is the last drawable value, so the range holds `pad - low` of
    # them. PADamage = 1 draws from [0, 0]: the reference cannot vary it, and a
    # judge that demanded variation there would demand what the client cannot do.
    return low, pad - 1, (pad - low) >= 2


# --------------------------------------------------------------------------- #
# the census: which mobs carry the flag, and what their PADamage is
# --------------------------------------------------------------------------- #
print("=== census: info/bodyAttack and info/PADamage, read from the archive ===")
MOB = archive_id("Mob.nx", BASE)
MAPA = archive_id("Map.nx", BASE)
MAP = f"{MAPA}/{MAP_IMAGE}"

listing = get("/api/children", path=MOB)
if not listing:
    Red(f"/api/children returned nothing for {MOB}, so the mob list is unknown "
        f"and every count below would be a count of nothing")
images = [c["name"] for c in listing
          if isinstance(c, dict) and str(c.get("name", "")).endswith(".img")]
if not images:
    Red(f"{MOB} lists no .img children, so there is nothing to judge")
# Rule 18: a census reports its own denominator and its truncation flag. This
# route has no limit parameter, so the listing is the whole archive -- but that
# is a claim, so it is printed rather than assumed.
print(f"  scanned        = {len(images)} mob images listed by /api/children")
print(f"  truncated      = False (the route takes no limit parameter)")
print(f"  archive        = {MOB} resolved BY NAME from /api/files")


def read_row(image):
    try:
        return (image,) + read_info(MOB, image)
    except urllib.error.HTTPError as e:
        return (image, -1, -1, str(e))


t0 = time.time()
with ThreadPoolExecutor(max_workers=THREADS) as ex:
    rows = list(ex.map(read_row, images))
print(f"  read in        = {time.time() - t0:.1f}s")

flagged = [r for r in rows if r[1] == 1]
zero = [r for r in flagged if r[2] == 0]
negative = [r for r in rows if r[2] < 0]
print(f"  carry bodyAttack = {len(flagged)} of {len(rows)}")
print(f"  of those, PADamage == 0 = {len(zero)}")
print(f"  PADamage < 0            = {len(negative)} "
      f"({'names: ' + ', '.join(r[0] for r in negative[:5]) if negative else 'none'})")

if not flagged:
    Red("no mob in this archive carries info/bodyAttack, so no contact hit can "
        "be measured at all")

# --------------------------------------------------------------------------- #
# sample selection, BY RULE and from the census -- not a hand-picked list
# --------------------------------------------------------------------------- #
damages = sorted({r[2] for r in flagged})
picks = []

# 1. the zero case (A3): the smallest PADamage there is, which is 0 when the
#    archive has one.
if zero:
    picks.append(("PADamage = 0 (A3: must land 0)", sorted(zero)[0][0]))

# 2. the tightest range that can still catch a FROZEN build without being able
#    to flake. A range holding N numbers comes up all-equal in N^-7 of runs at
#    HITS = 8, so a two-number range (PADamage = 6) would be wrong ~1 run in 130
#    -- and a judge whose answer changes from run to run is not a judge. Sixteen
#    numbers is 1 run in 2.7 billion.
variants = [p for p in damages
            if reference_hits(p)[1] - reference_hits(p)[0] + 1 >= 16]
if variants:
    p = variants[0]
    picks.append((f"tightest range that cannot flake (PADamage = {p})",
                  sorted(r[0] for r in flagged if r[2] == p)[0]))

# 3. the median flagged PADamage.
p = damages[len(damages) // 2]
picks.append((f"median flagged PADamage (= {p})",
              sorted(r[0] for r in flagged if r[2] == p)[0]))

# 4. the widest range below 1000, so the sample spans a big spread without
#    summoning a boss-sized sprite.
big = [x for x in damages if x <= 1000]
if big:
    p = big[-1]
    picks.append((f"widest range under PADamage 1000 (= {p})",
                  sorted(r[0] for r in flagged if r[2] == p)[0]))

# The same mob twice would measure it twice; keep the first of each PADamage.
seen_pad = set()
samples = []
for why, image in picks:
    _, pad = read_info(MOB, image)
    if pad in seen_pad:
        continue
    seen_pad.add(pad)
    samples.append((why, image, pad))

print("\n=== samples (PADamage read from the archive, not from the port) ===")
for why, image, pad in samples:
    lo, hi, varies = reference_hits(pad)
    print(f"  {image:<14} PADamage={pad:<6} reference range=[{lo}, {hi}]"
          f"  varies={varies}   ({why})")

# --------------------------------------------------------------------------- #
# the measurement
# --------------------------------------------------------------------------- #
def start_session():
    s = post("/api/simulate/session",
             {"path": MAP, "startX": MAP_START_X, "startY": MAP_START_Y,
              "weapon": "01302000.img", "invulnerable": True})
    return need(s, "sessionId", "POST /api/simulate/session")


def summon(sid, mob_id):
    r = post(f"/api/simulate/session/{sid}/summon", {"mobId": mob_id, "count": 1})
    if not r.get("spawned"):
        Red(f"the simulator refused to summon {mob_id}: "
            f"{json.dumps(r.get('rejected') or r)[:200]}")
    return r


def step(sid, keys=None):
    return post(f"/api/simulate/session/{sid}/frames",
                {"count": 1, "first": keys or {}, "then": {}})


def touch_log(sid):
    d = get("/api/simulate/debug/mob-swing", sessionId=sid)
    return d.get("mobs") or []


def mob_pos(mob):
    """The mob's own position, as the log prints it.

    `MapSimSessionStore.cs:1925` writes `"{X:F0},{Y:F0} ... TOUCH"`, so the
    toucher's rounded position is IN the entry. Returned as a tolerance window
    rather than a string: C# `:F0` rounds halves away from zero and Python's
    `:0f` rounds them to even, so the two can disagree by one on an exact .5.
    """
    return (float(mob["x"]), float(mob["y"]))


def same_mob(entry, mob):
    m = re.match(r"^(-?\d+),(-?\d+)\s+", entry)
    if not m:
        return False
    mx, my = mob_pos(mob)
    # F0 prints the nearest integer, so +/-1.5 covers every rounding difference.
    return abs(int(m.group(1)) - mx) <= 1.5 and abs(int(m.group(2)) - my) <= 1.5


def is_hit_frame(sid, mob, frame):
    """True when THIS mob's box was the one that landed a hit on this frame.

    Two things have to be true, and both were measured rather than assumed:

    1. the toucher is the mob under test. The map carries its own mobs, and
       `find_colliding` returns the FIRST overlapping one
       (MapMobs.cpp:255-268), so a hit can belong to a mob this judge never
       summoned. MEASURED: with `9300162` (PADamage 0) under test, one touch
       was recorded as 90 -- the map's own mob. The log entry carries the
       toucher's rounded X,Y and the frame carries the summoned mob's, so the
       two are matched instead of assumed.
    2. the cooldown was 0. The entry is written BEFORE the roll
       (`MapSimSessionStore.cs:1925` then `:1926`) and prints the cooldown the
       toucher had at that moment, so `cd=0` is the frame the hit landed on.
       This is the only way to see a hit that deals 0 -- the whole A3 case.

    The log holds THIS frame's touches and nothing else: it is CLEARED at the
    start of every frame (`MapSimSessionStore.cs:1840`) and refilled only
    inside `if (toucher is not null)` (`:1930`).  So a touch the judge sees
    here happened on the frame it is reading, not on an earlier one.
    """
    if mob is None:
        return False
    for entry in reversed(touch_log(sid)):
        if not entry.endswith("TOUCH") or not same_mob(entry, mob):
            continue
        m = re.search(r"\bcd=(-?\d+)", entry)
        return bool(m) and int(m.group(1)) == 0
    return False


def close_in(sid, mob_id, limit=500):
    """Walk the character until the mob's box overlaps its swept body rect."""
    for _ in range(limit):
        f = step(sid)
        mob = next((m for m in (f.get("mobs") or []) if m["mobId"] == mob_id), None)
        if mob is None:
            continue
        dx = mob["x"] - f["x"]
        if abs(dx) > 24:
            step(sid, {"right": True} if dx > 0 else {"left": True})
            continue
        if is_hit_frame(sid, mob, f):
            return True
    return False


def collect(sid, mob_id, wanted, limit=4000):
    """Every hit's damage, in order, until `wanted` of them are in."""
    hits = []
    touches = 0
    for _ in range(limit):
        f = step(sid)
        need(f, "playerDamage", "the session frame")
        mob = next((m for m in (f.get("mobs") or []) if m["mobId"] == mob_id), None)
        if is_hit_frame(sid, mob, f):
            touches += 1
            hits.append(int(f["playerDamage"]))
            if len(hits) >= wanted:
                return hits, touches
        if mob is not None:
            dx = mob["x"] - f["x"]
            if abs(dx) > 24:
                step(sid, {"right": True} if dx > 0 else {"left": True})
    return hits, touches


def drop(sid):
    try:
        urllib.request.urlopen(urllib.request.Request(
            f"{BASE}/api/simulate/session/{sid}", method="DELETE"), timeout=60)
    except Exception:
        pass          # a session that will not delete is not a verdict


print("\n=== every hit must be inside the reference's range (A1) ===")
for why, image, pad in samples:
    mob_id = image[:-4] if image.endswith(".img") else image
    lo, hi, varies = reference_hits(pad)
    # THE SAMPLE IS A MEASURED PRECONDITION, not an assumption.  See
    # PLACEMENT_ATTEMPTS above: the placement is a draw, the number of hits
    # follows it, and A2 must not be judged on a sample the suite did not
    # control.
    #
    # The session lives in the try so that a RE-DRAWN placement can replace
    # it: a fresh session is a fresh placement, because the summon endpoint
    # puts the mob at a random x within 100px of the character.
    sid = start_session()
    try:
        hits, touches, attempts = [], 0, 0
        touched = False
        while attempts < PLACEMENT_ATTEMPTS:
            attempts += 1
            if attempts > 1:
                drop(sid)
                sid = start_session()
            summon(sid, mob_id)
            if not close_in(sid, mob_id):
                continue
            touched = True
            hits, touches = collect(sid, mob_id, HITS)
            if len(hits) >= HITS:
                break

        if not touched:
            Check(f"{mob_id} ever touched the character", False,
                  f"no touch in 500 frames on any of {attempts} placement(s); "
                  f"the range [{lo}, {hi}] was never measured")
            continue
        if len(hits) < HITS:
            Check(f"{mob_id} ({pad}): the sample reached {HITS} hits", False,
                  f"only {len(hits)} hit(s) could be collected on {attempts} "
                  f"placement(s): {hits}; the placement did not allow a "
                  f"sustained contact, so the range [{lo}, {hi}] was measured "
                  f"on a sample too small to judge -- this is not a verdict "
                  f"about the damage rule")
            continue
        print(f"  {mob_id}: PADamage={pad} range=[{lo}, {hi}] "
              f"hits={hits} (touches={touches}, placements={attempts})")
        outside = [h for h in hits if h < lo or h > hi]
        Check(f"{mob_id} ({pad}): {len(hits)} hits all inside [{lo}, {hi}]",
              hits and not outside,
              f"outside the reference range: {outside}" if hits else
              "no hit was observed, so nothing was measured")
        if varies and hits:
            Check(f"{mob_id} ({pad}): the hits are not all one number",
                  len(set(hits)) > 1,
                  f"every hit was {hits[0]}; the reference's range holds "
                  f"{hi - lo + 1} numbers, so a frozen build cannot pass")
    finally:
        drop(sid)

# --------------------------------------------------------------------------- #
print()
print(f"asserts={asserts} fails={fails}")
print(f"BAD={fails}")
if fails:
    print(">>> RED: contact-damage -- see the FAIL lines above")
    raise SystemExit(1)
print("PASS contact-damage: every contact hit the port lands is inside the "
      "range the reference can produce for that mob")