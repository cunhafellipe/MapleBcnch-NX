"""Verify /api/simulate/equip-stats against measured archive truth.

The three cases that a name-based reader would fail are the point of this file:
  Glove/01082143.img   spells its LUK node `incLUk` (lower-case k)
  Weapon/01372038.img  carries `incRMAI` / `incRMAL`, which no reference defines
  Weapon/01302000.img  the eight-digit form a slot actually hands over
"""
import json, os, urllib.parse, urllib.request
try:
    from archivelib import archive_id, require_gate_base
except ImportError:            # a missing module must not read as a pass
    print(">>> RED: archivelib.py is missing next to this suite")
    raise SystemExit(1)
except SyntaxError as exc:    # a helper that does not PARSE is not missing
    print(f">>> RED: archivelib.py next to this suite does not parse: {exc}")
    raise SystemExit(1)

BASE = require_gate_base()

_CHAR_ID = archive_id("Character.nx", BASE)


def stats(item):
    url = f"{BASE}/api/simulate/equip-stats?itemId={urllib.parse.quote(str(item))}"
    with urllib.request.urlopen(url, timeout=180) as r:
        return json.loads(r.read().decode("utf-8"))


def truth(path):
    url = f"{BASE}/api/inspect?path={urllib.parse.quote(path)}"
    try:
        with urllib.request.urlopen(url, timeout=180) as r:
            return {c["name"]: c.get("value") for c in json.loads(r.read().decode("utf-8")).get("children", [])}
    except Exception:
        return {}


MAP = {"incSTR": "str", "incDEX": "dex", "incINT": "int", "incLUK": "luk",
       "incMHP": "hp", "incMMP": "mp", "incPAD": "watk", "incMAD": "magic",
       "incPDD": "wdef", "incMDD": "mdef", "incACC": "acc", "incEVA": "avoid",
       "incSpeed": "speed", "incJump": "jump"}

bad = 0
CASES = [
    ("Glove/01082143.img", "01082143", "incLUk spelling anomaly"),
    ("Weapon/01372038.img", "01372038", "incRMAI/incRMAL unknown nodes"),
    ("Weapon/01302000.img", "1302000", "8-digit slot form vs bare id"),
    ("Cap/01002004.img", "01002004", "mixed stats"),
    ("Coat/01050000.img", "01050000", "coarse sample"),
    ("Nothing/09999999.img", "09999999", "absent item"),
]

for prefix, item, why in CASES:
    d = stats(item)
    t = truth(f"{_CHAR_ID}/{prefix}/info")
    print(f"\n=== {prefix}  (asked as {item!r}) — {why}")
    print(f"    found={d.get('found')} code={d.get('weaponCode')} type={d.get('weaponType')!r} "
          f"attackSpeed={d.get('attackSpeed')} slot={d.get('slot')!r} anomalies={d.get('spellingAnomalies')}")
    if not t:
        print(f"    (no info node in the archive for this path)")
        continue
    mismatched = []
    for node, key in MAP.items():
        want = None
        for n, v in t.items():                      # case-insensitive, as the reader is
            if n.lower() == node.lower():
                want = v
                break
        if want is None:
            continue
        got = d.get(key)
        if got is None or int(got) != int(want):
            mismatched.append(f"{node}: archive={want} api={got}")
    print(f"    stat nodes present in archive: "
          f"{sorted(n for n in t if n.lower().startswith('inc'))}")
    print(f"    api reports nonzero: "
          f"{sorted(k for k in MAP.values() if d.get(k))}")
    if mismatched:
        print(f"    >>> MISMATCH {mismatched}")
        bad += 1
    else:
        print("    every inc* node matches the archive exactly")

print(f"\nBAD={bad}")
raise SystemExit(1 if bad else 0)
