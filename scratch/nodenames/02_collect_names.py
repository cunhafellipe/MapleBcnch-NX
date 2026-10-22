#!/usr/bin/env python3
"""Collapse comment text into DISTINCT candidate archive node names.

Two extraction strategies:
  A. slash-paths      e.g. info/level,  character/.../info/PDDamage
  B. backtick-quoted  e.g. `info/elemAttr`
  C. curated keyword  scan for bare node-shaped words from the task list
Plus: split slash-path tails so `info/PDDamage` also yields `PDDamage`.
"""
import json, os, re, collections

OUT = r"C:\MUWORK\GAME\MAPLESOTRY\wz工具\MapleBcnch-NX-audit\scratch\nodenames"
recs = json.load(open(os.path.join(OUT, "comments.json"), encoding="utf-8"))

# ---------- strategy A: slash paths inside comments ----------
SLASH = re.compile(r"[A-Za-z_][\w]*(?:/[A-Za-z_][\w]*)+")
# node-ish bracket form e.g. info/attack[N]
SLASH_BR = re.compile(r"[A-Za-z_]\w*(?:/\w+|\[[^\]]*\])+")
# strategy B: backticks / quotes / angle brackets
QUOTED = re.compile(r"[`'\u201c]([A-Za-z_][\w]*(?:/[A-Za-z_]\w*)+)[`'\u201d]")
QUOTED1 = re.compile(r"[`'\u201c]([A-Za-z_]\w*)[`'\u201d]")

# strategy C: curated node-shaped keywords
CURATED = [
    "info", "origin", "delay", "lt", "rb", "head", "z", "format", "_inlink", "_outlink",
    "elemAttr", "PDDamage", "MDDamage", "PADamage", "MADamage", "maxHP", "exp", "level",
    "masterLevel", "vslot", "islot", "drawLayer", "afterimage", "notAttack", "noFlip",
    "firstAttack", "bodyAttack", "speed", "category", "link", "attack", "ball",
    "script", "speak", "wdef", "mdef", "wdef_", "charmEXP", "charismaEXP",
    "inc", "incWAT", "incMAT", "incPDD", "incMDD", "incPAD", "incMAD", "incSpeed", "incJump",
    "foothold", "portal", "life", "reactor", "il0", "pl0", "pl", "il",
    "obj", "ms0", "back", "backS", "front", "cape", "capeS", "hair", "hairOverHead",
    "face", "stand", "sit", "swingT1", "swingT2", "swingT3", "attack1", "attack2",
    "move", "jump", "fall", "die", "revive", "skill0", "ski", "infoExp",
    "master", "subName", "trophy", "npc", "link", "ranking", "expN", "seed",
    "quest", "require", "stat", "time", "chara", "unableToInteract",
]

hits = collections.defaultdict(list)   # name -> [(file, line, comment_text, strategy)]

def add(name, r, strat):
    name = name.strip().strip("`'\u201c\u201d\"")
    if not name:
        return
    hits[name].append((r["file"], r["line"], r["text"], strat))

for r in recs:
    t = r["text"]
    for m in SLASH.finditer(t):
        add(m.group(0), r, "slash")
    for m in SLASH_BR.finditer(t):
        add(m.group(0), r, "slash-br")
    for m in QUOTED.finditer(t):
        add(m.group(1), r, "quoted-path")
    for m in QUOTED1.finditer(t):
        add(m.group(1), r, "quoted-word")
    for kw in CURATED:
        # word-boundary match anywhere in the comment
        for m in re.finditer(r"(?<![A-Za-z0-9_])" + re.escape(kw) + r"(?![A-Za-z0-9_])", t):
            hits[kw].append((r["file"], r["line"], r["text"], "curated"))

# also the tail segment of every slash path, so info/PDDamage -> PDDamage
for name in list(hits.keys()):
    if "/" in name:
        tail = name.rsplit("/", 1)[1]
        if tail and tail not in ("", "N", "*"):
            for h in hits[name]:
                hits[tail].append(h)

out = {k: v for k, v in hits.items()}
with open(os.path.join(OUT, "candidate_names.json"), "w", encoding="utf-8") as fh:
    json.dump(out, fh, ensure_ascii=False, indent=1)

with open(os.path.join(OUT, "candidate_names.txt"), "w", encoding="utf-8") as fh:
    def key(kv):
        k, v = kv
        return (-len(v), k.lower())
    for name, cites in sorted(out.items(), key=key):
        files = sorted({c[0] for c in cites})
        fh.write(f"{name}\t{len(cites)} cites\t{len(files)} files\t{'; '.join(files[:6])}\n")

print("distinct candidate names:", len(out))
print("\n-- top 60 by cite count --")
for name, cites in sorted(out.items(), key=lambda kv: -len(kv[1]))[:60]:
    print(f"{len(cites):5d}  {name}")