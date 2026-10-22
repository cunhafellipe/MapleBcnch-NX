"""TASK 17 — read the real z / priority / slots / anchor numbers out of the NX.

Everything TASK 17 asserts about the composed avatar has to come out of the
archive, not out of a draft. This dumps, for one concrete pose:

  Base.nx       zmap.img, smap.img          -- the layer tables AvatarLayers reads
  Character.nx  00002000.img/stand1         -- body parts, their z and map points
               00012000.img/stand1         -- head part, its z and map points
               0002xxxx.img/stand1         -- one hair
               Face/000000xx.img           -- one face

so the composed order can be computed here and compared against what the server
later reports.

    python tools/probe/nx/task17-dump.py            # summary
    python tools/probe/nx/task17-dump.py --dump stand1
"""

import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import nxparse

NX_DIR = os.environ.get("NX_FIXTURE_DIR", r"C:\MUWORK\GAME\MPSHS")

VECTOR, INTEGER, STRING, BITMAP = 4, 1, 3, 5


def child_map(f, n):
    """name -> node, for one node's children.

    A child's `name` field is a string-table INDEX, not a string -- resolving it
    is what makes this a map keyed by the name the reference addresses it with.
    """
    return {f.string_at(c["name"]): c for c in f.child_nodes(n)}


def find(f, n, *names):
    """Walk down a child path by name. Returns None when the path ends."""
    cur = n
    for want in names:
        nxt = child_map(f, cur).get(want)
        if nxt is None:
            return None
        cur = nxt
    return cur


def root(f, *names):
    """Resolve a path from the archive root."""
    return find(f, f.nodes[0], *names)


def int_children(f, n):
    return {f.string_at(c["name"]): c["i"] for c in f.child_nodes(n) if c["type"] == INTEGER}


def str_children(f, n):
    return {f.string_at(c["name"]): f.string_at(c["s"]) for c in f.child_nodes(n) if c["type"] == STRING}


def point(f, n):
    """A VECTOR node's (x, y)."""
    return f.nodes[n["index"]]["xy"] if n["type"] == VECTOR else None


def map_points(f, part):
    """The `map` child of a part node: {pointName: (x, y)}."""
    m = child_map(f, part).get("map")
    if m is None:
        return {}
    return {f.string_at(c["name"]): point(f, c) for c in f.child_nodes(m) if c["type"] == VECTOR}


def zname_of(f, part):
    """AvatarLayers::zname_of -- the part's z string, else the part's own name."""
    z = child_map(f, part).get("z")
    if z is not None and z["type"] == STRING:
        return f.string_at(z["s"])
    return f.string_at(part["name"])


# ---------------------------------------------------------------- tables


def load_tables():
    base = nxparse.NxFile(os.path.join(NX_DIR, "Base.nx"))
    zmap_node = root(base, "zmap.img")
    zmap = int_children(base, zmap_node) if zmap_node else {}
    smap_node = root(base, "smap.img")
    smap = str_children(base, smap_node) if smap_node else {}
    return base, zmap, smap


def code_mask(codes, table):
    """Tables::mask -- two-letter codes to bits, numbered in first-seen order."""
    result = 0
    for i in range(0, len(codes) - 1, 2):
        code = codes[i:i + 2]
        if code not in table:
            if len(table) >= 64:
                continue
            table[code] = len(table)
        result |= 1 << table[code]
    return result


def decode_mask(maskval, table):
    inv = {bit: code for code, bit in table.items()}
    return "".join(sorted(inv[b] for b in range(64) if (maskval >> b) & 1))


# ---------------------------------------------------------------- poses


def dump_body(f, zmap, smap, codetab, stance="stand1", frame="0", verbose=False):
    """Body.cpp's stance loop: every bitmap part, its z, its map points."""
    img = root(f, "00002000.img")
    st = find(f, img, stance)
    if st is None:
        return []
    fr = find(f, st, frame)
    if fr is None:
        return []
    rows = []
    for c in f.child_nodes(fr):
        name = f.string_at(c["name"])
        if c["type"] != BITMAP or name in ("delay", "face"):
            continue
        z = zname_of(f, c)
        rows.append((name, z, zmap.get(z), map_points(f, c)))
    return rows


def dump_head(f, zmap, smap, codetab, stance="stand1", frame="0"):
    img = root(f, "00012000.img")
    st = find(f, img, stance)
    if st is None:
        return []
    fr = find(f, st, frame)
    if fr is None:
        return []
    rows = []
    for c in f.child_nodes(fr):
        name = f.string_at(c["name"])
        if c["type"] != BITMAP:
            continue
        z = zname_of(f, c)
        rows.append((name, z, zmap.get(z), map_points(f, c)))
    return rows


