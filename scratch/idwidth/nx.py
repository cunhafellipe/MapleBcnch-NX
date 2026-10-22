"""Shared NX archive API client for the id-width audit.

Server is SHARED: never start/stop it. Read-only calls only.
- /api/inspect      -> VALUES   ({node:{...,value:...}})
- /api/children     -> DESCRIPTORS ONLY (no 'value' field)
"""
import requests, json, os, sys, time

BASE = "http://127.0.0.1:5591"
SCRATCH = os.path.dirname(os.path.abspath(__file__))

_sess = requests.Session()


MISSING = object()


def _get(path, params, timeout=60, retries=3):
    """Returns MISSING (sentinel) on HTTP 404 -- measured: an absent node is a
    404, NOT a 200 carrying {"node": null}."""
    last = None
    for i in range(retries):
        try:
            r = _sess.get(BASE + path, params=params, timeout=timeout)
            if r.status_code == 404:
                return MISSING
            r.raise_for_status()
            return r.json()
        except Exception as e:  # noqa
            last = e
            time.sleep(0.4 * (i + 1))
    raise last


def files():
    """Return {fileId: filename} re-derived from the live server."""
    j = _get("/api/files", {})
    out = {}
    items = j if isinstance(j, list) else j.get("files", j.get("items", []))
    for it in items:
        if isinstance(it, dict):
            fid = it.get("id") or it.get("fileId")
            nm = it.get("name") or it.get("fileName") or it.get("filename") or it.get("path")
            if fid is not None:
                out[str(fid)] = nm
    return out


def fid_by_name(name):
    """'Mob.nx' -> 'f5' (or whatever it is now)."""
    for k, v in files().items():
        if v and os.path.basename(str(v)) == name:
            return k
    raise KeyError(name + " not open; open files=" + json.dumps(files()))


def children(path):
    """Node DESCRIPTORS ONLY -- no 'value'. Use to learn names."""
    j = _get("/api/children", {"path": path})
    if j is MISSING:
        return []
    if isinstance(j, dict):
        for key in ("children", "nodes", "items"):
            if key in j and isinstance(j[key], list):
                return j[key]
    if isinstance(j, list):
        return j
    raise ValueError("unexpected /api/children shape: " + json.dumps(j)[:400])


def child_names(path):
    return [c.get("name") for c in children(path)]


def inspect(path):
    """VALUES. Returns the node dict, or None when the path is absent (404)."""
    j = _get("/api/inspect", {"path": path})
    if j is MISSING or j is None:
        return None
    if isinstance(j, dict):
        n = j.get("node")
        if n is None:
            return None
        return n
    return None


def value(path, default=None):
    n = inspect(path)
    if n is None:
        return default
    return n.get("value", default)


def exists(path):
    n = inspect(path)
    return n is not None


def widths(names):
    """Histogram of len(str) over an iterable of node names."""
    from collections import Counter
    c = Counter(len(str(n)) for n in names)
    return dict(sorted(c.items()))


def save(obj, fname):
    p = os.path.join(SCRATCH, fname)
    with open(p, "w", encoding="utf-8") as f:
        json.dump(obj, f, ensure_ascii=False, indent=1)
    print("wrote", p)
    return p