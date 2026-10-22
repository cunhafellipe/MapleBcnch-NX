import nx, json
from collections import Counter

print("=== f7/Special group images ===")
for c in nx.children("f7/Special"):
    n = c["name"]
    kids = nx.children("f7/Special/" + n) if c.get("hasChildren") else []
    keys = [k["name"] for k in kids]
    w = Counter(len(k) for k in keys)
    print("  %-16s kind=%-10s children=%-5d widths=%s" % (n, c["kind"], len(keys), dict(sorted(w.items()))))
    ex = {}
    for k in keys:
        ex.setdefault(len(k), k)
    print("        one example per width:", {L: ex[L] for L in sorted(ex)})

print("\n=== f7/Pet group images (IconService:119 D7) ===")
pets = [c["name"] for c in nx.children("f7/Pet")]
print("  count", len(pets), "first", pets[:6], "last", pets[-4:])
print("  widths(stem)", dict(sorted(Counter(len(p[:-4]) for p in pets).items())))

print("\n=== f1/Afterimage image names (CombatService:125 DefaultWeapon, AfterimageName) ===")
aft = [c["name"] for c in nx.children("f1/Afterimage")]
print("  count", len(aft))
print("  names:", aft)
print("  widths(stem)", dict(sorted(Counter(len(a[:-4]) for a in aft).items())))

print("\n=== f1 root images (Body/Head sheets) ===")
root = [c["name"] for c in nx.children("f1") if c["name"].endswith(".img")]
print("  count", len(root), root)
print("  widths(stem)", dict(sorted(Counter(len(r[:-4]) for r in root).items())))