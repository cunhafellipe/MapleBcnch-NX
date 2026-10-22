#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Read the real shape of an NX mob action node, through /api/inspect.

/api/children reported `stand` with zero children and /api/node returned an
empty body for the same path (TASK23 trap #1: the children endpoint does not
reflect the real node structure). This walks the same path the rest of the
codebase walks it and prints what is actually there.

Usage: python task23-shapes.py f2/9409000.img [depth]
"""
import json
import sys
import urllib.parse
import urllib.request

BASE = 'http://127.0.0.1:5100'


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
    root = sys.argv[1] if len(sys.argv) > 1 else 'f2/9409000.img'
    depth = int(sys.argv[2]) if len(sys.argv) > 2 else 3
    walk(root, depth)