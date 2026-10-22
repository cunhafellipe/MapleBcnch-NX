#!/usr/bin/env python3
"""Final targeted verification round. Writes every result to a file too."""
import json, os, sys, collections
from concurrent.futures import ThreadPoolExecutor
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import mc

HERE = os.path.dirname(os.path.abspath(__file__))
F = mc.files()
CH, MP, NP, EF, MO, SK, BS = (F["Character.nx"], F["Map.nx"], F["Npc.nx"], F["Effect.nx"],
                             F["Mob.nx"], F["Skill.nx"], F["Base.nx"])
OUT = open(os.path.join(HERE, "targeted2.txt"), "w", encoding="utf-8")


say = lambda s="": (print(s, flush=True),
                     OUT.write(str(s).encode("ascii", "backslashreplace").decode("ascii") + "\n"),
                     OUT.flush())


def kids(p):
    try:
        return sorted({c["name"] for c in (mc.inspect(p, use_cache=False).get("children") or [])})
    except Exception:
        return None


def val(p):
    try:
        return (mc.inspect(p, use_cache=False).get("node") or {}).get("value")
    except Exception:
        return None


say("=" * 100)
say("A. Skill.nx -- where do masterLevel / weapon / elemAttr / common actually live?")
say(f"   Skill.nx/<book>.img/common          -> {kids(f'{SK}/000.img/common')}   (book level)")
s0 = (kids(f"{SK}/000.img/skill") or ["0"])[0]
say(f"   sample skill: 000.img/skill/{s0}")
for sub in ("", "/info", "/common", "/level", "/level/1"):
    say(f"      000.img/skill/{s0}{sub} -> {kids(f'{SK}/000.img/skill/{s0}{sub}')}")

say("\n   sweeping EVERY skill's own children + info + common (skill level):")
books = kids(SK) or []
sk_common = collections.Counter()
sk_top = collections.Counter()
sk_info = collections.Counter()
skill_ids = []
for b in books:
    for sid in (kids(f"{SK}/{b}/skill") or []):
        skill_ids.append(f"{b}/skill/{sid}")
for cat, store in (("", sk_top), ("/info", sk_info), ("/common", sk_common)):
    def probe(p, c=cat):
        return p, kids(f"{SK}/{p}{c}")
    with ThreadPoolExecutor(max_workers=6) as ex:
        for p, n in ex.map(probe, skill_ids):
            if n:
                for x in n:
                    store[x] += 1
N = len(skill_ids)
say(f"   skills measured: {N}")
say(f"   skill-level child names ({len(sk_top)}): {sorted(sk_top)}")
say(f"   skill info/* names  ({len(sk_info)}): "
    f"{sorted(sk_info.items(), key=lambda kv: -kv[1])[:40]}")
say(f"   skill common/* names({len(sk_common)}): {sorted(sk_common)}")
for want in ("masterLevel", "weapon", "elemAttr"):
    say(f"      {want!r} at skill level: {sk_top.get(want,0)}/{N};  in info: {sk_info.get(want,0)}/{N};"
        f"  in common: {sk_common.get(want,0)}/{N}")
for want in ("lt", "rb", "damage", "action", "key", "maxLevel", "summonType"):
    say(f"      common/{want!r}: {sk_common.get(want,0)}/{N}")

say("\n" + "=" * 100)
say("B. Effect.nx/SetEff.img -- the setItemID claim (PortSatellites.cs:144)")
se = kids(f"{EF}/SetEff.img") or []
say(f"   SetEff.img ids: {len(se)} (comment claims 260 rows in 1..999)")
sample = sorted(se, key=lambda x: (len(x), x))[:6]
for sid in sample:
    n = kids(f"{EF}/SetEff.img/{sid}")
    say(f"      SetEff.img/{sid} -> {n}")
    if n:
        d = mc.inspect(f"{EF}/SetEff.img/{sid}", use_cache=False)
        say(f"         values: {[(c['name'], c.get('value')) for c in (d.get('children') or [])][:8]}")

say("\n   every setItemID-shaped node anywhere in Effect.nx and Character.nx:")
c = collections.Counter()
for im in (kids(EF) or []):
    for a in (kids(f"{EF}/{im}") or [])[:40]:
        for b in (kids(f"{EF}/{im}/{a}") or []):
            n = kids(f"{EF}/{im}/{a}/{b}") or []
            for x in n:
                if "setItem" in x or "setitem" in x.lower():
                    c[f"{im}/{a}/{b}/{x}"] += 1
say(f"      matches: {dict(c) if c else 'NONE'}")

say("\n" + "=" * 100)
say("C. Character.nx incRMA* -- which images, and is Weapon/01372038.img one of them?")
rma = []
for cat in [c for c in (kids(CH) or []) if not c.endswith(".img")]:
    for im in [i for i in (kids(f"{CH}/{cat}") or []) if i.endswith(".img")]:
        n = kids(f"{CH}/{cat}/{im}/info")
        if n and any(x.startswith("incRMA") for x in n):
            rma.append((f"{cat}/{im}", sorted(x for x in n if x.startswith("incRMA"))))
say(f"   equips carrying incRMA*: {len(rma)}")
for p, v in rma:
    say(f"      {p}: {v}")
say(f"   Weapon/01372038.img/info exists: {kids(f'{CH}/Weapon/01372038.img/info') is not None}")
say(f"   Weapon/01372038.img children   : {kids(f'{CH}/Weapon/01372038.img')}")

