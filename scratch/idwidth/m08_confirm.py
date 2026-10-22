import nx, json
from collections import Counter

print("=== A. is there ANY 4-digit '0500.img' anywhere under f7? ===")
hits = []
for g in [c["name"] for c in nx.children("f7")]:
    for c in nx.children("f7/" + g):
        if c["name"] == "0500.img":
            hits.append("f7/" + g + "/0500.img")
print("  hits:", hits or "NONE (0 of the 155 f7 images scanned)")
allimg = []
for g in [c["name"] for c in nx.children("f7")]:
    allimg += [c["name"] for c in nx.children("f7/" + g) if c["name"].endswith(".img")]
print("  total f7 images scanned:", len(allimg),
      "widths(stem):", dict(sorted(Counter(len(n[:-4]) for n in allimg).items())))

print("\n=== B. the icon node the D7 key reaches (C++ UICashShop.cpp:1111 spelling) ===")
for k in ("9101599", "9110000", "9100000"):
    p = "f7/Special/%04d.img/%s" % (int(k) // 10000, k)
    print(" ", p, "->", "children:", [c["name"] for c in nx.children(p)],
          "| icon kind:", (nx.inspect(p + "/icon") or {}).get("kind"),
          "| info/icon:", nx.inspect(p + "/info/icon"))

print("\n=== C. ItemCategories.DataImageName(5000000) ===")
print("  (5000000/10000).ToString('D4') =", format(5000000 // 10000, "04d"), "-> '0500.img'")
print("  f7/Pet/5000000.img exists:", nx.exists("f7/Pet/5000000.img"))
print("  f7/.../0500.img exists anywhere:", bool(hits))

print("\n=== D. NpcChatService:184 (D7 + plain) vs the SHORT keys in String Npc.img ===")
for k in ["10000", "2000", "2001", "9101002"]:
    n = int(k)
    d7 = format(n, "07d")
    plain = str(n)
    print("  key %-9s plain -> %s ; D7 -> %s  (plain resolves: %s)"
          % (k, plain, d7, nx.exists("f3/Npc.img/" + plain)))

print("\n=== E. six-digit keys in String Mob.img (measured 16 of 1735) ===")
six = [k for k in nx.child_names("f3/Mob.img") if len(str(k)) == 6]
print("  six-digit keys:", six)
for k in six[:6]:
    n = int(k)
    print("   ", k, "-> Mob.nx image D7 '%s' exists: %s" % (format(n, "07d"), nx.exists("f5/%07d.img" % n)))