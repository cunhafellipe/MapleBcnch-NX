"""Dump the project's ENTIRE agent command surface, in full, to a file.

The 52 tools are not "names I half know" -- this prints every tool's description
and complete inputSchema so the working knowledge is the real contract, not an
inference from a tool name. Written through the project's own client wrapper
(tools/maple_mcp.py), which encodes the SDK-10 and WZ_SERVER traps.

No product file is touched. This only reads.
"""
import io
import json
import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "tools"))
os.environ["WZ_SERVER"] = "http://127.0.0.1:5591"

from maple_mcp import MapleMcp  # noqa: E402

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "ALL-MCP-TOOLS.txt")

with MapleMcp() as m:
    tools = m.send("tools/list", {}).get("result", {}).get("tools", [])
    caps = m.call("wz_map_capabilities", {})

    buf = io.StringIO()
    w = buf.write
    w("=" * 78 + "\n")
    w(f"backend : {m.backend}\n")
    w(f"server  : {json.dumps(m.server)}\n")
    w(f"tools   : {len(tools)}\n")
    w("=" * 78 + "\n\n")

    w("# CAPABILITIES (what the backend says it can do right now)\n")
    w(json.dumps(caps, ensure_ascii=False, indent=2)[:4000] + "\n\n")

    families = {}
    for t in tools:
        families.setdefault(t["name"].split("_")[1], []).append(t["name"])

    w("# FAMILIES\n")
    for fam, names in sorted(families.items()):
        w(f"  {fam:<10} {len(names):>3}  {' '.join(sorted(names))}\n")
    w("\n" + "=" * 78 + "\n")

    for i, t in enumerate(sorted(tools, key=lambda x: x["name"]), 1):
        name = t["name"]
        w(f"\n[{i}] {name}\n" + "-" * (len(name) + 4) + "\n")
        desc = (t.get("description") or "").strip()
        w(desc + "\n")
        sch = t.get("inputSchema") or {}
        props = sch.get("properties") or {}
        if props:
            w("  PARAMETERS\n")
            for pname, p in props.items():
                typ = p.get("type", "?")
                req = "REQUIRED" if pname in (sch.get("required") or []) else "optional"
                bits = [f"{pname} ({typ}, {req})"]
                if p.get("enum"):
                    bits.append("enum=" + ",".join(str(e) for e in p["enum"]))
                if p.get("default") is not None:
                    bits.append(f"default={p['default']!r}")
                d = (p.get("description") or "").strip().replace("\n", " ")
                if d:
                    bits.append(d)
                w("    " + "  |  ".join(bits) + "\n")
        else:
            w("  PARAMETERS: none\n")

    with open(OUT, "w", encoding="utf-8") as fh:
        fh.write(buf.getvalue())

    print(f"wrote {OUT}  ({os.path.getsize(OUT)} bytes)")
    print(f"tools: {len(tools)}")
    for fam, names in sorted(families.items()):
        print(f"  {fam:<10} {len(names):>3}  {' '.join(sorted(names))}")