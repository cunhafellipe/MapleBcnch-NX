import nx, json
from collections import Counter

print("=== f11 (Etc.nx) root ===")
print([c["name"] for c in nx.children("f11")])

p = "f11/Commodity.img"
print("\n=== %s ===" % p)
if nx.exists(p):
    print(" children:", nx.children(p)[:5])
    print(" count:", len(nx.children(p)))
    print(" names sample:", nx.child_names(p)[:20])
else:
    for c in nx.children("f11"):
        if "Commod" in c["name"]: print("  candidate:", c["name"])