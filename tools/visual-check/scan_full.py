"""Sweep every map for the forever-fall defect. Resumable: state lives in a
checkpoint file so a killed run keeps its progress.

    python scan_full.py <port> <totalMaps> [batchSize]

Writes shots/full-scan-<n>.json after every batch.
"""
import json
import os
import sys
import time
import urllib.error
import urllib.request

PORT = sys.argv[1] if len(sys.argv) > 1 else "5511"
TOTAL = int(sys.argv[2]) if len(sys.argv) > 2 else 5366
BATCH = int(sys.argv[3]) if len(sys.argv) > 3 else 400

BASE = f"http://127.0.0.1:{PORT}"
HERE = os.path.dirname(os.path.abspath(__file__))
CKPT = os.path.join(HERE, "shots", "full-scan-progress.json")
os.makedirs(os.path.dirname(CKPT), exist_ok=True)


def post(path, body, timeout=120, attempts=3):
    """POST with a retry. A transport failure is not a verdict: it is this
    process talking to a server that was restarted underneath it."""
    for i in range(attempts):
        try:
            req = urllib.request.Request(
                BASE + path, data=json.dumps(body).encode(),
                headers={"Content-Type": "application/json"})
            with urllib.request.urlopen(req, timeout=timeout) as x:
                return x.status, json.loads(x.read().decode())
        except urllib.error.HTTPError as e:
            return e.code, None          # the server answered: that IS the result
        except Exception as e:
            if i == attempts - 1:
                return 0, {"err": str(e)[:80]}
            time.sleep(1.5 * (i + 1))
    return 0, {"err": "unreachable"}


with urllib.request.urlopen(BASE + "/api/simulate/maps?limit=100000", timeout=60) as x:
    maps = json.loads(x.read().decode())["maps"][:TOTAL]

rows = []
if os.path.exists(CKPT):
    try:
        rows = json.load(open(CKPT, encoding="utf-8"))
        print(f"resuming from checkpoint: {len(rows)} already done", flush=True)
    except Exception:
        rows = []

# A map whose verdict is `http-0` was never tested — the request failed, which
# during this project's runs always meant "the server was killed to swap
# builds", not "the map is broken". Retrying those is the difference between
# 3300/5366 and 5366/5366, so a failed request is NOT a result.
done = {r["path"] for r in rows if not str(r.get("verdict", "")).startswith("http-0")}
todo = [m for m in maps if m["path"] not in done]
print(f"  retrying {len(rows) - len(done)} never-tested maps", flush=True)
print(f"{len(maps)} maps total, {len(rows)} done, {len(todo)} to go", flush=True)

t0 = time.time()
for i, m in enumerate(todo):
    path = m["path"]
    c, b = post("/api/simulate/session", {"path": path, "input": {}})
    if c != 200 or not b or not b.get("sessionId"):
        rows.append({"path": path, "verdict": f"http-{c}"})
        continue
    bo = b["bounds"]
    s = b["sessionId"]
    fell = b["y"] < bo["top"] or b["y"] > bo["bottom"]
    max_y = b["y"]
    for _ in range(240):
        c2, f = post(f"/api/simulate/session/{s}/frame", {"right": True, "down": True})
        if c2 != 200:
            break
        if f["y"] > max_y:
            max_y = f["y"]
        fb = f.get("bounds") or bo
        # FallOutSlack (CharacterPhysics.cs) is 60. A character resting a few
        # px past bounds.bottom is ON the last foothold, not out of the map --
        # judging at +1 flagged 11/12 working maps as broken.
        if (f["y"] > fb["bottom"] + 60 or f["x"] < fb["left"] - 200
                or f["x"] > fb["right"] + 200):
            fell = True
            break
    post(f"/api/simulate/session/{s}/close", {})
    rows.append({"path": path, "spawn": b["y"], "bottom": bo["bottom"],
                 "maxY": round(max_y), "fell": fell,
                 "fellOut": f.get("fellOut") if fell else None})

    if (i + 1) % BATCH == 0:
        json.dump(rows, open(CKPT, "w", encoding="utf-8"))
        el = time.time() - t0
        rate = (i + 1) / el
        print(f"  {len(rows)}/{len(maps)}  {el:.0f}s  {rate:.1f}/s  "
              f"eta {(len(todo)-i-1)/rate:.0f}s", flush=True)

json.dump(rows, open(CKPT, "w", encoding="utf-8"))

fell = [r for r in rows if r.get("fell")]
err = [r for r in rows if r.get("verdict")]
ok = [r for r in rows if not r.get("fell") and not r.get("verdict")]
print(f"\n=== {len(rows)} maps, {time.time()-t0:.0f}s ===", flush=True)
print(f"  stayed on the map : {len(ok)} ({100*len(ok)/max(len(rows),1):.1f}%)")
print(f"  FELL OFF          : {len(fell)}")
print(f"  errors            : {len(err)}")
if err:
    from collections import Counter
    print("  ", dict(Counter(r["verdict"] for r in err)))
if fell:
    print("\n--- fell off ---", flush=True)
    for r in sorted(fell, key=lambda x: -(x.get("maxY") or 0))[:25]:
        print(f"  {r['path'][-34:]:<34} spawn={r['spawn']:<8} "
              f"bottom={r['bottom']:<8} maxY={r['maxY']}", flush=True)