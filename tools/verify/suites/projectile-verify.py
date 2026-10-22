"""The type-2 PROJECTILE, as the reference flies it.

MobAttackGeometry.h:104-105 -- the reach cone starts at `launch(origin, flipped)`,
the attack's OWN source point mirrored by the facing, and not at the mob.
MobAttack.cpp:150-157, 202-211:
    entry.start    = data.geometry.launch(origin, flipped && !noflip)
    dx = destination.x() - start.x();  dy = destination.y() - 25 - start.y();
    entry.finish   = start + normalise(dx, dy) * data.geometry.radius;
    entry.lifetime = max(1, data.geometry.radius * 600 / data.bullet_speed);   // ms
    ...
    t       = min(1, (elapsed - delay) / lifetime);
    current = lerp(start, finish, t);
    expanded= {target.left-8, target.right+8, target.top-8, target.bottom+8};
    hit     = MobAttackGeometry::crosses(entry->previous, current, expanded);
    entry->previous = current;   expired = t >= 1.0;
Stage.cpp:792 -- the box it is tested against is {x-10, x+10, y-50, y}.

So px/sec = bullet_speed / 0.6, and the hit is a SWEPT SEGMENT -- a fast projectile
crosses a 20px character between two ticks.

MEASURED on the archive: `ball` on 172 of 1536 attack slots, `bulletSpeed` on 77,
ten distinct values (200, 140, 230, 170, 250, 150, -200, 350, 260, 120) and the
`range` is `{r, sp}` on exactly 326 slots.
"""
import json, os, urllib.parse, urllib.request

BASE = os.environ.get("GATE_BASE") or "http://127.0.0.1:5591"
STEP_MS = 1000 / 60
bad = 0


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


