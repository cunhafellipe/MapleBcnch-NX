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
  * the published frame count is the ARCHIVE's, and the index is one of the
    ball's own frames;
  * the published phase is the REFERENCE's phase -- the ball's own per-frame
    delays (`Animation.cpp:64-67`), advanced CYCLICALLY, because the
    reference's animation wraps (`Animation.cpp:316-320`: on the last frame
    the reference sets `nextframe = 0`);
  * the ball cannot still be on frame 0 at a moment when the reference's
    PHASE has left frame 0 -- the phase, not the raw time, because the
    reference's phase is a sawtooth and a shot one whole cycle old is
    legitimately back on frame 0.

TWO OF THOSE RULES USED TO ENCODE A FREEZE, and both were wrong:

  * `phase_own_delays` walked the delays without reducing the time into one
    cycle, so it stopped on the LAST frame -- which is a ball frozen in
    mid-air, the very defect the suite exists to catch, written into the
    judge as if it were correct;
  * the "cannot still be on frame 0" check compared the RAW time against the
    threshold, so a shot that had completed a whole cycle and come back to
    frame 0 -- the reference's own behaviour -- was reported as a defect.

The reference does not freeze (`Animation.cpp:316-320`, `MobAttack.cpp:36`
advances the ball on every tick), so the judge that encoded the freeze was
the thing that was out of date, and it reported a CORRECT port as broken.
That is the failure this file's rules are now written against.

The ball's frame set and each frame's duration are read from the ARCHIVE, not
from the port's published numbers, so a port that mis-reads its own ball
cannot pass by agreeing with itself.

The fights repeat until the phase has been observed far enough to be judged at
all, and the number of fights is printed.
"""
import json, os, pathlib, re, urllib.error, urllib.parse, urllib.request


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
try:
    from archivelib import require_gate_base
except ImportError:            # a missing module must not read as a pass
    print(">>> RED: archivelib.py is missing next to this suite")
    raise SystemExit(1)
except SyntaxError as exc:    # a helper that does not PARSE is not missing
    print(f">>> RED: archivelib.py next to this suite does not parse: {exc}")
    raise SystemExit(1)
BASE = require_gate_base()
# The map the fight happens on. The archive's session id is resolved BY NAME
# below: it used to be the literal "f4", which is the id Map.nx happens to get
# when the gate opens its twenty archives in its usual order. On an instance
# with a different set of archives open -- which is every instance but the
# gate's -- "f4" is a different archive, or nothing at all.
MAP = None
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
def archive_id(name):
    """The session id of an open archive, BY NAME.

    The ids are handed out in the order the archives were opened, so `f1` is
    Mob.nx on one instance and something else on another. A suite that
    hard-codes an id measures a different archive than the one it means to.
    """
    files = get("/api/files")
    if files is None:
        files = []
    if not isinstance(files, list):
        # The same defect archivelib.py had: the container is never checked, so
        # a JSON scalar (42) reached `for f in 42`, raised a TypeError, and the
        # traceback then read as "could not reach the backend" -- while the
        # backend was alive and the listing simply was not a list. "Could not
        # list" and "not open" are different failures and must say so.
        print(f">>> RED: could not list the open archives on this instance: "
              f"the instance returned {type(files).__name__}, not a list")
        raise SystemExit(1)
    for f in files:
        if f.get("name") == name:
            return f.get("id")
    return None


def read_fields(path):
    """The direct children of one node, as {name: (type, value)}.

    The TYPE is read as well as the value because the reference's rules are
    stated in terms of it: an integer, a real and a string are three
    different answers.
    """
    doc = None
    try:
        doc = get("/api/inspect", path=path)
    except urllib.error.HTTPError as e:
        # A node that is not there answers 404, and that IS an answer: the
        # attack simply declares no ball there. Anything else is a failure and
        # is not swallowed.
        if e.code == 404:
            return {}
        raise
    if not isinstance(doc, dict):
        return {}
    node = doc.get("node")
    if not isinstance(node, dict):
        return {}
    # An image node carries no `type` of its own -- it reports kind:"Image"
    # and hasChildren -- so "no type" is not the same as "not there".
    if node.get("type") in (None, "Null") and not node.get("hasChildren"):
        return {}
    fields = {}
    for c in doc.get("children") or []:
        if isinstance(c, dict):
            fields[c.get("name")] = (c.get("type"), c.get("value"))
    return fields


def node_int(kind, value):
    """nl::node::get_integer -- vendor/NoLifeNx/nlnx/node.cpp:155-173.

    An absent node is 0, an integer is itself, a real is truncated, and a
    STRING is parsed with std::stoll -- so "60" is the number 60. A string
    that does not start with a number makes the reference THROW; 0 is
    returned for it here, which is the value such a node can carry anyway.
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


