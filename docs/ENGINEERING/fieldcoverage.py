import os, re

REPO = r"C:\MUWORK\GAME\MAPLESOTRY\wz工具\MapleBcnch-NX-audit"
FIELDS = ["path", "direction", "jumpEvery", "dropAtStep", "reachedPortal", "portalTargetMap",
          "portalTargetName", "climb", "ladders", "footholds", "reachedEdge", "fellOut",
          "chainEndedShort", "stuck", "startedOnLadder", "ladderX", "reason", "startX", "startY",
          "finalX", "finalY", "ticks", "seconds", "onGround", "distance", "mapWidth",
          "percentOfMap", "bounds", "track", "stepSeconds", "acceleration", "maxWalkSpeed",
          "jumpSpeed", "maxFallSpeed"]


def load(d):
    out = {}
    p = os.path.join(REPO, d)
    if os.path.isdir(p):
        for f in sorted(os.listdir(p)):
            q = os.path.join(p, f)
            if os.path.isfile(q) and f.rsplit(".", 1)[-1] in ("py", "mjs", "js", "ps1"):
                out[f] = open(q, encoding="utf-8", errors="replace").read()
    return out


gate = load(os.path.join("tools", "verify", "suites"))
probes = load(os.path.join("tools", "probe"))
mcp = load(os.path.join("MapleBench", "tools"))
mine = gate.get("position-accuracy.py", "")

rows = []
for fld in FIELDS:
    pat = re.compile(r"(?<![A-Za-z0-9_])" + re.escape(fld) + r"(?![A-Za-z0-9_])")
    g = sorted(n for n, t in gate.items() if n != "position-accuracy.py" and pat.search(t))
    p = sorted(n for n, t in list(probes.items()) + list(mcp.items()) if pat.search(t))
    rows.append((fld, g, p))

print(f"{'field':<20} {'gate suite':<34} {'probe only'}")
for fld, g, p in rows:
    gs = ",".join(g) if g else ("probes only" if p else "-")
    print(f"{fld:<20} {gs:<34} {','.join(p[:3])}")

by_gate = [f for f, g, p in rows if g]
probe_only = [f for f, g, p in rows if not g and p]
none = [f for f, g, p in rows if not g and not p]
print()
print("READ by a GATE suite (tools/verify/suites/) :", len(by_gate))
print("  ->", ", ".join(by_gate))
print("READ only by a probe (not a gate)            :", len(probe_only))
print("  ->", ", ".join(probe_only))
print("UNTOUCHED (no committed device names it)     :", len(none))
print("  ->", ", ".join(none))
print()
print("MY suite (position-accuracy.py) calls /api/simulate/character?",
      "/api/simulate/character" in mine)
print("MY suite reads these character fields: NONE (it never calls that endpoint)")
