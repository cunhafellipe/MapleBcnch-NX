# TASK 13 — Mobs with real art and real animation

Depends on TASK 9C, 11, 12 landing. Patrol logic is already verified; this is
pixels and animation.

## The situation

`Mob.nx` is **817.6 MB** — bigger than `Map.nx`. We opened it for geometry and
found 29 mobs per map. **None of them have art on screen.**

`Mob.nx` has never been decoded for canvases in this project. TASK 8 proved the
NX canvas decoder works on `Character.nx` and `Map.nx`; `Mob.nx` is the same
format and may work immediately, or may hit a case we have not seen. Find out
which — do not assume.

## The specification

    C:\MUWORK\GAME\MPSH\clean-client\src\Gameplay\MapleMap\Objects\Mob.cpp

94 delay/frame references. Read:

- how a mob picks its animation state from its AI state (idle, walk, attack,
  hit, die)
- the frame model — whether it shares `Graphics\Animation.cpp`'s `Frame` or has
  its own
- how a mob's sprite is anchored to its position, given that `CharLook.cpp:98`
  established the marker is a **navel**, not a ground point
- whether dead mobs and disappearing corpses are drawn differently

Also read `Mob.h` for the state enum, and `MobAttack.cpp` for the attack
timing that the animation has to agree with.

## Requirements

1. Decode mob canvases from `Mob.nx`. Report the first node that failed, if any
   — a format case we have not seen is a finding, not a blocker.
2. Draw mobs with real art, correctly anchored, Z-sorted against terrain and
   against the player using TASK 3's ordering.
3. Animate per the real state machine: a patrolling mob walks, an aggroed mob
   attacks, a hit mob reacts, a dead mob dies.
4. Animation timing must agree with the physics — a mob that attacks visually
   one frame before it can actually hit is a bug, and it is measurable.

## Verify

1. Build clean, restart against the new build, confirm the DLL timestamp.
2. Open `Map.nx` and `Mob.nx` from `C:\MUWORK\GAME\MPSHS`.
3. **Screenshot with mob art visible** — at least three different mob types on
   screen at once, plus a close-up of one so the art is judgeable. Give paths.
4. Report the PNG byte count and non-transparent pixel count for at least one
   mob, fetched by the browser.
5. Report the measured attack timing: the frame index when the animation shows
   the hit versus the tick when damage is applied. They must line up.

Use `tools/visual-check/verify-char.mjs <cdpPort> <out.png>` to drive the page.
Do not write a new harness.

## Constraints

- **Do NOT modify `C:\MUWORK\GAME\MPSH\`** — read-only reference.
- Do not change patrol logic, physics, camera, Z formulas, session semantics,
  or NX decoding.
- Do not weaken a check to make it pass.

## Scope limit

Finite budget. Order: (1) decode and draw one mob type standing still,
(2) animation across states, (3) attack timing alignment, (4) every mob type.
Report which you finished. **One mob drawn correctly is worth more than twenty
drawn at the wrong anchor** — a user can see instantly when something is
floating or buried.