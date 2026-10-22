import nx, json

print("== /api/children f5 (Mob.nx root) ==")
c = nx.children("f5")
print("count:", len(c))
print("first 3:", json.dumps(c[:3], ensure_ascii=False)[:600])

print("\n== /api/inspect f5/0100100.img/info/level ==")
print(json.dumps(nx.inspect("f5/0100100.img/info/level"), ensure_ascii=False)[:400])

print("\n== /api/inspect f5/0100100.img (no value on children?) ==")
print(json.dumps(nx.inspect("f5/0100100.img"), ensure_ascii=False)[:500])

print("\n== children of f6 (Npc.nx) ==")
c = nx.children("f6")
print("count:", len(c), "names:", [x.get("name") for x in c[:15]])

print("\n== children of f4 (Map.nx) ==")
print([x.get("name") for x in nx.children("f4")])

print("\n== children of f7 (Item.nx) ==")
print([x.get("name") for x in nx.children("f7")])

print("\n== children of f1 (Character.nx) ==")
print([x.get("name") for x in nx.children("f1")])