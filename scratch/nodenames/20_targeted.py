#!/usr/bin/env python3
"""Targeted verification of the highest-risk assertions, one named case each.
Each case states the archive, the exact path, and what the comment claims.
"""
import json, os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import mc

HERE = os.path.dirname(os.path.abspath(__file__))
F = mc.files()
CH, MP, NP, IT, EF, MO, SK, ST, BS, TM = (F["Character.nx"], F["Map.nx"], F["Npc.nx"],
                                          F["Item.nx"], F["Effect.nx"], F["Mob.nx"],
                                          F["Skill.nx"], F["String.nx"], F["Base.nx"],
                                          F["TamingMob.nx"])


def kids(p):
    try:
        d = mc.inspect(p, use_cache=False)
    except Exception as e:
        return None, str(e)
    return sorted({c["name"] for c in (d.get("children") or [])}), None


def val(p):
    try:
        d = mc.inspect(p, use_cache=False)
    except Exception as e:
        return None
    return (d.get("node") or {}).get("value")


def case(label, path, note=""):
    n, err = kids(path)
    if n is None:
        print(f"[ABSENT 404] {label}\n            path={path}  {err}")
    else:
        print(f"[OK n={len(n)}] {label}\n            path={path}  {note}\n            children={n}")
    return n


print("=" * 100)
print("1. info/speedMaxOver  vs  info/speedMaxOver<SPACE>   (Map.nx map root info)")
# find a map that the comment claims carries the trailing-space key: sample 200 maps
import collections
c_plain = collections.Counter()
c_space = collections.Counter()
found = []
regions = mc.child_names(f"{MP}/Map") or []
allmaps = []
for r in regions:
    for m in (mc.child_names(f"{MP}/Map/{r}") or []):
        allmaps.append(f"Map/{r}/{m}")
print(f"   scanning {len(allmaps)} map images for BOTH spellings...")
from concurrent.futures import ThreadPoolExecutor
def probe(mp):
    n, _ = kids(f"{MP}/{mp}/info")
    if n is None:
        return mp, None, None
    has_plain = "speedMaxOver" in n
    has_space = "speedMaxOver " in n
    if has_plain or has_space:
        return mp, has_plain, has_space
    return mp, None, None
with ThreadPoolExecutor(max_workers=4) as ex:
    for mp, hp, hs in ex.map(probe, allmaps):
        if hp:
            c_plain[hp] += 1
            found.append((mp, hp, hs))
        if hs:
            c_space[hs] += 1
print(f"   info/speedMaxOver      (no trailing space): {c_plain.get(True,0)} maps")
print(f"   info/speedMaxOver<SP>  (trailing space)   : {c_space.get(True,0)} maps")
print(f"   maps carrying either: {len(found)}  e.g. {found[:6]}")

print()
print("=" * 100)
print("2. info/canPartyStatChangeIgnoreParty vs <SP>   (Map.nx)")
def probe2(mp):
    n, _ = kids(f"{MP}/{mp}/info")
    if n is None:
        return None
    hit = [x for x in n if "canPartyStatChangeIgnoreParty" in x]
    return hit
hits = []
with ThreadPoolExecutor(max_workers=4) as ex:
    for h in ex.map(probe2, allmaps):
        if h:
            hits.append(h)
print(f"   maps carrying any spelling: {len(hits)}")
from collections import Counter
cc = Counter(tuple(h) for h in hits)
for k, v in cc.items():
    print(f"      {v:5d} maps: {list(k)}")

print()
print("=" * 100)
print("3. info/limitUI   (Map.nx map root info)  -- comment WzNode.cs:637 claims 2,669 values")
lim = 0
with ThreadPoolExecutor(max_workers=4) as ex:
    for n in ex.map(lambda mp: (kids(f"{MP}/{mp}/info")[0] or []), allmaps):
        if "limitUI" in n:
            lim += 1
print(f"   info/limitUI present on {lim} of {len(allmaps)} maps")
print("   searching every OTHER Map.nx parent for a 'limitUI' child...")
for probe_path in ["Obj", "Tile", "Back", "MapHelper.img", "Physics.img", "Effect.img",
                   "MapLatest", "WorldMap"]:
    n, err = kids(f"{MP}/{probe_path}")
    print(f"      {probe_path}: {n if n else err}")

print()
print("=" * 100)
print("4. info/AmbientBGM , info/bgmSub   (Map.nx map root info)")
amb = bgmsub = 0
with ThreadPoolExecutor(max_workers=4) as ex:
    for n in ex.map(lambda mp: (kids(f"{MP}/{mp}/info")[0] or []), allmaps):
        if "AmbientBGM" in n:
            amb += 1
        if "bgmSub" in n:
            bgmsub += 1
print(f"   info/AmbientBGM : {amb} / {len(allmaps)} maps")
print(f"   info/bgmSub     : {bgmsub} / {len(allmaps)} maps")

print()
print("=" * 100)
print("5. info/setItemID -- Character.nx equips AND Effect.nx/SetEff.img")
case("Character.nx root: is there a SetEff-like folder?",
     CH, "top-level folders")
hits = []
for cat in [c for c in (mc.child_names(CH) or []) if not c.endswith(".img")]:
    for im in [i for i in (mc.child_names(f"{CH}/{cat}") or []) if i.endswith(".img")][:80]:
        n, _ = kids(f"{CH}/{cat}/{im}/info")
        if n and "setItemID" in n:
            hits.append(f"{cat}/{im}")
