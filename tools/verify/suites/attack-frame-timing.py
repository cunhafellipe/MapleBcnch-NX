"""ADR-0005 criterion 2 -- "mechanism accuracy", judged on the attack's frames.

ADR-0005's second acceptance criterion is "mechanism accuracy: does the
simulator compute what the reference computes". Criterion 1 (position
accuracy) has a judge -- tools/verify/suites/position-accuracy.py. This is the
second one, and it judges the MECHANISM the reference defines for a mob's
attack:

  WHICH node supplies the animation,
  WHICH frames that animation has,
  how long each frame lasts,
  and when the hit lands.

WHAT IT ASSERTS, and why each one is a PROMISE and not a snapshot of today:

  A1  The set of attack slots a mob has is the reference's set: the slots the
      reference admits (an animation with a frame "0" and an info node), taken
      from the mob's OWN image or from the mob its info/link points at.

  A2  An attack's DURATION is the sum of its animation's frame delays, and a
      frame with no delay of its own lasts 100 ms.

  A3  An attack's HIT lands at info/attackAfter ms after the attack starts.

  A4  A projectile's frames run on the BALL's own delays, with the same 100 ms
      fallback.

  A5  Two KNOWN ANSWERS, written down, so the judge has expected values that
      do not come from the thing under test.

The reference source is the clean-client tree -- the authority; MapleNecrocer is
not accepted (pm/PARITY.md says where it lives). It is NOT inside this
repository and this judge does not read it at run time: the rules below are
TRANSCRIBED from it, each with the file and the line in the comment beside it.
That is deliberate -- a judge that opened a path outside the tree would be
reading a machine-local path, and the fix for that is to put the rule in the
code instead of in a path. The judge reads NO file of this repository at all,
so it needs no repository root and no environment variable to find one.

The mob data IS read at run time, and it is read from the ARCHIVE through the
running instance, not from this repository's working tree -- so a clone that
talks to the same backend measures that backend's data, and a clone with its
own backend measures its own. The reference's rules are applied to the
archive's own numbers, so a port that mis-reads its own data cannot pass this
judge by agreeing with itself.

It opens no archive, saves nothing, and writes nothing.
"""
import json
import os
import urllib.error
import urllib.parse
import urllib.request
from concurrent.futures import ThreadPoolExecutor

# The gate's own override, so a gate pointed at another instance reaches this
# judge too (the same two lines the other suites use).
BASE = os.environ.get("GATE_BASE") or "http://127.0.0.1:5591"

# The archive the mobs live in. Its session id (f1..fN) depends on which
# archives are open and in what order, so it is looked up by NAME rather than
# assumed -- on one instance Mob.nx is f5, on another it is f1.
ARCHIVE_NAME = "Mob.nx"

# How many requests to keep in flight. The census is a few thousand small
# reads, and the backend serialises node reads behind one lock, so this is a
# small number on purpose.
THREADS = 8

# The known answers (A5). Both were read off the archive by hand on
# 2026-10-22 and are written down so the judge has expected values that do
# not come from the thing under test.
#
# 2400049 attack3: twenty frames, and NOT ONE of them carries a delay node, so
# the reference's duration is twenty times the 100 ms fallback -- 2000 ms. The
# hit lands at the info's attackAfter, 1560 ms.
#
# 3400000 carries no attack of its own: its info/link points at 9410005, whose
# attack1 has fourteen frames -- ten at 120 ms and four at 150 ms, so 1800 ms
# -- and an attackAfter of 600 ms.
KNOWN_SLOT = ("2400049", 3, 2000, 1560)
KNOWN_LINKED_SLOT = ("3400000", 1, 1800, 600)

asserts = 0
fails = 0


def Check(name, ok, detail=""):
    global asserts, fails
    asserts += 1
    if ok:
        print(f"  ok    {name}")
    else:
        fails += 1
        print(f"  FAIL  {name} -- {detail}")
        print(f">>> RED: attack-frame-timing -- {name} ({detail})")


def get(route, **q):
    url = BASE + route + ("?" + urllib.parse.urlencode(q) if q else "")
    try:
        with urllib.request.urlopen(url, timeout=600) as r:
            return r.read().decode("utf-8")
    except urllib.error.HTTPError as e:
        return json.dumps({"error": e.read().decode("utf-8", "replace")[:200]})


def get_json(route, **q):
    try:
        return json.loads(get(route, **q))
    except (ValueError, TypeError):
        return None


def parallel(items, fn):
    """Run fn over items, keeping the order."""
    if not items:
        return []
    with ThreadPoolExecutor(max_workers=THREADS) as ex:
        return list(ex.map(fn, items))


# --------------------------------------------------------------------------- #
# The reference's rules, transcribed.
# --------------------------------------------------------------------------- #

