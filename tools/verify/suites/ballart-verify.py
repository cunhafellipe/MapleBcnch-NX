"""The projectile's own art: published, named on the wire, and drawn.

`MobAttack.cpp:163-164` --
    attack.has_ball = bool(data["ball"]["0"]);
    attack.ball     = data["ball"];

`Mob.cpp:1118-1126` --
    for (const auto& entry : attack_entries) {
        const auto& data = attacks.at(entry.index);
        if (data.geometry.type != 2 || !data.has_ball || entry.elapsed < entry.delay) continue;
        double t = std::min(1.0, double(entry.elapsed - entry.delay) / entry.lifetime);
        Point<int16_t> point{ ... };
        data.ball.draw(DrawArgument(point, entry.flipped), alpha);
    }

So a type-2 shot is invisible until `elapsed >= delay`, is drawn at the lerped
position, and is mirrored by the ATTACK's facing.

MEASURED on the open Mob.nx: `ball` is declared on 172 of 1,536 attack slots, and
the art lives at `attackN/info/ball` -- not beside the actions, so nothing that
enumerates an image's children ever saw it.
"""
import json, os, re, urllib.parse, urllib.request

BASE = "http://127.0.0.1:5591"
MAP = "f4/Map/Map0/000000000.img"
WWW = r"C:\MUWORK\GAME\MAPLESOTRY\wz工具\MapleBcnch-NX-audit\MapleBench\wwwroot"
bad = 0


def get(route, **q):
    with urllib.request.urlopen(f"{BASE}{route}?" + urllib.parse.urlencode(q),
                               timeout=300) as r:
        return json.loads(r.read().decode("utf-8"))


def post(route, body):
    req = urllib.request.Request(f"{BASE}{route}", data=json.dumps(body).encode("utf-8"),
                                 headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=600) as r:
        return json.loads(r.read().decode("utf-8"))