MOB_ARCHIVE = archive_id("Mob.nx")
if not MOB_ARCHIVE:
    print(">>> RED: ballart-verify -- Mob.nx is not open on this instance, so "
          "there is no ball to judge")
    raise SystemExit(1)

MAP_ARCHIVE = archive_id("Map.nx")
if not MAP_ARCHIVE:
    print(">>> RED: ballart-verify -- Map.nx is not open on this instance, so "
          "there is no map to fight on")
    raise SystemExit(1)
MAP = f"{MAP_ARCHIVE}/Map/Map0/000000000.img"


def read_ball_from_archive(mob_id, action):
    """The ball's frames and their durations, read from the ARCHIVE.

    `Animation.cpp:177-193` -- the frames are the children whose name is an
    integer AND whose type is a bitmap, in numeric order (`std::set<int16_t>`
    is sorted, and so is this list).

    `Animation.cpp:64-67` -- `delay = src["delay"]; if (delay == 0) delay =
    100;` so a frame with no delay node lasts 100 ms and a frame with one
    lasts its own.

    Returns (frame ids, per-frame durations), or None when the attack
    declares no ball.
    """
    for host in ("info/ball", "ball"):
        base = f"{MOB_ARCHIVE}/{mob_id}.img/{action}/{host}"
        fields = read_fields(base)
        ids = []
        for name, (kind, _value) in fields.items():
            if re.fullmatch(r"-?\d+", str(name or "")) and kind == "Canvas":
                ids.append(int(name))
        if not ids:
            continue
        ids.sort()
        delays = []
        for fid in ids:
            frame = read_fields(f"{base}/{fid}")
            node = frame.get("delay")
            delay = node_int(*node) if node else 0
            delays.append(delay if delay else 100)
        return ids, delays
    return None


