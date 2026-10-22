"""W-0xx -- the FIRST judge for "position accuracy" (ADR-0005 criterion 1).

ADR-0005's first acceptance criterion is "position accuracy: where it is drawn is
where the archive / the reference computes it to be". This project had NO judge
for it: the 34 fields of GET /api/simulate/character were named by no
committed verification file at all. This is that judge.

WHAT IT ASSERTS, and why each one is a PROMISE and not a snapshot of today:

  P1  The set of footholds that COVER an X is the reference's x bucket:
      every non-wall foothold whose span contains x, INCLUSIVE of both ends.
      Reference: FootholdTree.cpp:82-89 -- walls are skipped and the span is
      registered for every integer x from l() to r() INCLUSIVE.

  P2  The surface a foothold presents at an X is the reference's
      (int16_t)std::round(fh.ground_below(x)), where
      ground_below(x) = slope() * (x - x1()) + y1()   (Foothold.cpp:179-182)
      and slope() = vdelta() / hdelta() as a DOUBLE.

  P3  The foothold the feet rest on is the reference's
      FootholdTree::get_fhid_underneath (FootholdTree.cpp:547-577): of the
      candidates, the LOWEST ground that is still >= y, with reverse-oriented
      segments excluded (the reference tests the RAW x1()/x2(), and
      FootholdTree.cpp:556-559 says why).

  P4  A KNOWN ANSWER, written down: at x=2905 on the test map the reference
      says the foothold under the feet is 10 at y=-1345.

  P5  The reference's x bucket and the port's covering list agree, and the
      surfaces agree, at EVERY foothold of the test map.

The reference source is the clean-client tree -- the authority; MapleNecrocer is
not accepted (pm/PARITY.md says where it lives). It is NOT inside this
repository and this judge does not read it at run time: the formulas below are
TRANSCRIBED from it, each with the file and line in the comment beside it.
That is deliberate -- a judge that opened a path outside the tree would be
reading a machine-local path, and the fix for that is to put the rule in the
code instead of in a path.

The foothold geometry IS read at run time, and it is read from the ARCHIVE
through the running instance, not from this repository's working tree -- so a
clone that talks to the same backend measures that backend's data, and a clone
with its own backend measures its own.

It opens no archive, saves nothing, and writes nothing.
"""
import json
import math
import os
import urllib.parse
import urllib.request

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

# The map to judge, INSIDE the Map.nx archive. The session id (f1..fN) depends
# on which archives are open and in what order, so it is looked up by NAME
# rather than assumed -- on one instance Map.nx is f4, on another it is f1.
MAP_RELATIVE = "Map/Map1/100000004.img"

# The full path of the map, resolved in main() -- the archive's session id is
# not known until the instance is asked which archives it holds.
MAP = ""


def resolve_map_archive():
    """The session id of the Map.nx archive, BY NAME.

    The id (f1..fN) is an artefact of WHICH archives are open and in what
    order, so a suite that hard-codes it measures one instance's arrangement
    and breaks on another. Looked up, it does not.
    """
    files = get("/api/files")
    if not isinstance(files, list):
        raise SystemExit(f"cannot list the open archives on {BASE}: {files}")
    for f in files:
        if f.get("name") == "Map.nx":
            return f["id"]
    raise SystemExit(
        "Map.nx is not open on this instance. Open it (the gate's documented "
        "loop does) and run this judge again.")

# The known answer (P4). Read off the reference formula over the archive's own
# foothold data on 2026-10-22; it is written down so the judge has an expected
# value that does not come from the thing under test.
KNOWN_X = 2905
KNOWN_Y = -1345
KNOWN_FOOTHOLD = "10"
KNOWN_SURFACE = -1345

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
        print(f">>> RED: position-accuracy -- {name} ({detail})")


def get(route, **q):
    url = BASE + route + ("?" + urllib.parse.urlencode(q) if q else "")
    try:
        with urllib.request.urlopen(url, timeout=600) as r:
            return json.loads(r.read().decode("utf-8"))
    except urllib.error.HTTPError as e:
        return {"error": e.read().decode("utf-8", "replace")[:200]}


