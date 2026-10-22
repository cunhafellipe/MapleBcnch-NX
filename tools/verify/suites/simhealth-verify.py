"""The standing audit, driven through a scripted scenario.

Every report the user made tonight was answered by a probe written for that one
report, and two of those probes first reported the OPPOSITE of the truth because
they ran unarmed. This suite is the replacement: one scenario, driven to a known
state, asserting on what the product's own audit reports.

IT IS NOT PROOF THE SIMULATION IS RIGHT. It is proof that the product says the
same thing this file measured independently, which is the check that was missing.

CALIBRATION FIRST (R-9). The armed/unarmed pair is the discriminator this whole
night turned on, so it runs first and both halves must agree.
"""
import json, urllib.parse, urllib.request

BASE = "http://127.0.0.1:5591"
MAP = "f4/Map/Map1/100000004.img"
bad = 0


def post(path, body):
    req = urllib.request.Request(f"{BASE}{path}", data=json.dumps(body).encode("utf-8"),
                                 headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=600) as r:
        return json.loads(r.read().decode("utf-8"))


def get(path, **q):
    with urllib.request.urlopen(f"{BASE}{path}?" + urllib.parse.urlencode(q),
                               timeout=600) as r:
        return json.loads(r.read().decode("utf-8"))


def start(equipped=None, invulnerable=True, summon=None):
    s = post("/api/simulate/session",
             {"path": MAP, "startX": 1710, "startY": -25,
              "weapon": "01302000.img", "invulnerable": invulnerable})
    sid = s["sessionId"]
    if equipped:
        post(f"/api/simulate/session/{sid}/weapon", {"equippedWeapon": equipped})
    if summon:
        post(f"/api/simulate/session/{sid}/summon", {"mobId": summon, "count": 1})
    return s, sid


def step(sid, keys=None):
    return post(f"/api/simulate/session/{sid}/frames",
                {"count": 1, "first": keys or {}, "then": {}})


def health(sid):
    return get("/api/simulate/debug/health", sessionId=sid)["findings"]


def codes(sid):
    return {f["code"]: f for f in health(sid)}


def close_in(sid, mob_id, gap=10, limit=420):
    for _ in range(limit):
        f = step(sid)
        ms = [m for m in (f.get("mobs") or [])
              if m["mobId"] == mob_id and m.get("onGround")
              and abs(m["y"] - f["y"]) <= 100]
        if not ms:
            continue
        m = min(ms, key=lambda m: abs(m["x"] - f["x"]))
        dx = m["x"] - f["x"]
        if abs(dx) < gap:
            return f, m
        step(sid, {"right": True} if dx > 0 else {"left": True})
    return None, None


# ------------------------------------------------------- CALIBRATION (armed)
print("=== calibration: armed vs unarmed, the discriminator ===")
unarmed_s, unarmed = start()
close_in(unarmed, "2220100")
hits = []
for _ in range(6):
    step(unarmed, {"attack": True})
    for _ in range(45):
        fr = step(unarmed)
        hits += [d.get("value") for d in (fr.get("damage") or [])]
print(f"  unarmed: {len(hits)} damage numbers")

# Ten swings, not six. The first version of this pair FLAKED: six swings landed
# 6 damage numbers on one run and 0 on the next, with the same seed and the same
# map. A gate that flakes is the failure this project has been fighting all
# evening (see TRAPS.md T-1 and T-2), so the calibration is given enough
# attempts that a single unlucky approach cannot decide it -- and the FAILING
# case is still reported rather than swallowed.
armed_s, armed = start(equipped="1302000")
hits2 = []
for attempt in range(4):
    if hits2:
        break
    close_in(armed, "2220100")
    for _ in range(10):
        step(armed, {"attack": True})
        for _ in range(45):
            fr = step(armed)
            hits2 += [d.get("value") for d in (fr.get("damage") or [])]
print(f"  armed  : {len(hits2)} damage numbers")
if hits and hits2:
    print("  >>> both landed; the armed/unarmed discriminator is not reproducing "
          "on this build and the rest of this file's reasoning is void")
    bad += 1
