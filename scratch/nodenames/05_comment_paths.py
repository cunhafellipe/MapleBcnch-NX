#!/usr/bin/env python3
"""Prong 2: comment-asserted ARCHIVE SLASH PATHS, with archive attribution.

Only keeps paths whose head segment names a known archive top-level folder
(Mob/Map/Npc/Skill/Item/String/Character/Base/Quest/Effect/Etc/Morph/Reactor/
Sound/TamingMob/UI/Etc) or that are rooted at a known per-entity img folder
(info/, stand/, move/, attack1/, miniMap/, fh/, ...).  Records the file:line of
every citation so the same name asserted in 12 places shows 12 places.
"""
import json, os, re, collections

OUT = r"C:\MUWORK\GAME\MAPLESOTRY\wz工具\MapleBcnch-NX-audit\scratch\nodenames"
comments = json.load(open(os.path.join(OUT, "comments.json"), encoding="utf-8"))

ARCHIVES = ["Character", "Mob", "Map", "Npc", "Skill", "Item", "String", "Base",
            "Quest", "Effect", "Etc", "Morph", "Reactor", "Sound", "TamingMob", "UI"]

ROOTS = set("""info stand move jump fall die sit idle turn fly swim attack1 attack2 attack3
swingT1 swingT2 swingT3 swingT swingF swingF1 swingF2 swingF3 skill ball balloon
miniMap fh foothold portal life reactor il0 il1 il2 il3 il4 il5 il6 il7 il8 il9
pl0 pl1 pl2 pl3 pl4 pl5 pl6 pl7 pl8 pl9 obj obje back capeBack cape hairOverHead
hair face cap body speak script require quest req stat common common2 lvl rune
img data obj canvas zmap smap infoExp exp charType reqLevel z static
""".split())

PATH = re.compile(r"[A-Za-z_]\w*(?:/[A-Za-z0-9_]+)+")

hits = collections.defaultdict(list)
for r in comments:
    for m in PATH.finditer(r["text"]):
        p = m.group(0)
        segs = p.split("/")
        head = segs[0]
        # skip obvious non-archive: api routes, file extensions, doc names
        if "." in head or head in ("api", "wwwroot", "scratch", "MapleBench", "docs", "pm"):
            continue
        if any(s.endswith((".cs", ".js", ".md", ".nx", ".img", ".json", ".cpp", ".h", ".lua", ".py", ".sh")) for s in segs):
            continue
        if head in ARCHIVES or head in ROOTS:
            hits[p].append((r["file"], r["line"], r["text"]))

with open(os.path.join(OUT, "comment_paths.txt"), "w", encoding="utf-8") as fh:
    for p, cites in sorted(hits.items(), key=lambda kv: (-len(kv[1]), kv[0])):
        fl = sorted({f"{f}:{l}" for f, l, _ in cites})
        fh.write(f"{p}\t{len(cites)}\t{'; '.join(fl)}\n")
        fh.write(f"      ex: {cites[0][2][:180]}\n")

print("distinct comment paths:", len(hits))
for p, cites in sorted(hits.items(), key=lambda kv: -len(kv[1]))[:200]:
    print(f"{len(cites):4d}  {p}")