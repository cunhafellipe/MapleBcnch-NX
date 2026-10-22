"""Open the map in the MAP EDITOR, render it, and read the simulation tools.

`wz_map_doc` refused with: "'…' is not open in the map editor. Open it first —
documents do not survive a restart or an eviction." So the editor keeps its own
open set, separate from the archive session. Opening it first.

`wz_map_simulate`, `wz_map_simulate_batch` and `wz_map_session_*` are the
project's own simulation surface. They are what a portal end-to-end test should
have been built on instead of hand-driving /api/simulate over HTTP.
"""
import json
import os
import sys

sys.path.insert(0, r"C:\MUWORK\GAME\MAPLESOTRY\wz工具\MapleBcnch-NX-audit\tools")
os.environ["WZ_SERVER"] = "http://127.0.0.1:5591"

from maple_mcp import MapleMcp  # noqa: E402

TEST_MAP = "f4/Map/Map1/100000004.img"
OUT = r"C:\MUWORK\GAME\MAPLESOTRY\wz工具\MapleBcnch-NX-audit\scratch"

with MapleMcp() as m:
    print(f"backend : {m.backend}\nserver  : {m.server}")

    print("\n=== open the map in the editor ===")
    print(f"  wz_map_open -> {json.dumps(m.call('wz_map_open', {'path': TEST_MAP}), ensure_ascii=False)[:300]}")

    doc = m.call("wz_map_doc", {"path": TEST_MAP})
    print(f"\nCONTROL  wz_map_doc -> {'resolves' if isinstance(doc, dict) and doc else 'FAILED'}")
    if not (isinstance(doc, dict) and doc):
        print(f"  {json.dumps(doc, ensure_ascii=False)[:400]}")
        raise SystemExit("VOID: still not open; nothing below would prove anything.")
    for k in ("path", "footholds", "portals", "spawns", "tiles", "width", "height"):
        v = doc.get(k)
        if v is not None:
            print(f"  {k}: {v if not isinstance(v, list) else f'{len(v)} entries'}")

    print("\n=== render ===")
    png = os.path.join(OUT, "map-100000004.png")
    r = m.call("wz_map_render", {"path": TEST_MAP, "output": png})
    print(f"  result: {json.dumps(r, ensure_ascii=False)[:400]}")
    print(f"  file  : {'WROTE ' + str(os.path.getsize(png)) + ' bytes' if os.path.exists(png) else 'not at ' + png}")

    print("\n=== the simulation tools the project already has ===")
    for t in ("wz_map_simulate", "wz_map_simulate_batch", "wz_map_session_open",
              "wz_map_session_step", "wz_map_cast", "wz_map_ground"):
        s = m.schema(t)
        props = (s.get("properties") or {})
        print(f"\n  {t}")
        print(f"    required: {s.get('required')}")
        print(f"    params  : {', '.join(f'{k}' for k in props) or '(none)'}")