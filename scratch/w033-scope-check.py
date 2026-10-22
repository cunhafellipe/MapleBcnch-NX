"""W-033 scope check: what is ALREADY there, measured rather than assumed.

MapNodes.cs:80-81 says three spellings ship:
    Bgm09/DarkShadow        on 17,385 maps
    Bgm00.img/Silence       on 49
    no slash at all         on 4
and PortService.cs:5037-5039 records the trap that appending ".img" blindly once
produced "Bgm34.img.img".

wz_map_open's own description claims it returns bgm. Check that claim, then
resolve every spelling shape against Sound.nx. Only what is genuinely missing
is W-033's work.
"""
import json
import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "tools"))
os.environ["WZ_SERVER"] = "http://127.0.0.1:5591"

from maple_mcp import MapleMcp  # noqa: E402

SAMPLES = ["f4/Map/Map1/100000004.img", "f4/Map/Map0/000000000.img",
           "f4/Map/Map0/000000002.img", "f4/Map/Map0/000000001.img"]


def is_node(x):
    return isinstance(x, dict) and "name" in x and "type" in x


def candidates(val):
    """Every spelling the code could need, derived from the value rather than
    assumed -- including the .img-suffixed and slash-less forms MapNodes names."""
    v = val.strip()
    out = []
    if "/" in v:
        grp, name = v.split("/", 1)
        bare = grp[:-4] if grp.lower().endswith(".img") else grp
        out += [f"f14/{grp}.img/{name}",      # Bgm00.img/FloralLife (group already suffixed)
                f"f14/{bare}.img/{name}",     # Bgm00/FloralLife
                f"f14/{grp}/{name}",
                f"f14/Bgm/{grp}/{name}"]
    else:
        out += [f"f14/{v}.img", f"f14/{v}", f"f14/Bgm00.img/{v}"]
    seen, uniq = set(), []
    for c in out:
        if c not in seen:
            seen.add(c)
            uniq.append(c)
    return uniq


with MapleMcp() as m:
    print("=== does wz_map_open already return bgm? ===")
    for mp in SAMPLES:
        d = m.call("wz_map_open", {"path": mp})
        if not isinstance(d, dict):
            print(f"  {mp}: no doc")
            continue
        bgm = d.get("bgm")
        if bgm is None and isinstance(d.get("info"), dict):
            bgm = d["info"].get("bgm")
        keys = sorted(d.keys())[:14]
        print(f"  {mp}\n    bgm={bgm!r}\n    doc keys: {keys}")

    print("\n=== resolve every spelling against Sound.nx ===")
    for mp in SAMPLES:
        d = m.call("wz_map_open", {"path": mp})
        if not isinstance(d, dict):
            continue
        bgm = d.get("bgm") or (d.get("info") or {}).get("bgm")
        if not bgm:
            print(f"  {mp}: no bgm")
            continue
        hit = None
        for c in candidates(bgm):
            n = m.call("wz_asset_node", {"path": c})
            if is_node(n):
                hit = c
                break
        print(f"  {mp}\n    {bgm!r} -> {hit or '>>> UNRESOLVED'}")

    print("\n=== how common is each spelling? census over a slice of maps ===")
    lst = m.call("wz_map_list", {"limit": 60})
    paths = [r["path"] for r in (lst or {}).get("maps", [])] if isinstance(lst, dict) else []
    print(f"  wz_map_list returned {len(paths)} rows")
    shapes = {}
    unresolved = []
    for p in paths:
        d = m.call("wz_map_open", {"path": p})
        bgm = (d or {}).get("bgm") if isinstance(d, dict) else None
        if not bgm:
            shapes["(absent)"] = shapes.get("(absent)", 0) + 1
            continue
        if "/" in bgm:
            grp = bgm.split("/", 1)[0]
            key = ("slash + .img" if grp.lower().endswith(".img") else "slash only")
        else:
            key = "no slash"
        shapes[key] = shapes.get(key, 0) + 1
        if not any(is_node(m.call("wz_asset_node", {"path": c}))
                   for c in candidates(bgm)):
            unresolved.append((p, bgm))
    print(f"  shapes seen: {shapes}")
    print(f"  unresolved: {len(unresolved)} of {len(paths)}")
    for p, b in unresolved[:8]:
        print(f"    {p}  {b!r}")