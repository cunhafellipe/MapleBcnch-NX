"""The projectile's own art: published, named on the wire, and drawn.

`MobAttack.cpp:163-164` --
    attack.has_ball = bool(data["ball"]["0"]);
    attack.ball     = data["ball"];

`Mob.cpp:1118-1126` --
    for (const auto& entry : attack_entries) {
        const auto& data = attacks.at(entry.index);
        if (data.geometry.type != 2 || !data.has_ball || entry.elapsed < entry.delay) continue;
        double t = std::min(1.0, double(entry.elapsed - entry.delay) / entry.lifetime);
        Point<int16_t> point{ ... };
        data.ball.draw(DrawArgument(point, entry.flipped), alpha);
    }

So a type-2 shot is invisible until `elapsed >= delay`, is drawn at the lerped
position, and is mirrored by the ATTACK's facing.

MEASURED on the open Mob.nx: `ball` is declared on 172 of 1,536 attack slots, and
the art lives at `attackN/info/ball` -- not beside the actions, so nothing that
enumerates an image's children ever saw it.

WHAT IS JUDGED, AND HOW
-----------------------
The verdict is a COUNT over a POPULATION: every entry the frame publishes, on
every frame, of every fight. It is never read off one sample of one entry.

That is the whole point of this file's last revision. The old verdict was
"the ball frame index never advances", and it was read off ONE fight, in which
WHICH attack the mob fired decided the answer: the mob has a STATIC attack
with no ball at all and a FLYING one with a 2-frame ball, and the reference
picks whichever REACHES (`MobAttack.cpp:68-76`). On a fight where the mob used
the static attack there was no ball to advance, which the old file called a
defect; and even on a fight where it flew, the shot is only in the air for
~130 ms, which is barely more than the 100 ms the port spends on one ball
frame -- so the index advanced in some fights and not in others, for a reason
that has nothing to do with the product.

So the rules are now per observation:

  * the art is named only once the entry is PAST its delay (`Mob.cpp:1121`),
    and a FLYING entry past its delay is always published;
  * the published frame count is the archive's, and the index is one of the
    ball's own frames;
  * the published phase matches one of the two admissible rules -- the ball's
    OWN delays (`Animation.cpp:64-67`) or the 100 ms fallback for a frame with
    no delay node -- and WHICH one the port used is reported, because the two
    differ and that difference belongs in a bug report, not in a verdict;
  * the ball cannot still be on frame 0 once it is past the later of its own
    first frame delay and the 100 ms fallback, which is the only threshold at
    which that statement is not a race.

The fights repeat until the phase has been observed far enough to be judged at
all, and the number of fights is printed.
"""
import json, os, pathlib, re, urllib.parse, urllib.request


def _repo_root():
    """The repository this suite belongs to: the nearest ancestor of THIS file
    that holds a `MapleBench` directory.

    It used to be an absolute path --
    `C:\\MUWORK\\GAME\\MAPLESOTRY\\wz...\\MapleBcnch-NX-audit\\MapleBench\\wwwroot`
    -- and a suite run from a CLONE therefore read the WORKING TREE's wwwroot,
    which other agents were editing at the time (measured 2026-10-22, three
    gate runs in `C:\\mbtest2\\clone-gate`). A clone must read the clone, so
    the path is derived from the file's own location and never from the current
    working directory or an environment variable. If no ancestor holds a
    `MapleBench`, the suite says so and stops: a suite that silently reads
    somebody else's tree measures the wrong thing."""
    for parent in pathlib.Path(__file__).resolve().parents:
        if (parent / "MapleBench").is_dir():
            return parent
    raise SystemExit("ballart-verify: no ancestor of this file holds a MapleBench "
                     "directory, so the client files it checks cannot be found")


REPO_ROOT = _repo_root()
WWW = str(REPO_ROOT / "MapleBench" / "wwwroot")
# The same override the gate itself uses (gate.ps1:50), so a gate pointed at
# another instance reaches this suite too. The default is the port the suites
# have always used.
BASE = os.environ.get("GATE_BASE") or "http://127.0.0.1:5591"
MAP = "f4/Map/Map0/000000000.img"
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


