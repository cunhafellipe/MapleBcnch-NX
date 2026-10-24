#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Fetch one mob frame's PNG and count its non-transparent pixels.

Requirement 2 of TASK23: prove /api/simulate/mob-sprite hands over a real PNG --
magic bytes, byte length, and how many distinct opaque pixels it carries. A 200
with hasArt:true and a canvas that decodes to nothing would otherwise pass.

Usage: python task23-pixels.py <mobId> [kind] [action]
"""
import io
import json
import os
import sys
import urllib.parse
import urllib.request
import zlib

# The instance comes from GATE_BASE and from nowhere else (task-139).  There is
# deliberately no default port: this probe used to carry one, and an instance
# older than this tree answers every one of these requests happily, so a
# fallback turns "that instance is 27 product commits behind" into a per-probe
# result that looks like a finding about this tree.
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)),
                                os.pardir, 'verify', 'suites'))
try:
    from archivelib import require_gate_base
except ImportError:
    print(">>> RED: archivelib.py is missing next to this suite")
    raise SystemExit(1)

BASE = require_gate_base()


def get_json(url):
    with urllib.request.urlopen(url, timeout=60) as r:
        return json.load(r)


def get_bytes(url):
    with urllib.request.urlopen(url, timeout=60) as r:
        return r.read(), r.headers.get('content-type', '')


def count_opaque(png):
    """Walk an 8-bit RGBA PNG's IDAT and count pixels with alpha != 0.

    Only the non-interlaced, 8-bit-per-channel case is handled, which is what
    /api/canvas emits; anything else is reported rather than guessed at.
    """
    if png[:8] != b'\x89PNG\r\n\x1a\n':
        return None
    pos, idat, meta = 8, bytearray(), {}
    while pos < len(png):
        length = int.from_bytes(png[pos:pos + 4], 'big')
        ctype = png[pos + 4:pos + 8]
        data = png[pos + 8:pos + 8 + length]
        if ctype == b'IHDR':
            w = int.from_bytes(data[0:4], 'big')
            h = int.from_bytes(data[4:8], 'big')
            meta = dict(w=w, h=h, depth=data[8], color=data[9], interlace=data[12])
        elif ctype == b'IDAT':
            idat += data
        pos += 12 + length

    if meta.get('depth') != 8 or meta.get('color') != 6 or meta.get('interlace') != 0:
        return {'unsupported': meta, 'opaque': None}

    raw = zlib.decompress(bytes(idat))
    w, h = meta['w'], meta['h']
    stride = w * 4
    opaque = 0
    prev = bytearray(stride)
    pos = 0
    for _ in range(h):
        ftype = raw[pos]
        line = bytearray(raw[pos + 1:pos + 1 + stride])
        pos += 1 + stride
        # Undo the PNG per-scanline filter in place.
        for i in range(stride):
            a = line[i - 4] if i >= 4 else 0
            b = prev[i]
            c = prev[i - 4] if i >= 4 else 0
            x = line[i]
            if ftype == 1:
                x += a
            elif ftype == 2:
                x += b
            elif ftype == 3:
                x += (a + b) // 2
            elif ftype == 4:
                p = a + b - c
                pa, pb, pc = abs(p - a), abs(p - b), abs(p - c)
                x += a if (pa <= pb and pa <= pc) else (b if pb <= pc else c)
            line[i] = x & 0xFF
        for i in range(3, stride, 4):
            if line[i] != 0:
                opaque += 1
        prev = line
    return {'opaque': opaque, 'w': w, 'h': h, 'total': w * h}


if __name__ == '__main__':
    mob = sys.argv[1] if len(sys.argv) > 1 else '9409000'
    kind = sys.argv[2] if len(sys.argv) > 2 else 'm'
    want = sys.argv[3] if len(sys.argv) > 3 else 'stand'

    url = f'{BASE}/api/simulate/mob-sprite?mobId={mob}&kind={kind}'
    d = get_json(url)
    print(f"mob {d['mobId']} kind={d['kind']} imagePath={d['imagePath']} hasArt={d['hasArt']}")
    acts = {a['name']: a for a in d['actions']}
    if want not in acts:
        print('actions:', list(acts))
        sys.exit(1)
    frame = acts[want]['frames'][0]
    png, ctype = get_bytes(
        f"{BASE}/api/canvas?path={urllib.parse.quote(frame['path'], safe='')}")
    print(f"  {want}/0 -> {frame['width']}x{frame['height']} path={frame['path']}")
    print(f"  magic={png[:8]!r} bytes={len(png)} content-type={ctype}")
    print(' ', count_opaque(png))