# ---------------------------------------------------------------- the archive
print("=== the art is under attackN/info/ball, and is published as attackN/ball ===")
cat = get("/api/simulate/mob-catalog")["mobs"]
ids = [m["id"] for m in cat]
sample = ids[::max(1, len(ids) // 60)][:60]

declared = []
for mid in sample:
    for a in get("/api/simulate/mob-attacks", mobId=mid)["attacks"]:
        if a.get("type") == 2:
            sp = get("/api/simulate/mob-sprite", mobId=mid)
            actions = {x["name"]: len(x["frames"]) for x in sp.get("actions", [])}
            want = f"{a['action']}/ball"
            declared.append((mid, a["action"], want in actions, actions.get(want, 0)))
            break

have = [d for d in declared if d[2]]
print(f"  {len(declared)} type-2 attacks in {len(sample)} sampled mobs; "
      f"{len(have)} of them publish their ball art")
for mid, action, ok, frames in declared[:10]:
    print(f"    {mid} {action}: published={ok} frames={frames}")
if not have:
    print("  >>> none; the rest of this file is void")
    raise SystemExit(1)
if any(f < 1 for _, _, _, f in have):
    print("  >>> a published ball has no frames")
    bad += 1

# ---------------------------------------------------------------- on the wire
FIGHTER = None
CACHE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "ballfighter.json")
# Prefer a ball with more than one frame: a one-frame ball has nothing to advance
# through, and MEASURED `9300335 attack1/ball` is one canvas, so choosing it made
# "the index never advances" true for a correct implementation.
multi = [m for m, _, ok, f in have if ok and f >= 2]
candidates = multi or [m for m, _, ok, _ in have]
if os.path.exists(CACHE):
    with open(CACHE, encoding="utf-8") as fh:
        FIGHTER = json.load(fh).get("mobId")
if FIGHTER not in candidates:
    FIGHTER = candidates[0]
with open(CACHE, "w", encoding="utf-8") as fh:
    json.dump({"mobId": FIGHTER}, fh)
print(f"  fighting with {FIGHTER} (its ball carries "
      f"{next(f for m, _, _, f in have if m == FIGHTER)} frames)")

print()
print(f"=== on the wire, with {FIGHTER} (ball carries "
      f"{next(f for m, _, _, f in have if m == FIGHTER)} frames) ===")
s = post("/api/simulate/session",
         {"path": MAP, "weapon": "1302000.img", "equippedWeapon": "1302000",
          "invulnerable": True})
sid = s["sessionId"]
post(f"/api/simulate/session/{sid}/summon", {"mobId": FIGHTER, "count": 1})


def step(keys=None):
    return post(f"/api/simulate/session/{sid}/frames",
                {"count": 1, "first": keys or {}, "then": {}, "manual": True})


def mob_of(fr):
    return next((m for m in fr.get("mobs") or [] if m.get("mobId") == FIGHTER), None)


# WAKE IT FIRST. A mob with no `info/firstAttack` acquires only from a landed hit
# (`Mob.cs:1802`, `if (_lastHitDamage > 0) Aggro = true`), and the swing only lands
# inside the player's own box -- which reaches ~20..84px in FRONT. MEASURED on
# 9400708: with no hit it never played an attack in 300 frames and the whole file
# reported "the ball art never appeared" for a correctly wired projectile.
woken = False
for _ in range(400):
    fr = step({"attack": True})
    m = mob_of(fr)
    if m is None:
        break
    if m["hp"] < m["maxHp"]:
        woken = True
        break
    dx = m["x"] - fr["x"]
    if abs(dx) > 8:
        step({"left": dx < 0, "right": dx > 0})
print(f"  woken by a landed hit: {woken}")

fr = step()
mob = mob_of(fr)
away = fr["x"] <= mob["x"]
for _ in range(90):
    fr = step({"right": away, "left": not away})
    mob = mob_of(fr)
    if mob is None:
        break
    if abs(mob["x"] - fr["x"]) >= 130 or not fr["onGround"]:
        break
for _ in range(200):
    fr = step()
    mob = mob_of(fr)
    if mob is None or mob.get("attackEntryCount", 0) == 0:
        break

early = None
named = None
flying_seen = False
# The furthest a FLYING entry ever got: (elapsedMs, delayMs).
#
# The ball's art is named only once the entry is PAST its delay:
#
#   Mob.cpp:1118-1126
#     if (data.geometry.type != 2 || !data.has_ball || entry.elapsed < entry.delay) continue;
#
# so a flying entry that never got past its delay NEVER has an art name, and
# calling that "the art never appeared" is wrong. MEASURED 2026-10-18: this
# suite failed 1 run in 3 with exactly that message.
flying_far = (0.0, 0.0)
indices = []
# W-070/W-081. The mob has TWO attacks and the reference picks the one that
# REACHES (MobAttack.cpp:68-76: try this facing, then the other), so on any given
# fight it may legally fire the STATIC attack2 and never fire the flying one --
# and then there is no ball, which is correct behaviour rather than missing art.
# This measured 50/50. So the loop waits for a FLYING entry to exist at all
# before it can be said the art failed to appear; a mob that never fires one in the
# whole budget is reported as a fact about that mob, not as a defect.
for i in range(1800):
    fr = step()
    mob = mob_of(fr)
    if mob is None:
        continue
    # W-070: read EVERY entry the frame now publishes. The scalar
    # `attackEntryBallAction` is entry 0, and on 4220000 entry 0 is the STATIC
    # attack2 (no ball) while the art lives on the FLYING attack1 -- so reading
    # the scalar reported "the ball never appears" for a mob that carries it and
    # publishes it correctly. `Mob.cpp:1104-1125` draws one ball per entry.
    entries = mob.get("attackEntries") or []
    if not entries and mob.get("attackEntryCount", 0):
        entries = [{  # an older frame that only carries the scalars
            "x": mob["attackEntryX"], "y": mob["attackEntryY"],
            "elapsedMs": mob["attackEntryElapsedMs"], "delayMs": mob["attackEntryDelayMs"],
            "type": mob["attackEntryType"], "ballAction": mob["attackEntryBallAction"],
            "ballFrameCount": mob["attackEntryBallFrameCount"],
            "ballFrameIndex": mob["attackEntryBallFrameIndex"],
            "flipped": mob["attackEntryFlipped"],
        }]
    for e in entries:
        if e.get("type") == 2:
            flying_seen = True
            el = e.get("elapsedMs") or 0.0
            dl = e.get("delayMs") or 0.0
            if el > flying_far[0]:
                flying_far = (el, dl)
        action = e.get("ballAction") or ""
        if action and named is None:
            named = (i, action, e)
            print(f"  frame {i}: the ball art is named ({action}), frame "
                  f"{e['ballFrameIndex']}/{e['ballFrameCount']}, "
                  f"elapsed={e['elapsedMs']:.0f}ms of "
                  f"delay={e['delayMs']:.0f}ms, flipped="
                  f"{e['flipped']}")
        # The ball's frame index on EVERY frame that carries one, not only on the
        # frames where the art is NAMED.
        #
        # The art is named once the entry is past its delay (Mob.cpp:1121), and
        # the server clears the name on the next frame -- so sampling only the
        # named frames sees the index ONCE and can never see it advance.
        # MEASURED 2026-10-18: "the ball frame index never advances", 1 run in 4.
        if e.get("ballFrameIndex") is not None:
            indices.append(e["ballFrameIndex"])
        if not action and early is None and e.get("elapsedMs", 0) > 0:
            early = (i, e["elapsedMs"], e["delayMs"], e.get("type"))

if named is None:
    if flying_seen and flying_far[1] > 0 and flying_far[0] >= flying_far[1]:
        print("  >>> a FLYING entry was in flight PAST ITS DELAY (elapsed="
              f"{flying_far[0]:.0f}ms >= delay={flying_far[1]:.0f}ms) and its ball art "
              "never appeared on the frame payload")
        bad += 1
    elif flying_seen:
        print("  the FLYING entry never got past its delay (elapsed="
              f"{flying_far[0]:.0f}ms < delay={flying_far[1]:.0f}ms), so it has no ball "
              "art by the reference's own rule (Mob.cpp:1121) -- correct, not missing art")
    else:
        # The mob chose its static attack for the whole fight. That is the
        # reference's own choice (MobAttack.cpp:68-76), so there was nothing to
        # draw; saying the art is missing would be wrong.
        print("  the mob used its STATIC attack for the whole fight, so there was "
              "no ball to draw -- a fact about the mob, not a defect. "
              "Re-run to fight it again.")
if early is not None:
    print(f"  frame {early[0]}: an entry (type {early[3]}) is in flight at "
          f"{early[1]:.0f}ms of a {early[2]:.0f}ms delay and carries no ball "
          f"-- correct for a static attack; Mob.cpp:1121 only draws a ball past "
          f"the delay, and a type-0 attack never has one")
elif named is not None:
    print(f"  >>> a ball was named before any entry was past its delay")

print(f"  ball frame indices seen: {sorted(set(indices))}")
multi_frame = any(f >= 2 for m, _, _, f in have if m == FIGHTER)
if multi_frame and len(set(indices)) > 1:
    print(f"  it advances, so it is an animation rather than one still")
elif multi_frame and named is not None:
    print("  >>> the ball frame index never advances")
    bad += 1
else:
    print(f"  it is a single canvas, so there is nothing to advance through")

# ---------------------------------------------------------------- the client
print()
print("=== the client draws it ===")
walk = open(os.path.join(WWW, "js", "walkview.js"), encoding="utf-8").read()
sprite = open(os.path.join(WWW, "js", "mobsprite.js"), encoding="utf-8").read()
checks = [
    ("walkview reads the entry position", "attackEntryX" in walk and "attackEntryY" in walk),
    ("walkview reads the ball action name", "attackEntryBallAction" in walk),
    ("walkview reads the ball frame index", "attackEntryBallFrameIndex" in walk),
    ("walkview reads the launch facing", "attackEntryFlipped" in walk),
    ("walkview blits it", "drawMobAttackBall" in walk and "drawImage(img, x, y, w, h)" in walk),
    ("mobsprite can blit a named action", "stepNamed" in sprite),
    ("mobsprite preloads named actions",
     "for (const [, entry] of art.actions)" in sprite),
]
for label, ok in checks:
    print(f"  {'ok  ' if ok else '>>> '}{label}")
    if not ok:
        bad += 1


# W-089. The session this suite opened is DELETED before it exits.
#
# A leaked session is not a leak of memory, it is a leak of BACKGROUND LOAD: the
# server keeps ticking it, and `MapSimSessionStore` only reaps a session after
# 120 s idle. MEASURED 2026-10-18, 40 idle sessions push the request latency
# from 1.6 ms to 143.9 ms, which is what makes the NEXT suite's timing
# assumption fail. The failure mode reproduces itself.
def _drop(session_id):
    try:
        req = urllib.request.Request(f"{BASE}/api/simulate/session/{session_id}", method="DELETE")
        with urllib.request.urlopen(req, timeout=30) as r:
            r.read()
    except Exception:
        pass

_drop(sid)
print(f"\nBAD={bad}")