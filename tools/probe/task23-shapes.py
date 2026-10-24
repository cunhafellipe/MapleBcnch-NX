#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Read the real shape of an NX mob action node, through /api/inspect.

/api/children reported `stand` with zero children and /api/node returned an
empty body for the same path (TASK23 trap #1: the children endpoint does not
reflect the real node structure). This walks the same path the rest of the
codebase walks it and prints what is actually there.

Usage: python task23-shapes.py [path] [depth]

`path` defaults to 9409000.img inside Mob.nx, resolved BY NAME -- the archive
id is handed out in the order the archives were opened, so it is not the same
id on every instance.
"""
import json
import os
import sys
import urllib.parse
import urllib.request

# The archive id is handed out in the order the archives were opened, so 'f2'
# is Mob.nx on one instance and something else on another. Resolve it by NAME,
# with the helper 0645f62 put in tools/verify/suites/archivelib.py.
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)),
                                os.pardir, 'verify', 'suites'))
try:
    from archivelib import archive_id, require_gate_base
except ImportError:
    print(">>> RED: archivelib.py is missing next to this suite")
    raise SystemExit(1)

# The instance comes from GATE_BASE and from nowhere else (task-139).  There is
# deliberately no default port: this probe used to carry one, and an instance
# older than this tree answers every one of these requests happily, so a
# fallback turns "that instance is 27 product commits behind" into a per-probe
# result that looks like a finding about this tree.
BASE = require_gate_base()


def get(path):
    url = f'{BASE}/api/inspect?path={urllib.parse.quote(path, safe="")}'
    with urllib.request.urlopen(url, timeout=60) as r:
        return json.load(r)


def walk(path, depth, indent=''):
    try:
        data = get(path)
    except Exception as exc:  # noqa: BLE001 - diagnostics report, never fix
        print(f'{indent}{path} -> ERROR {exc}')
        return
    node = data.get('node') or {}
    kids = data.get('children') or []
    print(f"{indent}{node.get('name') or path}"
          f" type={node.get('type')}/{node.get('propertyType')}"
          f" hasChildren={node.get('hasChildren')} value={node.get('value')!r}"
          f" children={len(kids)}")
    if depth <= 0:
        for k in kids[:6]:
            print(f"{indent}  · {k.get('name')} type={k.get('type')}"
                  f" value={str(k.get('value'))[:40]!r} hasChildren={k.get('hasChildren')}")
        return
    for k in kids[:4]:
        walk(f"{path}/{k.get('name')}", depth - 1, indent + '  ')


if __name__ == '__main__':
    root = sys.argv[1] if len(sys.argv) > 1 else f'{archive_id("Mob.nx", BASE)}/9409000.img'
    depth = int(sys.argv[2]) if len(sys.argv) > 2 else 3
    walk(root, depth)