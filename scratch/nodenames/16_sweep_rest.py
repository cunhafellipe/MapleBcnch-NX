#!/usr/bin/env python3
"""Sweeps for Item.nx, Npc.nx, Map.nx, Skill.nx, String.nx, Base.nx, TamingMob.nx,
Reactor.nx, Morph.nx -- the archives the remaining comment-asserted names point at.
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


def _kids(path):
    try:
        d = mc.inspect(path, use_cache=False)
    except Exception:
        return None
    return sorted({c["name"] for c in (d.get("children") or [])})


LOG = open(os.path.join(HERE, "sweep_rest_log.txt"), "w", encoding="utf-8")


def emit(s=""):
    print(s, flush=True)
    LOG.write(s + "\n")
    LOG.flush()


def sweep(label, fid, parents, workers=6, top=120):
    """Unordered completion + short per-request timeout: one slow parent must not
    block the sweep. Parents that fail are counted and reported, never silently
    folded into the denominator."""
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


# ---------------- Item.nx : <cat>/<book>.img/<id>/info ----------------
IT = F["Item.nx"]
it_parents = []
for cat in (mc.child_names(IT) or []):
    for book in (mc.child_names(f"{IT}/{cat}") or []):
        for iid in (mc.child_names(f"{IT}/{cat}/{book}") or []):
            it_parents.append(f"{cat}/{book}/{iid}/info")
emit(f"Item.nx item parents: {len(it_parents)}")
sweep("Item.nx/<cat>/<book>.img/<id>/info", IT, it_parents, top=90)

# ---------------- Npc.nx : <id>.img/info ----------------
NP = F["Npc.nx"]
npc_ids = [n for n in (mc.child_names(NP) or []) if n.endswith(".img")]
emit(f"Npc.nx images: {len(npc_ids)}")
sweep("Npc.nx/<id>.img/info", NP, [f"{i}/info" for i in npc_ids], top=90)
sweep("Npc.nx/<id>.img (top level)", NP, npc_ids, top=60)

# ---------------- Map.nx : Map/<region>/<id>.img/info ----------------
MP = F["Map.nx"]
map_parents = []
for region in (mc.child_names(f"{MP}/Map") or []):
    for mid in (mc.child_names(f"{MP}/Map/{region}") or []):
        map_parents.append(f"Map/{region}/{mid}/info")
emit(f"Map.nx map images: {len(map_parents)}")
sweep("Map.nx/Map/<region>/<id>.img/info", MP, map_parents, top=90)
sweep("Map.nx/Map/<region>/<id>.img (top level)", MP, [p[:-5] for p in map_parents], top=60)

# ---------------- Map.nx life / reactor / portal / foothold ----------------
sweep("Map.nx/Map/<region>/<id>.img/life", MP, [p[:-5] + "/life" for p in map_parents], top=40)
sweep("Map.nx/Map/<region>/<id>.img/reactor", MP, [p[:-5] + "/reactor" for p in map_parents], top=40)
sweep("Map.nx/Map/<region>/<id>.img/portal", MP, [p[:-5] + "/portal" for p in map_parents], top=40)
sweep("Map.nx/Map/<region>/<id>.img/foothold", MP, [p[:-5] + "/foothold" for p in map_parents], top=40)
sweep("Map.nx/Map/<region>/<id>.img/miniMap", MP, [p[:-5] + "/miniMap" for p in map_parents], top=40)

# ---------------- Skill.nx ----------------
SK = F["Skill.nx"]
books = mc.child_names(SK) or []
emit(f"Skill.nx books: {len(books)}")
sweep("Skill.nx/<book>.img (top level)", SK, books, top=40)
sweep("Skill.nx/<book>.img/info", SK, [f"{b}/info" for b in books], top=90)
sweep("Skill.nx/<book>.img/common", SK, [f"{b}/common" for b in books], top=90)
sweep("Skill.nx/<book>.img/skill", SK, [f"{b}/skill" for b in books], top=40)

# ---------------- String.nx ----------------
ST = F["String.nx"]
for im in (mc.child_names(ST) or []):
    sweep(f"String.nx/{im} (top level)", ST, [im], top=40)

# ---------------- Base.nx ----------------
BS = F["Base.nx"]
sweep("Base.nx (top level)", BS, [""], top=20)
sweep("Base.nx/zmap.img", BS, ["zmap.img"], top=40)
sweep("Base.nx/smap.img", BS, ["smap.img"], top=40)

# ---------------- TamingMob / Morph / Reactor ----------------
TM = F["TamingMob.nx"]
tmids = [n for n in (mc.child_names(TM) or []) if n.endswith(".img")]
sweep("TamingMob.nx/<id>.img/info", TM, [f"{i}/info" for i in tmids], top=60)
MO2 = F["Morph.nx"]
mo2ids = [n for n in (mc.child_names(MO2) or []) if n.endswith(".img")]
sweep("Morph.nx/<id>.img (top level)", MO2, mo2ids, top=60)
sweep("Morph.nx/<id>.img/info", MO2, [f"{i}/info" for i in mo2ids], top=60)
RE = F["Reactor.nx"]
reids = [n for n in (mc.child_names(RE) or []) if n.endswith(".img")]
sweep("Reactor.nx/<id>.img (top level)", RE, reids, top=40)
sweep("Reactor.nx/<id>.img/info", RE, [f"{i}/info" for i in reids], top=40)

with open(os.path.join(HERE, "sweep_results.json"), "w", encoding="utf-8") as fh:
    json.dump(results, fh, ensure_ascii=False, indent=1)
emit("\nwrote sweep_results.json")