# ---------------------------------------------------------------- the archive
print("=== the art is under attackN/info/ball, and is published as attackN/ball ===")
cat = get("/api/simulate/mob-catalog")["mobs"]
ids = [m["id"] for m in cat]
sample = ids[::max(1, len(ids) // 60)][:60]

declared = []
undescribed = []
for mid in sample:
    for a in get("/api/simulate/mob-attacks", mobId=mid)["attacks"]:
        if a.get("type") == 2:
            want = f"{a['action']}/ball"
            try:
                sp = get("/api/simulate/mob-sprite", mobId=mid)
            except urllib.error.HTTPError as e:
                # The endpoint answered 404 for a mob whose image EXISTS.
                #
                # MEASURED 2026-10-24: mob 9500135, whose f5/9500135.img
                # carries only an `info` node (no action node at all), answers
                #   "'attack1' not found under 'f5/9500135.img'."
                # and the endpoint's own contract says it answers "200 with
                # hasArt:false rather than 404" -- SimulatorEndpoints.cs:662.
                # So this is a defect of the PORT's response shape, and it is
                # counted as one.
                #
                # It used to KILL this file instead: one mob out of sixty
                # raised, the traceback ended the suite, and the other
                # fifty-nine measurements never ran. A suite that dies has no
                # verdict, so the gate reported "the suite died" and nobody
                # could tell a port defect from a broken harness. The defect
                # is now counted, named, and the rest of the file still runs.
                if e.code == 404:
                    detail = e.read()[:200].decode("utf-8", "replace")
                    undescribed.append((mid, a.get("action"), detail))
                    bad += 1
                    print(f"    {mid} {a.get('action')}: THE PORT COULD NOT DESCRIBE IT"
                          f" -- HTTP 404 {detail}")
                    break
                raise
            actions = {x["name"]: len(x["frames"]) for x in sp.get("actions", [])}
            declared.append((mid, a["action"], want in actions, actions.get(want, 0)))
            break
if undescribed:
    print(f"  {len(undescribed)} mob(s) the port answered 404 for. The endpoint"
          " promises '200 with hasArt:false rather than 404'"
          " (SimulatorEndpoints.cs:662), so each one is a defect of the port's"
          " response shape, not of the archive.")

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
# What the PORT publishes, and what the REFERENCE says the same numbers are.
#
# The port's own numbers are read for the report only. The rule the phase is
# judged against is computed from the ARCHIVE, because the whole point of
# this suite is that a port which mis-reads its own ball cannot pass by
# agreeing with itself.
FIGHTER_ATTACKS = get("/api/simulate/mob-attacks", mobId=FIGHTER)["attacks"]
PORT_BALL_COUNT = {}
PORT_BALL_DELAYS = {}
for _a in FIGHTER_ATTACKS:
    _key = (_a.get("type"), int(_a.get("delayMs") or 0))
    PORT_BALL_COUNT[_key] = int(_a.get("ballFrameCount") or 0)
    PORT_BALL_DELAYS[_key] = [int(d) for d in (_a.get("ballFrameDelays") or [])]
TYPE2_HAS_BALL = any(k[0] == 2 and PORT_BALL_COUNT.get(k, 0) > 0 for k in PORT_BALL_COUNT)
# A ball with one frame has nothing to advance through, so the phase rules have
# nothing to say about it.
MULTIFRAME_BALL = any(PORT_BALL_COUNT.get(k, 0) >= 2 for k in PORT_BALL_COUNT)

# The REFERENCE's ball, from the archive: the frame set and each frame's own
# duration. `Animation.cpp:177-193` for the frame set, `:64-67` for the
# duration, and `:316-320` for the wrap that makes the phase a sawtooth.
BALL_REF = {}
for _a in FIGHTER_ATTACKS:
    _action = _a.get("action")
    if not _action:
        continue
    _ref = read_ball_from_archive(FIGHTER, _action)
    if _ref is not None:
        BALL_REF[_action + "/ball"] = _ref
print(f"  {FIGHTER} declares {len(FIGHTER_ATTACKS)} attacks: "
      + "; ".join(f"type {_a.get('type')} delay {_a.get('delayMs')}ms "
                  f"ball {_a.get('ballFrameCount')}f "
                  f"delays {_a.get('ballFrameDelays')}" for _a in FIGHTER_ATTACKS))
for _name, (_ids, _delays) in sorted(BALL_REF.items()):
    print(f"  the reference's {_name}: {len(_ids)} frame(s) "
          f"{_ids}, delays {_delays}, one cycle {sum(_delays)} ms")


def phase_own_delays(since, delays, count):
    """The reference's rule (`Animation.cpp:64-67` with the wrap of :316-320).

    The shape is the port's own (`MobPatrol.cs:1325-1343`), because that is
    the shape the reference describes: reduce the time into ONE cycle, then
    walk the frames' own delays.

    THE WRAP IS THE POINT. `Animation.cpp:316-320`: when the animation is on
    its last frame the reference sets `nextframe = 0`, so its phase is a
    sawtooth with a period of sum(delays) and it never sits still. Walking
    the delays without the reduction stops on the last frame the moment the
    shot has been in the air longer than one cycle -- a ball frozen in
    mid-air, which is the defect this suite exists to catch, written into
    the judge as if it were correct.
    """
    if count <= 0:
        return 0
    if not delays:
        delays = [100] * count
    period = sum(delays)
    if period > 0:
        since = since % period
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
    """The 100 ms per frame rule, as a REPORT of what the port published.

    This is NOT one of the reference's rules for a ball whose frames declare
    their own delays (`Animation.cpp:64-67` gives each frame its own
    duration, and 100 is only the fallback for a frame with no delay node).
    It is kept so that a port which published it can be named in the report
    instead of being counted as a nameless mismatch.
    """
    if count <= 0:
        return 0
    return int(since) // 100 % count


def still_threshold(delays, count):
    """The point at which the ball cannot still be on frame 0.

    The ball's own first frame lasts `delays[0]`, and the reference's fallback
    for a frame with no delay node is 100 ms (`Animation.cpp:64-67`). The LATER
    of the two is the first moment at which every admissible rule has left
    frame 0, so it is the only threshold a rule of the form "the ball advances"
    can be stated at without becoming a race.

    The threshold is compared against the PHASE (`since % sum(delays)`), not
    against the raw time. The reference's phase is a sawtooth, so a shot that
    has completed a whole cycle is legitimately back on frame 0; comparing the
    raw time called the reference's own behaviour a defect.
    """
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
            # The reference's ball for the art this entry publishes, read from
            # the archive. `action` is empty until the entry is past its delay
            # (`Mob.cpp:1121`), and the phase is only judged while the ball is
            # in the air.
            ref = BALL_REF.get(action)
            ref_ids, ref_delays = ref if ref else ([], [])
            ref_count = len(ref_ids)
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
            # The published frame count is the ARCHIVE's -- the reference's own
            # frame set (Animation.cpp:177-193), counted from the archive and
            # not from the port's own answer.
            if action and ref and ref_count != count:
                counters["v_count"] += 1

            # The phase. The reference's rule is the ball's OWN delays, walked
            # cyclically (Animation.cpp:64-67 with the wrap of :316-320). The
            # 100 ms per frame rule is NOT the reference's rule for a ball
            # whose frames declare their own delays, so a port that published
            # it is a mismatch -- it is named in the report so the bug report
            # can say which rule the port used.
            if ref_count >= 2 and past:
                counters["max_since"] = max(counters["max_since"], since)
                period = sum(ref_delays)
                phase = (since % period) if period > 0 else 0.0
                own = phase_own_delays(since, ref_delays, ref_count)
                if idx == own:
                    counters["own"] += 1
                else:
                    counters["v_phase"] += 1
                    if idx == phase_fallback(since, ref_count):
                        counters["fallback"] += 1
                # The ball cannot still be on frame 0 once the PHASE has passed
                # the later of its own first frame delay and the 100 ms
                # fallback. The phase and not the raw time: the reference's
                # phase is a sawtooth (Animation.cpp:316-320), so a shot one
                # whole cycle old is legitimately back on frame 0.
                if phase >= still_threshold(ref_delays, ref_count):
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
          f"multi-frame ball, {counters['beyond']} at a phase where the ball "
          f"cannot be on frame 0")
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
      f"multi-frame ball; {totals['beyond']} at a phase where the ball cannot "
      f"be on frame 0 (the shot was observed up to "
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
    print(f"  the published phase matched the REFERENCE's rule -- the ball's own "
          f"delays, cyclically -- on {totals['own']} observations")
    if totals["fallback"]:
        print(f"  >>> {totals['fallback']} of the mismatches published the 100 ms "
              f"per frame rule, which is not the reference's rule for a ball "
              f"whose frames declare their own delays (Animation.cpp:64-67)")
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
if bad:
    # The gate judges on the RED marker and on BAD=[1-9] (gate.ps1:97-99), and
    # every other suite in this directory prints one of the two. This one used
    # to print neither and to exit 0, so a run that found 18 defects was
    # reported as a PASS by every caller that reads the exit code -- and a
    # judge that cannot fail is not a judge.
    print(">>> RED: ballart-verify -- the ball's phase or its art does not "
          "match the reference")
    raise SystemExit(1)
raise SystemExit(0)