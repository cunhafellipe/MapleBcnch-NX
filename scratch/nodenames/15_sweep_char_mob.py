#!/usr/bin/env python3
"""Cross-archive node-name inventory sweeps.

Each sweep records, for one archive and one parent path pattern, the set of
child node NAMES observed across a stated sample, with counts.  One
/api/inspect per parent returns the whole child list (names + values), so a
sweep is one request per parent.
"""
import json, os, sys, time, collections
from concurrent.futures import ThreadPoolExecutor, as_completed
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import mc

HERE = os.path.dirname(os.path.abspath(__file__))
F = mc.files()
try:
    results = json.load(open(os.path.join(HERE, "sweep_results.json"), encoding="utf-8"))
except FileNotFoundError:
    results = {}
LOG = open(os.path.join(HERE, "sweep_char_mob_log.txt"), "w", encoding="utf-8")


def emit(s=""):
    print(s, flush=True)
    LOG.write(s + "\n")
    LOG.flush()


def sweep(label, fid, parents, workers=6, top=120):
    t0 = time.time()
    counts = collections.Counter()
    details = {}
    ok = 0
    failed = []
    q = [f"{fid}/{p}" if p else fid for p in parents]
    with ThreadPoolExecutor(max_workers=workers) as ex:
        futs = {ex.submit(_kids, path): path for path in q}
        for fut in as_completed(futs):
            path = futs[fut]
            try:
                names = fut.result()
            except Exception:
                names = None
            if names is None:
                failed.append(path)
                continue
            ok += 1
            details[path] = names
            for n in names:
                counts[n] += 1
            if ok % 1000 == 0:
                emit(f"    ...{label} {ok}/{len(parents)} resolved ({time.time()-t0:.0f}s)")
    emit(f"\n### {label}: {ok}/{len(parents)} parents resolved, {len(failed)} FAILED, "
         f"{len(counts)} distinct names, {time.time()-t0:.0f}s")
    for k, v in counts.most_common(top):
        emit(f"   {v:7d}/{ok}  {k}")
    if failed:
        emit(f"   FAILED sample: {failed[:8]}")
    results[label] = {"sample": ok, "of": len(parents), "failed": len(failed),
                      "failed_paths": failed[:200],
                      "counts": dict(counts), "details": details}
    with open(os.path.join(HERE, "sweep_results.json"), "w", encoding="utf-8") as fh:
        json.dump(results, fh, ensure_ascii=False, indent=1)
    return counts


def _kids(path):
    try:
        d = mc.inspect(path, use_cache=False)
    except Exception:
        return None
    return sorted({c["name"] for c in (d.get("children") or [])})


def imgs(fid, folder):
    try:
        return [n for n in (mc.child_names(f"{fid}/{folder}") or []) if n.endswith(".img")]
    except Exception:
        return []


# ---------- A. Character.nx : every equip's info ----------
CH = F["Character.nx"]
cats = [c for c in (mc.child_names(CH) or []) if not c.endswith(".img")]
emit(f"Character.nx category folders: {cats}")
parents = []
for c in cats:
    for im in imgs(CH, c):
        parents.append(f"{c}/{im}/info")
sweep("Character.nx/<cat>/<id>.img/info", CH, parents)

# ---------- B. Mob.nx : attackN/info ----------
MO = F["Mob.nx"]
mimgs = [n for n in (mc.child_names(MO) or []) if n.endswith(".img")]
emit(f"\nMob.nx images: {len(mimgs)}")
# first pass: which mobs carry an attackN node at all (unordered, so one slow
# mob cannot stall the pass)
attack_parents = []
with ThreadPoolExecutor(max_workers=6) as ex:
    futs = {ex.submit(_kids, f"{MO}/{m}"): m for m in mimgs}
    for fut in as_completed(futs):
        m = futs[fut]
        try:
            k = fut.result()
        except Exception:
            k = None
        for n in (k or []):
            if n.startswith("attack"):
                attack_parents.append((m, n))
emit(f"mobs with attackN nodes: {len({m for m, _ in attack_parents})}  attack nodes total: {len(attack_parents)}")
sweep("Mob.nx/<id>.img/attackN (top level)", MO, [f"{m}/{n}" for m, n in attack_parents])
sweep("Mob.nx/<id>.img/attackN/info", MO, [f"{m}/{n}/info" for m, n in attack_parents])

emit("\nwrote sweep_results.json")