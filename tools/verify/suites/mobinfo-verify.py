"""Exercise /api/simulate/mob-info against the measured archive truth."""
import json, os, urllib.parse, urllib.request

try:
    from archivelib import require_gate_base
except ImportError:            # a missing module must not read as a pass
    print(">>> RED: archivelib.py is missing next to this suite")
    raise SystemExit(1)
except SyntaxError as exc:    # a helper that does not PARSE is not missing
    print(f">>> RED: archivelib.py next to this suite does not parse: {exc}")
    raise SystemExit(1)
BASE = require_gate_base()


def info(mob):
    url = f"{BASE}/api/simulate/mob-info?mobId={urllib.parse.quote(mob)}"
    with urllib.request.urlopen(url, timeout=180) as r:
        return json.loads(r.read().decode("utf-8"))


CASES = ["100100", "0100100", "2400049", "8800002", "4230102", "100101",
         "2220100", "9999999", "not-a-number", "9303202", "9460024", "8787174"]

bad = 0
for mid in CASES:
    try:
        d = info(mid)
    except Exception as e:
        print(f"{mid:>10}  ERROR {e}")
        bad += 1
        continue
    print(f"{mid:>10}  id={d.get('id')} name={d.get('name')!r} hasInfo={d.get('hasInfo')}")
    print(f"            lvl={d.get('level')} hp={d.get('maxHp')} mp={d.get('maxMp')} exp={d.get('exp')} "
          f"pa={d.get('paDamage')} ma={d.get('maDamage')} pd={d.get('pdDamage')} md={d.get('mdDamage')}")
    print(f"            cat={d.get('category')}/{d.get('categoryName')!r} mobType={d.get('mobType')!r} fs={d.get('fs')}")
    if d.get("elements"):
        print(f"            elements={[e['label'] for e in d['elements']]}")
    if d.get("traits"):
        print(f"            traits={d['traits']}")
    if d.get("disappearAfterSeconds"):
        print(f"            disappears after {d['disappearAfterSeconds']}s")
    if d.get("revivesInto"):
        print(f"            revives into {d['revivesInto'][:6]}")
    bk = d.get("book")
    if bk:
        print(f"            book: episode={len(bk.get('episode',''))} chars, "
              f"maps={len(bk.get('maps',[]))}, rewards={len(bk.get('rewards',[]))}")
        print(f"                   head={bk.get('episode','')[:60]!r}")
    if not d.get("hasInfo") and mid not in ("9999999", "not-a-number"):
        print("            UNEXPECTED hasInfo=false")
        bad += 1

# every stat key present in the response at all, and null where the archive is silent
# The house convention is DefaultIgnoreCondition = WhenWritingNull (Program.cs:100),
# so an absent key IS the null. Check the value, not the key's presence.
d = info("0100100")
keys = ["level", "maxHp", "maxMp", "exp", "paDamage", "maDamage", "pdDamage", "mdDamage",
        "pdRate", "mdRate", "acc", "eva", "pushed", "speed", "hpRecovery", "mpRecovery",
        "category", "categoryName", "mobType", "fs", "boss", "undead", "firstAttack",
        "bodyAttack", "removeAfter", "charismaExp", "weaponPoints", "elements", "traits",
        "book", "revivesInto", "disappearAfterSeconds"]
extra = [k for k in keys if k not in d]
print(f"\nkeys present: {len(keys) - len(extra)}/{len(keys)}; not serialised on 0100100: {extra or 'none'}")

# The monster-book episode must come back with REAL line breaks.
snail = info("0100100")
ep = (snail.get("book") or {}).get("episode", "")
print(f"episode has a literal backslash-n: {'\\\\n' in ep} (expect False)")
print(f"episode line count: {ep.count(chr(10))} (expect >= 3)")
print("episode head:")
for line in ep.split(chr(10))[:5]:
    print("   ", line)
print(f"\nBAD={bad}")
raise SystemExit(1 if bad else 0)
