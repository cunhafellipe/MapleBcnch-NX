import nx, json
from collections import Counter

# database.js:84-88 -> image `${book}.img` and `${pad(book,4)}.img`, child `skill/${id}` (unpadded)
print("=== f8 (Skill.nx) root image stem widths (database.js:84-87 book image) ===")
stems = [c["name"][:-4] for c in nx.children("f8") if c["name"].endswith(".img")]
w = Counter(len(s) for s in stems)
print("  n=", len(stems), "widths", dict(sorted(w.items())), "sample", stems[:12])
# for a skill id, book = floor(id/10000); does `${book}.img` or pad(book,4) exist?
sk = [k for k in nx.child_names("f3/Skill.img") if str(k).isdigit()]
b_plain = sum(1 for k in sk if ("%d.img" % (int(k) // 10000)) in [c["name"] for c in nx.children("f8")])
print("  String Skill.img keys:", len(sk), "widths", dict(sorted(Counter(len(k) for k in sk).items())))
print("  of those, book image `${book}.img` present:", b_plain)

print("\n=== f8/<book>.img/skill/<id> entry key widths (the child database.js builds) ===")
tot = plain_hit = 0
per = {}
for c in nx.children("f8"):
    if not c["name"].endswith(".img"):
        continue
    skids = [k["name"] for k in nx.children("f8/" + c["name"] + "/skill")] if nx.exists("f8/" + c["name"] + "/skill") else []
    skids = [k for k in skids if str(k).isdigit()]
    if not skids:
        continue
    per[c["name"]] = {"n": len(skids), "widths": dict(sorted(Counter(len(k) for k in skids).items()))}
    tot += len(skids)
    plain_hit += sum(1 for k in skids if str(k).lstrip("0") == k or True)  # key IS the literal it asks for
print("  total skill/*/ entries:", tot)
merged = Counter()
for v in per.values():
    for k, n in v["widths"].items():
        merged[k] += n
print("  width histogram over ALL book images:", dict(sorted(merged.items())))
print("  note: database.js asks for `skill/${id}` with id UNPADDED -> "
      "resolves only where the stored key is already unpadded")
unpadded = sum(n for k, n in merged.items() if k < 7)
print("  unpadded (<7) keys:", unpadded, " padded(>=7):", tot - unpadded)

print("\n=== MapleNecrocer reference for skill ids (SkillTooltipRender2.cs:53 uses {0:D7}) ===")
print("  MapleBench database.js uses the raw id with no pad -> both widths are tried? NO: one width only.")
nx.save({"skill_img_stems": len(stems), "skill_img_widths": dict(sorted(w.items())),
         "string_skill_keys": len(sk), "book_image_present": b_plain,
         "skill_entry_total": tot, "skill_entry_widths": dict(sorted(merged.items())),
         "per_book_image": per}, "m10_skill.json")