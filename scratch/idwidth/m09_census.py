import nx, json
from collections import Counter

def stems(p):
    return {c["name"][:-4] for c in nx.children(p) if c["name"].endswith(".img")}

R = {}
mob = stems("f5"); npc = stems("f6")
print("Mob.nx images:", len(mob), " Npc.nx images:", len(npc))

# --- Mob string pool -> Palette:471 (D7 only) and ImageNames (D7 + plain)
mobkeys = [k for k in nx.child_names("f3/Mob.img") if str(k).isdigit()]
hit7 = sum(1 for k in mobkeys if format(int(k), "07d") in mob)
hitany = sum(1 for k in mobkeys if format(int(k), "07d") in mob or str(int(k)) in mob)
print("\n[Mob] String Mob.img numeric keys:", len(mobkeys),
      " resolved by D7 only:", hit7, " by D7+plain:", hitany)
print("  key widths:", dict(sorted(Counter(len(k) for k in mobkeys).items())))
R["mob"] = {"pool": "f3/Mob.img", "n_keys": len(mobkeys),
            "d7_only_hits": hit7, "d7_plus_plain_hits": hitany,
            "key_widths": dict(sorted(Counter(len(k) for k in mobkeys).items())),
            "archive_image_widths": dict(sorted(Counter(len(n) for n in mob).items())),
            "sample": "100% census both sides"}

# --- Npc string pool -> Palette:471 (D7 only), NpcChatService:184 (D7+plain)
npckeys = [k for k in nx.child_names("f3/Npc.img") if str(k).isdigit()]
hit7 = sum(1 for k in npckeys if format(int(k), "07d") in npc)
hitany = sum(1 for k in npckeys if format(int(k), "07d") in npc or str(int(k)) in npc)
print("\n[Npc] String Npc.img numeric keys:", len(npckeys),
      " resolved by D7 only:", hit7, " by D7+plain:", hitany)
print("  key widths:", dict(sorted(Counter(len(k) for k in npckeys).items())))
R["npc"] = {"pool": "f3/Npc.img", "n_keys": len(npckeys),
            "d7_only_hits": hit7, "d7_plus_plain_hits": hitany,
            "key_widths": dict(sorted(Counter(len(k) for k in npckeys).items())),
            "archive_image_widths": dict(sorted(Counter(len(n) for n in npc).items())),
            "sample": "100% census both sides"}

# --- MonsterBook: MobBestiaryService:281 (D7 + plain)
mbkeys = [k for k in nx.child_names("f3/MonsterBook.img") if str(k).isdigit()]
hit = sum(1 for k in mbkeys
          if nx.exists("f3/MonsterBook.img/" + format(int(k), "07d"))
          or nx.exists("f3/MonsterBook.img/" + str(int(k))))
print("\n[Book] MonsterBook.img keys:", len(mbkeys), "resolved by D7+plain:", hit,
      "widths", dict(sorted(Counter(len(k) for k in mbkeys).items())))
R["book"] = {"n": len(mbkeys), "d7_plus_plain_hits": hit,
             "widths": dict(sorted(Counter(len(k) for k in mbkeys).items())),
             "sample": "100% census, one /api/inspect per spelling"}

# --- Item group entry keys: IconService:127 (D8 only) vs D8+D7
for g, imgs in (("f7/Consume", None), ("f7/Cash", None), ("f7/Etc", None),
                ("f7/Install", None), ("f7/Special", None)):
    tot = d8 = both = 0
    per_img = {}
    for c in nx.children(g):
        if not c["name"].endswith(".img"):
            continue
        ks = [k["name"] for k in nx.children(g + "/" + c["name"]) if k["name"].isdigit()]
        if not ks:
            continue
        a = sum(1 for k in ks if len(k) == 8)
        tot += len(ks); d8 += a
        per_img[c["name"]] = {"n": len(ks), "widths": dict(sorted(Counter(len(k) for k in ks).items()))}
    print("\n[Item] %-12s entry keys=%-5d  8-digit=%-5d  D8 finds %d" % (g, tot, d8, d8))
    for k, v in per_img.items():
        print("     ", k, v)
    R[g] = {"entry_keys": tot, "eight_digit": d8, "per_image": per_img, "sample": "100% census"}

nx.save(R, "m09_census.json")