#!/usr/bin/env python3
"""Compact: for each name, the FIRST comment citation with 5 lines of leading
context. Small enough to read in one pass."""
import json, os, re, sys, collections

HERE = os.path.dirname(os.path.abspath(__file__))
names = sys.argv[1:]
comments = json.load(open(os.path.join(HERE, "comments.json"), encoding="utf-8"))

ROOT = r"C:\MUWORK\GAME\MAPLESOTRY\wz工具\MapleBcnch-NX-audit"
src_cache = {}


def src(path):
    if path not in src_cache:
        full = os.path.join(ROOT, path.replace("/", os.sep))
        try:
            src_cache[path] = open(full, encoding="utf-8-sig", errors="replace").read().splitlines()
        except Exception:
            src_cache[path] = []
    return src_cache[path]


out = open(os.path.join(HERE, "ctx_first.txt"), "w", encoding="utf-8")
for name in names:
    pat = re.compile(r"(?<![A-Za-z0-9_])" + re.escape(name) + r"(?![A-Za-z0-9_])")
    hits = [r for r in comments if pat.search(r["text"])]
    files = sorted({r["file"] for r in hits})
    out.write(f"\n{'='*90}\n### {name}  ({len(hits)} cites in {len(files)} files)\n")
    out.write(f"    files: {', '.join(files)}\n{'='*90}\n")
    for r in hits[:2]:
        out.write(f"\n--- {r['file']}:{r['line']}\n")
        lines = src(r["file"])
        lo = max(0, r["line"] - 6)
        for i in range(lo, min(len(lines), r["line"] + 2)):
            out.write(f"{'>>' if i+1==r['line'] else '  '} {i+1:5d}| {lines[i]}\n")
out.close()
print("wrote ctx_first.txt")