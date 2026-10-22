import nx, json, os
from collections import Counter

R = {}

def hist(path, filt=None):
    """Width histogram of child NAMES under `path`, full census (no sampling)."""
    names = [c["name"] for c in nx.children(path)]
    if filt: names = [n for n in names if filt(n)]
    w = Counter(len(n) for n in names)
    R[path + ("/" + filt.__name__ if filt else "")] = {
        "path": path, "total_children": len(names), "sample": "100% census of /api/children",
        "widths": dict(sorted(w.items())),
    }
    return names

def stems(path, filt=None):
    n = hist(path, filt)
    return [x[:-4] if x.lower().endswith(".img") else x for x in n]

print("### 1. MOB  -- f5 root image names (ImageNames / MobSpriteService / MobBestiary / Palette D7+plain)")
mob = stems("f5", lambda n: n.lower().endswith(".img"))
print(json.dumps(R["f5/" + "<lambda>"], indent=1))

print("\n### 2. NPC  -- f6 root image names (NpcChatService:184/201 D7+plain, Palette:471 D7 only)")
npc = stems("f6", lambda n: n.lower().endswith(".img"))
print(json.dumps(R["f6/<lambda>"], indent=1))

print("\n### 3. MAP  -- f4/Map and Map001/MapLatest image names (D9 / PadLeft(9))")
for sub in ("Map", "Map001", "MapLatest"):
    names = []
    for d in [c for c in nx.children("f4/" + sub) if c["name"].startswith("Map") and c["name"][3:4].isdigit()]:
        names += stems("f4/" + sub + "/" + d["name"])
    w = Counter(len(n) for n in names)
    R["f4/" + sub] = {"total": len(names), "sample": "100% of every Map<d> subfolder of f4/" + sub,
                      "widths": dict(sorted(w.items()))}
    print(sub, "total", len(names), "widths", dict(sorted(w.items())))

print("\n### 4. CHARACTER equip image names per folder (D8)")
tot = []
for folder in [c["name"] for c in nx.children("f1") if c["name"] not in ("00002000.img",)]:
    p = "f1/" + folder
    try:
        kids = nx.children(p)
    except Exception as e:
        print("skip", p, e); continue
    if not kids or not kids[0].get("name", "").endswith(".img"):
        continue
    names = [c["name"][:-4] for c in kids if c["name"].endswith(".img")]
    tot += names
    w = Counter(len(n) for n in names)
    if names:
        print(" f1/%-12s n=%-5d widths=%s" % (folder, len(names), dict(sorted(w.items()))))
w = Counter(len(n) for n in tot)
R["f1 root+folders equip"] = {"total": len(tot), "sample": "100% census, every .img under f1 root and each folder",
                               "widths": dict(sorted(w.items()))}
print(" ALL equip total", len(tot), "widths", dict(sorted(w.items())))

print("\n### 5. ITEM groups + entry keys (D4 group / D8 entry / D7 pet)")
for g in [c["name"] for c in nx.children("f7")]:
    kids = nx.children("f7/" + g)
    gnames = [c["name"] for c in kids if c["name"].endswith(".img")]
    gw = Counter(len(x) for x in [n[:-4] for n in gnames])
    ek = []
    for gn in gnames:
        ek += [c["name"] for c in nx.children("f7/" + g + "/" + gn)]
    ek = [e for e in ek if e.isdigit()]
    ew = Counter(len(e) for e in ek)
    print(" f7/%-9s groupimgs=%-4d widths=%-30s entrykeys=%-6d widths=%s"
          % (g, len(gnames), dict(sorted(gw.items())), len(ek), dict(sorted(ew.items()))))
    R["f7/" + g] = {"group_imgs": len(gnames), "group_widths": dict(sorted(gw.items())),
                    "entry_keys": len(ek), "entry_widths": dict(sorted(ew.items()))}

nx.save(R, "m01_image_widths.json")