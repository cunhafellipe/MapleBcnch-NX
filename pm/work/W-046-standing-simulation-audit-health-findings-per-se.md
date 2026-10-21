---
id: W-046
title: Standing simulation audit: health findings per session
state: done
priority: 3
owner: 
opened: 2026-10-16
closed: 2026-10-16
depends: []
refs: [DrawArgument.h:143; MobAttack.cpp:88; Mob.cpp:1590-1601]
blocked_on: 
evidence: GET /api/simulate/debug/health reports 4 finding codes per live session; simhealth-verify.py BAD=0 through the armed/unarmed calibration; the suite caught a wrong predicate in the audit itself (MaxDamage>0 for an unarmed character) which is fixed; gate 15/15 green
---
## Intent

Turn the one-off probes into a permanent surface, so the next question costs a
call instead of a new script and a new chance to be wrong.

## Reference

`DrawArgument.h:143` (`rlt = pos - center - origin`) · `MobAttack.cpp:88` ·
`Mob.cpp:1590-1601` · `CharLook.cpp:594`.

## Approach

1. Find what the reference actually does for each reported symptom, and record
   the line.
2. Make the product report the same thing, per live session, either way.
3. Assert in the gate that the product's audit agrees with an independent
   measurement — with the armed/unarmed pair as the calibration, because that is
   the discriminator the whole evening turned on.

## Acceptance

1. `GET /api/simulate/debug/health?sessionId=` returns findings with numbers
2. `python ...\simhealth-verify.py  # BAD=0 through the armed/unarmed calibration`
3. `python tools/pm.py gate  # 15 suites green`

## Result

**`GET /api/simulate/debug/health`** reports, per live session:

| code | what it measured | what it said |
|---|---|---|
| `mobWithoutAttackArt` | how many mobs carry an `attack*` node | "2 of 2 mob(s) carry no attack\* node in Mob.nx, so MobAttack.cpp:88 never enters the animation branch for them. They still deal contact damage. **This is the archive, not the port.**" |
| `mobHitOriginShift` | `hit1` frame 0's origin against `stand`'s | `2220100:(27,58)->(28,65)`, `2110200:(27,52)->(27,56)` — and that `DrawArgument.h:143` subtracts the frame's own origin, so the offset is the reference's own arithmetic |
| `mobPositionJump` | every mob's position, sampled every frame | "no mob moved more than 8px in any frame this session" |
| `playerCanFinishFight` / `playerCannotFight` | the sheet's window against each mob's `maxHP` and defence | how many of the character's best swings each mob takes |

**The audit caught a defect in itself, which is the whole reason it exists.** Its
first version gated "can this character fight?" on `MaxDamage <= 0`. An unarmed
level-1 character computes `((4*12+5)/100) * (1 * 1.2) = 0.636` and rounds to
**1**, so the audit answered "can finish the fight" for a character that cannot
swing at all. `Swing()` (MapSimSessionStore.cs:1435) reads
`AttackStancesFor(session.EquippedWeapon)` — the WEAPON, not the damage — so the
predicate is now `EquippedWeapon is null`, and the finding carries
`maxDamageIfItCould` so the contradiction is visible rather than hidden.

Caught by the suite's own calibration, which is why that pair is in it:
```
unarmed: 0 damage numbers
armed  : 6 damage numbers
calibration reproduced
```

**Two things this settles that probes kept re-deriving:**

1. **W-045 is not a displacement.** `DrawArgument.h:143` computes the drawn
   rectangle as `pos - center - origin`, so the reference subtracts each frame's
   own origin exactly as the renderer here does. A flinching mob is drawn 7px
   higher and 1px left because its `hit1` origin says so, and its position never
   moved (0.00px, largest single-frame movement in a six-swing run).
2. **W-044 is not a broken chain.** `MobAttack.cpp:88` only enters the animation
   when `available` is non-empty, and 59% of Mob.nx carries no `attack*` node at
   all. `f4/Map/Map1/100000004.img` is 0 of 2.

**Own design, declared:** the `MaxMobStepPx = 8` threshold has no reference
counterpart, because the reference has no such audit. It is deliberately
generous — a grounded mob walks at about 2.5px/frame, so 8px survives a slope
and a knockback and still catches a teleport-sized jump.

**Not a claim that the simulation is right.** It is a claim that the product
now says the same thing an independent measurement says. The gap between those
two is exactly what the suite is checking.
