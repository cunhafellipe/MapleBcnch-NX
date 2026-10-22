#!/usr/bin/env python3
"""Prong 1b: the DEFECTIVE-AUTHORSHIP list.

A comment asserting a node name is highest-risk when the name appears ONLY in
comments and never as a code string literal (nobody ever read it), or when the
same comment block also mentions the code's real name (a disagreement).

This emits, for a curated watchlist, one line per name:
    name | in-code cites | in-comment cites | representative comment(s)
so the next script can measure only the small high-risk set.
"""
import json, os, re, collections

OUT = r"C:\MUWORK\GAME\MAPLESOTRY\wz工具\MapleBcnch-NX-audit\scratch\nodenames"
comments = json.load(open(os.path.join(OUT, "comments.json"), encoding="utf-8"))
code = json.load(open(os.path.join(OUT, "code_names.json"), encoding="utf-8"))
code_names = set(code.keys())
code_bare = {k for k in code_names if "/" not in k}

# Watchlist: node names the audit brief flags, plus every bare word that shows
# up in a comment INSIDE a node-path or backtick context.
WATCH = [
    # brief's explicit list
    "wdef", "mdef", "elemAttr", "PDDamage", "MDDamage", "PADamage", "MADamage",
    "maxHP", "exp", "level", "masterLevel", "vslot", "islot", "drawLayer",
    "afterimage", "notAttack", "noFlip", "firstAttack", "bodyAttack", "speed",
    "category", "link", "attack", "attack1", "ball", "script", "speak",
    "origin", "delay", "lt", "rb", "head", "z", "format", "_inlink", "_outlink",
    "icon", "info",
    # attribute EXP family named in the brief
    "charismaEXP", "charmEXP", "strEXP", "dexEXP", "intEXP", "lukEXP", "hpEXP", "mpEXP",
    "strEXPBase", "expRate",
    # equip inc family
    "incSTR", "incDEX", "incINT", "incLUK", "incPAD", "incMAD", "incPDD", "incMDD",
    "incMHP", "incMMP", "incSpeed", "incJump", "incACC", "incEVA", "incHANDS",
    "incWAT", "incMAT", "incLUk", "incSPEED", "incJUMP",
    # animation / canvas
    "zmap", "miniMap", "infoExp", "puppet", "balloon", "subName",
    # misc named in code
    "common", "common2", "rune", "runeIndex", "req", "require",
    "unknown", "fs", "cise", "collision", "triple", "obstacle",
    "mP", "md", "H1", "H0", "H1x", "H0x", "fh", "fhx",
]

rows = []
for name in sorted(set(WATCH)):
    pat = re.compile(r"(?<![A-Za-z0-9_])" + re.escape(name) + r"(?![A-Za-z0-9_])")
    cc = [(r["file"], r["line"], r["text"]) for r in comments if pat.search(r["text"])]
    if not cc:
        continue
    in_code_list = code.get(name, [])
    rows.append((name, len(in_code_list), len(cc), cc[:3]))

with open(os.path.join(OUT, "watchlist_report.txt"), "w", encoding="utf-8") as fh:
    for name, nc, ncm, ex in rows:
        fh.write(f"### {name}   code-literals={nc}  comments={ncm}\n")
        for f, l, t in ex:
            fh.write(f"      {f}:{l}  {t[:190]}\n")
        fh.write("\n")

for name, nc, ncm, ex in rows:
    flag = "COMMENT-ONLY" if nc == 0 else ""
    print(f"{name:16s} code={nc:4d} comments={ncm:4d} {flag}")