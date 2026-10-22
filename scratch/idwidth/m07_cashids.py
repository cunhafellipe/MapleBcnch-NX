import nx, json, random
from collections import Counter

names = nx.child_names("f11/Commodity.img")
print("commodity rows:", len(names))
k0 = names[0]
print("fields of", k0, ":", [c["name"] for c in nx.children("f11/Commodity.img/" + k0)])

random.seed(7)
sample = random.sample(names, 60)
ids = []
for k in sample:
    kids = {c["name"]: c for c in nx.children("f11/Commodity.img/" + k)}
    for f in ("item", "Item", "itemid", "id", "ItemId"):
        if f in kids:
            v = nx.value("f11/Commodity.img/%s/%s" % (k, f))
            try: ids.append(int(str(v)))
            except Exception: pass
            break
print("\nsampled %d rows, got %d item ids" % (len(sample), len(ids)))
if ids:
    print(" min", min(ids), "max", max(ids))
    grp = Counter(i // 10000 for i in ids)
    print(" itemId/10000 groups:", dict(sorted(grp.items())))
    print(" id widths:", dict(sorted(Counter(len(str(i)) for i in ids).items())))
    in910 = [i for i in ids if 9100000 <= i < 9120000]
    print(" ids in the 910xxxx/911xxxx range:", len(in910), sorted(set(in910))[:10])

print("\n=== do those 0910-range keys carry an icon? (IconService path) ===")
for i in sorted(set(in910))[:5]:
    g = (i // 10000)
    print("  id", i, "groupimg 'f7/Special/%04d.img'" % g,
          "exists:", nx.exists("f7/Special/%04d.img" % g),
          "| D8 entry:", nx.exists("f7/Special/%04d.img/%08d" % (g, i)),
          "| D7 entry:", nx.exists("f7/Special/%04d.img/%07d" % (g, i)))