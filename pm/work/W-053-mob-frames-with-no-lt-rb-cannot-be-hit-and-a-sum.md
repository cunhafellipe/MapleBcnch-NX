---
id: W-053
title: Mob frames with no lt/rb cannot be hit; a flying mob was asked for a `stand` it does not have
state: done
priority: 4
owner: 
opened: 2026-10-16
closed: 2026-10-16
depends: []
refs: [Mob.cpp:81-83, Mob.cpp:115-124, Animation.cpp:58-61, Rectangle.h:30, Range.h:99-101, Mob.cpp:55]
blocked_on: 
evidence: mobfly-verify.py BAD=0 (70 fly-only + 25 both; all summoned report a real stance and a real box); census: 549/2053 unhittable, 26.7%
---
## Intent

A mob the player summons is a creature they can fight. Two archive shapes break
that here, and one of them is ours:

1. **OURS.** 95 mobs carry a `fly` animation, and the reference makes a flying
   mob stand on `fly` for both STAND and MOVE — so asking for `stand` asks for art
   that does not exist. Those mobs drew nothing and had no collision box, so
   nothing could hit them.
2. **THE ARCHIVE'S.** 108 mobs have frames that carry no `lt`/`rb`, and the client
   reads nothing else for a collision box. Those are unhittable in the real game
   too. The simulator must not pretend otherwise, and must not block them either.

## Reference

- `Mob.cpp:81-83` — `canjump = anisrc["jump"].size() > 0; canfly =
  anisrc["fly"].size() > 0; canmove = anisrc["move"].size() > 0 || canfly;`
- `Mob.cpp:115-124` — `if (canfly) { animations[Stance::STAND] = anisrc["fly"];
  animations[Stance::MOVE] = anisrc["fly"]; } else { animations[Stance::STAND] =
  anisrc["stand"]; animations[Stance::MOVE] = anisrc["move"]; }`
- `Mob.cpp:55` — a linked mob is drawable when `linksrc["stand"] || linksrc["fly"]`,
  so the summon guard accepting a fly-only mob is correct and must not change.
- `Animation.cpp:58-61` — `Frame::Frame(nl::node src) { texture = src; bounds =
  src; ... }`
- `Rectangle.h:30` — `Rectangle(nl::node source) : left_top(source["lt"]),
  right_bottom(source["rb"]) {}`
- `Range.h:99-101` — `overlaps` is `smaller() <= v.greater() && greater() >=
  v.smaller()`, INCLUSIVE, so a degenerate box is a point that can overlap only a
  range containing that exact point.

## Approach

1. `Services/Simulate/MapSimSessionStore.cs` — `MapSimSession.MobCanFly(mobId,
   kind)`, reading `Mob.cpp:82` off the sprite service and caching per mob, the
   same way the attack list and the labels are cached.
2. `Services/Simulate/MobAnimation.cs` — `Advance`: when the wanted action is
   `stand` or `move` and the mob can fly, use `fly`.
3. Do NOT touch `Summon`. Rejecting a mob whose frame has no box would be a
   divergence from `Mob.cpp:55`, which accepts `stand` OR `fly`.

## Acceptance

```
python %SCRATCH%\mobfly-verify.py
# 2053 mobs in the catalog
# 25 carry BOTH stand and fly
# 70 carry fly and NO stand
#   stances reported that the archive does not have: 0
#   mobs whose reported box is the (0, 0, 0, 0) point: 0
# BAD=0

python %SCRATCH%\mobbox-census.py
# 1504 box / 441 no frame at all / 108 point box only
#   549/2053 mobs cannot be hit or touched by anything (26.7%)

powershell -File %SCRATCH%\gate.ps1
# =========== RED SUITES: 0 ===========
```

## Result

The 95 are fixed; the 549 are recorded as an archive fact and deliberately left
alone.

**MEASURED, the flying case.** Over all 2,053 mobs: 70 carry `fly` and no `stand`
at all (`2230107`, `2230109`, `2300100`, `3210206`, `3230104`, `3230303`,
`3230304`, `3230307`, `3230308`, `4230107`, `4230117`, `4230200`, …), and 25 carry
BOTH — where `canfly` still wins and the stance changes to `fly`. Before the fix
the 70 were asked for `stand`, got no art and a `{0,0,0,0}` box; `mobfly-verify.py`
summons all 70 on an empty map and checks the reported stance against the archive's
own action list and the reported box against zero. Both are now clean.

**MEASURED, the archive case.** Over the same 2,053:

| count | shape |
| --- | --- |
| 1504 | a frame carries `lt` and `rb` — hittable |
| 108 | a frame exists but carries no `lt`/`rb` — a point box, 106 of them `stand`, 2 `fly` |
| 441 | neither `stand/0` nor `fly/0` resolves at all |

549 of 2,053 — **26.7%** — cannot be hit or touched by anything, in this simulator
and in the client, because `Frame::get_bounds` reads `lt` and `rb` and nothing
else. Inventing a box from `origin` or from the canvas size would be exactly the
second-hand divergence studio rule 7.6 forbids, so it is declined here and the
census is the deliverable instead.

Two corrections to what this item claimed when it was opened:

- The plan said to make `Summon` reject a mob whose `stand/0` is not a node. That
  was wrong on two counts: `Mob.cpp:55` accepts `fly` as well, and `9300104` —
  the mob that prompted the item — has `fly/0` with a perfectly good `lt`/`rb`.
  The summon was right; the STANCE LOOKUP was wrong.
- The plan described `9300104`'s `stand/0` 404 as the summon guard accepting "a
  frame that is not there". It is there; it is the `fly` frame that is there, and
  it was never being asked for.