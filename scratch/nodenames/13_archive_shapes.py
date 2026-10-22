#!/usr/bin/env python3
"""Print the shape (top 3 levels, child names + counts) of every open archive."""
import json, os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import mc

F = mc.files()
for name, fid in F.items():
    try:
        kids = mc.child_names(fid) or []
    except Exception as e:
        print(f"{name} ({fid}): ERR {e}")
        continue
    print(f"\n=== {name} ({fid})  top-level {len(kids)} ===")
    print("   ", sorted(kids)[:14], "..." if len(kids) > 14 else "")
    if kids:
        k = sorted(kids)[0]
        try:
            g = mc.child_names(f"{fid}/{k}") or []
        except Exception:
            g = []
        print(f"    {k}/ -> {len(g)}:", sorted(g)[:16])
        if g:
            g2 = sorted(g)[0]
            try:
                h = mc.child_names(f"{fid}/{k}/{g2}") or []
            except Exception:
                h = []
            print(f"      {k}/{g2}/ -> {len(h)}:", sorted(h)[:20])