say("\n" + "=" * 100)
say("D. Glove/01082143.img -- incLUk (comment: EquipStatsService.cs:40)")
say(f"   info children: {kids(f'{CH}/Glove/01082143.img/info')}")
for c_ in ("incLUk", "incLUK", "incLuk", "incluk"):
    say(f"      info/{c_} = {val(f'{CH}/Glove/01082143.img/info/{c_}')!r}")

say("\n" + "=" * 100)
say("E. Weapon/01302000.img -- afterImage / attackSpeed / attack (CombatService.cs:92,530)")
say(f"   info/afterImage  = {val(f'{CH}/Weapon/01302000.img/info/afterImage')!r}")
say(f"   info/attackSpeed = {val(f'{CH}/Weapon/01302000.img/info/attackSpeed')!r}")
say(f"   info/attack      = {val(f'{CH}/Weapon/01302000.img/info/attack')!r}")
say(f"   Weapon/01402000.img/info/attack = {val(f'{CH}/Weapon/01402000.img/info/attack')!r}")
say(f"   Weapon/01432000.img/info/attack = {val(f'{CH}/Weapon/01432000.img/info/attack')!r}")

say("\n" + "=" * 100)
say("F. Character.nx/Afterimage -- barehands.img/0/stabO1 (DamageRoll.cs:18)")
ah = kids(f"{CH}/Afterimage") or []
say(f"   Afterimage classes: {ah}")
if "barehands.img" in ah:
    say(f"   barehands.img -> {kids(f'{CH}/Afterimage/barehands.img')}")
    say(f"   barehands.img/0 -> {kids(f'{CH}/Afterimage/barehands.img/0')}")
    say(f"   barehands.img/0/stabO1 -> {kids(f'{CH}/Afterimage/barehands.img/0/stabO1')}")
    for k in ("lt", "rb", "origin", "delay", "z", "head", "format"):
        say(f"      stabO1/{k} = {val(f'{CH}/Afterimage/barehands.img/0/stabO1/{k}')!r}")

say("\n" + "=" * 100)
say("G. Mob 'never seen' names -- WHICH mobs carry them")
inv = json.load(open(os.path.join(HERE, "mob_info_inventory.json"), encoding="utf-8"))["ok"]
for name in ("charismaEXP", "wp", "noDebuff", "ignoreMovable", "notAttack", "icon", "wdef",
             "charmEXP", "senseEXP", "insightEXP", "willEXP", "craftEXP",
             "fixedBodyAttackDamageR", "onlyHittedByCommonAttack", "allyMob", "ignoreDamage",
             "bodyattack", "firstattack", "hidename", "Speed", "flyspeed", "FlySpeed",
             "thumbNail", "default", "revive", "category", "elemAttr"):
    who = [(k, (v or {})[name]) for k, v in inv.items() if name in (v or {})]
    if who:
        say(f"   info/{name:26s} {len(who):5d}/2053   e.g. {who[:4]}")
    else:
        say(f"   info/{name:26s} {0:5d}/2053   ABSENT")

say("\n" + "=" * 100)
say("H. Npc.nx -- union of script|speak|illustration2|default (NpcService.cs:686-688)")
u = t = 0
for i in (kids(NP) or []):
    n = kids(f"{NP}/{i}/info")
    if n is None:
        continue
    t += 1
    if any(x in n for x in ("script", "speak", "illustration2", "default")):
        u += 1
say(f"   Npc.nx images with info: {t}; carrying >=1 of the four: {u}  (comment: '7,000-odd of the 10,742')")

say("\n" + "=" * 100)
say("I. Map.nx life -- isCategory (MapEditorService.cs:1952 '25 maps') and rx0/rx1")
regs = kids(f"{MP}/Map") or []
maps = []
for r in regs:
    for m in (kids(f"{MP}/Map/{r}") or []):
        maps.append(f"Map/{r}/{m}")
c = collections.Counter()
withexp = 0
def plife(mp):
    return mp, kids(f"{MP}/{mp}/life")
with ThreadPoolExecutor(max_workers=4) as ex:
    for mp, n in ex.map(plife, maps):
        if n is None:
            continue
        withexp += 1
        for x in n:
            if not x.isdigit():
                c[x] += 1
say(f"   maps with a life node: {withexp} / {len(maps)}")
say(f"   NON-NUMERIC life child names ({len(c)}): {sorted(c.items(), key=lambda kv:-kv[1])[:40]}")
say(f"   isCategory on {c.get('isCategory',0)} maps; rx0 on {c.get('rx0',0)}; rx1 on {c.get('rx1',0)}")

say("\n" + "=" * 100)
say("J. Map.nx -- how many map images does THIS archive hold vs the 17,442 cited?")
tot = 0
for r in regs:
    tot += len(kids(f"{MP}/Map/{r}") or [])
say(f"   under Map/: {tot}")
m1 = kids(f"{MP}/Map001") or []
say(f"   Map001 sub-folders: {len(m1)} {m1[:8]}")
t1 = sum(len(kids(f"{MP}/Map001/{x}") or []) for x in m1)
say(f"   under Map001/: {t1}")
say(f"   TOTAL: {tot + t1}   (comments cite 17,442 maps of Map002.wz)")
say(f"   regions under Map/: {regs}")

say("\nDONE")
OUT.close()