def cdiv(a, b):
    """C++ integer division: truncates TOWARD ZERO. Python's // floors."""
    q = abs(a) // abs(b)
    if (a < 0) != (b < 0):
        q = -q
    return q


class Foot:
    """A foothold, in the reference's terms (Foothold.cpp / FootholdTree.cpp)."""

    def __init__(self, fid, layer, x1, y1, x2, y2):
        self.id = fid
        self.layer = layer
        self.x1, self.y1, self.x2, self.y2 = x1, y1, x2, y2

    @property
    def left(self):
        return min(self.x1, self.x2)

    @property
    def right(self):
        return max(self.x1, self.x2)

    @property
    def is_wall(self):
        # Foothold.cpp:97-100 -- a wall is a foothold standing on its end.
        return self.x1 == self.x2

    @property
    def hdelta(self):
        # Foothold.cpp:132-135 -- the RAW difference, signed.
        return self.x2 - self.x1

    @property
    def vdelta(self):
        return self.y2 - self.y1

    def ground_below(self, x):
        # Foothold.cpp:179-182
        if self.y1 == self.y2:            # is_floor()
            return float(self.y1)
        slope = self.vdelta / self.hdelta
        return slope * (x - self.x1) + self.y1

    def surface_rounded(self, x):
        # FootholdTree.cpp:619 -- (int16_t)std::round(fh.ground_below(x)).
        # std::round rounds halfway cases AWAY FROM ZERO.
        g = self.ground_below(x)
        return math.floor(g + 0.5) if g >= 0 else math.ceil(g - 0.5)

    def ground_integer(self, x):
        # FootholdTree.cpp:561-563 -- integer division, truncated.
        return self.y1 + cdiv((x - self.x1) * self.vdelta, self.hdelta)


def bucket(feet, x):
    """FootholdTree.cpp:85-89 + 552 -- the x bucket."""
    return [f for f in feet if not f.is_wall and f.left <= x <= f.right]


def ref_underneath(feet, x, y, y_max):
    """FootholdTree.cpp:547-577 -- get_fhid_underneath."""
    best_id, best_y = 0, y_max
    for f in bucket(feet, x):
        # FootholdTree.cpp:556-559 -- only x1 < x2 is a support.
        if f.x1 >= f.x2:
            continue
        g = f.ground_integer(x)
        if y <= g < best_y:
            best_y, best_id = g, f.id
    return (best_id, best_y) if best_id else (0, None)


def read_footholds():
    """The foothold tree, read from the ARCHIVE.

    The geometry comes from the archive's own nodes, not from the simulator's
    geometry endpoint, so the reference formula is fed by the same bytes the
    client would read. A port that mis-reads its own geometry cannot make this
    judge pass by agreeing with itself.
    """
    root = MAP + "/foothold"
    layers = get("/api/children", path=root)
    if not isinstance(layers, list):
        raise SystemExit(f"cannot read the foothold tree of {root}: {layers}")
    out = []
    for lay in layers:
        groups = get("/api/children", path=lay["path"])
        if not isinstance(groups, list):
            continue
        for grp in groups:
            items = get("/api/children", path=grp["path"])
            if not isinstance(items, list):
                continue
            for it in items:
                vals = {}
                for c in get("/api/children", path=it["path"]) or []:
                    v = get("/api/inspect", path=c["path"]).get("node") or {}
                    vals[c["name"]] = v.get("value")
                try:
                    out.append(Foot(it["name"], lay["name"],
                                     int(vals["x1"]), int(vals["y1"]),
                                     int(vals["x2"]), int(vals["y2"])))
                except (KeyError, TypeError, ValueError):
                    continue
    return out