def node_int(raw):
    """nl::node::get_integer -- vendor/NoLifeNx/nlnx/node.cpp:155-173.

    An absent node is 0, an integer is itself, a real is truncated, and a
    STRING is parsed with std::stoll -- so "110" is the number 110, not a
    string the reference cannot read. A string that is not a number makes the
    reference THROW (std::invalid_argument); None is returned for that, so
    the judge can count it instead of pretending it did not happen.
    """
    if raw is None:
        return 0
    if isinstance(raw, bool):
        return 0
    if isinstance(raw, int):
        return raw
    if isinstance(raw, float):
        return int(raw)
    if isinstance(raw, str):
        try:
            return int(raw.strip())
        except ValueError:
            return None
    return 0


def frame_ids(action):
    """Animation.cpp:177-193 -- the frames of an animation.

    A frame is a child whose name parses as an int16 and whose type is a
    bitmap; the ids are collected in a std::set, so the frames are in
    ASCENDING id order and a child with a name that is not a number is not a
    frame at all.
    """
    ids = set()
    for name, value in action.items():
        if not isinstance(value, dict) or not value.get("canvas"):
            continue                       # Animation.cpp:183 -- a bitmap only
        try:
            fid = int(name)
        except ValueError:
            continue                       # Animation.cpp:181 -- a number only
        if 0 <= fid <= 32767:
            ids.add(fid)                  # Animation.cpp:182 -- fid >= 0
    return sorted(ids)


def frame_delay(frame):
    """Animation.cpp:64-67 -- delay = src["delay"]; if (delay == 0) delay = 100.

    The 100 is the FALLBACK for a frame that declares no delay (or declares a
    zero), not a rate every frame runs at.
    """
    kids = frame.get("children") if isinstance(frame, dict) else None
    value = node_int(kids.get("delay")) if isinstance(kids, dict) else 0
    if value is None:
        return None                        # the reference throws here
    return value if value else 100


def action_duration(action):
    """Mob.cpp:151-152 -- attack.duration += attack.animation.get_delay(frame).

    None means the reference would throw while reading a delay.
    """
    total = 0
    for fid in frame_ids(action):
        frame = action.get(str(fid))
        if frame is None:
            continue
        delay = frame_delay(frame)
        if delay is None:
            return None
        total += delay
    return total


def ball_delays(info):
    """The projectile's own frame delays.

    Mob.cpp:163-165 -- attack.has_ball = bool(data["ball"]["0"]) and
    attack.ball = data["ball"]: the ball is read off the INFO node, and its
    frames are an animation, so each of them is Animation.cpp:64-67's rule.
    """
    ball = info.get("ball") if isinstance(info, dict) else None
    if not isinstance(ball, dict):
        return []
    out = []
    for fid in frame_ids(ball):
        frame = ball.get(str(fid))
        if frame is None:
            continue
        delay = frame_delay(frame)
        if delay is None:
            return None
        out.append(delay)
    return out


def hit_delay(info):
    """Mob.cpp:153 -- attack.delay = max(0, data["attackAfter"]).

    MobAttack.cpp:199 -- the hit resolves when the entry's elapsed time
    reaches that delay.
    """
    raw = info.get("attackAfter") if isinstance(info, dict) else None
    value = node_int(raw)
    if value is None:
        return None
    return max(0, value)


# --------------------------------------------------------------------------- #
# The census: the whole archive, read through the instance.
# --------------------------------------------------------------------------- #

def resolve_archive():
    """The session id of the Mob.nx archive, BY NAME."""
    files = get_json("/api/files")
    if not isinstance(files, list):
        raise SystemExit(f"cannot list the open archives on {BASE}: {files}")
    for f in files:
        if f.get("name") == ARCHIVE_NAME:
            return f["id"]
    raise SystemExit(
        f"{ARCHIVE_NAME} is not open on this instance. Open it (the gate's "
        "documented loop does) and run this judge again.")


def read_children(path):
    kids = get_json("/api/children", path=path)
    return kids if isinstance(kids, list) else []


def read_tree(path):
    """One node as a subtree.

    /api/export/json walks the node and replaces every canvas with a short
    descriptor, so the frames' delay values come back without the pictures.
    """
    doc = get_json("/api/export/json", path=path, depth=5)
    if not isinstance(doc, dict):
        return {}
    data = doc.get("data")
    return data if isinstance(data, dict) else {}


