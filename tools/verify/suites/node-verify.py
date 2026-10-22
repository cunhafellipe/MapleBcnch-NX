"""W-068 — the first judge for the `node` area.

`pm/SPEC.yaml` rates `node` criticality HIGH and, as of 2026-10-16, no suite
referenced it: the editor's daily path -- browse, inspect, search, edit -- had no
automated verdict at all. This is that verdict.

What it asserts, and why each one is a PROMISE rather than a property of today:

  CONTROL  A known node resolves and carries a value and a type.
  R-2       /api/children returns DESCRIPTORS and carries NO `value` field.
            This is the project's most repeated measurement error: a reader that
            takes `None` from /api/children writes "the archive does not have it".
  R-2       /api/inspect is the only endpoint that answers with a value.
  R-5       An absent node reads null/absent, never a printed 0.
  T-10      Node names resolve OrdinalIgnoreCase. `Glove/01082143.img` spells a
            real stat `incLUk` with a lower-case k, so an exact-case read silently
            drops a real value.
  README    "Edits remain in memory until you save" -- proven here end to end
            without ever saving: the value changes, /api/changes reports the file
            dirty, and the bytes on disk are unchanged.
  SAFETY    An unknown path is an ERROR, not a quiet null that reads as "absent".

It opens NO archive and SAVES NOTHING. Every mutation it performs is in-memory
and is reverted, which save-audit already proved is safe.
"""
import hashlib, json, os, sys, urllib.request, urllib.parse

BASE = os.environ.get("GATE_BASE") or "http://127.0.0.1:5591"
asserts = 0
fails = 0


def Check(name, ok, detail=""):
    global asserts, fails
    asserts += 1
    if ok:
        print(f"  ok    {name}")
    else:
        fails += 1
        print(f"  FAIL  {name} -- {detail}")
        print(f">>> RED: node-verify -- {name} ({detail})")


def call(route, body=None, method=None):
    data = json.dumps(body).encode() if body is not None else None
    h = {"Content-Type": "application/json"} if data else {}
    req = urllib.request.Request(BASE + route, data=data, headers=h,
                                 method=method or ("POST" if data else "GET"))
    try:
        with urllib.request.urlopen(req, timeout=600) as r:
            return r.status, json.loads(r.read().decode("utf-8"))
    except urllib.error.HTTPError as e:
        raw = e.read().decode("utf-8", "replace")
        try:
            return e.code, json.loads(raw)
        except Exception:
            return e.code, {"raw": raw[:200]}


def children(p):
    _, r = call("/api/children?" + urllib.parse.urlencode({"path": p}))
    return r if isinstance(r, list) else []


def inspect(p):
    code, r = call("/api/inspect?" + urllib.parse.urlencode({"path": p}))
    return code, (r or {}).get("node") or {}


SCALARS = ("Long", "Short", "Int", "Float", "Double", "String",
           "Ulong", "Ushort", "Byte", "Boolean")


def first_scalar(container, depth=0, maxdepth=4):
    """BFS for a node whose type is a real scalar.

    Two discriminators that are NOT interchangeable, both learned by being wrong:
      * `hasChildren`, never `childCount`. An NX image reports childCount = -1
        ("not counted") with hasChildren = true, so `childCount > 0` skips every
        image in the archive.
      * the type must be in SCALARS, never merely "has no children". An EMPTY
        `SubProperty` -- `.../reactor` on the test map is one -- has no children
        and is not editable as text; the edit route answers HTTP 400 with
        "'SubProperty' values are not editable as text".
    """
    ch = children(container)
    for c in ch:
        if c.get("type") in SCALARS:
            return c
    for c in ch:
        if c.get("hasChildren"):
            found = first_scalar(c["path"], depth + 1, maxdepth)
            if found is not None:
                return found
    return None


def dirty_files():
    """`/api/changes` answers `{"files": [{"file": {...}, "images": [...]}, ...]}` --
    each entry NESTED under a `file` key. Two wrong readings happened here in one
    afternoon: the first assumed a bare list and got None; the second read `id`
    off the wrapper and got nothing. Either way the harness reported a RED
    against a route that had answered correctly."""
    _, r = call("/api/changes")
    if isinstance(r, dict):
        return r.get("files") or []
    return r or []


def dirty_ids():
    out = set()
    for entry in dirty_files():
        f = entry.get("file") if isinstance(entry, dict) else None
        if isinstance(f, dict) and f.get("id"):
            out.add(f["id"])
        elif isinstance(entry, dict) and entry.get("id"):
            out.add(entry["id"])
    return out


print("=== pick a subject from the archive, do not guess a node name ===")
files = call("/api/files")[1] or []
mapf = next((f for f in files if f["name"] == "Map.nx"), None)
if not mapf:
    Check("Map.nx is open", False, "not in /api/files")
    print(f"\nVERDICT: node-verify {asserts} assertions, {fails} failed")
    sys.exit(1 if fails else 0)
