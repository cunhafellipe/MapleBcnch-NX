"""W-033 step 1: MEASURE. No code change.

The map declares `info/bgm`; the acceptance is "GET the bgm node, resolve it
under Sound.nx, play it on map entry". Before writing a line, measure:

  1. does `info/bgm` actually exist on maps, and what does it contain
  2. is it a plain string like 'Bgm00/FloralLife', or something else
  3. does that resolve to a real node under Sound.nx (f14), and at what path
  4. is the audio data reachable (an ext node? a .mp3? a canvas?)

CONTROL FIRST: the map must resolve, or none of the rest means anything.

Uses the project's own MCP (maplebench-assets) -- not ad-hoc HTTP.
"""
import json
import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "tools"))
os.environ["WZ_SERVER"] = "http://127.0.0.1:5591"

from maple_mcp import MapleMcp  # noqa: E402

MAPS = ["f4/Map/Map1/100000004.img", "f4/Map/Map0/000000000.img",
        "f4/Map/Map0/000000002.img"]


def show(v, n=160):
    s = json.dumps(v, ensure_ascii=False) if not isinstance(v, str) else v
    return s if len(s) <= n else s[:n] + "…"


def is_node(x):
    """wz_asset_node returns the NODE ITSELF, not {"node": …}. The first version of
    this script looked for the wrapper, found nothing, and would have printed 'no bgm
    node found' while the archive plainly carries one -- which is TRAPS T-8 wearing a
    different hat: a reader bug that reads exactly like absent data."""
    return isinstance(x, dict) and "name" in x and "type" in x


with MapleMcp() as m:
    caps = m.call("wz_map_capabilities", {})
    print(f"backend {m.backend}  server {m.server.get('name')} v{m.server.get('version')}")
    print(f"maps in the open archive: {caps.get('capabilities', {}).get('mapCount')}")

    print("\n=== 1. does info/bgm exist, and what is in it? ===")
    found = []
    for mp in MAPS:
        d = m.call("wz_map_doc", {"path": mp})
        node = m.call("wz_asset_node", {"path": f"{mp}/info/bgm"})
        ok = is_node(node)
        val = node.get("value") if ok else None
        print(f"  {mp}")
        print(f"    doc ok={bool(d)}   info/bgm -> type={node.get('type') if ok else '?'} "
              f"value={val!r}")
        if ok:
            found.append((mp, val))
    if not found:
        print("  >>> no bgm node found on these maps; VOID for W-033")
        raise SystemExit(1)

    print("\n=== 2. how many maps carry one at all? ===")
    lst = m.call("wz_map_list", {"q": "100000004", "limit": 5})
    print(f"  wz_map_list sample rows: {len(lst) if isinstance(lst, list) else show(lst, 200)}")

    print("\n=== 3. resolve it under Sound.nx (f14) ===")
    for mp, val in found:
        print(f"\n  {mp}  bgm = {val!r}")
        if not isinstance(val, str) or "/" not in val:
            print("    (not a 'Group/Name' string; nothing to resolve)")
            continue
        grp, name = val.split("/", 1)
        # Measured shape from step 4: Sound.nx carries Bgm00.img .. Bgm21.img and
        # the regional ones DIRECTLY at the archive root -- there is no `Bgm/`
        # folder. So `Bgm00/FloralLife` means f14/Bgm00.img/FloralLife. The
        # nested spellings below are still probed, because guessing the layout from
        # one sample is how this project loses a day.
        cands = [f"f14/{grp}.img/{name}", f"f14/Bgm/{grp}.img/{name}",
                 f"f14/Bgm/{val}", f"f14/{grp}/{name}", f"f14/{val}"]
        hit_path = None
        for cand in cands:
            n = m.call("wz_asset_node", {"path": cand})
            if is_node(n):
                hit_path = cand
                print(f"    HIT   {cand}   type={n.get('type')}")
                break
            print(f"    miss  {cand}")
        if not hit_path:
            print(f"    >>> no spelling resolved for {val!r}; the value may name a file")
            continue

        print("\n    what is inside the hit node:")
        kids = m.call("wz_asset_children", {"path": hit_path})
        if isinstance(kids, list):
            for c in kids[:12]:
                print(f"      {c.get('name')!r} type={c.get('type')}")
            if len(kids) > 12:
                print(f"      … {len(kids)-12} more")
        else:
            print(f"      children -> {show(kids, 200)}")
        # Audio in a WZ/NX Sound archive is usually an `ext` node holding the
        # encoded stream, sometimes as binary on the leaf itself.
        for probe in ("ext", "0", "mp3"):
            n = m.call("wz_asset_node", {"path": f"{hit_path}/{probe}"})
            if is_node(n):
                print(f"      {probe}: type={n.get('type')} "
                      f"childCount={n.get('childCount')}")
                sub = m.call("wz_asset_children", {"path": f"{hit_path}/{probe}"})
                if isinstance(sub, list):
                    for c in sub[:8]:
                        print(f"        {c.get('name')!r} type={c.get('type')}")
            else:
                print(f"      {probe}: ABSENT")

    print("\n=== 4. what is under Sound.nx at the top? ===")
    top = m.call("wz_asset_children", {"path": "f14"})
    names = [c.get("name") for c in top] if isinstance(top, list) else []
    print(f"  f14 children ({len(names)}): {names[:30]}")
    for guess in ("Bgm", "Bgm.img", "Sound", "Music"):
        n = m.call("wz_asset_node", {"path": f"f14/{guess}"})
        print(f"  f14/{guess:<10} -> {'present' if (isinstance(n, dict) and n.get('node')) else 'ABSENT'}")