elif not hits2:
    print("  >>> an ARMED character landed nothing, which the sheet says it should not")
    bad += 1
else:
    print("  calibration reproduced: unarmed cannot swing, armed can")

# ------------------------------------------------- the audit agrees with it
print("\n=== the audit must agree with that calibration ===")
cu = codes(unarmed)
ca = codes(armed)
u_code = cu.get("playerCannotFight", {}).get("code")
a_code = ca.get("playerCanFinishFight", {}).get("code")
print(f"  unarmed -> {u_code}")
print(f"  armed   -> {a_code}")
if u_code != "playerCannotFight":
    print("  >>> the audit did not report the unarmed character as unable to fight")
    bad += 1
if a_code != "playerCanFinishFight":
    print("  >>> the audit did not report the armed character as able to fight")
    bad += 1

# ------------------------------------------------------ the two reports
print("\n=== W-044: attack art ===")
w = ca.get("mobWithoutAttackArt") or ca.get("mobAttackArt")
print(f"  {w['code']}: {w['text']}")
if ca.get("mobWithoutAttackArt") is None:
    print("  >>> expected mobWithoutAttackArt on this map (0 of 2 mobs carry one)")
    bad += 1

print("\n=== W-045: the hit pose's draw origin ===")
sh = ca.get("mobHitOriginShift")
if sh is None:
    print("  >>> no mobHitOriginShift finding, but every sampled mob's hit1 differs "
          "from stand (2220100 (27,58)->(28,65))")
    bad += 1
else:
    print(f"  {sh['text']}")
    print(f"  examples: {sh['data']['examples'][:4]}")

print("\n=== a mob must not move while attacking (MobAttack.cpp:93-94) ===")
sm_s, sm = start(equipped="1302000", summon="9500302")
for _ in range(200):
    fr = step(sm)
    ms = [m for m in fr.get("mobs") or [] if m["mobId"] == "9500302"
          and m.get("onGround") and abs(m["y"] - fr["y"]) <= 100]
    if ms:
        m = min(ms, key=lambda m: abs(m["x"] - fr["x"]))
        dx = m["x"] - fr["x"]
        if abs(dx) < 12:
            break
        step(sm, {"right": True} if dx > 0 else {"left": True})
for _ in range(1400):
    step(sm)
    if _ % 45 == 44:
        step(sm, {"attack": True})
        for _ in range(45):
            step(sm)
cm = codes(sm)
mv = cm.get("mobMovesWhileAttacking")
print(f"  {mv['code']}: {mv['text']}")
if mv["severity"] == "error":
    print(f"  >>> {mv['data']['examples']}")
    bad += 1
    # A finding made ONLY of the 2.17px recoil means the exclusion for the
    # sanctioned hit impulse (Mob.cpp:595-598) stopped working.
    if all("recoil" in e for e in mv["data"]["examples"]):
        print("  >>> every example is the sanctioned 2.17px recoil, not a walk")

print("\n=== position jumps ===")
pj = ca.get("mobPositionJump")
print(f"  {pj['code']}: {pj['text']}")
if pj["severity"] == "error":
    print(f"  >>> {pj['data']['examples']}")
    bad += 1

# -------------------------------------------------- a mob WITH attack art
print("\n=== a summoned mob that DOES carry attack art ===")
s, sid = start(equipped="1302000", summon="9500302")
for _ in range(60):
    step(sid)
c = codes(sid)
print(f"  findings: {sorted(c)}")
if c.get("mobWithoutAttackArt"):
    print(f"  {c['mobWithoutAttackArt']['text']}")
    print("  (the summoned 9500302 has one; the map's own two mobs do not, so a "
          "warn listing only those two is the correct answer)")

# ------------------------------------------------------- the audit is alive
print("\n=== the audit on a session that does not exist ===")
ghost = get("/api/simulate/debug/health", sessionId="no-such-session")["findings"]
print(f"  {[f['code'] for f in ghost]}")
if not ghost or ghost[0]["code"] != "noSession":
    print("  >>> a missing session did not report noSession")
    bad += 1

print(f"\nBAD={bad}")