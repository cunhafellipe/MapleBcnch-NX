---
id: W-102
title: sim-health's calibration asserts that SOME damage number appeared, not that the mob it walked up to was hit
state: todo
priority: 70
owner: 
opened: 2026-10-24
closed: 
depends: []
refs: [tools/verify/suites/simhealth-verify.py:103-135; tools/verify/suites/simhealth-verify.py:86-99]
blocked_on: 
evidence: 
---
## Intent

`sim-health` is green today, but part of what it asserts is not yet a property
of the TREE. Its calibration walks the character up to a mob and then asserts
that a damage number appeared — any damage number, on any mob. Whether the
character's swing connects depends on where it stopped, and `close_in` stops
within **10px** of the mob. Ten pixels is inside or outside the mob's hit box
depending on that mob's animation, so the verdict can turn on a pixel rather
than on the game.

This item is NOT about a red gate. It is about a measurement that would report
a defect that is not there — or miss one that is — the moment the world state
shifts under it. W-101 is the same family of disease and was found the same
way: a suite that reddens for a reason that is not the tree's.

## Reference

- `tools/verify/suites/simhealth-verify.py:86-99` — `close_in` picks the nearest
  mob by position (this part is right) and stops when `abs(dx) < gap`, `gap` = 10.
- `tools/verify/suites/simhealth-verify.py:111` — `hits += [d.get("value") for d in (fr.get("damage") or [])]`: every damage event in the session is collected, with no filter on WHICH mob took it.
- `tools/verify/suites/simhealth-verify.py:123-135` — the armed half of the calibration collects `hits2` the same way.
- The judgement is `armed produced damage while unarmed produced none`, so it
  turns on "a damage number appeared", not on "the mob I walked up to was hit".
- The shape of the fix is the one W-101 used: `MapSimDamageNumber` carries the
  hit's `X`/`Y` (`MapleBench/Services/Simulate/MapSimSessionStore.cs:142-153`),
  and this map carries eleven mobs with the id 2220100 — so a damage event
  names the animal, an id does not.

## Approach

1. Do not change the tree. Measure first: run the calibration many times and
   record, for each run, which mob the character stopped next to, where it
   stopped, and which mob the damage event names. If the damage always lands
   on the mob it walked to, the assertion is sound as it stands and this item
   closes with that evidence.
2. If it does not always land there, make the assertion about that mob:
   filter the collected damage events by the hit position, and assert the
   VICTIM's HP dropped (the W-101 shape) rather than that a number appeared.
3. Only if the swing legitimately misses depending on where the character
   stopped, make the calibration try again — walk up and swing again, and fail
   only after N attempts that all missed — so that a one- or two-pixel
   difference stops deciding the verdict.

## Acceptance

```
# 1. the calibration's verdict does not depend on where the character stopped
python tools\verify\suites\simhealth-verify.py
# EXPECT: run it N times (N >= 10) against the same instance; every run prints
#         "BAD=0", exit 0, and the SAME victim id is named every time

# 2. the gate
powershell -NoProfile -ExecutionPolicy Bypass -File tools\verify\gate-loop.ps1
# EXPECT: no RED_SUITE=sim-health
```

## Result

Not started. The measurement in step 1 has not been taken yet; do not close
this on a single green run — W-100 was green in the gate on the same tree and
was red in every earlier run, and the reason turned out to be the gate, not
the suite. The question here is the same one: is the verdict a property of the
tree?
