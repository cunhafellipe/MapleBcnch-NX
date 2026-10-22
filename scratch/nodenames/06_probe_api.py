#!/usr/bin/env python3
"""Probe the API shape (fileId is the FIRST path segment, e.g. f5/100100/info)."""
import json, sys, os
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import mc

F = mc.files()
print("archives:", F)
MOB = F["Mob.nx"]

names = mc.child_names(MOB)
print("\nMob.nx top-level count:", len(names) if names else names)
print("first 15:", sorted(names)[:15] if names else None)

first = sorted(names)[0]
for p in [f"{MOB}", f"{MOB}/{first}", f"{MOB}/{first}/info", f"{MOB}/{first}/info/PDDamage", f"{MOB}/{first}/info/wdef"]:
    print(f"\n===== children {p} =====")
    try:
        d = mc.children(p)
        print(json.dumps(d, ensure_ascii=False)[:1400])
    except Exception as e:
        print("ERR", e)
    print(f"----- inspect {p} -----")
    try:
        d = mc.inspect(p)
        print(json.dumps(d, ensure_ascii=False)[:1400])
    except Exception as e:
        print("ERR", e)