print(f"   Character.nx equip info/setItemID found on {len(hits)} of the first-80-per-category sample: {hits[:10]}")
se = kids(f"{EF}/SetEff.img")[0] or []
print(f"   Effect.nx/SetEff.img children: {len(se)} ids, first {sorted(se)[:6]}")
for sid in sorted(se)[:4]:
    print(f"      SetEff.img/{sid} -> {kids(f'{EF}/SetEff.img/{sid}')[0]}")
    print(f"      SetEff.img/{sid}/info -> {kids(f'{EF}/SetEff.img/{sid}/info')[0]}")
    print(f"      SetEff.img/{sid} children values ->",
          {c['name']: c.get('value') for c in (mc.inspect(f'{EF}/SetEff.img/{sid}', use_cache=False).get('children') or [])})

print()
print("=" * 100)
print("6. incRMA* -- which images carry them?")
rma = []
for cat in [c for c in (mc.child_names(CH) or []) if not c.endswith(".img")]:
    for im in [i for i in (mc.child_names(f"{CH}/{cat}") or []) if i.endswith(".img")]:
        n, _ = kids(f"{CH}/{cat}/{im}/info")
        if n and any(x.startswith("incRMA") for x in n):
            rma.append((f"{cat}/{im}", [x for x in n if x.startswith("incRMA")]))
print(f"   Character.nx equips with incRMA*: {len(rma)}")
for p, v in rma:
    print(f"      {p}: {v}")

print()
print("=" * 100)
print("7. Glove/01082143.img -- the incLUk case, verified exactly")
case("Character.nx/Glove/01082143.img/info", f"{CH}/Glove/01082143.img/info")
for cand in ("incLUk", "incLUK", "incluk"):
    print(f"      info/{cand} value = {val(f'{CH}/Glove/01082143.img/info/{cand}')!r}")

print()
print("=" * 100)
print("8. Weapon/01302000.img -- afterImage, attackSpeed, attack")
case("Character.nx/Weapon/01302000.img/info", f"{CH}/Weapon/01302000.img/info")
for k in ("afterImage", "attackSpeed", "attack", "level"):
    print(f"      info/{k} = {val(f'{CH}/Weapon/01302000.img/info/{k}')!r}")

print()
print("=" * 100)
print("9. Mob 'never seen' names -- which mobs DO carry them?")
inv = json.load(open(os.path.join(HERE, "mob_info_inventory.json"), encoding="utf-8"))
ok = inv["ok"]
for name in ("charismaEXP", "wp", "noDebuff", "ignoreMovable", "notAttack",
             "senseEXP", "insightEXP", "willEXP", "craftEXP", "charmEXP", "icon",
             "fixedBodyAttackDamageR", "onlyHittedByCommonAttack", "allyMob", "ignoreDamage",
             "wdef", "defence", "mdef"):
    who = [k for k, v in ok.items() if name in (v or {})]
    print(f"   info/{name:26s} {len(who):5d}/2053   {who[:6]}")

print()
print("=" * 100)
print("10. Mob attack1/info/range,hit,ball on the mob MobPatrol.cs:698 names")
print(f"   Mob.nx/9409000.img/info/notAttack = {val(f'{MO}/9409000.img/info/notAttack')!r}")
case("Mob.nx/9409000.img/info", f"{MO}/9409000.img/info", "the mob the comment cites")

print()
print("=" * 100)
print("11. barehands.img/0/stabO1 -- Afterimage class in Character.nx")
case("Character.nx/Afterimage (top level)", CH + "/Afterimage")
for cls in (mc.child_names(f"{CH}/Afterimage") or [])[:6]:
    print(f"      Afterimage/{cls} -> {kids(f'{CH}/Afterimage/{cls}')}")
    print(f"      Afterimage/{cls}/0 -> {kids(f'{CH}/Afterimage/{cls}/0')}")

print()
print("=" * 100)
print("12. Base.nx zmap.img / smap.img content")
case("Base.nx/zmap.img", f"{BS}/zmap.img")
case("Base.nx/smap.img", f"{BS}/smap.img")

print()
print("=" * 100)
print("13. Npc.nx -- union of script|speak|illustration2|default (comment: '7,000-odd of the 10,742')")
u = 0
tot = 0
for i in (mc.child_names(NP) or []):
    n, _ = kids(f"{NP}/{i}/info")
    if n is None:
        continue
    tot += 1
    if any(x in n for x in ("script", "speak", "illustration2", "default")):
        u += 1
print(f"   Npc.nx images with an info node: {tot}; carrying >=1 of the four: {u}")

print()
print("=" * 100)
print("14. Skill.nx shape -- masterLevel / weapon / elemAttr / common / level")
books = mc.child_names(SK) or []
print(f"   books: {len(books)}")
b0 = books[0]
print(f"   {b0} -> {kids(f'{SK}/{b0}')}")
sk = kids(f"{SK}/{b0}/skill") or []
print(f"   {b0}/skill -> {len(sk)}: {sk[:6]}")
if sk:
    s0 = sk[0]
    print(f"   {b0}/skill/{s0} -> {kids(f'{SK}/{b0}/skill/{s0}')}")
    print(f"   {b0}/skill/{s0}/info -> {kids(f'{SK}/{b0}/skill/{s0}/info')}")
    print(f"   {b0}/skill/{s0}/common -> {kids(f'{SK}/{b0}/skill/{s0}/common')}")
    print(f"   {b0}/skill/{s0}/level -> {kids(f'{SK}/{b0}/skill/{s0}/level')}")
    print(f"   {b0}/info -> {kids(f'{SK}/{b0}/info')}")

print()
print("=" * 100)
print("15. String.nx MonsterBook.img count (established: 344)")
mb = mc.child_names(f"{ST}/MonsterBook.img")
print(f"   String.nx/MonsterBook.img entries: {len(mb)}")