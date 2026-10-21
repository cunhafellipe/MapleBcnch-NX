---
id: W-047
title: Mob walks while attacking
state: done
priority: 3
owner: 
opened: 2026-10-16
closed: 2026-10-16
depends: []
refs: [MobAttack.cpp:39-49, 93-94, 119-126; MobPatrol.cs:1071]
blocked_on: 
evidence: before: 9500302 played attack1 285 frames and moved on 22 of them at 0.6px/frame; after: 0 of 276, x constant at 1767. Root cause: the chase block excluded a swinging mob by making its own condition FALSE, falling through into the patrol; fix returns with Direction=0 when AttackStance.Length>0. New audit finding mobMovesWhileAttacking at zero tolerance. Gate 15/15 green, calibration stable over 3 runs
---
## Intent

Reported, and confirmed: a mob walks along its own patrol route while it is
playing an attack.

## Reference

`MobAttack.cpp:93-94` and `:48`:

```
phobj.inputx = phobj.inputy = 0;  phobj.hspeed = phobj.vx = 0;
...
if (active_attack >= 0) { ...; return; }        // :39-49
```

The move decision at `:119-126` is unreachable for the length of a swing.

## Approach

1. Read the reference's attack branch and its early return.
2. Find where this port's locomotion is decided relative to the attack decision.
3. Compare positions frame by frame while an attack pose is on screen.

## Acceptance

1. `python ...\mobattack-claims.py  # 0 attack frames with a position change`
2. `GET /api/simulate/debug/health  # mobMovesWhileAttacking: info`
3. `python tools/pm.py gate  # 15 suites green`

## Result

**Measured, before the fix.** `9500302` played `attack1` for 285 frames and moved
on **22** of them, 0.6px per frame, in a straight line — the tell-tale of a
patrol, not a knockback.

**The cause was a fall-through.** `MobPatrol.Step()` already excluded a swinging
mob from the chase:

```csharp
if (Aggro && _canMove && AttackStance.Length == 0 && characterX is { } chx)
```

which reads as though it covers the case. It does not: a false condition makes
the whole `if` fall THROUGH, and what follows is the **patrol**, which walks the
mob along its own route. The reference has no such fall-through because
`update_attack` returns at `:48` while `active_attack >= 0`.

Here locomotion is a separate pass from the decision, so the decision has to stop
it explicitly:

```csharp
if (AttackStance.Length > 0) { Direction = 0; return; }
```

Zeroing `Direction` also retires the speed, because `Move()` returns immediately
on a zero direction — which is the port's equivalent of `hspeed = vx = 0`.

**Measured, after the fix:** 0 of 276 attack frames moved, and the x is a single
value (1767) across the whole attack.

**Now a standing finding**, not a one-off: `mobMovesWhileAttacking` at ZERO
tolerance, unlike `mobPositionJump`'s generous 8px. A mob that flinches must not
move; that is not a tolerance question.

**The other two claims from the same report, measured against the reference:**

- *"A mob only ever uses one attack mode"* — **the opposite of the reference.**
  `MobAttack.cpp:90` is `available[randomizer.next_int(available.size())]`, a
  random pick among the attacks that reach. Verified with a mob that DECLARES
  three: `2400212` played `attack2` 760 frames, `attack1` 424, `attack3` 416 —
  all three. Attack-node counts over 150 sampled mobs:
  `{0: 80, 1: 37, 2: 16, 3: 12, 4: 3, 5: 1, 8: 1}`.
- *"It attacks from very far away"* — the reach is the attack's own. `9500302`'s
  declares `radius 200`, and the attack began at gaps of 0.8 / 60 / 47 / 111px
  depending on where the mob had walked, all inside 200, and it did **not** walk
  in first when the reach already covered the gap. `MobAttack.cpp:62-78` builds
  `available` from `geometry.reaches(...)` and nothing else decides distance.

**Also fixed while here:** the audit suite's own armed/unarmed calibration
FLAKED — six swings landed six damage numbers on one run and none on the next,
same seed and same map. A gate that flakes is the failure this project has been
fighting all evening, so it now retries with ten swings and up to four
approaches. Three consecutive runs, identical, BAD=0.
