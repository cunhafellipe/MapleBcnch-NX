#!/usr/bin/env python3
"""Cross the Mob.nx info sweep against every `info/<name>` asserted in a comment."""
import json, os, sys, re, collections

HERE = os.path.dirname(os.path.abspath(__file__))
comments = json.load(open(os.path.join(HERE, "comments.json"), encoding="utf-8"))
sweep = json.load(open(os.path.join(HERE, "mob_info_sweep.json"), encoding="utf-8")) \
    if os.path.exists(os.path.join(HERE, "mob_info_sweep.json")) else None

inv = json.load(open(os.path.join(HERE, "mob_info_inventory.json"), encoding="utf-8"))
counts = json.load(open(os.path.join(HERE, "mob_info_counts.json"), encoding="utf-8"))
ok = inv["ok"]
N = len(ok)

# every `info/<name>` asserted in a comment
cites = collections.defaultdict(list)
PATH = re.compile(r"\binfo/([A-Za-z_]\w*)")
for r in comments:
    for m in PATH.finditer(r["text"]):
        cites[m.group(1)].append((r["file"], r["line"], r["text"]))

# also bare names cited right after the word "info" in prose, e.g. `<c>icon</c>`
PROSE = re.compile(r"<c>(\w+)</c>|`(\w+)`")
KEYWORD_CONTEXT = re.compile(r"\binfo\b", re.I)

rows = []
for name in sorted(cites):
    fl = sorted({f"{f}:{l}" for f, l, _ in cites[name]})
    n = counts.get(name, 0)
    rows.append((name, n, N, len(fl), fl, cites[name][:2]))

with open(os.path.join(HERE, "mob_info_verdicts.txt"), "w", encoding="utf-8") as fh:
    fh.write(f"Mob.nx sample: ALL {N} images that carry an `info` node (of 2053 total)\n\n")
    for name, n, tot, nf, fl, ex in sorted(rows, key=lambda r: (r[1] > 0, r[0].lower())):
        tag = f"{n}/{tot}" if n else f"ABSENT 0/{tot}"
        fh.write(f"{tag:>18}  {name:28s} {nf} cites\n")
        for cite in fl:
            fh.write(f"                       {cite}\n")
        fh.write("\n")

print(f"Mob.nx info sample N={N}\n")
present = [r for r in rows if r[1] > 0]
absent = [r for r in rows if r[1] == 0]
print(f"comment-asserted info/* names: {len(rows)}   present: {len(present)}   ABSENT: {len(absent)}\n")
print("=== ABSENT from Mob.nx info ===")
for name, n, tot, nf, fl, ex in absent:
    print(f"  info/{name}   0/{tot}   {nf} cites: {fl[:4]}")
print("\n=== PRESENT (with count) ===")
for name, n, tot, nf, fl, ex in present:
    print(f"  info/{name}   {n}/{tot}")

# case-variant neighbours for every absent name
print("\n=== case-insensitive neighbours of each ABSENT name ===")
inv_ci = collections.defaultdict(set)
for k, v in counts.items():
    inv_ci[k.lower()].add(k)
for name, n, tot, nf, fl, ex in absent:
    sib = sorted(inv_ci.get(name.lower(), set()))
    print(f"  info/{name:26s} -> case-insensitive matches in archive: {sib if sib else 'NONE'}")