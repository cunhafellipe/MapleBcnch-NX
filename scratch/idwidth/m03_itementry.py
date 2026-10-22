import nx, json
from collections import Counter

print("=== f7/Special/0910.img/9100000 shape (IconService.cs:127 D8 entry key) ===")
n = nx.inspect("f7/Special/0910.img/9100000")
print("  D7 entry node:", json.dumps(n, ensure_ascii=False)[:300])
print("  children:", [c["name"] for c in nx.children("f7/Special/0910.img/9100000")][:20])
print("  D8 spelling f7/Special/0910.img/09100000 resolves? ->",
      nx.inspect("f7/Special/0910.img/09100000") is not None)
print("  value at info/icon:", nx.value("f7/Special/0910.img/9100000/info/icon"))

print("\n=== f7/Special/0910.img : full entry-key census ===")
keys = [c["name"] for c in nx.children("f7/Special/0910.img")]
print("  n=", len(keys), "widths", dict(sorted(Counter(len(k) for k in keys).items())))
print("  min", min(keys), "max", max(keys))
n_icon = sum(1 for k in keys if nx.exists("f7/Special/0910.img/%s/info/icon" % k))
print("  entries with info/icon: %d of %d" % (n_icon, len(keys)))
w_icon = Counter(len(k) for k in keys if nx.exists("f7/Special/0910.img/%s/info/icon" % k))
print("  widths OF THOSE WITH info/icon:", dict(sorted(w_icon.items())))

print("\n=== f7/Special/0911.img ===")
k11 = [c["name"] for c in nx.children("f7/Special/0911.img")]
print("  n=", len(k11), "widths", dict(sorted(Counter(len(k) for k in k11).items())), k11)

print("\n=== f7/Special/0900.img ===")
k00 = [c["name"] for c in nx.children("f7/Special/0900.img")]
print("  n=", len(k00), "widths", dict(sorted(Counter(len(k) for k in k00).items())), k00)

print("\n=== f7/Pet: info/icon coverage (IconService.cs:119-121 pet branch) ===")
pets = [c["name"][:-4] for c in nx.children("f7/Pet")]
cov = sum(1 for p in pets if nx.exists("f7/Pet/%s.img/info/icon" % p))
print("  %d of %d Pet images carry info/icon" % (cov, len(pets)))