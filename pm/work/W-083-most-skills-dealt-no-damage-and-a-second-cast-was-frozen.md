---
id: W-083
title: Most skills dealt no damage, and a second cast of the same skill froze the effect
state: done
priority: 99
owner: 
opened: 2026-10-17
closed: 2026-10-17
depends: []
refs: SkillCastService.cs MultiStrike; MapSimSessionStore.cs ApplySkillHits and CastSkill; livewalk.js paint; MapleBench/tools/browser-verify.mjs
blocked_on: 
evidence: |
  before: 66 castable skills, 3 of them could damage (MultiStrike false for exactly
          1121008 / 1001005 / 1311012, and ApplySkillHits opened with that guard)
  after:  skill 11111006 (not one of the three) -> mob 1000 -> 900
  before: casting the same skill twice left skillFxBorn on the first cast's clock
  after:  castSeq 1 -> 2 and the second cast animated and hit (mob 900 -> 800)
  per-frame: 9 distinct drawn positions over 52 draws, 9 frame origins in the
          archive, zero draws using an offset that is not a frame's own
workstream: simulate
kind: defect
risk: med
---

## Intent

Four reports, one of them the largest defect found in this project.

1. "The skill does no damage." True for 63 of the 66 castable skills.
2. "After casting once, the second cast shows no animation."
3. "The x/y origin is wrong" and "the per-frame offset problem is still there" --
   which the judge now checks frame by frame and cannot reproduce on 1121008.

## Root causes

**1. `SkillCastService.MultiStrike` was gating damage.**

```csharp
public static bool MultiStrike(string skillId) =>
    skillId is not ("1121008" or "1001005" or "1311012");
```

and `ApplySkillHits` opened with `if (plan.MultiStrike) return;`. Three ids in
the whole archive could damage. Every other skill played a complete animation,
passed the box test, and changed no hit point -- which reads as a broken
collision box, so ADR-0005 puts it squarely in scope.

MultiStrike selects between six collision sprites over the cast's timeline and
one. This port resolves a whole cast on one frame, so the distinction cannot
mean anything here; it only had the power to switch damage off. Guard removed.

**2. `skillFxBorn` was keyed on the skill ID, which is not a cast's identity.**

`livewalk.js` reset the animation clock when `state.skillId` CHANGED, so casting
the same skill twice changed nothing and the second animation started wherever
the first cast's clock happened to be. `session.SkillCastSeq` is now incremented
per cast and carried on the frame; the client resets on that.

**3 and 4 could not be reproduced.** The judge now records every `drawImage` of
the effect for the whole cast and checks each against the archive's per-frame
origin set: 9 distinct positions, 52 draws, 9 origins, zero strays, and the
animation advances from (-30,-23) to (-133,-107). The per-frame offset is
correct for 1121008.

## Result

`browser-verify` grew from 12 to 14 assertions and is green 5 runs in a row.
The gate is unchanged at one red suite (`ball-art`, W-070).

The judge's own two bugs were fixed in the same pass, and both had been hiding
in plain sight:

- it read the cast's state AFTER the pose ended, so it asserted on the frame
  after the cast;
- it took the map from `sim.visibleMaps()`, which is one 200-row page out of
  5,366 maps -- so whether the target was found depended on where it fell in
  the archive's order, and the same harness passed four runs then failed three.