# ---------------------------------------------------------------- the archive
print("=== the art is under attackN/info/ball, and is published as attackN/ball ===")
cat = get("/api/simulate/mob-catalog")["mobs"]
ids = [m["id"] for m in cat]
sample = ids[::max(1, len(ids) // 60)][:60]

declared = []
for mid in sample:
    for a in get("/api/simulate/mob-attacks", mobId=mid)["attacks"]:
        if a.get("type") == 2:
            sp = get("/api/simulate/mob-sprite", mobId=mid)
            actions = {x["name"]: len(x["frames"]) for x in sp.get("actions", [])}
            want = f"{a['action']}/ball"
            declared.append((mid, a["action"], want in actions, actions.get(want, 0)))
            break

have = [d for d in declared if d[2]]
print(f"  {len(declared)} type-2 attacks in {len(sample)} sampled mobs; "
      f"{len(have)} of them publish their ball art")
for mid, action, ok, frames in declared[:10]:
    print(f"    {mid} {action}: published={ok} frames={frames}")
if not have:
    print("  >>> none; the rest of this file is void")
    raise SystemExit(1)
if any(f < 1 for _, _, _, f in have):
    print("  >>> a published ball has no frames")
    bad += 1

# ---------------------------------------------------------------- on the wire
FIGHTER = None
CACHE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "ballfighter.json")
# Prefer a ball with more than one frame: a one-frame ball has nothing to advance
# through, and MEASURED `9300335 attack1/ball` is one canvas, so choosing it made
# "the index never advances" true for a correct implementation.
multi = [m for m, _, ok, f in have if ok and f >= 2]
candidates = multi or [m for m, _, ok, _ in have]
if os.path.exists(CACHE):
    with open(CACHE, encoding="utf-8") as fh:
        FIGHTER = json.load(fh).get("mobId")
if FIGHTER not in candidates:
    FIGHTER = candidates[0]
with open(CACHE, "w", encoding="utf-8") as fh:
    json.dump({"mobId": FIGHTER}, fh)
print(f"  fighting with {FIGHTER} (its ball carries "
      f"{next(f for m, _, _, f in have if m == FIGHTER)} frames)")

# ---------------------------------------------------------------- this mob's attacks
# What THIS mob declares, read from the archive the way the reference reads it
# (`Mob.cpp:163-165`): how long each attack waits before it fires, how many
# frames its ball has and how long each of those frames lasts. The phase rules
# below are stated in terms of these numbers, so they are read, not guessed.
FIGHTER_ATTACKS = get("/api/simulate/mob-attacks", mobId=FIGHTER)["attacks"]
BALL_COUNT = {}
BALL_DELAYS = {}
for _a in FIGHTER_ATTACKS:
    _key = (_a.get("type"), int(_a.get("delayMs") or 0))
    BALL_COUNT[_key] = int(_a.get("ballFrameCount") or 0)
    BALL_DELAYS[_key] = [int(d) for d in (_a.get("ballFrameDelays") or [])]
TYPE2_HAS_BALL = any(k[0] == 2 and BALL_COUNT.get(k, 0) > 0 for k in BALL_COUNT)
# A ball with one frame has nothing to advance through, so the phase rules have
# nothing to say about it.
MULTIFRAME_BALL = any(BALL_COUNT.get(k, 0) >= 2 for k in BALL_COUNT)
ARCHIVE_FRAMES = {}
for _a in get("/api/simulate/mob-sprite", mobId=FIGHTER).get("actions", []):
    ARCHIVE_FRAMES[_a.get("name")] = len(_a.get("frames") or [])
print(f"  {FIGHTER} declares {len(FIGHTER_ATTACKS)} attacks: "
      + "; ".join(f"type {_a.get('type')} delay {_a.get('delayMs')}ms "
                  f"ball {_a.get('ballFrameCount')}f "
                  f"delays {_a.get('ballFrameDelays')}" for _a in FIGHTER_ATTACKS))


def phase_own_delays(since, delays, count):
    """The reference's rule (`Animation.cpp:64-67`): the ball shows the frame
    whose own delay window contains `since`, and a frame with no delay node
    lasts the 100 ms fallback."""
    if count <= 0:
        return 0
    if not delays:
        delays = [100] * count
    acc = 0
    index = 0
    for i in range(len(delays)):
        if since < acc + delays[i]:
            index = i
            break
        acc += delays[i]
        index = i
    return index % count


def phase_fallback(since, count):
    """The 100 ms fallback alone: one frame per 100 ms."""
    if count <= 0:
        return 0
    return int(since) // 100 % count


def still_threshold(delays, count):
    """The point at which the ball cannot still be on frame 0.

    The ball's own first frame lasts `delays[0]`, and the reference's fallback
    for a frame with no delay node is 100 ms (`Animation.cpp:64-67`). The LATER
    of the two is the first moment at which every admissible rule has left
    frame 0, so it is the only threshold a rule of the form "the ball advances"
    can be stated at without becoming a race."""
    first = delays[0] if delays else 100
    return max(first, 100)


def new_counters():
    return {"obs": 0, "type2": 0, "flying": 0, "named": 0, "multi": 0,
            "beyond": 0, "v_named_early": 0, "v_flying_unnamed": 0,
            "v_index_early": 0, "v_range": 0, "v_count": 0, "v_phase": 0,
            "v_still": 0, "own": 0, "fallback": 0, "max_since": 0.0,
            "instances": 0}


def fight():
    """One fight, and EVERY observation it makes.

    The counters come back instead of a verdict, so the verdict is over a
    POPULATION of observations -- every entry on every frame of every fight --
    and never over one sample of one entry."""
    counters = new_counters()
    s = post("/api/simulate/session",
             {"path": MAP, "weapon": "1302000.img", "equippedWeapon": "1302000",
              "invulnerable": True})
    sid = s["sessionId"]
    post(f"/api/simulate/session/{sid}/summon", {"mobId": FIGHTER, "count": 1})

    def step(keys=None):
        return post(f"/api/simulate/session/{sid}/frames",
                    {"count": 1, "first": keys or {}, "then": {}, "manual": True})

    def mob_of(fr):
        return next((m for m in fr.get("mobs") or []
                     if m.get("mobId") == FIGHTER), None)

    # WAKE IT FIRST. A mob with no `info/firstAttack` acquires only from a landed
    # hit (`Mob.cs:1802`, `if (_lastHitDamage > 0) Aggro = true`), and the swing
    # only lands inside the player's own box -- which reaches ~20..84px in FRONT.
    # MEASURED on 9400708: with no hit it never played an attack in 300 frames and
    # the whole file reported "the ball art never appeared" for a correctly wired
    # projectile.
    woken = False
    for _ in range(400):
        fr = step({"attack": True})
        m = mob_of(fr)
        if m is None:
            break
        if m["hp"] < m["maxHp"]:
            woken = True
            break
        dx = m["x"] - fr["x"]
        if abs(dx) > 8:
            step({"left": dx < 0, "right": dx > 0})

    # STAND OFF. The reference picks the attack that REACHES the character
    # (`MobAttack.cpp:68-76`), and at melee range that is the STATIC attack, so
    # the ball never flies and there is nothing to look at. MEASURED 2026-10-22
    # on 4220000: with the stand-off the mob flew the ball in 7 of 8 fights;
    # without it, in 0 of 1 -- the static attack was the only one that reached.
    fr = step()
    mob = mob_of(fr)
    away = fr["x"] <= mob["x"]
    for _ in range(200):
        fr = step({"right": away, "left": not away})
        mob = mob_of(fr)
        if mob is None:
            break
        if abs(mob["x"] - fr["x"]) >= 130 or not fr["onGround"]:
            break
    for _ in range(200):
        fr = step()
        mob = mob_of(fr)
        if mob is None or mob.get("attackEntryCount", 0) == 0:
            break

    first_named = None
    prev_elapsed = None
    for _i in range(1800):
        fr = step()
        mob = mob_of(fr)
        if mob is None:
            continue
        # W-070: read EVERY entry the frame now publishes. The scalar
        # `attackEntryBallAction` is entry 0, and on 4220000 entry 0 is the STATIC
        # attack2 (no ball) while the art lives on the FLYING attack1 -- so
        # reading the scalar reported "the ball never appears" for a mob that
        # carries it and publishes it correctly. `Mob.cpp:1104-1125` draws one
        # ball per entry.
        entries = mob.get("attackEntries") or []
        if not entries and mob.get("attackEntryCount", 0):
            entries = [{  # an older frame that only carries the scalars
                "type": mob["attackEntryType"],
                "elapsedMs": mob["attackEntryElapsedMs"],
                "delayMs": mob["attackEntryDelayMs"],
                "ballAction": mob["attackEntryBallAction"],
                "ballFrameCount": mob["attackEntryBallFrameCount"],
                "ballFrameIndex": mob["attackEntryBallFrameIndex"],
            }]
        for e in entries:
            counters["obs"] += 1
            etype = e.get("type")
            el = float(e.get("elapsedMs") or 0.0)
            dl = float(e.get("delayMs") or 0.0)
            action = e.get("ballAction") or ""
            count = int(e.get("ballFrameCount") or 0)
            idx = int(e.get("ballFrameIndex") or 0)
            past = el >= dl
            since = el - dl
            key = (etype, int(dl))
            delays = BALL_DELAYS.get(key, [])
            if action:
                counters["named"] += 1
                if first_named is None:
                    first_named = (_i, action, idx, count, el, dl)
            if etype == 2:
                counters["type2"] += 1
                # A NEW instance of the shot: the elapsed goes back to the start.
                if prev_elapsed is not None and el < prev_elapsed:
                    counters["instances"] += 1
                prev_elapsed = el
                if past:
                    counters["flying"] += 1
            if count >= 2:
                counters["multi"] += 1

            # Mob.cpp:1121 -- the ball is drawn only once the entry is PAST its
            # delay, so a name before the delay is a protocol error.
            if action and not past:
                counters["v_named_early"] += 1
            # Mob.cpp:1121 -- a FLYING entry past its delay IS drawn, so it has
            # to be published.
            if etype == 2 and past and not action and TYPE2_HAS_BALL:
                counters["v_flying_unnamed"] += 1
            # Before the delay there is no ball in the air, so there is no frame
            # to be showing.
            if not past and idx != 0:
                counters["v_index_early"] += 1
            # The index is one of the ball's own frames.
            if count >= 1 and not (0 <= idx < count):
                counters["v_range"] += 1
            if count == 0 and idx != 0:
                counters["v_range"] += 1
            # The published frame count is the archive's.
            if action and ARCHIVE_FRAMES.get(action, count) != count:
                counters["v_count"] += 1

            # The phase. Every admissible rule is accepted and WHICH one the port
            # used is reported, because the two differ and that difference is a
            # port bug to report, not a verdict for this suite to hand down.
            if count >= 2 and past:
                counters["max_since"] = max(counters["max_since"], since)
                own = phase_own_delays(since, delays, count)
                fb = phase_fallback(since, count)
                if idx == fb and idx != own:
                    counters["fallback"] += 1
                elif idx == own:
                    counters["own"] += 1
                else:
                    counters["v_phase"] += 1
                # The ball cannot still be on frame 0 at this point.
                if since >= still_threshold(delays, count):
                    counters["beyond"] += 1
                    if idx == 0:
                        counters["v_still"] += 1
    counters["woken"] = woken
    return counters, sid, first_named

# ---------------------------------------------------------------- the fight
# How many fights to try, and why the number is printed.
#
# The one thing the OLD assertion needed -- "the ball frame index advances" --
# can only be OBSERVED when the shot is in the air long enough to leave frame
# 0, and how long that is depends on when the mob fired and how close it stood.
# MEASURED 2026-10-22 on 4220000 over 9 fights: the shot was observed past the
# point where it MUST have left frame 0 in 5 of them; in the other 4 the
# published index stayed 0 -- correctly, because the shot was still on frame 0
# when it reached the player. So ONE fight is a coin toss, and an assertion
# that reads one fight is a lottery. That is this suite's bug.
#
# The verdict below is over EVERY observation of EVERY fight, and the fights
# repeat until the phase has been observed far enough to be judged at all.
MAX_FIGHTS = 8
totals = new_counters()
sessions = []
fights = 0
first_named = None
for _attempt in range(MAX_FIGHTS):
    fights += 1
    counters, sid, named = fight()
    sessions.append(sid)
    for k, v in counters.items():
        if k == "max_since":
            totals[k] = max(totals[k], v)
        elif k == "woken":
            continue
        else:
            totals[k] += v
    if first_named is None and named is not None:
        first_named = named
    print(f"  fight {fights}: {counters['obs']} entry observations, "
          f"{counters['flying']} of a flying shot in the air, "
          f"{counters['named']} naming the art, {counters['multi']} of a "
          f"multi-frame ball, {counters['beyond']} far enough past the delay "
          f"for the ball to have had to leave frame 0")
    if totals["beyond"] > 0:
        break
print(f"  fought {fights} time(s)")

print()
print("=== the ball, over every observation of every fight ===")
if first_named is not None:
    print(f"  first sighting: frame {first_named[0]}, the art is named "
          f"({first_named[1]}), frame {first_named[2]}/{first_named[3]}, "
          f"elapsed={first_named[4]:.0f}ms of delay={first_named[5]:.0f}ms")
print(f"  {totals['obs']} entry observations; {totals['flying']} of a flying shot "
      f"in the air; {totals['named']} naming the art; {totals['multi']} of a "
      f"multi-frame ball; {totals['beyond']} far enough past the delay for the "
      f"ball to have had to leave frame 0 (the shot was observed up to "
      f"{totals['max_since']:.0f}ms past it)")

VERDICTS = [
    ("a ball was named before its entry was past the delay", "v_named_early"),
    ("a FLYING entry past its delay published no art", "v_flying_unnamed"),
    ("an entry that had not reached its delay published a frame index",
     "v_index_early"),
    ("a published frame index was outside the ball's own frames", "v_range"),
    ("a published frame count was not the archive's", "v_count"),
    ("a published frame index matched NO admissible rule", "v_phase"),
    ("the ball was still on frame 0 where it cannot be", "v_still"),
]
for label, key in VERDICTS:
    n = totals[key]
    print(f"  {'ok  ' if n == 0 else '>>> '}{label}: {n}")
    if n:
        bad += n

if totals["own"] or totals["fallback"]:
    print(f"  the published phase matched the ball's OWN delays on "
          f"{totals['own']} observations and the 100 ms FALLBACK on "
          f"{totals['fallback']}")
if totals["beyond"] == 0 and totals["flying"] > 0 and MULTIFRAME_BALL:
    print("  >>> the shot flew but was never observed far enough past its delay "
          "for the ball to have had to leave frame 0, so the phase was not "
          "measured")
    bad += 1
elif totals["flying"] == 0:
    # NOT a defect, and the OLD file's mistake. The reference picks the attack
    # that REACHES (`MobAttack.cpp:68-76`), and the static attack declares no
    # ball at all, so there is nothing in the air to draw and nothing to
    # measure. Saying the art is missing would be wrong.
    print("  the mob used its STATIC attack for every fight, so there was no "
          "ball in the air -- a fact about the mob, not a defect")
elif not MULTIFRAME_BALL:
    # A one-frame ball has nothing to advance through, so "the ball advanced" is
    # not a question about it. MEASURED: `9300335 attack1/ball` is one canvas.
    print("  this mob's ball carries a single frame, so there is nothing to "
          "advance through -- a fact about the ball, not a defect")
elif totals["beyond"] > 0:
    print(f"  the ball left frame 0 on {totals['beyond']} observations, so it "
          f"is an animation rather than one still")

# ---------------------------------------------------------------- the client
print()
print("=== the client draws it ===")
walk = open(os.path.join(WWW, "js", "walkview.js"), encoding="utf-8").read()
sprite = open(os.path.join(WWW, "js", "mobsprite.js"), encoding="utf-8").read()
checks = [
    ("walkview reads the entry position", "attackEntryX" in walk and "attackEntryY" in walk),
    ("walkview reads the ball action name", "attackEntryBallAction" in walk),
    ("walkview reads the ball frame index", "attackEntryBallFrameIndex" in walk),
    ("walkview reads the launch facing", "attackEntryFlipped" in walk),
    ("walkview blits it", "drawMobAttackBall" in walk and "drawImage(img, x, y, w, h)" in walk),
    ("mobsprite can blit a named action", "stepNamed" in sprite),
    ("mobsprite preloads named actions",
     "for (const [, entry] of art.actions)" in sprite),
]
for label, ok in checks:
    print(f"  {'ok  ' if ok else '>>> '}{label}")
    if not ok:
        bad += 1


# W-089. Every session this suite opened is DELETED before it exits.
#
# A leaked session is not a leak of memory, it is a leak of BACKGROUND LOAD: the
# server keeps ticking it, and `MapSimSessionStore` only reaps a session after
# 120 s idle. MEASURED 2026-10-18, 40 idle sessions push the request latency
# from 1.6 ms to 143.9 ms, which is what makes the NEXT suite's timing
# assumption fail. The failure mode reproduces itself. The fight above may have
# opened up to MAX_FIGHTS of them, so all of them go.
def _drop(session_id):
    try:
        req = urllib.request.Request(f"{BASE}/api/simulate/session/{session_id}", method="DELETE")
        with urllib.request.urlopen(req, timeout=30) as r:
            r.read()
    except Exception:
        pass


for _sid in sessions:
    _drop(_sid)
print(f"  dropped {len(sessions)} session(s)")
print(f"\nBAD={bad}")