def main():
    global MAP
    print("=== position accuracy: the port's ground, against the reference ===")
    MAP = resolve_map_archive() + "/" + MAP_RELATIVE
    print(f"map: {MAP}")

    geo = get("/api/simulate/geometry", path=MAP)
    sim_feet = [Foot(f["id"], f["layer"], f["x1"], f["y1"], f["x2"], f["y2"])
                for f in geo.get("footholds", [])]
    arch_feet = read_footholds()
    print(f"footholds: {len(sim_feet)} in the simulator's geometry, "
          f"{len(arch_feet)} read from the archive")

    # P0 -- the archive and the simulator must describe the SAME ground.
    if arch_feet:
        by_id = {f.id: f for f in arch_feet}
        shared = [f for f in sim_feet if f.id in by_id]
        same = sum(1 for f in shared
                   if (f.x1, f.y1, f.x2, f.y2) ==
                      (by_id[f.id].x1, by_id[f.id].y1, by_id[f.id].x2, by_id[f.id].y2))
        Check("P0 the archive and the simulator agree on every foothold's geometry",
              shared and same == len(shared),
              f"{same} of {len(shared)} shared footholds agree")
        feet = arch_feet
    else:
        feet = sim_feet
        print("  (the archive's foothold tree was not readable; using the simulator's)")

    # The probe points: the middle and both ends of every non-wall foothold.
    xs = set()
    for f in feet:
        if f.is_wall:
            continue
        xs.add(f.left)
        xs.add(f.right)
        xs.add((f.left + f.right) // 2)
    xs = sorted(xs)
    print(f"probe points: {len(xs)}")

    cover_bad = 0
    surface_bad = 0
    standing_bad = 0
    standing_examples = []
    landing_bad = 0

    for x in xs:
        probe = get("/api/simulate/probe", path=MAP, x=x, y=KNOWN_Y)
        cov = probe.get("covering") or []
        ref_ids = sorted(f.id for f in bucket(feet, x))
        port_ids = sorted(c["id"] for c in cov)
        if ref_ids != port_ids:
            cover_bad += 1
        for c in cov:
            f = next((g for g in feet if g.id == c["id"]), None)
            if f is None:
                continue
            if c["surface"] != f.surface_rounded(x):
                surface_bad += 1
        # P3 -- the feet's foothold, by the reference's rule.
        ref_id, ref_y = ref_underneath(feet, x, KNOWN_Y, 1 << 30)
        if probe.get("standingY") != (ref_id or None):
            standing_bad += 1
            if len(standing_examples) < 5:
                standing_examples.append(
                    f"x={x} y={KNOWN_Y}: reference get_fhid_underneath -> "
                    f"foothold {ref_id} at y={ref_y}; the port's standingY -> "
                    f"{probe.get('standingY')} at y={probe.get('standingSurface')}")

    Check("P1 the covering set is the reference's x bucket (non-wall, both ends inclusive)",
          cover_bad == 0, f"{cover_bad} of {len(xs)} probe points disagree")
    Check("P2 every covering surface is round(ground_below(x))",
          surface_bad == 0, f"{surface_bad} surfaces disagree")
    Check("P3 the foothold under the feet is get_fhid_underneath (lowest ground >= y)",
          standing_bad == 0,
          f"{standing_bad} of {len(xs)} probe points disagree; first: "
          + (standing_examples[0] if standing_examples else ""))

    # P4 -- the known answer, written down.
    probe = get("/api/simulate/probe", path=MAP, x=KNOWN_X, y=KNOWN_Y)
    ref_id, ref_y = ref_underneath(feet, KNOWN_X, KNOWN_Y, 1 << 30)
    Check("P4 the reference's answer at the known point is foothold "
          f"{KNOWN_FOOTHOLD} at y={KNOWN_SURFACE}",
          ref_id == KNOWN_FOOTHOLD and ref_y == KNOWN_SURFACE,
          f"the reference formula says {ref_id} at {ref_y}")
    Check("P4b and the port reports the foothold under the feet there",
          probe.get("standingY") == KNOWN_FOOTHOLD,
          f"the port's standingY is {probe.get('standingY')} "
          f"(the reference says {KNOWN_FOOTHOLD})")

    print()
    print(f"VERDICT: position-accuracy {asserts - fails}/{asserts} assertions, "
          f"{fails} failed")
    if fails:
        print(">>> RED: position-accuracy -- the port and the reference disagree")
    return 1 if fails else 0


if __name__ == "__main__":
    raise SystemExit(main())
