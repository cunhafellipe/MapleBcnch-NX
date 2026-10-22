#!/usr/bin/env python3
"""Minimal client for the already-running MapleBench server at 127.0.0.1:5591.
READ-ONLY: only GET /api/files, /api/inspect, /api/children.
Never starts, stops or reconfigures the server.
"""
import json, urllib.request, urllib.parse, time, threading

BASE = "http://127.0.0.1:5591"
_cache = {}
_lock = threading.Lock()


def _get(url, tries=2, timeout=25):
    last = None
    for i in range(tries):
        try:
            req = urllib.request.Request(url, headers={"Accept": "application/json"})
            with urllib.request.urlopen(req, timeout=timeout) as r:
                return json.loads(r.read().decode("utf-8", "replace"))
        except Exception as e:      # noqa: BLE001
            last = e
            time.sleep(0.3 * (i + 1))
    raise RuntimeError(f"GET {url} failed: {last}")


def files():
    """Re-derive archive file ids. NEVER hard-code."""
    with _lock:
        if "files" not in _cache:
            data = _get(f"{BASE}/api/files")
            # tolerate either a bare list or {files:[...]}
            if isinstance(data, dict):
                for k in ("files", "items", "data"):
                    if k in data:
                        data = data[k]
                        break
            _cache["files"] = {f["name"]: f["id"] for f in data}
    return _cache["files"]


def inspect(path, fid=None, use_cache=True):
    """Returns the parsed /api/inspect payload (VALUES)."""
    key = ("i", fid, path)
    if use_cache:
        with _lock:
            if key in _cache:
                return _cache[key]
    q = {"path": path}
    if fid:
        q["fileId"] = fid
    data = _get(f"{BASE}/api/inspect?{urllib.parse.urlencode(q)}")
    if use_cache:
        with _lock:
            _cache[key] = data
    return data


def children(path, fid=None, use_cache=True):
    """Returns /api/children payload -- NODE DESCRIPTORS ONLY, no value field."""
    key = ("c", fid, path)
    if use_cache:
        with _lock:
            if key in _cache:
                return _cache[key]
    q = {"path": path}
    if fid:
        q["fileId"] = fid
    data = _get(f"{BASE}/api/children?{urllib.parse.urlencode(q)}")
    if use_cache:
        with _lock:
            _cache[key] = data
    return data


def child_names(path, fid=None):
    """Names directly under `path` (or None if unavailable)."""
    d = children(path, fid)
    if isinstance(d, dict):
        for k in ("children", "nodes", "items", "data"):
            if k in d:
                d = d[k]
                break
    if not isinstance(d, list):
        return None
    names = []
    for n in d:
        if isinstance(n, str):
            names.append(n)
        elif isinstance(n, dict):
            names.append(n.get("name") or n.get("path", "").rstrip("/").rsplit("/", 1)[-1])
    return names


if __name__ == "__main__":
    print(json.dumps(files(), indent=1))