"""ADR-0005 criterion 2 -- "mechanism accuracy", judged on the attack's DAMAGE.

ADR-0005's second acceptance criterion is "mechanism accuracy: does the
simulator compute what the reference computes". Criterion 1 (position
accuracy) has a judge -- tools/verify/suites/position-accuracy.py -- and the
frames and their timing have one -- attack-frame-timing.py. This one judges the
other two numbers the same attack's info node carries:

  HOW HARD the attack hurts, and
  HOW FAST the projectile it throws travels.

WHAT IT ASSERTS, and why each one is a PROMISE and not a snapshot of today:

  A1  An attack's DAMAGE is `MADamage` when the attack is magic and `PADamage`
      when it is not -- taken from the ATTACK's own info node, and when the
      attack declares neither, from the MOB's own `info/MADamage` or
      `info/PADamage`. The second half is the load-bearing half: most attacks
      in this archive declare no damage of their own, and the reference does
      not read that as "this attack deals nothing".

  A2  An attack is magic only when its `magic` node is an INTEGER that is not
      zero. A missing node, a string, and a real are all "not magic".

  A3  A projectile's speed is `max(1, info/bulletSpeed)` with 100 for an
      attack that declares none.

  A4  An attack's geometry type is `info/type` read as an integer, and 0 when
      it declares none. It is the switch that decides whether the bullet path
      runs at all (MobAttack.cpp:148, :202).

  A5  Six KNOWN ANSWERS, read off the archive by hand on 2026-10-22 and written
      down here, so the judge has expected values that do not come from the
      thing under test.

The reference source is the clean-client tree -- the authority; MapleNecrocer is
not accepted (pm/PARITY.md says where it lives). It is NOT inside this
repository and this judge does not read it at run time: the rules below are
TRANSCRIBED from it, each with the file and the line in the comment beside it.
That is deliberate -- a judge that opened a path outside the tree would be
reading a machine-local path, and the fix for that is to put the rule in the
code instead of in a path. The judge reads NO file of this repository at all,
so it needs no repository root and no environment variable to find one. (A
transcription is still a copy and a copy can be wrong: the first draft of this
file read a string `delay` as 0, and the string case is spelled out below with
its file and line so that the next reader can check it rather than trust it.)

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
import re
import urllib.error
import urllib.parse
import urllib.request
from concurrent.futures import ThreadPoolExecutor

# The gate's own override, so a gate pointed at another instance reaches this
# judge too (the same two lines the other suites use).
try:
    from archivelib import require_gate_base
except ImportError:            # a missing module must not read as a pass
    print(">>> RED: archivelib.py is missing next to this suite")
    raise SystemExit(1)
except SyntaxError as exc:    # a helper that does not PARSE is not missing
    print(f">>> RED: archivelib.py next to this suite does not parse: {exc}")
    raise SystemExit(1)
BASE = require_gate_base()

# The archive the mobs live in. Its session id (f1..fN) depends on which
# archives are open and in what order, so it is looked up by NAME rather than
# assumed -- on one instance Mob.nx is f5, on another it is f1.
ARCHIVE_NAME = "Mob.nx"

# How many requests to keep in flight. The census is a few thousand small
# reads, and the backend serialises node reads behind one lock, so this is a
# small number on purpose.
THREADS = 8

# The known answers (A5), read off Mob.nx by hand on 2026-10-22.
#
# 2400049 attack3 declares no damage of its own; the mob's own
# info/PADamage is 1500 (Long) and info/MADamage is also 1500, and the attack
# is not magic -- so the reference's damage is 1500. This is the case the
# port answers 0 for.
KNOWN_FALLBACK_SLOT = ("2400049", 3, 1500)
#
# 3110301 attack1 declares PADamage 125 while the mob's own info/PADamage is
# 90: the attack's own number wins, so the reference's damage is 125 and NOT
# 90. This is the case that catches a judge which always prefers the mob.
KNOWN_OWN_SLOT = ("3110301", 1, 125)
#
# 2400212 attack1 is magic (magic = Long 1) and declares no MADamage; the mob's
# own info/MADamage is 900. A magic attack reads MADamage on both sides.
KNOWN_MAGIC_FALLBACK_SLOT = ("2400212", 1, 900)
#
# 2400212 attack3 is not magic and declares PADamage 1000 while the mob's own
# info/PADamage is 950: the attack's own number wins again, on the physical
# side of the same mob.
KNOWN_OWN_SLOT_2 = ("2400212", 3, 1000)
#
# 8820003 attack3 carries its PADamage as the STRING "900" and its mob's own
# info/PADamage is 1440. node.cpp:168-169 parses a string with std::stoll, so
# the reference's damage is 900. This is the case a judge that treats a string
# as unreadable gets wrong, in the direction of blaming the port.
KNOWN_STRING_SLOT = ("8820003", 3, 900)
#
# 3220000 attack2 declares bulletSpeed 140 and is a type-2 attack.
KNOWN_BULLET_SLOT = ("3220000", 2, 140)

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
        print(f">>> RED: attack-damage -- {name} ({detail})")


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

# nlnx/node.hpp:37-44 -- the data types a node can have, and nothing else:
#   none = 0, integer = 1, real = 2, string = 3, vector = 4, bitmap = 5,
#   audio = 6
# The type names below are the ones the instance reports for each of those.
INT_TYPES = {"Int", "Short", "Long", "Byte", "SByte", "UShort", "UInt", "ULong", "UByte"}
REAL_TYPES = {"Double", "Float", "Single"}
STRING_TYPES = {"String"}
# node.cpp:159-163 -- none, vector, bitmap and audio all return the DEFAULT.
DEFAULT_ZERO_TYPES = {"SubProperty", "Null", "Vector", "Canvas", "Sound", "Convex"}

INT64_MIN = -(2 ** 63)
INT64_MAX = 2 ** 63 - 1

_STOLL = re.compile(r"\s*([+-]?[0-9]+)")


def node_int(kind, value):
    """nl::node::get_integer -- vendor/NoLifeNx/nlnx/node.cpp:155-173.

    An absent node is the default (0), an integer is itself, a real is
    truncated, and a STRING is parsed with std::stoll -- so "110" is the number
    110, not a value the reference cannot read. A string that does not start
    with a number makes the reference THROW (std::invalid_argument), and so
    does one whose number does not fit in an int64; None stands for "the
    reference throws here", so the judge counts it instead of pretending it
    did not happen.

    std::stoll also STOPS at the first character it cannot use: "650x" is 650.
    int.TryParse would reject that string outright, which is why the two sides
    can disagree on a string and why this function is a transcription and not
    the port's rule.
    """
    if kind is None:
        return 0                                   # the node is absent
    if kind in INT_TYPES:
        try:
            number = int(str(value).strip())
        except (TypeError, ValueError):
            return None
    elif kind in REAL_TYPES:
        try:
            number = int(float(str(value).strip()))
        except (TypeError, ValueError):
            return None
    elif kind in STRING_TYPES:
        match = _STOLL.match(str(value or ""))
        if match is None:
            return None                            # the reference throws
        number = int(match.group(1))
    elif kind in DEFAULT_ZERO_TYPES:
        return 0                                   # node.cpp:159-163
    else:
        return None                                # a type this judge does not know
    if number < INT64_MIN or number > INT64_MAX:
        return None                                # std::stoll throws out_of_range
    return number


def node_bool(kind, value):
    """nl::node::get_bool -- vendor/NoLifeNx/nlnx/node.cpp:227-229.

    `m_data && m_data->type == type::integer && to_integer() ? true : false`
    -- so only an INTEGER node can be true, and a string "1" or a real 1.0 is
    NOT magic.
    """
    if kind not in INT_TYPES:
        return False
    try:
        return int(str(value).strip()) != 0
    except (TypeError, ValueError):
        return False


def mob_stat(mob_info, key):
    """Mob.cpp:60-61 -- `watk = info["PADamage"]; matk = info["MADamage"];`.

    Read ONCE in the constructor from the MOB'S OWN info node -- never from the
    mob its info/link points at, because the link carries graphics only. The
    assignment is a node-to-int conversion, so it goes through node_int.
    """
    node = mob_info.get(key)
    if node is None:
        return 0
    return node_int(node[0], node[1])


def attack_power(data, mob_info):
    """Mob.cpp:160 -- attack.power.

        attack.power = attack.magic
            ? (data["MADamage"] ? data["MADamage"].get_integer() : matk)
            : (data["PADamage"] ? data["PADamage"].get_integer() : watk);

    The condition in each ternary is an nl::node, so it is the node's
    EXISTENCE (node.cpp:152-154), not its value: a MADamage of 0 is used as 0
    rather than falling through to the mob's stat.

    None means the reference would throw while reading one of the two nodes.
    """
    key = "MADamage" if node_bool(*(data.get("magic") or (None, None))) else "PADamage"
    node = data.get(key)
    if node is not None:
        return node_int(node[0], node[1])
    return mob_stat(mob_info, key)


def bullet_speed(data):
    """Mob.cpp:161 -- attack.bullet_speed.

        std::max<int32_t>(1, data["bulletSpeed"]
                              ? data["bulletSpeed"].get_integer() : 100);

    The condition is the node's existence again, so a bulletSpeed of 0 or a
    negative one is used as written and then floored at 1 -- the 100 is for an
    attack that declares no speed at all. None means the reference throws.
    """
    node = data.get("bulletSpeed")
    if node is None:
        return 100
    speed = node_int(node[0], node[1])
    if speed is None:
        return None
    return max(1, speed)


def attack_type(data):
    """Mob.cpp:169 -- `attack.geometry.type = data["type"]`.

    The node is stored whole and compared as an integer later
    (MobAttack.cpp:100, :148, :202 -- `type == 2` is a projectile), so the
    number this judge compares is the node's integer conversion, and a
    declared-nothing attack is 0. None means the reference throws.
    """
    node = data.get("type")
    if node is None:
        return 0
    return node_int(node[0], node[1])


def attack_lifetime(radius, speed):
    """MobAttack.cpp:157 -- a projectile's flight time, in ms.

        entry.lifetime = std::max(1, data.geometry.radius * 600 / data.bullet_speed);

    Derived from the two numbers this judge reads, and printed so that the
    bullet speed's consequence is visible next to the number itself.
    """
    return max(1, int(radius) * 600 // max(1, int(speed)))


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


def read_fields(path):
    """The direct children of one node, as {name: (type, value)}.

    /api/inspect reports each child's data TYPE as well as its value, and the
    reference's rules are stated in terms of that type: an integer, a real and
    a string are three different answers, and a string is parsed while the
    other two are not.
    """
    doc = get_json("/api/inspect", path=path)
    if not isinstance(doc, dict):
        return {}
    node = doc.get("node")
    if not isinstance(node, dict):
        return {}
    # An IMAGE node carries no `type` of its own -- it reports kind:"Image"
    # and hasChildren -- so "no type" is not the same as "not there". Testing
    # the type alone made every image read as an empty node and the judge
    # compared nothing while still printing a verdict; testing for the absence
    # of both the type and the children is the test that survives it.
    if node.get("type") in (None, "Null") and not node.get("hasChildren"):
        return {}
    fields = {}
    for c in doc.get("children") or []:
        if not isinstance(c, dict):
            continue
        fields[c.get("name")] = (c.get("type"), c.get("value"))
    return fields


def mob_id_of(path):
    name = path.rsplit("/", 1)[-1]
    return name[:-4] if name.endswith(".img") else name


def main():
    print("=== attack damage: the port's attack numbers, against the reference ===")
    fid = resolve_archive()
    print(f"archive: {ARCHIVE_NAME} (session {fid})")

    # Every mob image in the archive -- the whole population, no sample.
    images = [c["path"] for c in read_children(fid) if c.get("name", "").endswith(".img")]
    if not images:
        raise SystemExit(f"{ARCHIVE_NAME} (session {fid}) holds no mob images")
    print(f"mob images: {len(images)}")

    # The top level of every image: which actions it declares, and whether it
    # carries the stand or the fly that a link target must have.
    tops = dict(zip(images, parallel(images, read_fields)))
    own_info = dict(zip(images, parallel(images, lambda p: read_fields(p + "/info"))))
    print(f"mobs with their own info/PADamage: "
          f"{sum(1 for f in own_info.values() if 'PADamage' in f)}; "
          f"with info/MADamage: "
          f"{sum(1 for f in own_info.values() if 'MADamage' in f)}")

    # info/link -- Mob.cpp:48-57. It is the mob's OWN info's child and it names
    # the mob whose GRAPHICS this one reuses; it never carries the numbers.
    link_targets = {}
    for path, fields in own_info.items():
        node = fields.get("link")
        if node is None:
            continue
        number = node_int(node[0], node[1])
        if number is None or number <= 0:
            continue
        link_targets[path] = str(number).zfill(7)   # Mob.cpp:52 extend_id(..., 7)

    def animation_source(path):
        target = link_targets.get(path)
        if not target:
            return path
        target_path = f"{fid}/{target}.img"
        names = tops.get(target_path, {})
        if not ({"stand", "fly"} & set(names)):
            return path                               # Mob.cpp:55
        return target_path

    sources = {p: animation_source(p) for p in images}
    linked = sum(1 for p in images if sources[p] != p)
    print(f"mobs whose graphics come from another mob (info/link): {linked}")

    # Every attack node in the archive, as the action's children and the
    # action's info fields. A borrowed animation needs nothing extra: the mob
    # it is borrowed from is itself an image of this archive, so its own
    # actions are already in this set (and an unresolvable link falls back to
    # the mob's own image, above).
    wanted = set()
    for path in images:
        for name in tops.get(path, {}):
            if name.startswith("attack"):
                wanted.add((path, name))
    wanted = sorted(wanted)
    action_names = dict(zip(wanted, parallel(
        wanted, lambda t: list(read_fields(t[0] + "/" + t[1])))))
    info_fields = dict(zip(wanted, parallel(
        wanted, lambda t: read_fields(t[0] + "/" + t[1] + "/info"))))
    print(f"attack nodes read from the archive: {len(wanted)}")

    # The reference's numbers for every slot it admits (Mob.cpp:142-148).
    reference = {}
    ref_slots = 0
    for path in images:
        source = sources[path]
        slots = {}
        for index in range(1, 9):
            action = f"attack{index}"
            node = (source, action)
            # Mob.cpp:145-148 -- the animation comes from the source, the
            # numbers from the mob's own attack node when it has one, and a
            # slot exists only with a frame "0" AND an info node.
            names = action_names.get(node)
            if not names or "0" not in names:
                continue
            data = info_fields.get((path, action)) or info_fields.get(node)
            if not data:
                continue
            slots[index] = {
                "power": attack_power(data, own_info.get(path, {})),
                "magic": node_bool(*(data.get("magic") or (None, None))),
                "bulletSpeed": bullet_speed(data),
                "type": attack_type(data),
            }
        ref_slots += len(slots)
        reference[path] = slots
    ref_mobs = sum(1 for p in images if reference[p])
    print(f"mobs with attacks in the reference: {ref_mobs}; slots: {ref_slots}")

    # The port's own answer for every mob in the archive.
    def port_attacks(path):
        doc = get_json("/api/simulate/mob-attacks", mobId=mob_id_of(path))
        attacks = (doc or {}).get("attacks")
        return path, (attacks if isinstance(attacks, list) else [])

    published = dict(parallel(images, port_attacks))
    port_slots = sum(len(v) for v in published.values())
    print(f"attack slots the port publishes: {port_slots}")

    compared = 0
    unpublishable = 0
    throws = 0

    def compare(label, key, extract=lambda a: a):
        """Compare one published field against the reference, over every slot."""
        bad, examples = 0, []
        for path in images:
            mob_id = mob_id_of(path)
            specs = {a.get("index"): a for a in published.get(path, [])
                     if isinstance(a, dict)}
            for index, want in reference[path].items():
                spec = specs.get(index)
                if spec is None:
                    continue                     # not published: reported below
                if want[key] is None:
                    continue                     # the reference throws here
                got = extract(spec)
                if got != want[key]:
                    bad += 1
                    if len(examples) < 5:
                        examples.append(
                            f"{mob_id}/attack{index}: the port says {got!r}, the "
                            f"reference says {want[key]!r}")
        Check(label, bad == 0,
              f"{bad} slots disagree; first: "
              f"{examples[0] if examples else ''}")

    def first_divergent_power():
        """The first slot where the damage number is wrong, with its reason."""
        for path in images:
            mob_id = mob_id_of(path)
            specs = {a.get("index"): a for a in published.get(path, [])
                     if isinstance(a, dict)}
            for index, want in sorted(reference[path].items()):
                spec = specs.get(index)
                if spec is None or want["power"] is None:
                    continue
                if spec.get("power") == want["power"]:
                    continue
                data = (info_fields.get((path, f"attack{index}"))
                        or info_fields.get((sources[path], f"attack{index}"))
                        or {})
                key = "MADamage" if want["magic"] else "PADamage"
                own = data.get(key)
                mob_id_own = own_info.get(path, {}).get(key)
                reason = (f"the attack declares {key}={own[1]!r}"
                          if own is not None else
                          f"the attack declares no {key}, and the mob's own "
                          f"info/{key} is "
                          f"{mob_id_own[1] if mob_id_own else 'absent (0)'}")
                return (f"{mob_id}/attack{index}: the port says "
                        f"{spec.get('power')}, the reference says "
                        f"{want['power']}; {reason}")
        return ""

    # How many slots there are to compare at all, and what was skipped.
    for path in images:
        specs = {a.get("index") for a in published.get(path, [])
                 if isinstance(a, dict)}
        for index, want in reference[path].items():
            if index in specs:
                compared += 1
            else:
                unpublishable += 1
            for key in ("power", "bulletSpeed", "type"):
                if want[key] is None:
                    throws += 1
    print(f"slots the port publishes that this judge could compare: {compared}")
    print(f"slots the reference admits and the port does not publish: "
          f"{unpublishable} (that gap is judged by attack-frame-timing)")
    print(f"slots where the reference would throw while reading a number: {throws}")

    # ---- A1: the damage number ------------------------------------------ #
    bad, examples = 0, []
    for path in images:
        specs = {a.get("index"): a for a in published.get(path, [])
                 if isinstance(a, dict)}
        for index, want in reference[path].items():
            spec = specs.get(index)
            if spec is None or want["power"] is None:
                continue
            if spec.get("power") != want["power"]:
                bad += 1
    detail = (f"{bad} slots disagree; first: {first_divergent_power()}"
              if bad else "")
    Check("A1 an attack's damage is the attack's own MADamage/PADamage, or "
          "the mob's own when the attack declares neither", bad == 0, detail)

    # ---- A2: the magic flag --------------------------------------------- #
    compare("A2 an attack is magic only when info/magic is a non-zero integer",
            "magic", lambda a: bool(a.get("magic")))

    # ---- A3: the projectile's speed ------------------------------------- #
    compare("A3 a projectile's speed is max(1, info/bulletSpeed), 100 by default",
            "bulletSpeed", lambda a: a.get("bulletSpeed"))

    # ---- A4: the geometry type ------------------------------------------ #
    compare("A4 an attack's geometry type is info/type read as an integer",
            "type", lambda a: a.get("type"))

    # ---- A5: the known answers ------------------------------------------ #
    for label, (mob_id, index, want_power) in (
            ("a boss whose attacks declare no damage of their own",
             KNOWN_FALLBACK_SLOT),
            ("an attack that declares its own damage and outranks the mob's",
             KNOWN_OWN_SLOT),
            ("a magic attack that falls back to the mob's MADamage",
             KNOWN_MAGIC_FALLBACK_SLOT),
            ("the physical attack of the same mob, declaring its own",
             KNOWN_OWN_SLOT_2),
            ("an attack whose damage is the STRING \"900\"",
             KNOWN_STRING_SLOT)):
        path = f"{fid}/{mob_id}.img"
        got = reference.get(path, {}).get(index, {}).get("power")
        Check(f"A5 the reference's damage for {mob_id}/attack{index} is "
              f"{want_power} ({label})",
              got == want_power,
              f"the reference's rule says {got!r}")

    mob_id, index, want_speed = KNOWN_BULLET_SLOT
    path = f"{fid}/{mob_id}.img"
    got = reference.get(path, {}).get(index, {}).get("bulletSpeed")
    Check(f"A5 the reference's bullet speed for {mob_id}/attack{index} is "
          f"{want_speed}", got == want_speed,
          f"the reference's rule says {got!r}")
    spec = {a.get("index"): a for a in published.get(path, [])
            if isinstance(a, dict)}.get(index)
    Check(f"A5 and the port publishes that bullet speed",
          spec is not None and spec.get("bulletSpeed") == want_speed,
          f"the port reports {spec.get('bulletSpeed') if spec else 'nothing'}")

    # A projectile's flight time is derived from the two numbers this judge
    # reads (MobAttack.cpp:157), so it is printed where it can be checked.
    # The radius is read from the archive -- with the same own-node-first rule
    # the numbers use -- rather than from the port's spec, so the line is the
    # reference's arithmetic and not the port's.
    samples = []
    for path in images:
        for index, want in sorted(reference[path].items()):
            if want["type"] != 2 or want["bulletSpeed"] is None:
                continue
            action = f"attack{index}"
            source = info_fields.get((path, action))
            holder = path if source else sources[path]
            radius_node = read_fields(
                f"{holder}/{action}/info/range").get("r")
            if radius_node is None:
                continue
            radius = node_int(*radius_node)
            if radius is None:
                continue
            samples.append(
                f"{mob_id_of(path)}/{action}: radius {radius} px at a speed of "
                f"{want['bulletSpeed']} flies "
                f"{attack_lifetime(radius, want['bulletSpeed'])} ms "
                f"(MobAttack.cpp:157)")
            break
        if len(samples) >= 3:
            break
    for line in samples:
        print(f"  note  projectile flight: {line}")

    print()
    print(f"population: {len(images)} mob images, {len(wanted)} attack nodes "
          f"read, {ref_mobs} mobs with attacks, {ref_slots} slots the reference "
          f"admits, {port_slots} the port publishes, {compared} slots compared "
          f"here")
    print(f"VERDICT: attack-damage {asserts - fails}/{asserts} assertions, "
          f"{fails} failed")
    if fails:
        print(">>> RED: attack-damage -- the port and the reference disagree")
    return 1 if fails else 0


if __name__ == "__main__":
    raise SystemExit(main())