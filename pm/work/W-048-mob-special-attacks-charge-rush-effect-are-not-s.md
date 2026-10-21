---
id: W-048
title: Mob special attacks: the type-2 projectile and the attack path around it
state: done
priority: 3
owner: 
opened: 2026-10-16
closed: 2026-10-16
depends: []
refs: [MobAttack.cpp:20-28, MobAttack.cpp:39-49, MobAttack.cpp:52-53, MobAttack.cpp:141-178, MobAttack.cpp:193-229, MobAttackGeometry.h:30-44, MobAttackGeometry.h:45-55, MobAttackGeometry.h:98-117, Stage.cpp:792]
blocked_on: 
evidence: 20 suites, RED SUITES: 0 on three consecutive full runs; projectile-verify.py BAD=0; holdkey-verify.py BAD=0
---
## Intent

A mob's special attack reads as the real thing: it fires a projectile that visibly
crosses the gap instead of hitting on the frame it decides to, it waits the
archive's own `attackAfter` before that projectile leaves, a hit interrupts it,
and it decides how often to swing the way the client does. What the user sees
today is the opposite on all four counts: the shot lands instantly, the mob
attacks while walking, it attacks from further away than the game allows, and it
re-rolls its attack choice sixty times a second instead of two and a half.

## Reference

- `MobAttackGeometry.h:104-105` — `auto start = launch(origin, flipped);
  return check_cone(start, radius, target, flipped);` — the reach cone starts at
  the attack's **source point**, mirrored by the facing, not at the mob.
- `MobAttackGeometry.h:52-55` — `launch()` is `{origin.x + (flipped ? -source.x
  : source.x), origin.y + source.y}`.
- `MobAttack.cpp:147` — `entry.range = MobAttackGeometry::world(data.geometry.range,
  origin, flipped && !noflip)` — the entry's static box is **mirrored** by facing.
- `MobAttack.cpp:22-28` — `attack_entries.clear()` on dead / doomed /
  FREEZE | STUN | SHADOW_WEB.
- `MobAttack.cpp:52-53` — `decision_elapsed += TIMESTEP; if (decision_elapsed <
  400) return;` — the attack DECISION is taken at most every 400ms.
- `MobAttack.cpp:150-157`, `:199-211` — the projectile: launch, aim at
  `(destination.x, destination.y - 25)`, finish `radius` away along that aim,
  `lifetime = max(1, radius * 600 / bullet_speed)`, then `t = min(1, (elapsed -
  delay) / lifetime)` and a **swept** `crosses(previous, current, target±8)`.
- `Stage.cpp:792` — the box the hit is tested against is
  `{x-10, x+10, y-50, y}`, NOT the `{x-22, x+22, y-65, y}` that
  `MobAttack.cpp:57-58` uses for the reach DECISION. Two rectangles, one client.

## Approach

1. `Services/Simulate/CombatService.cs` — `MobAttackSpec.Reaches`, the type 1/2
   branch. Start the cone at `originX + (flipped ? -SourceX : SourceX)` /
   `originY + SourceY`, and run the existing 20px-column loop from there. The
   `radius <= 0` guard stays first.
2. `Services/Simulate/CombatService.cs` — `MobAttackSpec.World` made `public`
   (it was `private static`) so `MobPatrol.BeginEntries` can use the reference's
   own mirroring instead of a raw shift.
3. `Services/MapEditor/MobPatrol.cs` — `BeginEntries`: build `Range` with
   `World(spec.Range, x, y, flipped && !_noFlip)`.
4. `Services/MapEditor/MobPatrol.cs` — `Engage`: on the stun gate, clear
   `_entries` as well as `_activeAttack`.
5. `Services/MapEditor/MobPatrol.cs` — `Engage`: add `_decisionElapsedMs` and the
   400ms gate in front of the whole decision (acquire, forget, pick), leaving the
   in-flight advance and `AdvanceEntries` outside it, because the reference runs
   both every tick (`MobAttack.cpp:37`, `Stage.cpp:792`).
