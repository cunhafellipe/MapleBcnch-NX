#!/usr/bin/env python3
"""Do speedMaxOver / canPartyStatChangeIgnoreParty / limitUI / AmbientBGM /
bgmSub exist ANYWHERE in Map.nx, at any depth the tool models?
Also: how many map images does this Map.nx actually hold, vs the 17,442 the
comments cite?  That difference decides whether an absence is a wrong NAME or a
different ARCHIVE VERSION."""
import json, os, sys, collections
from concurrent.futures import ThreadPoolExecutor, as_completed
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import mc

HERE = os.path.dirname(os.path.abspath(__file__))
F = mc.files()
MP = F["Map.nx"]

TARGETS = ["speedMaxOver", "speedMaxOver ", "canPartyStatChangeIgnoreParty",
           "canPartyStatChangeIgnoreParty ", "limitUI", "AmbientBGM", "bgmSub",
           "tS", "tSMag", "mapMark", "mapName", "returnMap", "fieldType", "mapDesc",
           "bgm", "forcedReturn", "cloud", "mobRate", "town", "version", "fieldLimit"]


def kids(p):
    try:
        d = mc.inspect(p, use_cache=False)
    except Exception:
        return None
    return sorted({c["name"] for c in (d.get("children") or [])})


print("### Map.nx top level:", kids(MP))
regions = kids(f"{MP}/Map") or []
print(f"### Map.nx/Map regions ({len(regions)}):", regions)
tot = 0
per = {}
for r in regions:
    n = len(kids(f"{MP}/Map/{r}") or [])
    per[r] = n
    tot += n
print(f"### map images under Map/: {tot}")
for k, v in sorted(per.items(), key=lambda kv: -kv[1])[:12]:
    print(f"      {k}: {v}")
m1 = kids(f"{MP}/Map001") or []
print(f"### Map001: {len(m1)} -> {m1[:12]}")
tot += sum(len(kids(f"{MP}/Map001/{x}") or []) for x in m1)
print(f"### TOTAL map images reachable under Map/ + Map001: {tot}")

print("\n### Search EVERY Map.nx map root + its layer roots for the target names")
allmaps = []
for r in regions:
    for m in (kids(f"{MP}/Map/{r}") or []):
        allmaps.append(f"Map/{r}/{m}")
for x in m1:
    for m in (kids(f"{MP}/Map001/{x}") or []):
        allmaps.append(f"Map001/{x}/{m}")
print(f"   scanning {len(allmaps)} map images")

hits = collections.Counter()
where = collections.defaultdict(list)


def probe(base):
    found = []
    for sub in ("info", "0/info", "1/info", "2/info", "3/info", "4/info", "5/info",
                "6/info", "7/info", "8/info", "9/info", "Obj", "Tile"):
        n = kids(f"{MP}/{base}/{sub}")
        if not n:
            continue
        for t in TARGETS:
            if t in n:
                found.append((sub, t))
    return base, found


with ThreadPoolExecutor(max_workers=4) as ex:
    futs = [ex.submit(probe, b) for b in allmaps]
    done = 0
    for fut in as_completed(futs):
        base, found = fut.result()
        done += 1
        if done % 1000 == 0:
            print(f"      ...{done}/{len(allmaps)}", flush=True)
        for sub, t in found:
            key = f"{sub}/{t}"
            hits[key] += 1
            if len(where[key]) < 5:
                where[key].append(f"{base}/{sub}")

print("\n### RESULTS")
for k, v in sorted(hits.items(), key=lambda kv: -kv[1]):
    print(f"   {v:6d} maps  {k!r}   e.g. {where[k][:3]}")
print("\n### TARGETS WITH ZERO HITS ANYWHERE IN Map.nx")
for t in TARGETS:
    if not any(k.endswith(repr(t)) or k.split("/", 1)[1] == t for k in hits):
        print(f"   NOT FOUND: {t!r}")