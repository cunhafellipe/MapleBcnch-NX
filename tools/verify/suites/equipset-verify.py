"""Verify the WHOLE equipped set moves the sheet, not just the weapon.

The reference's sheet is `base + sum over the inventory` (Player.cpp:309-313).
Measured 2026-10-16 over 840 items across the 12 stat-bearing categories:
incPDD 326, incDEX 134, incMDD 129, incSTR 123, incLUK 105, incINT 70,
incPAD 69, incMMP 62, incEVA 42, incMHP 39, incACC 31, incSpeed 27,
incMAD 18, incJump 15. So armour is NOT cosmetic: it moves accuracy, max HP,
max damage and both defences.
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

_MAP_ID = archive_id("Map.nx", BASE)
MAP = f"{_MAP_ID}/Map/Map1/100000004.img"

bad = 0


def post(path, body):
    req = urllib.request.Request(f"{BASE}{path}", data=json.dumps(body).encode("utf-8"),
                                 headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=600) as r:
        return json.loads(r.read().decode("utf-8"))


def stats(item):
    url = f"{BASE}/api/simulate/equip-stats?itemId={urllib.parse.quote(str(item))}"
    with urllib.request.urlopen(url, timeout=180) as r:
        return json.loads(r.read().decode("utf-8"))


def sheet(st):
    return (f"hp={st['maxHp']} str={st['strength']} dex={st['dexterity']} "
            f"int={st['intelligence']} luk={st['luck']} watk={st['watk']} "
            f"acc={st['accuracy']} dmg={st['minDamage']}-{st['maxDamage']} "
            f"def={st['physicalDefence']}/{st['magicalDefence']}")


# --------------------------------------------------------------- calibration
# Items chosen for what they CARRY, read from the archive rather than assumed.
#
# The first pass at this file asserted that `01050000` carries incDEX, because an
# earlier probe had printed incDEX 3 / incMMP 10 next to a Longcoat row. It does
# not: it carries incSTR 4 / incMHP 5 / incPDD 60 / incMDD 20. Accuracy is derived
# from DEX and LUK ONLY (CharStats.cpp:186-192), so a +4 STR item cannot move it and
# the check was wrong, not the code. Every item below is now read first.
LONGCOAT = "01050000"    # measured: incSTR 4, incMHP 5, incPDD 60, incMDD 20
GLOVE = "01082143"       # measured: incDEX 3 AND `incLUk` 3 -- the spelling anomaly
SWORD = "1302000"        # measured: incPAD 17, attackSpeed 4
for label, item in (("longcoat", LONGCOAT), ("glove", GLOVE), ("sword", SWORD)):
    s = stats(item)
    print(f"  {label:10s} {item}: found={s['found']} str={s.get('str')} dex={s.get('dex')} "
          f"luk={s.get('luk')} pdd={s.get('wdef')} mdd={s.get('mdef')} "
          f"mhp={s.get('hp')} pad={s.get('watk')} anomalies={s.get('spellingAnomalies')}")
    if not s["found"]:
        print(f">>> {item} is not in the archive; the rest of this check is void")
        bad += 1

s = post("/api/simulate/session", {"path": MAP, "startX": 1710, "startY": -25,
                                   "invulnerable": True})
sid = s["sessionId"]
st = s["status"]
print(f"\nsession {sid}")
print(f"  bare      {sheet(st)}")

# ------------------------------------------------- weapon alone (the old path)
f = post(f"/api/simulate/session/{sid}/equip", {"items": {"Weapon-1": SWORD}})
print(f"  +sword    {sheet(f['status'])}   attackStance={f.get('attackStance', '')!r}")
armed = f["status"]
if armed["watk"] != 17:
    print(f">>> the weapon's incPAD did not reach the sheet (watk={armed['watk']})"); bad += 1

# ------------------------------------------- sword AND two stat-carrying pieces
f = post(f"/api/simulate/session/{sid}/equip",
         {"items": {"Weapon-1": SWORD, "Longcoat": LONGCOAT, "Glove": GLOVE}})
both = f["status"]
print(f"  +all      {sheet(both)}")

if both["physicalDefence"] <= armed["physicalDefence"]:
    print(">>> the longcoat's incPDD did not move physical defence")
    bad += 1
if both["maxHp"] <= armed["maxHp"]:
    print(f">>> the longcoat's incMHP did not raise max HP ({armed['maxHp']} -> {both['maxHp']})")
    bad += 1

# Accuracy is DERIVED from DEX and LUK (CharStats.cpp:186-192), so the glove --
# incDEX 3 plus `incLUk` 3 -- must move it even though it carries no incACC.
# The longcoat's +4 STR must NOT: STR feeds damage (CharStats.cpp:118) and
# accuracy is not derived from it.
want_acc = int(5 * 0.8 + 4 * 0.5) + int(3 * 0.8 + 3 * 0.5)
print(f"  accuracy: bare 6, with the glove {both['accuracy']}, "
      f"reference says trunc((5+3)*0.8 + (4+3)*0.5) = {want_acc}")
if both["accuracy"] != want_acc:
    print(f">>> accuracy is {both['accuracy']}, the formula says {want_acc}")
    bad += 1

# ------------------------------------------------- a REPLACEMENT, not a patch
f = post(f"/api/simulate/session/{sid}/equip", {"items": {"Weapon-1": SWORD}})
stripped = f["status"]
print(f"  armour off {sheet(stripped)}")
for name, got, want in (("def", stripped["physicalDefence"], armed["physicalDefence"]),
                        ("maxHp", stripped["maxHp"], armed["maxHp"]),
                        ("acc", stripped["accuracy"], armed["accuracy"])):
    if got != want:
        print(f">>> stripping the armour left {name} at {got}; a patch would have "
              f"kept it. Expected {want}.")
        bad += 1
if stripped["watk"] != armed["watk"]:
    print(">>> removing the armour also removed the weapon")
    bad += 1

# ------------------------------------------------- an empty strip is not an absence
f = post(f"/api/simulate/session/{sid}/equip", {"items": {"Weapon-1": ""}})
print(f"  all off   {sheet(f['status'])}")
if f["status"]["maxDamage"] != 1:
    print(f">>> a character with nothing equipped should swing for 1, "
          f"not {f['status']['maxDamage']}")
    bad += 1

# ------------------------------------------------- slots that are not equipment
f = post(f"/api/simulate/session/{sid}/equip",
         {"items": {"Weapon-1": SWORD, "Head": "00012000", "Hair-1": "00002010",
                    "Body": "00002000", "Face-1": "00012000"}})
if f["status"]["maxDamage"] != armed["maxDamage"]:
    print(f">>> Head/Body/Hair/Face changed the damage window "
          f"({f['status']['maxDamage']} vs {armed['maxDamage']}); they are not equipment")
    bad += 1
else:
    print("  Head/Body/Hair/Face contribute nothing, as intended")

print(f"\nBAD={bad}")
raise SystemExit(1 if bad else 0)
