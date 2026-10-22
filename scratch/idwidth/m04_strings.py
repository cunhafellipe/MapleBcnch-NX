import nx, json
from collections import Counter

R = {}

def pool(path, label):
    keys = nx.child_names(path)
    dig = [k for k in keys if str(k).isdigit()]
    w = Counter(len(k) for k in dig)
    R[label] = {"path": path, "children_total": len(keys), "numeric_keys": len(dig),
                "non_numeric_keys": len(keys) - len(dig),
                "widths_numeric": dict(sorted(w.items())),
                "min": min(dig) if dig else None, "max": max(dig) if dig else None,
                "sample": "100% census of /api/children"}
    print("%-42s total=%-6d numeric=%-6d widths=%-34s min=%s max=%s"
          % (label, len(keys), len(dig), dict(sorted(w.items())),
             min(dig) if dig else "-", max(dig) if dig else "-"))
    return dig

print("=== STRING POOLS (f3 = String.nx) ===")
for img in ["Mob.img", "Npc.img", "Skill.img", "MonsterBook.img", "MobEff.img", "Quest.img"]:
    pool("f3/" + img, "f3/" + img)

print("\n  String.nx root children:", [c["name"] for c in nx.children("f3")])

print("\n=== String.nx/Map.img : regioned layout ===")
mroot = nx.children("f3/Map.img")
print("  children:", [(c["name"], c.get("childCount")) for c in mroot][:12])
tot = []
for c in mroot:
    tot += [k for k in nx.child_names("f3/Map.img/" + c["name"]) if str(k).isdigit()]
w = Counter(len(k) for k in tot)
R["f3/Map.img (regioned)"] = {"numeric_keys": len(tot), "widths_numeric": dict(sorted(w.items())),
                              "regions": [c["name"] for c in mroot],
                              "sample": "100% census of every region's children"}
print("  regioned numeric keys:", len(tot), "widths", dict(sorted(w.items())))

print("\n=== String.nx item pools (Flat = ids direct, Nested = wrapper/cat/id) ===")
for img, nested in [("Eqp.img", True), ("Consume.img", False), ("Etc.img", True),
                    ("Ins.img", False), ("Cash.img", False), ("Pet.img", False)]:
    kids = nx.children("f3/" + img)
    lv = [k for k in nx.child_names("f3/" + img) if str(k).isdigit()]
    w = Counter(len(k) for k in lv)
    print("  f3/%-12s children=%-6d direct-numeric=%-6d widths=%s" % (img, len(kids), len(lv), dict(sorted(w.items()))))
    if nested:
        # descend one level for wrapper -> category -> id
        d2 = []
        for c in kids:
            for c2 in nx.child_names("f3/%s/%s" % (img, c["name"])):
                if str(c2).isdigit():
                    d2.append(c2)
        w2 = Counter(len(k) for k in d2)
        print("        nested ids (2 levels down): %-6d widths=%s" % (len(d2), dict(sorted(w2.items()))))
        R["f3/" + img + " nested"] = {"ids": len(d2), "widths": dict(sorted(w2.items())),
                                     "sample": "100% census 2 levels down"}
    else:
        R["f3/" + img] = {"ids": len(lv), "widths": dict(sorted(w.items())), "sample": "100% census direct"}

nx.save(R, "m04_string_pools.json")