# ---------------------------------------------------------------- the data
#
# The SURVEY is measured every run, on a 60-mob stride. It was 200 before, and a
# full-catalog fighter search ran every run too -- about 2,250 HTTP requests per
# run, which is enough to exhaust the machine's ephemeral ports inside four runs
# (WinError 10048) and take the rest of the gate down with it. The fighter CHOICE
# is now cached to fighter.json and re-validated against the live server in three
# requests, which keeps the measurement and drops the load.
print("=== which mobs have a projectile attack, and what does it declare ===")
cat = get("/api/simulate/mob-catalog")["mobs"]
ids = [m["id"] for m in cat]
sample = ids[::max(1, len(ids) // 60)][:60]
proj = []
for mid in sample:
    for a in get("/api/simulate/mob-attacks", mobId=mid)["attacks"]:
        if a.get("type") == 2:
            proj.append((mid, a))

print(f"  {len(proj)} type-2 attack(s) in {len(sample)} sampled mobs")
if not proj:
    print("  >>> none in this sample; the rest of this file is void")
    raise SystemExit(1)

with_ball = sum(1 for _, a in proj if a.get("bulletSpeed"))
print(f"  carrying bulletSpeed: {with_ball}/{len(proj)}")
for mid, a in proj[:6]:
    r = a.get("radius") or 0
    bs = a.get("bulletSpeed") or 100
    life = max(1, r * 600.0 / bs)
    print(f"    {mid} {a['action']}: radius={r} source=({a.get('sourceX')},{a.get('sourceY')}) "
          f"bulletSpeed={bs} power={a.get('power')} delay={a.get('delayMs')} "
          f"-> life={life:.0f}ms = {r / (life / 1000.0):.1f} px/s (bulletSpeed/0.6 "
          f"= {bs / 0.6:.1f})")

# The DERIVED speed, restated: it is bulletSpeed/0.6 px/s, NOT bulletSpeed.
wrong = [(mid, a) for mid, a in proj
         if a.get("radius") and a.get("bulletSpeed")
         and abs((a["radius"] / ((max(1, a["radius"] * 600.0 / a["bulletSpeed"])) / 1000.0))
                 - a["bulletSpeed"] / 0.6) > 1.0]
print(f"  entries where px/s != bulletSpeed/0.6: {len(wrong)}  "
      f"(0 means the derivation holds on all of them)")

# THE CONE MUST START AT THE ATTACK'S SOURCE POINT. A mob whose source.x is far from
# zero is the only place this shows: with the cone drawn from the mob's own feet it
# reaches ~100px further than the reference, so it fires at a range it must not.
off = [(mid, a) for mid, a in proj if abs(a.get("sourceX") or 0) >= 40]
print(f"  entries whose source.x is >= 40px from the mob: {len(off)}/{len(proj)}"
      f"  (the reach cone is drawn from the source point, not from the mob)")
if not off:
    print("  >>> no mob in this sample with a distant source point; the cone test "
          "below would prove nothing")

# ---------------------------------------------------------------- the flight
#
# A projectile is watched, not inferred. The only mobs that cannot touch the
# character (`info/bodyAttack` absent) and still carry a type-2 attack with real
# power are 9500307 and 9500308 -- and BOTH have frames with no `lt`/`rb`, which
# `Frame::get_bounds` (Animation.cpp:58-61, `bounds = src`) reads as a POINT at
# the mob's own feet. A point box is unreachable by the player's swing (whose box
# sits ~15px above its own feet) and `ApplySkillHits` skips `IsEmpty` boxes, so
# neither mob can be woken at all -- in the reference too, not only here.
#
# So the fight is run against a mob that self-acquires (`info/firstAttack`), on a
# character that will die, and the EVIDENCE is the entry itself: it sits at its
# launch point for the whole delay and then moves. The character's HP is never the
# signal, because contact damage cannot be told apart from the shot by amount.
print()
print("=== the entry itself: frozen for the delay, then moving ===")
from concurrent.futures import ThreadPoolExecutor

FIGHTER_CACHE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "fighter.json")


def node_value(path):
    try:
        return get("/api/inspect", path=path)["node"].get("value")
    except Exception:
        return None


def fighter_of(mob_id):
    """The mob this run fights: one type-2 attack, real power, a real box, and
    `info/firstAttack` so it acquires without a blow landing first."""
    try:
        atks = get("/api/simulate/mob-attacks", mobId=mob_id)["attacks"]
    except Exception:
        return None, None
    proj = [a for a in atks if a["type"] == 2 and a.get("power") and a.get("radius", 0) > 0]
    if len(proj) != 1:
        return None, None                 # one projectile attack: the timeline is unambiguous
    try:
        f0 = get("/api/inspect", path=f"f5/{mob_id}.img/stand/0")
    except Exception:
        return None, None
    kids = {c["name"] for c in f0.get("children", [])}
    if not ("lt" in kids and "rb" in kids):
        return None, None                 # a point box can never be hit or touched
    if str(node_value(f"f5/{mob_id}.img/info/firstAttack")) != "1":
        return None, None                 # must acquire on its own, no blow needed
    return mob_id, proj[0]


FIGHTER, spec = None, None
if os.path.exists(FIGHTER_CACHE):
    try:
        with open(FIGHTER_CACHE, encoding="utf-8") as fh:
            FIGHTER, spec = fighter_of(json.load(fh)["mobId"])
    except Exception:
        FIGHTER, spec = None, None

if FIGHTER is None:
    def attacks_of(mob_id):
        try:
            return mob_id, get("/api/simulate/mob-attacks", mobId=mob_id)["attacks"]
        except Exception:
            return mob_id, []

    print("  (no usable fighter cached; sweeping all %d mobs -- once)" % len(ids))
    with ThreadPoolExecutor(max_workers=8) as pool:
        for mob_id, atks in pool.map(attacks_of, ids):
            if FIGHTER is not None:
                break
            proj = [a for a in atks
                    if a["type"] == 2 and a.get("power") and a.get("radius", 0) > 0]
            if len(proj) != 1:
                continue
            found, chosen = fighter_of(mob_id)
            if found:
                FIGHTER, spec = found, chosen
    if FIGHTER is not None:
        with open(FIGHTER_CACHE, "w", encoding="utf-8") as fh:
            json.dump({"mobId": FIGHTER, "why": "one type-2 attack with power, a stand/0 "
                                                 "carrying lt+rb, and info/firstAttack=1"},
                      fh)

if FIGHTER is None:
    print("  >>> no self-acquiring single-projectile mob with a real box in the catalog")
    bad += 1
    raise SystemExit(1)

mid = FIGHTER
delay_frames = (spec.get("delayMs") or 0) / STEP_MS
life = max(1, (spec.get("radius") or 1) * 600.0 / (spec.get("bulletSpeed") or 100))
life_frames = life / STEP_MS
earliest = delay_frames + life_frames
print(f"  {mid} {spec['action']}: type={spec['type']} power={spec['power']} "
      f"radius={spec['radius']} bulletSpeed={spec['bulletSpeed']} "
      f"source=({spec['sourceX']},{spec['sourceY']}) delay={spec['delayMs']}ms")
print(f"    delay {delay_frames:.0f} frames + flight {life_frames:.0f} frames "
      f"= {earliest:.0f} frames from the begin_attack before it can touch anything")

# An EMPTY map. MEASURED: `f4/Map/Map0/000000000.img` starts the character at
# (0, 95) with zero mobs and a foothold underfoot; `f4/Map/Map1/100000004.img`, the
# map every earlier probe used, carries 24 map mobs whose contact hits land on top
# of the one under test.
#
# `invulnerable` is not a convenience here, it is required. MEASURED on 3220000 on
# this very map: contact damage killed the 50 HP character on frame 15, and the
# reference's first attack DECISION is not due until 400ms (MobAttack.cpp:52-53,
# decision_elapsed starts at 0) -- frame 24. A character that dies on frame 15 can
# never observe a projectile, and neither can it observe the decision cadence. The
# evidence below is the ENTRY's own position and clock, which `Hurt` does not touch.
s = post("/api/simulate/session",
         {"path": "f4/Map/Map0/000000000.img",
          # `weapon` is the SWING BOX, `equippedWeapon` is the WZ image that decides
          # which swing STANCES exist. Omit the second and `AttackStancesFor` is
          # empty, `Swing` arms nothing, and there is no weapon box at all.
          "weapon": "1302000.img", "equippedWeapon": "1302000",
          "invulnerable": True})
sid = s["sessionId"]
post(f"/api/simulate/session/{sid}/summon", {"mobId": mid, "count": 1})


def mob_of(fr):
    for m in fr.get("mobs") or []:
        if m.get("mobId") == mid:
            return m
    return None


# STEP THE CHARACTER BACK before watching. MEASURED: `summon` drops the mob within
# 100px of the player and it then walks in until `ChaseReach` (20px), so a shot is
# BORN 6px from the player -- inside the 20x50 hit box `Stage.cpp:792` uses -- and
# resolves on the very tick its delay expires. Two runs in eight recorded three
# shots of exactly 79 frames each and no travel at all, and that is the code
# behaving correctly on a shot that was born inside its target, not a cone.
# Backing off to 150px makes the mob fire from ~96px out, where the shot has to
# cross something.
print()
print("=== back off so the mob has to fire from a distance ===")
TARGET = 130
fr = post(f"/api/simulate/session/{sid}/frames", {"count": 1, "first": {}, "then": {}})
m0 = mob_of(fr)
away_right = fr["x"] <= (m0 or {"x": 0})["x"]
for _ in range(90):
    fr = step(sid, {"right": away_right, "left": not away_right})
    m = mob_of(fr)
    if m is None or not fr["onGround"]:
        break
    if abs(m["x"] - fr["x"]) >= TARGET:
        break
m = mob_of(fr) or {"x": fr["x"]}
gap = abs(m["x"] - fr["x"])
print(f"  player x={fr['x']:.0f}, mob x={m['x']:.0f}, {gap:.0f}px apart")
if gap < 100:
    print(f"  >>> only {gap:.0f}px of room; a shot born that close lands inside the "
          f"character and this run cannot judge the flight")
    bad += 1

# Let any shot the mob already launched finish BEFORE recording starts. Recording
# mid-shot makes the first recorded entry look as though it moved after 11 or 29
# frames, when in truth it had been flying since before the loop began -- the
# series then judges a tail as if it were a beginning.
for _ in range(200):
    fr = step(sid)
    m = mob_of(fr)
    if m is None or m.get("attackEntryCount", 0) == 0:
        break


hp0 = s["status"]["hp"]
# The whole flight is recorded, not a latch. A latch on "first frame the entry
# moved" is fragile in a way that hid a working projectile: an entry that HITS is
# removed and the mob fires again from the same spot, so consecutive entries sit at
# the same x and the difference never appears. Judging on the recorded series
# cannot be fooled that way.
flight = []
hits = []
began = None

for i in range(700):
    fr = step(sid)
    m = mob_of(fr)
    if m is not None:
        # Only a TYPE-2 entry has a flight; a type-0 attack pushes an entry too, and
        # its position is a box that does not move, so it would be judged on the
        # wrong thing. MEASURED: 3220000 opens with attack1 (type 0, delay 1500ms)
        # and that entry is what the first version of this file latched onto.
        if m.get("attackEntryCount", 0) > 0 and m.get("attackEntryType") == 2:
            flight.append((i, m["attackEntryX"], m["attackEntryY"],
                           m["attackEntryElapsedMs"], m["attackEntryDelayMs"],
                           m["attackEntryLifetimeMs"], m["x"]))
        if began is None and m["stance"] == spec["action"]:
            began = i
    dmg = fr.get("playerDamage") or 0
    if dmg:
        hits.append((i, dmg))

# Split the recording into separate shots: a gap in `attackEntryCount` means the
# previous entry resolved (hit or expiry) and this is a new one.
shots = []
for row in flight:
    if not shots or row[0] != shots[-1][-1][0] + 1:
        shots.append([row])
    else:
        shots[-1].append(row)

print()
if not shots:
    print(f"  >>> {mid} never put a type-2 attack entry in flight in 700 frames")
    bad += 1
    raise SystemExit(1)

print(f"  {len(shots)} type-2 shot(s) recorded; "
      f"lengths {[len(s) for s in shots]}")
for shot in shots[:3]:
    row = shot[0]
    print(f"    frame {row[0]}: x={row[1]:.1f} y={row[2]:.1f} elapsed={row[3]:.0f}ms "
          f"delay={row[4]:.0f}ms lifetime={row[5]:.0f}ms mobX={row[6]:.0f} "
          f"-> launch point {row[1] - row[6]:+.0f}px from the mob")

# Judge EVERY shot that was captured WHOLE, and COUNT what disagrees.
#
# A shot captured whole has its first frame = the frame the entry was created
# on, which the frame reports as `elapsed = 17ms` (one fixed step). A shot
# captured mid-flight has a larger elapsed and would be read as having "moved
# after 11 frames" when it had simply been flying before the loop began.
#
# The verdict used to be read off the FIRST such shot, and THIS FILE records
# why that is a coin toss: a shot that HITS stops early, and how early depends
# on which side of the character the mob landed on -- `summon` picks x
# uniformly in (player-100, player+100). A shot born inside the character
# never moves at all, so the FIRST one can be unjudgeable while the others are
# fine -- and the same file's note below says the first-shot judgement "went
# red on roughly one run in three".
whole = [s for s in shots if s[0][3] <= 2 * STEP_MS]
if not whole:
    print(f"  >>> no shot was captured from the frame it was created on "
          f"(first elapsed values {[s[0][3] for s in shots]}ms)")
    bad += 1
    raise SystemExit(1)
print(f"  the archive's attackAfter for this attack is {spec['delayMs']}ms = "
      f"{delay_frames:.0f} frames")
judged = 0
frozen = 0
off_delay = []
for shot in whole:
    launch_x = shot[0][1]
    moved = next((r for r in shot if abs(r[1] - launch_x) > 0.5), None)
    if moved is None:
        # Born inside the character: it never had room to travel, so there is
        # nothing to compare its delay with. A fact about the fight, not a
        # defect -- as long as some other shot CAN be judged.
        frozen += 1
        continue
    judged += 1
    held = moved[0] - shot[0][0]
    if abs(held - delay_frames) > 3:
        off_delay.append((shot[0][0], held))
    if judged <= 3:
        print(f"  the shot from frame {shot[0][0]} stayed at the launch point "
              f"until frame {moved[0]}, i.e. for {held} frames "
              f"({held * STEP_MS:.0f}ms)")
print(f"  {judged} of {len(whole)} shot(s) could be judged; {frozen} was born "
      f"inside the character and never had room to travel")
if off_delay:
    for frame, held in off_delay:
        print(f"  >>> the shot from frame {frame} moved after {held} frames, "
              f"which is not the {delay_frames:.0f} its own delay calls for")
    bad += len(off_delay)
elif judged:
    print("  every one of them waited exactly its own attackAfter and then set "
          "off -- the delay is honoured, not smeared")
else:
    print("  >>> every shot was born inside the character, so this run cannot "
          "judge the delay at all")
    bad += 1

# The travel check reads the LONGEST shot, not the first. A shot that HITS stops
# early, and how early depends on which side of the character the mob happened to
# land on -- `summon` picks x uniformly in (player-100, player+100), so a mob that
# lands at arm's length fires a shot that crosses two pixels before it lands. The
# first version judged the first shot and went red on roughly one run in three for
# that reason alone. Measured lengths across runs: 89, 85, and flights of 31.5px and
# 16.2px for the same mob.
best = max(shots, key=lambda s: abs(s[-1][1] - s[0][1]))
travel_px = abs(best[-1][1] - best[0][1])
print(f"  the longest of them (frames {best[0][0]}..{best[-1][0]}) flew "
      f"{travel_px:.1f}px in {best[-1][3] - best[0][3]:.0f}ms, "
      f"out of a {spec['radius']}px radius")
if travel_px < 5.0:
    print(f"  >>> no shot travelled more than 5px, and the shots were "
          f"{[len(s) for s in shots]} frames long each -- every one was born inside "
          f"the character and resolved the instant its delay expired")
    bad += 1

print()
print(f"  player HP {hp0} -> {fr['status']['hp']} (invulnerable, so the HP is not "
      f"the signal; the entry's position is)")
if hits:
    print(f"  the character took {len(hits)} damage events, the first on frame "
          f"{hits[0][0]} for {hits[0][1]} -- contact and shot are NOT separable by "
          f"amount, which is why the entry is what this file judges on")

print(f"\nBAD={bad}")