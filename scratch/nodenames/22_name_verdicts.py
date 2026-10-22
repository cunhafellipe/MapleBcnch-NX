#!/usr/bin/env python3
"""Query the saved sweeps for the specific names this audit needs, with
neighbour evidence: case-insensitive matches under the same parent."""
import json, os, sys, collections

HERE = os.path.dirname(os.path.abspath(__file__))
S = json.load(open(os.path.join(HERE, "sweep_results.json"), encoding="utf-8"))
MOB = json.load(open(os.path.join(HERE, "mob_info_inventory.json"), encoding="utf-8"))

# add the mob <id>.img/info sweep under a synthetic key
counts = collections.Counter()
for names in MOB["ok"].values():
    for n in names:
        counts[n] += 1
S["Mob.nx/<id>.img/info"] = {"sample": len(MOB["ok"]), "of": 2053, "failed": 0,
                             "counts": dict(counts), "details": {}}

QUERIES = [
    ("Mob.nx/<id>.img/info", ["wdef", "mdef", "defence", "icon", "charismaEXP", "charmEXP",
                              "senseEXP", "insightEXP", "willEXP", "craftEXP", "wp",
                              "noDebuff", "notAttack", "ignoreMovable", "ignoreDamage",
                              "allyMob", "onlyHittedByCommonAttack", "fixedBodyAttackDamageR",
                              "maxHP", "MaxHP", "level", "bodyattack", "bodyAttack",
                              "firstattack", "firstAttack", "hideName", "hidename",
                              "afterImage", "afterimage", "attack1", "speedMaxOver"]),
    ("Mob.nx/<id>.img/attackN/info", ["ball", "range", "hit", "type", "attackAfter",
                                      "bulletSpeed", "PADamage", "MADamage", "MADamge",
                                      "PADamge", "elemAttr", "damageElemAttr", "knockback",
                                      "conMP", "comMP", "mpBurn", "mpburn", "disease", "diseae",
                                      "desease", "delay", "level"]),
    ("Character.nx/<cat>/<id>.img/info", ["setItemID", "specialID", "incLUk", "incLUK", "incHANDS",
                                          "incSPEED", "incJUMP", "incSpeed", "incJump",
                                          "incRMAF", "incRMAS", "incRMAI", "incRMAL",
                                          "incCraft", "incHP", "incFatigue", "incSwim",
                                          "afterImage", "attackSpeed", "attack",
                                          "islot", "vslot", "cash", "icon", "iconRaw",
                                          "maxHP", "MaxHP", "masterLevel", "incPAD",
                                          "incMHP", "incMAD", "level", "reqLevel"]),
    ("Npc.nx/<id>.img/info", ["script", "speak", "illustration", "illustration2", "default",
                              "name", "hideName", "dcMark", "dcmark", "dcBottom", "dcBattom"]),
    ("Npc.nx/<id>.img (top level)", ["stand", "stand1", "info", "say", "move", "sit", "jump",
                                     "die1", "hit1", "swingT1", "swingO1", "attack1"]),
    ("Map.nx/Map/<region>/<id>.img/info", ["tS", "tSMag", "mapMark", "mapName", "returnMap",
                                           "fieldType", "mapDesc", "limitUI", "AmbientBGM",
                                           "bgmSub", "speedMaxOver", "speedMaxOver ",
                                           "canPartyStatChangeIgnoreParty",
                                           "canPartyStatChangeIgnoreParty ",
                                           "bgm", "forcedReturn", "everlast", "cloud"]),
    ("Map.nx/Map/<region>/<id>.img (top level)", ["info", "foothold", "life", "portal", "reactor",
                                                  "miniMap", "back", "ladderRope", "0", "1"]),
    ("Map.nx/Map/<region>/<id>.img/life", ["isCategory", "rx0", "rx1", "id", "fh", "cy", "x", "y",
                                           "mobTime", "hide", "f", "mob", "0"]),
    ("Map.nx/Map/<region>/<id>.img/foothold", ["oS", "oL", "oR", "oU", "oD", "x1", "x2", "y1",
                                                "y2", "fh", "cy", "id", "0"]),
    ("Map.nx/Map/<region>/<id>.img/portal", ["name", "pn", "tn", "cn", "pt", "x", "y", "0", "1"]),
    ("Map.nx/Map/<region>/<id>.img/reactor", ["name", "rn", "x", "y", "0"]),
    ("Item.nx/<cat>/<book>.img/<id>/info", ["setItemID", "masterLevel", "lv", "maxLevel", "lvMin",
                                            "lvMax", "incPAD", "incPDD", "incSTR", "incDEX",
                                            "incINT", "incLUK", "incMHP", "incMMP", "incACC",
                                            "incEVA", "incMAD", "incMDD", "incSpeed", "incJump",
                                            "incHANDS", "cash", "icon", "name", "mobHP",
                                            "incMaxHP", "incMaxMP", "tragetBlock", "randstat",
                                            "randStat", "skill", "exp", "monsterBook"]),
]

out = open(os.path.join(HERE, "name_verdicts.txt"), "w", encoding="utf-8")
for label, names in QUERIES:
    if label not in S:
        out.write(f"\n### {label}: NOT SWEPT YET\n")
        continue
    d = S[label]
    n = d["sample"]
    present = d["counts"]
    ci = collections.defaultdict(set)
    for k in present:
        ci[k.lower()].add(k)
    out.write(f"\n{'='*95}\n### {label}   sample={n} of {d['of']}  failed={d.get('failed')}\n{'='*95}\n")
    for name in names:
        exact = present.get(name, 0)
        sib = sorted(ci.get(name.lower(), set()))
        if exact:
            out.write(f"   EXISTS  {exact:6d}/{n}   {name}\n")
        else:
            out.write(f"   ABSENT  {0:6d}/{n}   {name}\n")
            out.write(f"           case-insensitive siblings under the same parent: "
                      f"{sib if sib else 'NONE'}\n")
out.close()

# also print to console
for label, names in QUERIES:
    if label not in S:
        continue
    d = S[label]
    n, present = d["sample"], d["counts"]
    ci = collections.defaultdict(set)
    for k in present:
        ci[k.lower()].add(k)
    print(f"\n### {label}  (sample {n}/{d['of']})")
    for name in names:
        e = present.get(name, 0)
        if e:
            print(f"   EXISTS {e:6d}/{n}  {name}")
        else:
            sib = sorted(ci.get(name.lower(), set()))
            print(f"   ABSENT {0:6d}/{n}  {name:34s} ci-siblings={sib if sib else 'NONE'}")
print("\nwrote name_verdicts.txt")