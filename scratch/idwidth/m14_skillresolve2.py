import nx, json
from collections import Counter

books = {c["name"][:-4] for c in nx.children("f8") if c["name"].endswith(".img")}

def pad(v, w):
    return str(v).rjust(w, "0")

def js_candidates(skill_id):
    book = skill_id // 10000
    return [("%d.img" % book, "skill/%d" % skill_id), ("%s.img" % pad(book, 4), "skill/%d" % skill_id)]

def cpp_spellings(skill_id):
    strid = pad(skill_id, 7) if skill_id < 10000000 else str(skill_id)
    return strid, pad(skill_id // 10000, 3)

entries = []
for c in nx.children("f8"):
    if not c["name"].endswith(".img"):
        continue
    p = "f8/" + c["name"] + "/skill"
    if not nx.exists(p):
        continue
    for k in nx.child_names(p):
        if k.isdigit():
            entries.append((c["name"][:-4], k, int(k)))
print("skill entries in f8 (100%% census of f8/<book>.img/skill):", len(entries))

js_full = js_imgonly = js_none = 0
why_book = Counter()
unresolved = []
for book, key, sid in entries:
    full = img_only = False
    tried_img = []
    for img, child in js_candidates(sid):
        stem = img[:-4]
        tried_img.append(img)
        if stem not in books:
            why_book["img-absent:" + img] += 0
            continue
        if nx.exists("f8/%s/skill/%s" % (img, child.split("/", 1)[1])):
            full = True
            break
        img_only = True
    if full:
        js_full += 1
    elif img_only:
        js_imgonly += 1; unresolved.append(("img-ok-entry-miss", book, key, tried_img))
    else:
        js_none += 1;   unresolved.append(("img-absent", book, key, tried_img))

print("[database.js] full path resolves : %d / %d" % (js_full, len(entries)))
print("[database.js] .img found, entry missed: %d / %d" % (js_imgonly, len(entries)))
print("[database.js] .img itself not found   : %d / %d" % (js_none, len(entries)))
print("  TOTAL unresolved:", len(unresolved))

cpp_ok = sum(1 for b, k, s in entries if nx.exists("f8/%s.img/skill/%s" % (cpp_spellings(s)[1], cpp_spellings(s)[0])))
print("[C++ PassiveBuffs.cpp:229-238] full path resolves : %d / %d" % (cpp_ok, len(entries)))

print("\n--- unresolved breakdown, first 12 of each kind ---")
for kind in ("img-absent", "img-ok-entry-miss"):
    sub = [u for u in unresolved if u[0] == kind]
    print(" %s (%d):" % (kind, len(sub)))
    for _, b, k, tried in sub[:12]:
        print("    f8/%-5s/skill/%-9s  js tried %s" % (b, k, tried))
nx.save({"entries": len(entries), "js_full": js_full, "js_img_only": js_imgonly,
         "js_none": js_none, "cpp_ok": cpp_ok, "unresolved": unresolved},
        "m14_skillresolve2.json")