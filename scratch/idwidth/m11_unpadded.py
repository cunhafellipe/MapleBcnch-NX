import nx
from collections import Counter

print("=== leading zeros in f8/*/skill/<id> keys? (database.js:86 asks for `skill/${id}` unpadded) ===")
allk = []
for c in nx.children("f8"):
    if not c["name"].endswith(".img"):
        continue
    if nx.exists("f8/" + c["name"] + "/skill"):
        allk += [k["name"] for k in nx.children("f8/" + c["name"] + "/skill") if k["name"].isdigit()]
print("  total", len(allk))
lead = [k for k in allk if k.startswith("0")]
print("  keys with a leading zero:", len(lead), lead[:10])
print("  widths:", dict(sorted(Counter(len(k) for k in allk).items())))
print("  min", min(allk, key=len), "max", max(allk))
# does the unpadded literal reproduce every key exactly?
bad = [k for k in allk if str(int(k)) != k]
print("  keys whose UNPADDED form differs from the stored key:", len(bad), bad[:10])
print("  min/max numerically:", min(int(k) for k in allk), max(int(k) for k in allk))
print("  widths of the numeric ids:", dict(sorted(Counter(len(str(int(k))) for k in allk).items())))

print("\n=== the same test for database.js:96 item entry key, per group ===")
for g in ("f7/Consume", "f7/Cash", "f7/Etc", "f7/Install", "f7/Special"):
    tot = same = 0
    for c in nx.children(g):
        if not c["name"].endswith(".img"):
            continue
        for k in nx.children(g + "/" + c["name"]):
            n = k["name"]
            if not n.isdigit():
                continue
            tot += 1
            if str(int(n)) == n:
                same += 1
    print("  %-12s entry keys=%-5d identical under the UNPADDED form=%-5d" % (g, tot, same))