def dump_hair(f, zmap, smap, codetab, hairimg, stance="stand1", frame="0"):
    img = root(f, "Hair", hairimg)
    if img is None:
        return None, [], [], []
    info = find(f, img, "info")
    vslot = str_children(f, info).get("vslot", "") if info else ""
    islot = str_children(f, info).get("islot", "") if info else ""
    st = find(f, img, stance)
    if st is None and stance == "dead":
        st = find(f, img, "stand1")
    rows = []
    if st is not None:
        fr = find(f, st, frame)
        if fr is not None:
            for c in f.child_nodes(fr):
                if c["type"] != BITMAP:
                    continue
                z = zname_of(f, c)
                rows.append((f.string_at(c["name"]), z, zmap.get(z), map_points(f, c)))
    return img, vslot, islot, rows


def dump_face(f, zmap, smap, codetab, faceimg):
    """Face.cpp: the marker carries the FACE's z and slots, not a per-pose one."""
    img = root(f, "Face", faceimg)
    if img is None:
        return None, None, None, None
    info = find(f, img, "info")
    vslot = str_children(f, info).get("vslot", "") if info else ""
    islot = str_children(f, info).get("islot", "") if info else ""
    default = find(f, img, "default")
    canvases = []
    z = None
    if default is not None:
        for c in f.child_nodes(default):
            if c["type"] == BITMAP:
                canvases.append((f.string_at(c["name"]), f.display(c)))
                # Face.cpp:94 reads the z off the FACE CANVAS, not off the image.
                z = zname_of(f, c)
    return img, vslot, islot, z, canvases


def priority_of(islot, zmap):
    """AvatarLayers::priority_of -- the smallest zmap value in the islot."""
    result = 32767
    for i in range(0, len(islot) - 1, 2):
        z = zmap.get(islot[i:i + 2], result)
        result = min(result, z)
    return result


def anchors_for(f, zmap, smap, codetab, stance="stand1", frame="0"):
    """BodyDrawInfo::init -- the five position tables for one pose."""
    body_img = root(f, "00002000.img")
    head_img = root(f, "00012000.img")
    shift = {}

    st = find(f, body_img, stance)
    fr = find(f, st, frame) if st else None
    if fr is not None:
        for c in f.child_nodes(fr):
            name = f.string_at(c["name"])
            if name in ("delay", "face"):
                continue
            z = zname_of(f, c)
            for k, v in map_points(f, c).items():
                shift.setdefault(z, {}).setdefault(k, v)

    hst = find(f, head_img, stance)
    hfr = find(f, hst, frame) if hst else None
    head_part = None
    if hfr is not None:
        head_part = child_map(f, hfr).get("head")
    elif hst is not None and stance == "dead":
        head_part = find(f, head_img, "stand1", "0", "head")
    if head_part is not None:
        hz = zname_of(f, head_part)
        for k, v in map_points(f, head_part).items():
            shift.setdefault(hz, {}).setdefault(k, v)

    body_navel = shift.get("body", {}).get("navel")
    body_neck = shift.get("body", {}).get("neck")
    head_neck = shift.get("head", {}).get("neck")
    head_brow = shift.get("head", {}).get("brow")

    def sub(a, b):
        return None if a is None or b is None else (a[0] - b[0], a[1] - b[1])

    def add(a, b):
        return None if a is None or b is None else (a[0] + b[0], a[1] + b[1])

    arm_layer = "arm" if "arm" in shift else "armOverHair"
    arm = None
    if arm_layer in shift:
        arm = add(sub(shift[arm_layer].get("hand"), shift[arm_layer].get("navel")), body_navel)

    return {
        "body": body_navel,
        "head": sub(body_neck, head_neck),
        "face": add(sub(body_neck, head_neck), head_brow),
        "hair": add(sub(head_brow, head_neck), body_neck),
        "arm": arm,
        "armLayer": arm_layer,
        "hand": shift.get("handBelowWeapon", {}).get("handMove"),
        "shift": shift,
    }


