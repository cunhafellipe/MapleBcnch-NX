#!/usr/bin/env python3
"""Dump every citation (file:line + the comment line, plus 6 lines of leading
context) for the names that were ABSENT from Mob.nx info, so each can be
attributed to the archive the comment actually means."""
import json, os, re, sys, collections

HERE = os.path.dirname(os.path.abspath(__file__))
names = sys.argv[1:]
comments = json.load(open(os.path.join(HERE, "comments.json"), encoding="utf-8"))
byfile = collections.defaultdict(dict)
for r in comments:
    byfile[r["file"]][r["line"]] = r["text"]

src_cache = {}
def src(path):
    if path not in src_cache:
        full = os.path.join(r"C:\MUWORK\GAME\MAPLESOTRY\wz工具\MapleBcnch-NX-audit",
                            path.replace("/", os.sep))
        try:
            src_cache[path] = open(full, encoding="utf-8-sig", errors="replace").read().splitlines()
        except Exception:
            src_cache[path] = []
    return src_cache[path]

out = open(os.path.join(HERE, "absent_citation_context.txt"), "w", encoding="utf-8")
for name in names:
    pat = re.compile(r"(?<![A-Za-z0-9_])" + re.escape(name) + r"(?![A-Za-z0-9_])")
    hits = [r for r in comments if pat.search(r["text"])]
    out.write(f"\n{'='*100}\n### {name}   ({len(hits)} comment citations)\n{'='*100}\n")
    for r in hits:
        out.write(f"\n--- {r['file']}:{r['line']}\n")
        lines = src(r["file"])
        lo = max(0, r["line"] - 8)
        for i in range(lo, min(len(lines), r["line"] + 3)):
            mark = ">>" if i + 1 == r["line"] else "  "
            out.write(f"{mark} {i+1:5d}| {lines[i]}\n")
out.close()
print("wrote absent_citation_context.txt")