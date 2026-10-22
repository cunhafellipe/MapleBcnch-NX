"""A mob that can FLY stands on its `fly` animation.

`Mob.cpp:81-83` --

    canjump = anisrc["jump"].size() > 0;
    canfly  = anisrc["fly"].size() > 0;
    canmove = anisrc["move"].size() > 0 || canfly;

`Mob.cpp:115-124` --

    if (canfly) { animations[Stance::STAND] = anisrc["fly"];
                  animations[Stance::MOVE]  = anisrc["fly"]; }
    else        { animations[Stance::STAND] = anisrc["stand"];
                  animations[Stance::MOVE]  = anisrc["move"];  }

So a mob whose image carries a non-empty `fly` action NEVER plays `stand`, even
when `stand` exists.

The damage that does: `f5/9300104.img` carries attack1, die1, fly and hit1 and
no `stand` at all, so asking it for `stand` returned no art AND a zero collision
box -- a mob the player could not hit with a swing, a skill, or contact damage,
while its `fly/0` carries a perfectly good `lt`/`rb`. The summon guard was right
to accept it: `Mob.cpp:55` accepts `stand` OR `fly`.
"""
import json, os, urllib.parse, urllib.request
from concurrent.futures import ThreadPoolExecutor

BASE = "http://127.0.0.1:5591"
MAP = "f4/Map/Map0/000000000.img"
bad = 0


def get(route, **q):
    with urllib.request.urlopen(f"{BASE}{route}?" + urllib.parse.urlencode(q), timeout=300) as r:
        return json.loads(r.read().decode("utf-8"))


def post(route, body):
    req = urllib.request.Request(f"{BASE}{route}", data=json.dumps(body).encode("utf-8"),
                                 headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=600) as r:
        return json.loads(r.read().decode("utf-8"))


def actions(mob_id):
    try:
        sp = get("/api/simulate/mob-sprite", mobId=mob_id)
    except Exception:
        return mob_id, set()
    return mob_id, {a["name"] for a in sp.get("actions", []) if a["frames"]}


print("=== how many mobs stand on `fly` instead of `stand`? ===")
CACHE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "mobfly.json")
cat = get("/api/simulate/mob-catalog")["mobs"]
ids = [m["id"] for m in cat]

have = {}
fly_only = []
fly_and_stand = 0
if os.path.exists(CACHE):
    try:
        with open(CACHE, encoding="utf-8") as fh:
            cached = json.load(fh)
        sample = cached["fly_only"][:6] + ([cached["both"][0]] if cached["both"] else [])
        # Re-validate the cached answer against the LIVE server before trusting it:
        # a cache that is never checked is a claim, not a measurement.
        fresh = {m: actions(m)[1] for m in sample}
        ok = all(("fly" in fresh[m]) for m in sample) \
            and all(("stand" not in fresh[m]) for m in cached["fly_only"][:6])
        if ok:
            have = fresh
            fly_only = cached["fly_only"]
            fly_and_stand = cached["bothCount"]
            print(f"  (census cached; re-validated {len(sample)} mobs against the "
                  f"live server)")
    except Exception:
        have = {}

if not have:
    with ThreadPoolExecutor(max_workers=10) as pool:
        for mob_id, acts in pool.map(actions, ids):
            have[mob_id] = acts
    fly_only, both = [], []
    for mob_id, acts in have.items():
        if "fly" not in acts:
            continue
        if "stand" in acts:
            both.append(mob_id)
        else:
            fly_only.append(mob_id)
    fly_and_stand = len(both)
    with open(CACHE, "w", encoding="utf-8") as fh:
        json.dump({"fly_only": fly_only, "both": both, "bothCount": fly_and_stand}, fh)

print(f"  {len(ids)} mobs in the catalog")
print(f"  {fly_and_stand} carry BOTH stand and fly -- these change stance too, "
      f"because canfly is true and wins")
print(f"  {len(fly_only)} carry fly and NO stand -- these were asked for art that "
      f"does not exist")
if fly_only:
    print(f"    {fly_only[:12]}")

# The check that matters is on a LIVE session: does the mob report a stance the
# archive actually has, and a collision box?
print()
print("=== in the simulation ===")
s = post("/api/simulate/session",
         {"path": MAP, "weapon": "1302000.img", "equippedWeapon": "1302000",
          "invulnerable": True})
sid = s["sessionId"]
summoned = [m for m in fly_only if post(f"/api/simulate/session/{sid}/summon",
                                        {"mobId": m, "count": 1}).get("spawned")]
print(f"  summoned {len(summoned)} of them on an empty map")

# W-056: `have` came from the cache holding ONLY the re-validation sample -- at
# most 7 ids -- while this suite judges up to 24 of the 70 in `fly_only`. The
# first lookup outside that sample raised KeyError and the suite DIED before its
# own `BAD=` line, so the gate saw no marker and scored a crashed suite as `ok`.
# A cached answer may be reused, but it may not silently narrow what gets judged:
# fill in whatever the loop is about to ask about, and say how many came off the
# wire rather than out of the cache.
judged = summoned[:24]
uncached = [m for m in judged if m not in have]
if uncached:
    for mob_id in uncached:
        have[mob_id] = actions(mob_id)[1]
    print(f"  {len(uncached)} of the {len(judged)} judged mobs were not in the "
          f"re-validation sample; read from the live server")
if not summoned:
    print("  >>> none could be summoned; the census and the simulator disagree")
    bad += 1

def mob_of(fr, mob_id):
    for m in fr.get("mobs") or []:
        if m.get("mobId") == mob_id:
            return m
    return None


bad_stance = []
no_box = []
for mob_id in summoned[:24]:
    fr = post(f"/api/simulate/session/{sid}/frames",
              {"count": 1, "first": {}, "then": {}})
    m = mob_of(fr, mob_id)
    if m is None:
        bad_stance.append((mob_id, "not on the map"))
        continue
    d = get("/api/simulate/debug/mob-swing", sessionId=sid)
    boxes = [b for b in d.get("mobBoxes", []) if b.get("mobId") == mob_id]
    box = (boxes[0]["box"] if boxes else None)
    if m["stance"] not in have[mob_id]:
        bad_stance.append((mob_id, m["stance"]))
    if box is None or box["left"] == 0 and box["top"] == 0 and box["right"] == 0 \
            and box["bottom"] == 0:
        no_box.append((mob_id, m["stance"]))

print(f"  stances reported that the archive does not have: {len(bad_stance)}")
for mob_id, stance in bad_stance[:8]:
    print(f"    {mob_id}: reported {stance!r}")
print(f"  mobs whose reported box is the {0,0,0,0} point: {len(no_box)}")
for mob_id, stance in no_box[:8]:
    print(f"    {mob_id} (stance {stance!r})")
if bad_stance:
    bad += 1
if no_box:
    bad += 1

if not summoned:
    print("  >>> nothing to judge")
elif not bad_stance and not no_box:
    print(f"  every one of them stands on art it actually has, with a real box")

print(f"\nBAD={bad}")