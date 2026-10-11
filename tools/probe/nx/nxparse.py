"""Reference NX (PKG4) reader — validates the format spec against real files.

Spec derived from OpenStory-Beidou/vendor/NoLifeNx/nlnx (AGPL, C++) and
independently cross-checked against MapleLib/WzLib/Serializer/WzToNxSerializer.cs.

Header (packed, 52 bytes @0x00; node table normally starts @0x40):
  u32 magic      = 0x34474B50 ("PKG4")
  u32 node_count
  u64 node_offset
  u32 string_count ; u64 string_offset
  u32 bitmap_count; u64 bitmap_offset
  u32 audio_count ; u64 audio_offset

Node record (packed, 20 bytes):
  u32 name      -> string-table index
  u32 children  -> index of first child (BFS level order)
  u16 num       -> child count
  u16 type      -> 0 none,1 integer,2 real,3 string,4 vector,5 bitmap,6 audio
  union 8 bytes -> i64 | f64 | u32(string idx) | i32[2] | {u32 idx,u16 w,u16 h} | {u32 idx,u32 len}

String pool entry: u16 byteLength, then UTF-8 bytes. String/offset tables are
arrays of u64 absolute file offsets.
"""

import struct
import sys
import os
import hashlib
from collections import Counter

NONE, INTEGER, REAL, STRING, VECTOR, BITMAP, AUDIO = range(7)
TYPE_NAME = {0: "none", 1: "integer", 2: "real", 3: "string",
             4: "vector", 5: "bitmap", 6: "audio"}
NODE_SIZE = 20
HEADER_SIZE = 52


class NxFile:
    def __init__(self, path):
        with open(path, "rb") as fh:
            self.buf = fh.read()
        self.path = path
        b = self.buf
        (self.magic, self.node_count, self.node_offset,
         self.string_count, self.string_offset,
         self.bitmap_count, self.bitmap_offset,
         self.audio_count, self.audio_offset) = struct.unpack_from("<IIQIQIQIQ", b, 0)
        if self.magic != 0x34474B50:
            raise ValueError("not a PKG4 NX file: 0x%08X" % self.magic)
        self.nodes = [self._node(i) for i in range(self.node_count)]
        self._strings = {}
        self._tabs = {}

    # -- tables ---------------------------------------------------------
    def _off_table(self, off, count):
        if not count:
            return ()
        cached = self._tabs.get(off)
        if cached is None:
            cached = struct.unpack_from("<%dQ" % count, self.buf, off)
            self._tabs[off] = cached
        return cached

    def string_at(self, idx):
        cached = self._strings.get(idx)
        if cached is not None:
            return cached
        soff = self._off_table(self.string_offset, self.string_count)[idx]
        (blen,) = struct.unpack_from("<H", self.buf, soff)
        s = self.buf[soff + 2:soff + 2 + blen].decode("utf-8", "replace")
        self._strings[idx] = s
        return s

    def bitmap_at(self, idx):
        offs = self._off_table(self.bitmap_offset, self.bitmap_count)
        o = offs[idx]
        (clen,) = struct.unpack_from("<I", self.buf, o)
        return o, clen

    def audio_at(self, idx):
        offs = self._off_table(self.audio_offset, self.audio_count)
        o = offs[idx]
        return o, self.audio_count

    def _node(self, i):
        o = self.node_offset + i * NODE_SIZE
        name, children, num, typ = struct.unpack_from("<IIHH", self.buf, o)
        payload = self.buf[o + 12:o + NODE_SIZE]
        n = {"index": i, "off": o, "name": name, "children": children,
             "num": num, "type": typ, "raw": payload}
        if typ == INTEGER:
            n["i"] = struct.unpack("<q", payload)[0]
        elif typ == REAL:
            n["f"] = struct.unpack("<d", payload)[0]
        elif typ == STRING:
            n["s"] = struct.unpack_from("<I", payload, 0)[0]
        elif typ == VECTOR:
            n["xy"] = struct.unpack("<ii", payload)
        elif typ == BITMAP:
            n["bm"] = struct.unpack("<IHH", payload)
        elif typ == AUDIO:
            n["au"] = struct.unpack("<II", payload)
        return n

    # -- tree walk ------------------------------------------------------
    def child_nodes(self, n):
        return [self.nodes[c] for c in range(n["children"], n["children"] + n["num"])]

    def display(self, n):
        t = n["type"]
        if t == INTEGER:
            return str(n["i"])
        if t == REAL:
            return repr(n["f"])
        if t == STRING:
            return '"%s"' % self.string_at(n["s"])
        if t == VECTOR:
            return "(%d, %d)" % n["xy"]
        if t == BITMAP:
            return "<canvas %dx%d idx=%d>" % (n["bm"][1], n["bm"][2], n["bm"][0])
        if t == AUDIO:
            return "<audio len=%d idx=%d>" % (n["au"][1], n["au"][0])
        return ""

    def walk(self, start=0, depth=0, max_depth=99, out=None):
        if out is None:
            out = []
        if depth > max_depth:
            return out
        stack = [(start, depth)]
        while stack:
            i, d = stack.pop()
            n = self.nodes[i]
            out.append((d, n))
            for c in reversed(self.child_nodes(n)):
                stack.append((c["index"], d + 1))
        return out