def main():
    verbose = "--dump" in sys.argv
    base, zmap, smap = load_tables()
    codetab = {}
    ch = nxparse.NxFile(os.path.join(NX_DIR, "Character.nx"))

    print(f"Base.nx  zmap.img entries: {len(zmap)}   smap.img entries: {len(smap)}")

    stance, frame = "stand1", "0"
    if "--dump" in sys.argv:
        stance = sys.argv[sys.argv.index("--dump") + 1]

    a = anchors_for(ch, zmap, smap, codetab, stance, frame)
    print(f"\n== BodyDrawInfo anchors, {stance}/{frame} ==")
    for k in ("body", "head", "face", "hair", "arm", "hand"):
        print(f"  {k:6s} = {a[k]}")
    print(f"  arm layer chosen: {a['armLayer']}")
    print("  raw map points by layer:")
    for layer, pts in sorted(a["shift"].items()):
        print(f"    {layer:20s} {pts}")

    body_slots = code_mask("", codetab)  # Body.cpp reads its slots from the data
    body_slots_node = find(ch, root(ch, "00002000.img") or ch.nodes[0], "info")
    binfo = str_children(ch, body_slots_node) if body_slots_node else {}
    headinfo_node = find(ch, root(ch, "00012000.img"), "info")
    hinfo = str_children(ch, headinfo_node) if headinfo_node else {}

    print(f"\n== body/head info ==")
    print(f"  00002000.img vslot={binfo.get('vslot')!r} islot={binfo.get('islot')!r}")
    print(f"  00012000.img vslot={hinfo.get('vslot')!r} islot={hinfo.get('islot')!r}")

    body_vslot = code_mask(binfo.get("vslot", ""), codetab)
    head_vslot = code_mask(hinfo.get("vslot", ""), codetab)
    body_prio = priority_of(binfo.get("islot", ""), zmap)
    head_prio = priority_of(hinfo.get("islot", ""), zmap)
    print(f"  body slots bits={body_vslot:#018x} priority={body_prio}")
    print(f"  head slots bits={head_vslot:#018x} priority={head_prio}")

    print(f"\n== 00002000.img {stance}/{frame} parts ==")
    rows = dump_body(ch, zmap, smap, codetab, stance, frame)
    for name, z, zv, pts in sorted(rows, key=lambda r: -(r[2] if r[2] is not None else -999)):
        claimed = smap.get(z, "")
        cm = code_mask(claimed, codetab) & body_vslot
        print(f"  part={name:10s} z={z:18s} zmap={zv!s:5s} claimed={cm:#018x} ({decode_mask(cm, codetab)})")
        print(f"      map={pts}")

    print(f"\n== 00012000.img {stance}/{frame} parts ==")
    hrows = dump_head(ch, zmap, smap, codetab, stance, frame)
    for name, z, zv, pts in sorted(hrows, key=lambda r: -(r[2] if r[2] is not None else -999)):
        claimed = smap.get(z, "")
        cm = code_mask(claimed, codetab) & head_vslot
        print(f"  part={name:10s} z={z:18s} zmap={zv!s:5s} claimed={cm:#018x} ({decode_mask(cm, codetab)})")
        print(f"      map={pts}")

    # one hair and one face, so the composed list has all three anchors in it
    print("\n== hair ==")
    for name in ("00030000.img", "00040000.img", "00030010.img"):
        got = dump_hair(ch, zmap, smap, codetab, name, stance, frame)
        if not got or got[0] is None:
            continue
        _, vslot, islot, hrows = got
        vm = code_mask(vslot, codetab)
        print(f"  {name} vslot={vslot!r} islot={islot!r} priority={priority_of(islot, zmap)}")
        for pname, z, zv, pts in hrows:
            cm = code_mask(smap.get(z, ""), codetab) & vm
            print(f"    part={pname:12s} z={z:18s} zmap={zv!s:5s} claimed={cm:#018x} ({decode_mask(cm, codetab)})")
            print(f"        map={pts}")

    print("\n== face ==")
    for name in ("00020000.img", "00020001.img"):
        img, vslot, islot, z, canvases = dump_face(ch, zmap, smap, codetab, name)
        if img is None:
            continue
        vm = code_mask(vslot, codetab)
        cm = code_mask(smap.get(z, ""), codetab) & vm
        print(f"  Face/{name} z={z} zmap={zmap.get(z)} vslot={vslot!r} islot={islot!r} "
              f"priority={priority_of(islot, zmap)} claimed={cm:#018x} ({decode_mask(cm, codetab)})")
        print(f"    default canvases: {canvases}")
        break

    print(f"\ncode table ({len(codetab)} codes): " + " ".join(
        f"{c}=b{b}" for c, b in sorted(codetab.items(), key=lambda kv: kv[1])))


if __name__ == "__main__":
    main()