#!/usr/bin/env python3
"""Deeper shape probe for the archives whose layout decides attribution:
Character.nx (equip data lives somewhere specific), Item.nx, Skill.nx, Map.nx."""
import json, os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import mc

F = mc.files()

def show(fid, path, depth=2, maxn=40):
    try:
        kids = mc.child_names(f"{fid}/{path}" if path else fid) or []
    except Exception as e:
        print(f"  {path}/ ERR {e}")
        return
    print(f"  {path}/ -> {len(kids)}: {sorted(kids)[:maxn]}")

print("### Character.nx top-level (all 35)")
show(F["Character.nx"], "", maxn=40)

print("\n### Item.nx/Etc")
show(F["Item.nx"], "Etc")
print("\n### Item.nx/Special")
show(F["Item.nx"], "Special")
print("\n### Item.nx/Consume")
show(F["Item.nx"], "Consume")

print("\n### Character.nx: does a `character` folder exist?")
for cand in ["character", "Equipment", "Equip"]:
    print(f"  probe {cand}:", (mc.child_names(f"{F['Character.nx']}/{cand}") or "n/a") if cand else "")

print("\n### Character.nx/00002000.img/info")
show(F["Character.nx"], "00002000.img/info", maxn=60)
print("\n### Character.nx/00002000.img/stand/0")
show(F["Character.nx"], "00002000.img/stand/0", maxn=20)
print("\n### Character.nx/00002000.img/stand/1")
show(F["Character.nx"], "00002000.img/stand/1", maxn=20)

print("\n### Character.nx/Weapon")
show(F["Character.nx"], "Weapon", maxn=10)
if "Weapon" in (mc.child_names(F["Character.nx"]) or []):
    show(F["Character.nx"], "Weapon/01302000.img/info", maxn=60)

print("\n### Skill.nx/000.img")
show(F["Skill.nx"], "000.img")
print("\n### Skill.nx/000.img/info")
show(F["Skill.nx"], "000.img/info", maxn=60)
print("\n### Skill.nx/100.img")
show(F["Skill.nx"], "100.img", maxn=20)