def audit(path):
    f = NxFile(path)
    print("file      : %s  (%d bytes)" % (path, len(f.buf)))
    print("magic     : 0x%08X  %s" % (f.magic, f.buf[:4].decode()))
    print("node      : count=%-8d offset=0x%X   (ends 0x%X, %s)"
          % (f.node_count, f.node_offset,
             f.node_offset + f.node_count * NODE_SIZE,
             "OK" if f.node_offset + f.node_count * NODE_SIZE <= f.string_offset else "OVERLAP!"))
    print("string    : count=%-8d offset=0x%X" % (f.string_count, f.string_offset))
    print("bitmap    : count=%-8d offset=0x%X" % (f.bitmap_count, f.bitmap_offset))
    print("audio     : count=%-8d offset=0x%X" % (f.audio_count, f.audio_offset))
    print()

    tree = f.walk()
    print("total nodes reachable from root: %d / %d declared" % (len(tree), f.node_count))
    tc = Counter(TYPE_NAME[n["type"]] for _, n in tree)
    print("type histogram (reachable): %s" % dict(sorted(tc.items(), key=lambda kv: -kv[1])))
    unreachable = set(range(f.node_count)) - {n["index"] for _, n in tree}
    print("unreachable node ids: %d" % len(unreachable))
    print()

    # verify node index == BFS position (i.e. children index is exactly i+1)
    off_by = [n["index"] for _, n in tree if n["num"] and n["children"] != n["index"] + 1]
    print("nodes whose children[] != index+1 (breaks BFS assumption): %d" % len(off_by))
    if off_by[:5]:
        print("   e.g. %s" % off_by[:5])
    print()

    # child sort order -> binary search (NoLifeNx get_child) validity
    unsorted = 0
    checked = 0
    dup = 0
    for _, n in tree:
        kids = f.child_nodes(n)
        if len(kids) < 2:
            continue
        checked += 1
        names = [f.string_at(k["name"]) for k in kids]
        enc = [s.encode("utf-8") for s in names]
        if enc != sorted(enc):
            unsorted += 1
        if len(set(enc)) != len(enc):
            dup += 1
    print("parents with >=2 children: %d" % checked)
    print("  ...whose children are NOT byte-sorted (binary search unsafe): %d" % unsorted)
    print("  ...that contain duplicate child names: %d" % dup)
    print()

    # canvas payload shape
    bmp = [n for _, n in tree if n["type"] == BITMAP]
    if bmp:
        kinds = Counter()
        for n in bmp[:400]:
            o, clen = f.bitmap_at(n["bm"][0])
            head = f.buf[o + 4:o + 4 + 12]
            if head[:4] == b"RIFF" and head[8:12] == b"WEBP":
                kinds["webp"] += 1
            else:
                kinds["lz4-block"] += 1
        print("canvas payloads sampled (%d): %s" % (min(len(bmp), 400), dict(kinds)))
    return f


def dump(path, max_depth=4, limit=200):
    f = NxFile(path)
    tree = f.walk()
    shown = 0
    for d, n in tree:
        if d > max_depth or shown >= limit:
            break
        pad = "  " * d
        print("%s%-40s %-8s %s" % (pad, f.string_at(n["name"]) or "<root>",
                                  TYPE_NAME[n["type"]], f.display(n)))
        shown += 1