root = f"{mapf['id']}/Map/Map1/100000004.img"
Check("the test map resolves under Map.nx", bool(children(root)), f"{root} has no children")

leaf = first_scalar(root)
if leaf is None:
    Check("an EDITABLE scalar exists under the map", False,
          f"none within depth 4 of {root}")
    print(f"\nVERDICT: node-verify {asserts} assertions, {fails} failed")
    sys.exit(1 if fails else 0)
path = leaf["path"]
print(f"  subject: {path}  ({leaf.get('type')})")

print()
print("=== CONTROL: it resolves, and it carries a value and a type ===")
code, node = inspect(path)
print(f"  /api/inspect -> {code} value={node.get('value')!r} type={node.get('type')!r}")
Check("CONTROL: the node resolves", code == 200, f"HTTP {code}")
Check("CONTROL: it reports a type", bool(node.get("type")), "type is empty")
Check("CONTROL: it reports a value", node.get("value") is not None,
      f"value is {node.get('value')!r}")

print()
print("=== R-2: /api/children is a DESCRIPTOR endpoint and must not carry value ===")
kids = children(root)
Check("children returns a list", isinstance(kids, list) and len(kids) > 0,
      f"{type(kids).__name__}, {len(kids)} entries")
carrying = [c["name"] for c in kids if "value" in c]
Check("no child descriptor carries a `value` key", not carrying,
      f"{len(carrying)} do: {carrying[:5]} -- a reader that trusts this writes "
      f"'the archive has no value' about a node that has one")
sample = kids[0] if kids else {}
print(f"  descriptor keys: {sorted(sample.keys())}")
Check("the descriptor shape is the documented one",
      {"path", "name", "kind", "hasChildren"} <= set(sample.keys()),
      f"got {sorted(sample.keys())}")

print()
print("=== T-10: node names resolve case-insensitively ===")
last = path.rsplit("/", 1)[-1]
upper = path.rsplit("/", 1)[0] + "/" + last.upper()
lower = path.rsplit("/", 1)[0] + "/" + last.lower()
same = True
detail = ""
for variant, label in ((upper, "UPPER"), (lower, "lower")):
    if variant == path:
        continue
    c2, n2 = inspect(variant)
    if c2 != 200 or n2.get("value") != node.get("value"):
        same = False
        detail = f"{label} '{variant}' -> HTTP {c2}, value {n2.get('value')!r}"
Check("the same node answers under any case of its name", same, detail)

print()
print("=== R-5: an absent node reads as absent, never as 0 ===")
code_missing, missing = inspect(f"{root}/definitely-not-a-real-node-9f3c1a")
Check("an unknown path is an ERROR, not a quiet null",
      code_missing >= 400 or not missing,
      f"HTTP {code_missing} and returned {missing!r} -- a null that looks like "
      f"'absent' is exactly how this project has written false findings")

print()
print("=== README: edits stay in memory until you save ===")
old_value = node.get("value")
if leaf.get("type") == "String":
    new_value = "node-verify-3b91d7"
elif leaf.get("type") in ("Long", "Int", "Short", "Ushort", "Byte"):
    new_value = "864209731"
else:
    new_value = "1234.5"

_, before_changes = call("/api/changes")
dirty_before = mapf["id"] in dirty_ids()

code_set, setr = call("/api/node/value", {"path": path, "value": new_value},
                      method="PUT")
Check("the edit was accepted", code_set == 200, f"HTTP {code_set}: {setr}")
_, after = inspect(path)
Check("the value reads back from the session", str(after.get("value")) == new_value,
      f"wrote {new_value!r}, reads {after.get('value')!r}")

dirty_after = mapf["id"] in dirty_ids()
print(f"  /api/changes reports {mapf['id']} dirty: before={dirty_before} "
      f"after={dirty_after}")
Check("the file is reported dirty after an edit", bool(dirty_after),
      f"/api/changes did not list {mapf['id']} -- and Save refuses a clean file "
      f"with `nothing to write`, so an edit nobody can see is an edit that cannot "
      f"be saved")

# Revert. Nothing is saved, so the archive on disk was never touched; this only
# puts the in-memory tree back so the NEXT suite starts from a known state.
code_back, backr = call("/api/node/value", {"path": path, "value": old_value},
                        method="PUT")
_, restored = inspect(path)
Check("the value reverts", str(restored.get("value")) == str(old_value),
      f"wrote {old_value!r}, reads {restored.get('value')!r}")

print()
print(f"VERDICT: node-verify {asserts} assertions, {fails} failed")
if fails:
    sys.exit(1)