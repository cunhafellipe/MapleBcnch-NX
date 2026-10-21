---
id: W-051
title: Not every mob acquires the player
state: done
priority: 5
owner:
opened: 2026-10-16
closed: 2026-10-16
depends: []
refs: "MobAttack.cpp:79-87; Mob.cpp:1802"
blocked_on:
evidence: "no defect: 1156 of 2053 mobs carry no attack* node and cannot enter MobAttack.cpp:88's animation branch; now reported per mob by GET /api/simulate/mob-attacks, the bestiary row, and the mobWithoutAttackArt audit finding"
---

## Intent

Reported, alongside the attack work: not every mob attacks.

## Reference

`MobAttack.cpp:79-87`:

```
if (first_attack && (std::abs(dx) / 10 + std::abs(dy) / 3 <= 40 || !available.empty()))
    aggro = true;
```

## Approach

1. Separate "acquisition" from "has anything to swing".
2. Measure the second across the whole archive, not a sample.
3. Report it on the product if the answer is data rather than code.

## Acceptance

1. `python ...\attackdrop-verify.py  # attack coverage, BAD=0`
2. `GET /api/simulate/debug/health  # mobWithoutAttackArt names the ones that cannot`
3. `# mob-attack-verify / first-attack-verify stay green`

## Result

**No defect in acquisition. The reported symptom is the archive, and it is now
reported on screen.**

MEASURED across the full population, not a sample: **1156 of 2053 mobs carry no
`attack*` node at all**. 522 carry one, 192 two, 125 three, down to a single mob
with eight. A mob with no attack node cannot enter `MobAttack.cpp:88`'s animation
branch — `available` is built by `geometry.reaches(...)` over an empty list — so
it never swings. It still deals contact damage, which is correct:
`MobAttack.cpp:88` plus `Stage.cpp:809-818` doing exactly what the reference
does.

So "not every mob attacks" is two facts that used to look like one:

1. 56% of mobs have nothing to attack WITH.
2. Of those that do, they only swing when `firstAttack` is set or they have been
   hit.

**Both are now visible without a probe.** `GET /api/simulate/mob-attacks` says
it per mob, the bestiary panel carries an `Attacks:` row that states the empty
case verbatim ("none in the archive — this mob only deals contact damage"), and
the standing audit finding `mobWithoutAttackArt` names them per session.

**Acquisition itself is faithful** to `MobAttack.cpp:79-81`, including the
distance weighting — `|dx|/10 + |dy|/3 <= 40`, where x is divided by 10 and y
by 3, which makes the reach four times wider than it is tall because a floor is
never walked across. `first-attack-verify.ps1` covers both halves: a mob WITH
`firstAttack` attacks unprovoked; a mob WITHOUT it never walks toward the player.