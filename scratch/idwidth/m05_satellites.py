import nx, json
from collections import Counter

R = {}
def widths_of(p):
    ks = [k for k in nx.child_names(p) if str(k).isdigit()]
    return len(nx.child_names(p)), len(ks), dict(sorted(Counter(len(k) for k in ks).items()))

print("=== f8 (Skill.nx) root ===")
sk = [c["name"] for c in nx.children("f8")]
print(" ", sk)

print("\n=== PortSatellites.Locate target: <role>/MobSkill/<id>.img ===")
for f in ("f8",):
    for c in nx.children(f):
        if "MobSkill" in c["name"]:
            print("  FOUND dir", f + "/" + c["name"])
            names = [x["name"][:-4] for x in nx.children(f + "/" + c["name"]) if x["name"].endswith(".img")]
            print("   images:", len(names), "widths", dict(sorted(Counter(len(n) for n in names).items())))
            print("   sample:", names[:6], "...", names[-3:])
            R["Skill/MobSkill images"] = {"n": len(names), "widths": dict(sorted(Counter(len(n) for n in names).items()))}

print("\n=== StringEditService.FindById target pools (plain + all widths <=10) ===")
targets = {
 "f10/ChaseEffect.img": "f10", "f10/MobEff.img": "f10",
 "f11/MobLocation.img": "f11", "f11/NpcLocation.img": "f11", "f11/MapObjectInfo.img": "f11",
 "f14/Mob.img": "f14", "f14/MobVoice.img": "f14", "f14/Voice.img": "f14",
 "f14/Skill.img": "f14", "f14/SkillVoice.img": "f14", "f14/Item.img": "f14",
 "f14/Pet.img": "f14", "f14/CashEffect.img": "f14", "f14/ConsumeEffect.img": "f14",
 "f14/Reactor.img": "f14", "f14/Npc.img": "f14", "f14/Etc.img": "f14", "f14/Cash.img": "f14",
 "f3/Mob.img": "f3", "f3/Npc.img": "f3", "f3/MonsterBook.img": "f3",
 "f3/Skill.img": "f3", "f3/Eqp.img": "f3", "f3/PetDialog.img": "f3",
 "f3/CashItemSearch.img": "f3", "f3/NameChange.img": "f3", "f3/TransferWorld.img": "f3",
}
for p, f in sorted(targets.items()):
    if not nx.exists(p):
        print("  %-28s ABSENT" % p); R[p] = {"present": False}; continue
    tot, dig, w = widths_of(p)
    print("  %-28s children=%-6d numeric=%-6d widths=%s" % (p, tot, dig, w))
    R[p] = {"children": tot, "numeric": dig, "widths": w}

print("\n=== MAX key width over every measured pool ===")
mx = 0
for k, v in R.items():
    if isinstance(v, dict) and v.get("widths"):
        for wd in v["widths"]:
            mx = max(mx, wd)
print("  max numeric key width observed:", mx)
R["_max_width_observed"] = mx
nx.save(R, "m05_satellites.json")