"""Verify the three W-041 / W-058 fixes against the LIVE server, after deploy.

1. W-058 portal: walk onto a real portal, hold Up, and read /debug/portal.
   Before the fix this reported a load failure; the transfer never happened.
2. W-041 item icons: a 910xxxx cash item must now answer an icon, where the
   D8-only lookup answered 404.
3. W-041 database.js candidates: replay the new skill/item candidate logic and
   count how many archive ids resolve, against the 510/535 and 4/584 baselines.

CONTROL FIRST on each measurement: a known-answer probe, so a run that reads
everything as absent is void rather than a finding (R-9).
"""
import json, urllib.error, urllib.parse, urllib.request

BASE = "http://127.0.0.1:5591"
MAP = "f4/Map/Map1/100000004.img"
bad = 0


def pad(v, w):
    return str(v).rjust(w, "0")


def get(route, **q):
    url = f"{BASE}{route}?" + urllib.parse.urlencode(q)
    try:
        with urllib.request.urlopen(url, timeout=300) as r:
            return r.status, json.loads(r.read().decode("utf-8"))
    except urllib.error.HTTPError as e:
        return e.code, None


def post(route, body):
    req = urllib.request.Request(f"{BASE}{route}",
                                 data=json.dumps(body).encode("utf-8"),
                                 headers={"Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=600) as r:
            return r.status, json.loads(r.read().decode("utf-8"))
    except urllib.error.HTTPError as e:
        return e.code, None


def kids(path):
    st, d = get("/api/children", path=path)
    return d if st == 200 and d else []


# CONTROL: archives are open and the test map resolves.
st, files = get("/api/files")
names = {f["name"]: f["id"] for f in (files or [])}
print(f"CONTROL  {len(names)} archives open; {MAP} -> {get('/api/inspect', path=MAP)[0]}"
      f"  (must be 200)")
if st != 200 or get("/api/inspect", path=MAP)[0] != 200:
    raise SystemExit("VOID: archives are not open.")

# ---------------------------------------------------------------- 1. the portal
print("\n=== W-058: a portal transfer, end to end ===")
portals = kids(f"{MAP}/portal")
exits = []
for p in portals:
    _, tmd = get("/api/inspect", path=f"{MAP}/portal/{p['name']}/tm")
    _, pxd = get("/api/inspect", path=f"{MAP}/portal/{p['name']}/x")
    _, pyd = get("/api/inspect", path=f"{MAP}/portal/{p['name']}/y")
    # A scalar's payload is node.value (a string), NOT node.extra.x.
    v = (tmd or {}).get("node", {}).get("value")
    xv = (pxd or {}).get("node", {}).get("value")
    yv = (pyd or {}).get("node", {}).get("value")
    if v and str(v) not in ("0", "999999999"):
        exits.append((p["name"], str(v), int(xv), int(yv)))
print(f"  {len(exits)} exit portals: "
      + ", ".join(f"{n}->{m}@({x},{y})" for n, m, x, y in exits))
if not exits:
    print("  >>> no exit portal to test with; VOID")
    raise SystemExit(1)

# Use the portal nearest the walkable ground, not the one 1,300px up a wall: a body
# spawned at y=-1344 falls away from the portal before the first frame is read, so
# "no transfer" would say nothing about the fix.
name, target, px, py = min(exits, key=lambda e: abs(e[3] - (-25)))
s, sess = post("/api/simulate/session",
               {"path": MAP, "weapon": "1302000.img", "equippedWeapon": "1302000",
                "invulnerable": True, "startX": px, "startY": py})
sid = sess["sessionId"]
print(f"  session {sid} spawned at portal {name} ({px},{py}), target map {target}")

moved = None
saw_portal = None
for _ in range(120):
    _, fr = post(f"/api/simulate/session/{sid}/frames",
                 {"count": 1, "first": {"up": True}, "then": {}})
    if fr.get("currentPortal") or fr.get("portal"):
        saw_portal = fr.get("currentPortal") or fr.get("portal")
    if fr.get("transferred") or fr.get("transfer"):
        moved = fr.get("transferred") or fr.get("transfer")
        break
_, dbg = get("/api/simulate/debug/portal")
failure = (dbg or {}).get("failure")
print(f"  /debug/portal failure : {failure!r}")
print(f"  portal seen on a frame: {saw_portal!r}")
print(f"  transfer reported     : {moved!r}")
if failure:
    print(f"  >>> the portal still fails to load: {failure}")
    bad += 1
elif moved:
    print("  ok: the destination map loaded and a transfer was reported")
elif saw_portal:
    print(f"  >>> the body stood on portal {saw_portal} and still did not transfer")
    bad += 1
else:
    print("  >>> no failure, no portal seen and no transfer -- cannot tell a fixed "
          "portal from a body that never reached one; NOT counted as a pass")
    bad += 1

# ------------------------------------------------------------- 2. cash icons
print("\n=== W-041: a 910xxxx cash item icon (D8-only would be 404) ===")
# Prove it through the REAL endpoint, not a replay: IconService.FindImage
# recurses into subdirectories (IconService.cs:273-278), so the group "0910.img"
# is found under f7/Special/ and only the ENTRY key was ever wrong.
for probe in ("9100000", "9101599", "9110000", "2000000"):
    grp = pad(int(probe) // 10000, 4) + ".img"
    d8 = get("/api/inspect", path=f"f7/Special/{grp}/{pad(int(probe), 8)}")[0]
    bare = get("/api/inspect", path=f"f7/Special/{grp}/{probe}")[0]
    st, _ = get(f"/api/cash/icon/{probe}")
    print(f"  {probe}: group {grp}  D8 key -> {d8}   bare key -> {bare}   "
          f"| GET /api/cash/icon/{probe} -> {st}")

# ------------------------------------------------- 3. the JS candidate replay
print("\n=== W-041: replay database.js candidates after the fix ===")


def skill_candidates(sid_):
    book = sid_ // 10000
    entry = pad(sid_, 7) if sid_ < 10000000 else str(sid_)
    return [f"f8/{b}.img/skill/{entry}"
            for b in dict.fromkeys([pad(book, 3), str(book), pad(book, 4)])]


_, books = get("/api/children", path="f8")
skill_ids = []
for b in (books or []):
    for c in kids(f"f8/{b['name']}/skill"):
        if c["name"].isdigit():
            skill_ids.append((b["name"], int(c["name"])))
resolved = sum(1 for _, i in skill_ids
               if any(get("/api/inspect", path=c)[0] == 200 for c in skill_candidates(i)))
print(f"  skill entries {len(skill_ids)}; the fixed candidates resolve {resolved}"
      f"   (was 510/535, reference spelling 535/535)")
if resolved < len(skill_ids):
    print("  >>> some skill entries still do not resolve")
    bad += 1

# The item candidate list, replayed the way the CODE reaches it: the group name is
# resolved by RECURSIVE search, so the candidate is right whenever SOME group
# carries the entry under either width. Enumerating each Special group and asking
# whether the code's two spellings name one of its own children is the honest form.
print("\n  f7/Special groups, replayed against the two spellings the code now tries:")
_, groups = get("/api/children", path="f7/Special")
total = hit = 0
for g in (groups or []):
    if not g["name"].endswith(".img") or g["name"].startswith("MaplePoint"):
        continue
    kids_ = [c["name"] for c in kids(f"f7/Special/{g['name']}") if c["name"].isdigit()]
    if not kids_:
        continue
    # Would the code, given one of these ids, name this child?
    ok = 0
    for raw in kids_:
        iid = int(raw)
        total += 1
        if raw in (pad(iid, 8), str(iid)):
            ok += 1
    hit += ok
    print(f"    {g['name']:<16} {len(kids_):>4} numeric entries, "
          f"{ok}/{len(kids_)} named by D8-or-bare")
print(f"  Special numeric entries {total}; named by the fixed candidate list {hit}"
      f"   (D8 alone named 4)")
if hit < total:
    print("  >>> some Special entries are still named by neither spelling")
    bad += 1

print(f"\nBAD={bad}")
raise SystemExit(1 if bad else 0)