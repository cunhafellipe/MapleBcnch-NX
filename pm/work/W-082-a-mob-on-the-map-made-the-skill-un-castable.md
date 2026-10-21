---
id: W-082
title: A mob on the map made the skill un-castable, and a cast aimed the wrong way
state: done
priority: 95
owner: 
opened: 2026-10-17
closed: 2026-10-17
depends: []
refs: MapSimSessionStore.cs Advance and Describe; PlayerStatus.cs BaseMaxHp; SimTuning.cs; clean-client PlayerStates.cpp:52-63 and Player.cpp:1214-1218; MapleMcp/LiveSimTools.cs
blocked_on: 
evidence: |
  before: opened x=1710 gap=15 tookHit=150 playerHp=0 dead=true -> CastSkill refuses on Dead
  before: one frame of (turn + cast) built the box from the PREVIOUS frame's facing
  after:  playerHp=1000, cast in contact -> mob 1000 -> 900
  after:  facing flips on the same frame and the box follows it, twice in a row
  after:  a dead body no longer travels
workstream: simulate
kind: defect
risk: med
---

## Intent

Three things a person hit in the editor, all of them the same mistake wearing
three costumes:

1. **A mob on the map made the skill un-castable.** Not a skill bug. The map's
   mobs are level 20 with 150 contact damage; the character had 50 HP, so one
   touch killed it, and `CastSkill` refuses on `Status.Dead`. The simulator was
   unusable anywhere a mob stood.
2. **The skill went out on the wrong side.** Facing was derived from the body's
   actual movement, in `Describe()`, which runs *after* the frame's combat — so
   the collision box in `ApplySkillHits` was built from the previous frame's
   facing. A character standing still (blocked, or under a cast pose, where
   `moveLeft` is zeroed) never turned, so the skill went wherever it last walked.
3. **A dead body kept walking.** `Swing` and `CastSkill` both refuse on
   `Status.Dead`; walking never did.

## Reference

- `PlayerStates.cpp:52-63` — the WALK state handler calls
  `player.set_direction(false)` / `(true)` **from the key handlers**.
- `Player.cpp:1214-1218` — `void Player::set_direction(bool f) { if (!attacking)
  Char::set_direction(f); }` — the direction does not change while attacking or
  casting, and that is the whole body of the function.
- `Mob.cpp:731-732` — the same rule for a mob: `if (phobj.inputx != 0 && !noflip)
  flip = phobj.inputx > 0;`

## Approach

1. `SimTuning.PlayerHp = 1000`, used as `PlayerStatus.BaseMaxHp`. The reference's
   50 is what it replaced. Same reasoning as ADR-0006's `MobHp`, one line of the
   fight away: both sides 1000, one hit 100, a 150-damage touch is now a bar and
   not a corpse.
2. Set `session.Facing` from the input at the top of `Advance`, **before** the
   combat resolution, and only when no cast and no swing is playing.
3. `moveLeft = 0` and no jump when `session.Status.Dead`.

## Acceptance

```
# 1. a mob in contact, not invulnerable
#    opened  x=1710 gap=91 playerHp=1000 dead=false
#    walked  x=1614 gap=5   playerHp=850 dead=false   <- 150 damage, alive
#    after   action="skill" stance="brandish1" mobHp=900
#
# 2. one frame that turns and casts, mob on the NEW side
#    before  x=1734 facing=1  mob to the LEFT  mobHp=1000
#    after   x=1744 facing=-1 mob to the LEFT  mobHp=900
```

Measured through `tools/probe/mcp-probe.mjs --script`, i.e. over MCP, not over
hand-written HTTP.

## Result

Landed. `MapSimSessionStore.Advance` now sets facing from input and stops a
corpse; `PlayerStatus.BaseMaxHp` is `SimTuning.PlayerHp`. The gate is unchanged at
one red suite (`ball-art`, W-070, open and unrelated).