def digest(path):
    """Emit the same canonical record digest as NxVerify (C#), so the two
    independent readers can be compared byte for byte.

    Format, one line per node in INDEX order (not tree order):
        index|type|name|childCount|payload
    hashed as SHA-256 over the UTF-8 encoding of the whole stream.
    """
    f = NxFile(path)
    h = hashlib.sha256()
    type_counts = [0] * 7
    buf = []
    for i in range(f.node_count):
        n = f.nodes[i]
        type_counts[n["type"]] += 1
        t = n["type"]
        if t == INTEGER:
            payload = str(n["i"])
        elif t == REAL:
            # Raw IEEE-754 bytes, matching NxVerify. Formatting doubles as text would make
            # the digest a test of .NET's float formatting against Python's, and those
            # disagree on exponent notation ("1E-05" vs "1e-05").
            payload = struct.pack("<d", n["f"]).hex()
        elif t == STRING:
            payload = f.string_at(n["s"])
        elif t == VECTOR:
            payload = "%d,%d" % n["xy"]
        elif t == BITMAP:
            payload = "%d:%dx%d" % (n["bm"][0], n["bm"][1], n["bm"][2])
        elif t == AUDIO:
            payload = "%d:%d" % (n["au"][0], n["au"][1])
        else:
            payload = "-"
        buf.append("%d|%d|%s|%d|%s\n" % (i, t, f.string_at(n["name"]), n["num"], payload))
        if len(buf) >= 4096:
            h.update("".join(buf).encode("utf-8"))
            buf = []
    if buf:
        h.update("".join(buf).encode("utf-8"))

    seen = set()
    stack = [0]
    while stack:
        i = stack.pop()
        if i in seen:
            continue
        seen.add(i)
        n = f.nodes[i]
        stack.extend(range(n["children"], n["children"] + n["num"]))

    multi = unsorted = dup_parents = 0
    for n in f.nodes:
        if n["num"] < 2:
            continue
        multi += 1
        names = [f.string_at(f.nodes[c]["name"]) for c in range(n["children"], n["children"] + n["num"])]
        enc = [s.encode("utf-8") for s in names]
        # Two distinct conditions, counted apart. A `>=` test conflates them and reports a
        # sorted parent holding two identically-named children as unsorted — which is what
        # NX and WZ both legitimately allow, so it would be a false alarm.
        if any(enc[i] > enc[i + 1] for i in range(len(enc) - 1)):
            unsorted += 1
        if len(set(enc)) != len(enc):
            dup_parents += 1

    encodings = {}
    for n in f.nodes:
        if n["type"] == BITMAP:
            o, clen = f.bitmap_at(n["bm"][0])
            head = f.buf[o + 4:o + 4 + 16]
            key = ("webp/" + head[12:16].decode("ascii", "replace")) if (
                head[:4] == b"RIFF" and head[8:12] == b"WEBP") else "lz4"
            encodings[key] = encodings.get(key, 0) + 1

    enc = ",".join("%s=%d" % (k, encodings[k]) for k in sorted(encodings)) if encodings else "-"
    return "\t".join([
        os.path.basename(path), "OK",
        "bytes=%d" % len(f.buf),
        "nodes=%d" % f.node_count,
        "strings=%d" % f.string_count,
        "bitmaps=%d" % f.bitmap_count,
        "audio=%d" % f.audio_count,
        "nodeOff=0x%x" % f.node_offset,
        "strOff=0x%x" % f.string_offset,
        "bmpOff=0x%x" % f.bitmap_offset,
        "audOff=0x%x" % f.audio_offset,
        "reachable=%d" % len(seen),
        "orphans=%d" % (f.node_count - len(seen)),
        "multiChildParents=%d" % multi,
        "unsortedParents=%d" % unsorted,
        "duplicateNameParents=%d" % dup_parents,
        "t_none=%d" % type_counts[0],
        "t_int=%d" % type_counts[1],
        "t_real=%d" % type_counts[2],
        "t_str=%d" % type_counts[3],
        "t_vec=%d" % type_counts[4],
        "t_bmp=%d" % type_counts[5],
        "t_aud=%d" % type_counts[6],
        "enc=%s" % enc,
        "digest=%s" % h.hexdigest(),
    ])


if __name__ == "__main__":
    mode = sys.argv[1] if len(sys.argv) > 1 else "audit"
    files = sys.argv[2:] or [r"C:\MUWORK\GAME\MPSH\Base.nx"]
    if mode == "digest":
        for p in files:
            print(digest(p))
    elif mode == "audit":
        for p in files:
            print("=" * 78)
            audit(p)
    else:
        for p in files:
            print("=" * 78)
            dump(p, max_depth=int(mode))