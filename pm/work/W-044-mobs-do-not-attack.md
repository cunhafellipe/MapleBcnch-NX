---
id: W-044
title: Mobs do not attack
state: done
priority: 4
owner: 
opened: 2026-10-16
closed: 2026-10-16
depends: []
refs: [MobAttack.cpp:20-127; Mob.cpp:1848-1858; Stage.cpp:809-818]
blocked_on: 
evidence: attack chain MEASURED WORKING: mob 9500302 (summoned, has attack1 type2 power10 radius200) aggroed and played 'attack1' for 35 frames after 4 provoked swings; 41% of Mob.nx carries an attack node (50/120 sampled); f4/Map/Map1/100000004.img has 0 of its 2 mob ids with one; new GET /api/simulate/mob-attacks surfaces it in the bestiary panel; gate 14/14 green
---
## Intent

Reported: monsters do not attack. Two separable things hide under that sentence —
whether the attack chain runs at all, and whether the archive gives the mob an
attack animation to play.

## Reference

`MobAttack.cpp:62-118` — the attack list, the reach test, and the rule that an
animation is played only when the list yields something:

```
if (!available.empty() && (canfly || phobj.onground))
{   flip = attack_facing;  set_stance(Stance::STAND);
    begin_attack(selected, flip, ...);  mp -= attacks.at(selected).conmp; }
```

## Approach

1. Establish whether the chain runs at all, with a mob that HAS attack art.
2. Establish how much of the archive has none.
3. If the chain works and the data does not exist, the defect is that the screen
   cannot tell those apart — so surface it rather than change behaviour.

## Acceptance

1. `python ...\mobdefect4.py  # a mob WITH attack art must aggro and play attack1`
2. `GET /api/simulate/mob-attacks?mobId=` — 0 for a mob with no attack node
3. `python tools/pm.py gate  # unchanged behaviour, 14 suites green`

## Result

**The chain works.** Measured on a summoned `9500302`, whose attack list parses
correctly (`action:'attack1' type:2 power:10 radius:200 delayMs:600
durationMs:2400`): after four provoked swings the debug reports `aggro=True` and
the frame carries the `attack1` stance for **35 frames**. Touch damage lands too
(90 per contact, killing a 50 HP character in one tick).

**The data is often absent.** 41% of Mob.nx carries an `attack*` node — 50 of 120
sampled. `f4/Map/Map1/100000004.img`, the map the walk view opens on, has **0 of
its 2 mob ids** carrying one: `2110200` and `2220100` both have only
`die1 / hit1 / jump / move / stand`. On that map no mob CAN play an attack, and
by `MobAttack.cpp:88` it is correct that it does not.

**So the actionable half is not the chain, it is the ambiguity.** A mob with no
attack art and a mob with a broken animation look identical from outside.
Added `GET /api/simulate/mob-attacks`, and a row in the bestiary panel that
says which it is — including the empty case, verbatim:

> Attacks: none in the archive — this mob only deals contact damage

and, for a mob that has them, `attack1 · power 10 · radius 200`.

**My own harness lied three times before the real answer.** Every probe started
unarmed, and `Swing()` (MapSimSessionStore.cs:1435-1439) puts the pose, the delay
and the hit box inside `if (AttackStancesFor(session.EquippedWeapon).Length !=
0)` — an unarmed character correctly cannot swing (`CharLook.cpp:594`, already
covered by `attack-test.ps1`'s unarmed control). So the player never hit
anything, no mob ever aggros, and "mobs do not attack" was partly a statement
about the probe. The armed/unarmed pair, run back to back, is the discriminator:

```
UNARMED : player x=2082 hits=0 [] swingBoxEmpty=True
SWORD   : player x=2082 hits=6 [2,1,3,1,1,3] swingBoxEmpty=False
```

**Left open:** whether the 59% of mobs with no attack art should get one. That
is archive authoring, not this project.
