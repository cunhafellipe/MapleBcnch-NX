"""Read the REAL z / priority / slots numbers for a body pose, the way CharLook does.

The reference (MPSH clean-client/src/Character/Look/) does:
  z_of(name)        -> Base.nx  zmap.img[name]            (int)
  priority_of(str)  -> min zmap value among the 2-char codes of info/islot
  claimed(z, vslot) -> smap.img[z]  AND  mask(info/vslot)      (two-char codes)
Both tables and both info nodes come out of the archive; nothing is inferred.

Usage: python layerdata.py [MPSHS] [state] [frame]
"""

import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from nxparse import NxFile, INTEGER, STRING, VECTOR, BITMAP  # noqa: E402


def by_name(f, n, name):
    for c in f.child_nodes(n):
        if f.string_at(c["name"]) == name:
            return c
    return None


def node_map(f, n):
    """child name -> child node"""
    return {f.string_at(c["name"]): c for c in f.child_nodes(n)}


def find_path(f, start, parts):
    n = start
    for p in parts:
        n = by_name(f, n, p)
        if n is None:
            return None
    return n


def read_zmap(base):
    root = f_root = f_root_node(base, 0)
    node = by_name(base, root, "zmap.img")
    if node is None:
        raise SystemExit("Base.nx has no zmap.img (children: %s)"
                         % [base.string_at(c["name"]) for c in base.child_nodes(root)][:20])
    out = {}
    for c in base.child_nodes(node):
        if c["type"] == INTEGER:
            out[base.string_at(c["name"])] = c["i"]
    return out


def read_smap(base):
    root = f_root_node(base, 0)
    node = by_name(base, root, "smap.img")
    if node is None:
        return {}
    out = {}
    for c in base.child_nodes(node):
        if c["type"] == STRING:
            out[base.string_at(c["name"])] = base.string_at(c["s"])
    return out


def f_root_node(f, index=0):
    return f.nodes[index]


def mask_of(codes, order):
    """Two-char code -> bit, assigned in first-seen order, exactly like Tables::mask."""
    result = 0
    for i in range(0, len(codes) - 1, 2):
        code = codes[i:i + 2]
        if code not in order:
            order[code] = len(order)
        if order[code] < 64:
            result |= 1 << order[code]
    return result


def zname_of(f, part):
    """AvatarLayers::zname_of -- the part's "z" string, else its own name."""
    m = node_map(f, part)
    z = m.get("z")
    if z is not None and z["type"] == STRING:
        return f.string_at(z["s"])
    return f.string_at(part["name"])


def z_of(zmap, name, fallback):
    return zmap.get(name, fallback)


def priority_of(zmap, islot):
    r = 32767
    for i in range(0, len(islot) - 1, 2):
        z = zmap.get(islot[i:i + 2], r)
        r = min(r, z)
    return r


def main():
    root_dir = sys.argv[1] if len(sys.argv) > 1 else r"C:\MUWORK\GAME\MPSHS"
    state = sys.argv[2] if len(sys.argv) > 2 else "stand1"
    frame = sys.argv[3] if len(sys.argv) > 3 else "0"

    base = NxFile(os.path.join(root_dir, "Base.nx"))
    zmap = read_zmap(base)
    smap = read_smap(base)
    print("Base.nx   zmap.img: %d entries, smap.img: %d entries" % (len(zmap), len(smap)))
    print()

    ch = NxFile(os.path.join(root_dir, "Character.nx"))
    chroot = ch.nodes[0]

    for skin in ("00002000.img", "00012000.img"):
        body = by_name(ch, chroot, skin)
        if body is None:
            continue
        info = node_map(ch, body).get("info")
        vslot = islot = None
        if info is not None:
            im = node_map(ch, info)
            if "vslot" in im and im["vslot"]["type"] == STRING:
                vslot = ch.string_at(im["vslot"]["s"])
            if "islot" in im and im["islot"]["type"] == STRING:
                islot = ch.string_at(im["islot"]["s"])
        order = {}
        own_slots = mask_of(vslot or "", order)
        own_prio = priority_of(zmap, islot or "")
        print("== %s  vslot=%r islot=%r" % (skin, vslot, islot))
        print("   priority_of(islot) = %d" % own_prio)

        st = by_name(ch, body, state)
        if st is None:
            print("   no %s" % state)
            continue
        fr = by_name(ch, st, frame)
        if fr is None:
            print("   no %s/%s" % (state, frame))
            continue

        # Draw a part the way Body.cpp does it: the map node gives the anchor
        # point, the "z" node gives the layer name.
        parts = []
        for c in ch.child_nodes(fr):
            name = ch.string_at(c["name"])
            if name in ("delay", "face") or c["type"] != BITMAP:
                continue
            zn = zname_of(ch, c)
            cm = node_map(ch, c)
            zp = cm.get("z")
            zval = zp["i"] if (zp is not None and zp["type"] == INTEGER) else None
            anchor_name, anchor_val = None, None
            mp = cm.get("map")
            if mp is not None:
                for mk, mv in node_map(ch, mp).items():
                    if mv["type"] == VECTOR:
                        anchor_name, anchor_val = mk, mv["xy"]
            slots_raw = smap.get(zn, "")
            claimed = mask_of(slots_raw, order) & own_slots if slots_raw else 0
            parts.append({
                "name": name,
                "zname": zn,
                "zmap": z_of(zmap, zn, 0),
                "zprop": zval,
                "anchor": (anchor_name, anchor_val),
                "slots": claimed,
                "slotsraw": slots_raw,
                "priority": own_prio,
                "vsize": (c["bm"][1], c["bm"][2]),
            })

        print("   parts in collection order (Body.cpp order == WZ child order):")
        for i, p in enumerate(parts):
            print("     %d. %-10s z=%-4s(%-13s) zmap=%-5d slots=0x%016x anchor=%s%s size=%dx%d"
                  % (i, p["name"], p["zname"], p["zprop"], p["zmap"], p["slots"],
                     p["anchor"][0], p["anchor"][1], p["vsize"][0], p["vsize"][1]))

        order_out = sorted(parts, key=lambda p: -p["zmap"])
        print("   std::stable_sort by z DESC (CharLook.cpp:175):")
        for i, p in enumerate(order_out):
            print("     %d. %-10s zmap=%d" % (i, p["name"], p["zmap"]))
        print()


if __name__ == "__main__":
    main()