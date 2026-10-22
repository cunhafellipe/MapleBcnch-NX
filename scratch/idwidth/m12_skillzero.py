import nx
from collections import Counter
lead = []
for c in nx.children("f8"):
    if not c["name"].endswith(".img"):
        continue
    p = "f8/" + c["name"] + "/skill"
    if not nx.exists(p):
        continue
    for k in nx.child_names(p):
        if k.isdigit() and str(int(k)) != k:
            lead.append((c["name"], k, len(k)))
print("zero-padded skill/<id> keys:", len(lead))
for b, k, w in lead:
    print("   f8/%-9s skill/%s  width=%d  unpadded=%s" % (b, k, w, str(int(k))))
print("\nwidths of these:", dict(sorted(Counter(w for _, _, w in lead).items())))
print("books holding them:", dict(sorted(Counter(b for b, _, _ in lead).items())))