def main():
    print("=== attack frame timing: the port's attacks, against the reference ===")
    fid = resolve_archive()
    print(f"archive: {ARCHIVE_NAME} (session {fid})")

    # Every mob image in the archive -- the whole population, no sample.
    images = [c["path"] for c in read_children(fid) if c.get("name", "").endswith(".img")]
    if not images:
        raise SystemExit(f"{ARCHIVE_NAME} (session {fid}) holds no mob images")
    print(f"mob images: {len(images)}")

    # The children of every mob image: which actions it declares, and whether
    # it declares an info of its own.
    child_lists = parallel(images, lambda p: read_children(p))
    child_names = {}
    for path, kids in zip(images, child_lists):
        child_names[path] = {k.get("name") for k in kids if isinstance(k, dict)}

    # info/link -- Mob.cpp:48-57. The link is the mob's OWN info's child, and
    # it names the mob whose GRAPHICS this one reuses.
    link_targets = {}
    info_paths = [f"{p}/info" for p in images]
    info_lists = parallel(info_paths, read_children)
    for path, kids in zip(images, info_lists):
        for k in kids:
            if not isinstance(k, dict) or k.get("name") != "link":
                continue
            node = get_json("/api/inspect", path=k.get("path"))
            value = (node or {}).get("node", {}).get("value")
            number = node_int(value)
            if number is None or number <= 0:
                continue
            # Mob.cpp:50 -- string_format::extend_id(link.get_integer(), 7).
            link_targets[path] = str(number).zfill(7)

    # The mob a link points at, when that mob is in this archive and has the
    # stand or the fly the reference asks for (Mob.cpp:55).
    def animation_source(path):
        target = link_targets.get(path)
        if not target:
            return path
        target_path = f"{fid}/{target}.img"
        names = child_names.get(target_path)
        if not names or not ({"stand", "fly"} & names):
            return path
        return target_path

    sources = {p: animation_source(p) for p in images}
    linked = sum(1 for p in images if sources[p] != p)
    print(f"mobs whose graphics come from another mob (info/link): {linked}")

    # Every attack node in the archive, as a subtree.
    action_paths = []
    for path in images:
        for name in sorted(child_names.get(path, ())):
            if name.startswith("attack"):
                action_paths.append(f"{path}/{name}")
    trees = parallel(action_paths, read_tree)
    own_tree = {}
    for full, tree in zip(action_paths, trees):
        parent, _, name = full.rpartition("/")
        own_tree.setdefault(parent, {})[name] = tree
    print(f"attack nodes read from the archive: {len(action_paths)}")

    # The reference's attacks, per mob (Mob.cpp:142-178, transcribed).
    reference = {}
    for path in images:
        source = sources[path]
        anim = own_tree.get(source, {})
        mine = own_tree.get(path, {})
        slots = {}
        for i in range(1, 9):
            action = f"attack{i}"
            node = anim.get(action)
            if node is None:
                continue                    # the mob has no such animation
            data = mine.get(action, {}).get("info")
            if not isinstance(data, dict) or not data:
                data = node.get("info")      # Mob.cpp:147 -- node["info"]
            if "0" not in node or not isinstance(data, dict):
                continue                    # Mob.cpp:148 -- both must exist
            slots[i] = {
                "duration": action_duration(node),
                "hit": hit_delay(data),
                "ball": ball_delays(data),
            }
        reference[path] = slots

    ref_mobs = sum(1 for p in images if reference[p])
    ref_slots = sum(len(reference[p]) for p in images)
    print(f"mobs with attacks in the reference: {ref_mobs}; slots: {ref_slots}")

    # The port's own answer for every mob in the archive.
    def port_attacks(path):
        mob_id = path.rsplit("/", 1)[-1].rsplit(".", 1)[0]
        doc = get_json("/api/simulate/mob-attacks", mobId=mob_id)
        attacks = (doc or {}).get("attacks")
        return path, (attacks if isinstance(attacks, list) else [])

    published = dict(parallel(images, port_attacks))
    port_slots = sum(len(v) for v in published.values())
    print(f"attack slots the port publishes: {port_slots}")

    # ---- A1: the same slots, from the same nodes -------------------------- #
    missing, extra = [], []
    for path in images:
        mob_id = path.rsplit("/", 1)[-1].rsplit(".", 1)[0]
        want = set(reference[path])
        got = {a.get("index") for a in published.get(path, []) if isinstance(a, dict)}
        for index in sorted(want - got):
            missing.append(f"{mob_id}/attack{index}")
        for index in sorted(got - want):
            extra.append(f"{mob_id}/attack{index}")
    Check("A1 the port publishes exactly the attack slots the reference admits",
          not missing and not extra,
          f"{len(missing)} slots the reference has and the port does not "
          f"(first: {missing[0] if missing else ''}); "
          f"{len(extra)} slots the port has and the reference does not "
          f"(first: {extra[0] if extra else ''})")

    # ---- A2: the duration ------------------------------------------------ #
    duration_bad, duration_examples = 0, []
    for path in images:
        mob_id = path.rsplit("/", 1)[-1].rsplit(".", 1)[0]
        specs = {a.get("index"): a for a in published.get(path, []) if isinstance(a, dict)}
        for index, want in reference[path].items():
            spec = specs.get(index)
            if spec is None:
                continue
            if want["duration"] is None:
                continue
            if spec.get("durationMs") != want["duration"]:
                duration_bad += 1
                if len(duration_examples) < 5:
                    duration_examples.append(
                        f"{mob_id}/attack{index}: the port says "
                        f"{spec.get('durationMs')} ms, the reference says "
                        f"{want['duration']} ms")
    Check("A2 an attack's duration is the sum of its frames' delays, "
          "100 ms where a frame declares none",
          duration_bad == 0,
          f"{duration_bad} slots disagree; first: "
          f"{duration_examples[0] if duration_examples else ''}")

    # ---- A3: the hit ----------------------------------------------------- #
    hit_bad, hit_examples = 0, []
    for path in images:
        mob_id = path.rsplit("/", 1)[-1].rsplit(".", 1)[0]
        specs = {a.get("index"): a for a in published.get(path, []) if isinstance(a, dict)}
        for index, want in reference[path].items():
            spec = specs.get(index)
            if spec is None or want["hit"] is None:
                continue
            if spec.get("delayMs") != want["hit"]:
                hit_bad += 1
                if len(hit_examples) < 5:
                    hit_examples.append(
                        f"{mob_id}/attack{index}: the port says "
                        f"{spec.get('delayMs')} ms, the reference says "
                        f"{want['hit']} ms")
    Check("A3 an attack's hit lands at info/attackAfter ms",
          hit_bad == 0,
          f"{hit_bad} slots disagree; first: "
          f"{hit_examples[0] if hit_examples else ''}")

    # ---- A4: the projectile's own frame delays --------------------------- #
    ball_bad, ball_examples = 0, []
    for path in images:
        mob_id = path.rsplit("/", 1)[-1].rsplit(".", 1)[0]
        specs = {a.get("index"): a for a in published.get(path, []) if isinstance(a, dict)}
        for index, want in reference[path].items():
            spec = specs.get(index)
            if spec is None or want["ball"] is None:
                continue
            got = spec.get("ballFrameDelays")
            if not isinstance(got, list):
                got = []
            if list(got) != list(want["ball"]):
                ball_bad += 1
                if len(ball_examples) < 5:
                    ball_examples.append(
                        f"{mob_id}/attack{index}: the port says {got}, the "
                        f"reference says {want['ball']}")
    Check("A4 a projectile's frames run on the ball's own delays",
          ball_bad == 0,
          f"{ball_bad} slots disagree; first: "
          f"{ball_examples[0] if ball_examples else ''}")

    # ---- A5: the known answers ------------------------------------------- #
    mob_id, index, want_duration, want_hit = KNOWN_SLOT
    path = f"{fid}/{mob_id}.img"
    specs = {a.get("index"): a for a in published.get(path, []) if isinstance(a, dict)}
    spec = specs.get(index)
    want = reference.get(path, {}).get(index, {})
    Check(f"A5 the reference's duration for {mob_id}/attack{index} is "
          f"{want_duration} ms",
          want.get("duration") == want_duration,
          f"the reference's rule says {want.get('duration')}")
    Check(f"A5b and the port reports {want_duration} ms for it",
          spec is not None and spec.get("durationMs") == want_duration,
          f"the port reports {spec.get('durationMs') if spec else 'nothing'}")
    Check(f"A5c the reference's hit for {mob_id}/attack{index} is {want_hit} ms",
          want.get("hit") == want_hit,
          f"the reference's rule says {want.get('hit')}")

    mob_id, index, want_duration, want_hit = KNOWN_LINKED_SLOT
    path = f"{fid}/{mob_id}.img"
    specs = {a.get("index"): a for a in published.get(path, []) if isinstance(a, dict)}
    spec = specs.get(index)
    want = reference.get(path, {}).get(index, {})
    Check(f"A5d the reference gives {mob_id} the attack of the mob its "
          f"info/link names ({want_duration} ms)",
          want.get("duration") == want_duration,
          f"the reference's rule says {want.get('duration')}")
    Check(f"A5e and the port publishes that attack for {mob_id}",
          spec is not None and spec.get("durationMs") == want_duration,
          f"the port reports {spec.get('durationMs') if spec else 'nothing'}")

    print()
    print(f"population: {len(images)} mob images, {len(action_paths)} attack "
          f"nodes read, {ref_mobs} mobs with attacks, {ref_slots} slots the "
          f"reference admits, {port_slots} the port publishes")
    print(f"VERDICT: attack-frame-timing {asserts - fails}/{asserts} assertions, "
          f"{fails} failed")
    if fails:
        print(">>> RED: attack-frame-timing -- the port and the reference disagree")
    return 1 if fails else 0


if __name__ == "__main__":
    raise SystemExit(main())