6. `Services/MapEditor/MobPatrol.cs` — `AdvanceEntries`: test against the
   `Stage.cpp:792` box (`HitBoxHalfWidth = 10`, `HitBoxTop = -50`), not the reach
   box. `BuildAvailable` keeps the reach box.
7. `Services/MapEditor/MobPatrol.cs` — new `AttackEntries` snapshot so a
   projectile can be observed instead of inferred from a damage number, and the
   new fields on `MapSimMobFrame` that carry it.

## Acceptance

```
python tools/pm.py check
# 0 problems

# the projectile is frozen for its own attackAfter and then travels
python %SCRATCH%\projectile-verify.py
# 3220000 attack2: type=2 power=135 radius=200 bulletSpeed=140 delay=1320ms
#   it stayed at the launch point until frame 215, i.e. for 79 frames (1317ms)
#   the archive's attackAfter for this attack is 1320ms = 79 frames
#   it waited exactly its own attackAfter and then set off
# BAD=0

# and holding the attack key still deals damage
python %SCRATCH%\holdkey-verify.py
# mob HP 50 -> 4 / damage numbers produced: 16
# BAD=0

powershell -File %SCRATCH%\gate.ps1
# =========== RED SUITES: 0 ===========
```

## Result

All six code changes landed; the full gate is 20 suites and green on three
consecutive runs.

MEASURED facts behind each change:

- **The cone started at the mob.** `source.x` is -93 on `4220000 attack1`, -129 on
  `8220004 attack1`, -111 on `9500308 attack1`; 12 of 21 type-2 slots in a
  200-mob sample carry a source at least 40px from the mob. The cone this port
  tested reached roughly that much further out than the reference's, which is the
  user's "邊走邊攻擊都離我很遠了還在攻擊" measured in a box.
- **The entry box was never mirrored.** `f5/4220000.img/attack2/info/range` is
  `{-190,-97,50,0}` — 190px to one side, 50 to the other — so a left-facing mob
  was handed a box on the wrong side of its own body.
- **Stun left the projectile flying.** `attack_entries.clear()` sits in the same
  three lines as the stun test; a mob struck during its 686ms flight went on to
  damage the character twenty frames later.
- **There was no decision cadence.** The whole `MobAttack.cpp:81-126` block ran
  every frame, so a mob with three attacks re-rolled sixty times a second instead
  of 2.5. Its own cooldown (`max(duration, delay) + 400`, `MobAttack.cpp:138`) is
  the only thing that used to bound it.
- **The hit box was twice as wide as the reference's.** `Stage.cpp:792` passes
  `{x-10, x+10, y-50, y}`; the port used the 22/65 reach box for both, so every
  landing attack was 12px too wide and 15px too tall.

The `ChaseReach` doc claimed the 400ms gate would make a mob "cross the player
forever" and was the reason the gate was absent. That is not what the C++ does:
`Mob.cpp:546` re-applies `phobj.inputx = flip ? 1 : -1` every tick from the
`flip` the decision set, so a mob 52px short walks 48px at 2px/frame, ends up 4px
past the character, and only then takes the `else` at `MobAttack.cpp:126`. The
rationale was wrong and the doc now says what actually happens.

Two facts the harness had to be taught, both measured here and both false-red
producing:

- `f4/Map0/000000000.img` starts the character at (0,95) with **no mobs**;
  `f4/Map1/100000004.img`, the map every earlier probe used, carries 24, and
  walking toward a summoned mob walks into them.
- MEASURED on 3220000: contact damage kills a 50 HP character on frame 15, and
  the first attack DECISION is not due until frame 24. A character that dies on
  frame 15 can observe neither a projectile nor the cadence, so this suite's
  character is invulnerable — which does not touch the evidence, because the
  entry's own position is what is judged.