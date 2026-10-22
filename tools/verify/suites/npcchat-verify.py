"""Verify /api/simulate/npc-chat against the reference's two-hop lookup.

Npc.nx names string KEYS (Npc.cpp:127-128); String.nx carries the lines (:60).
A reader that read only one archive would find every NPC silent, and a reader
that guessed the key names would find the wrong ones.
"""
import json, os, urllib.parse, urllib.request
try:
    from archivelib import archive_id
except ImportError:            # a missing module must not read as a pass
    print(">>> RED: archivelib.py is missing next to this suite")
    raise SystemExit(1)
except SyntaxError as exc:    # a helper that does not PARSE is not missing
    print(f">>> RED: archivelib.py next to this suite does not parse: {exc}")
    raise SystemExit(1)

BASE = os.environ.get("GATE_BASE") or "http://127.0.0.1:5591"

_STRING_ID = archive_id("String.nx", BASE)
_NPC_ID = archive_id("Npc.nx", BASE)
bad = 0


def chat(nid):
    url = f"{BASE}/api/simulate/npc-chat?npcId={urllib.parse.quote(nid)}"
    with urllib.request.urlopen(url, timeout=300) as r:
        return json.loads(r.read().decode("utf-8"))


def strings(nid):
    """Read String.nx/Npc.img/<id> directly, as the truth the endpoint claims.

    THREE shapes, because the archive is not padded: 7,123 entries measured,
    all of them seven characters, and an NPC id of 2000 is stored as `2000` --
    not `0002000`. `9101002` only looks padded because it is already seven
    digits. `str(nid).zfill(7)` does NOT help: the id arrives already padded, so
    zfill is the identity and the un-padded form is never tried. Three shapes is
    the same lesson as W-041, arriving again in a fourth archive.
    """
    shapes = [nid]
    try:
        numeric = int(nid)
        shapes += [f"{numeric:07d}", str(numeric)]
    except ValueError:
        pass
    for name in dict.fromkeys(shapes):
        url = f"{BASE}/api/inspect?path={urllib.parse.quote(f'{_STRING_ID}/Npc.img/{name}')}"
        try:
            with urllib.request.urlopen(url, timeout=240) as r:
                return {c["name"]: c.get("value")
                        for c in json.loads(r.read().decode("utf-8")).get("children", [])}
        except Exception:
            continue
    return {}


def npcnode(nid, *path):
    url = f"{BASE}/api/inspect?path={urllib.parse.quote(_NPC_ID + '/' + nid + '.img/' + '/'.join(path))}"
    try:
        with urllib.request.urlopen(url, timeout=240) as r:
            return [c.get("value") for c in json.loads(r.read().decode("utf-8")).get("children", [])]
    except Exception:
        return None


CASES = ["9101002", "9101003", "0002000", "10000", "0002102", "9999999"]
for nid in CASES:
    d = chat(nid)
    print(f"\n=== {nid}: found={d.get('found')} image={d.get('imagePath')!r}")
    gen = d.get("general") or []
    by = d.get("byAction") or {}
    print(f"    general={len(gen)} byAction={ {k: len(v) for k, v in by.items()} } "
          f"actions={d.get('actions')} states={d.get('states')}")

    if not d.get("found"):
        if nid != "9999999":
            print("    >>> not found, but not an absent id"); bad += 1
        continue

    # The endpoint's general lines must be exactly the strings the image names.
    truth = strings(nid)
    keys = npcnode(nid, "info", "speak")
    if keys is not None:
        want = [truth.get(k) for k in keys if truth.get(k)]
        if gen != want:
            print(f"    >>> general lines are not the strings info/speak names: "
                  f"{gen} vs {want}"); bad += 1
        else:
            print(f"    info/speak names {keys} -> {len(want)} lines, all match")

    for action, lines in by.items():
        keys = npcnode(nid, action, "speak")
        if keys is None:
            print(f"    >>> byAction has {action!r} but the image has no {action}/speak")
            bad += 1
            continue
        want = [truth.get(k) for k in keys if truth.get(k)]
        if lines != want:
            print(f"    >>> {action} lines {lines} != {want}"); bad += 1

    # The balloon: 9 edges + arrow + the colour.
    b = d.get("balloon") or {}
    edges = sorted(b.get("edges") or {})
    if b.get("found"):
        print(f"    balloon: clr={b.get('textColor')} (0x{b.get('textColor', 0) & 0xffffffff:08x}) "
              f"edges={edges} arrow={'yes' if b.get('arrow') else 'NO'}")
        if len(edges) != 9:
            print(f">>> {len(edges)} edges, the 9-slice needs 9"); bad += 1
        if not b.get("arrow"):
            print(">>> no arrow — the balloon would be a rectangle"); bad += 1
    else:
        print("    >>> ChatBalloon.img/npc not found — nothing could be drawn")
        bad += 1

print(f"\nBAD={bad}")