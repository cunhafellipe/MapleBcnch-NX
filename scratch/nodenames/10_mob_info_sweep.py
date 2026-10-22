#!/usr/bin/env python3
"""Measure `info` child-name inventory over a sample of Mob.nx images.

One /api/inspect per mob (`f5/<id>.img/info`) returns the whole info child list
with names AND values, so 1 request per mob gives the complete inventory.
404 = that mob has no info node at all (counted separately, never as "absent").
"""
import json, os, sys, time
from concurrent.futures import ThreadPoolExecutor
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import mc

OUT = os.path.dirname(os.path.abspath(__file__))
F = mc.files()
MOB = F["Mob.nx"]

ids = mc.child_names(MOB)
ids = [i for i in ids if i.endswith(".img")]
print(f"Mob.nx images: {len(ids)}")


def one(img):
    p = f"{MOB}/{img}/info"
    try:
        d = mc.inspect(p, use_cache=False)
    except Exception:
        return img, None, "no-info"
    node = d.get("node") or {}
    kids = d.get("children") or []
    names = {c["name"]: c.get("value") for c in kids}
    return img, names, "ok"


t0 = time.time()
rows = {}
with ThreadPoolExecutor(max_workers=8) as ex:
    for i, (img, names, status) in enumerate(ex.map(one, ids)):
        rows[img] = (names, status)
        if (i + 1) % 250 == 0:
            print(f"  {i+1}/{len(ids)}  {time.time()-t0:.0f}s")

noinfo = [k for k, v in rows.items() if v[1] == "no-info"]
ok = {k: v[0] for k, v in rows.items() if v[1] == "ok"}
print(f"\ndone in {time.time()-t0:.0f}s; info present on {len(ok)}/{len(ids)}; absent on {len(noinfo)}")

with open(os.path.join(OUT, "mob_info_inventory.json"), "w", encoding="utf-8") as fh:
    json.dump({"ok": ok, "noinfo": noinfo}, fh, ensure_ascii=False)

from collections import Counter
c = Counter()
for names in ok.values():
    for n in names:
        c[n] += 1
print("\ndistinct info child names:", len(c))
with open(os.path.join(OUT, "mob_info_counts.json"), "w", encoding="utf-8") as fh:
    json.dump(c, fh, ensure_ascii=False, indent=1)
for n, k in c.most_common(200):
    print(f"{k:6d}/{len(ok)}  {n}")