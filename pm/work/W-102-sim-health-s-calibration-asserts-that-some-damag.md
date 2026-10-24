---
id: W-102
title: sim-health's calibration asserts that SOME damage number appeared, not that the mob it walked up to was hit
state: done
priority: 70
owner: 
opened: 2026-10-24
closed: 2026-10-24
depends: []
refs: [tools/verify/suites/simhealth-verify.py:103-135; tools/verify/suites/simhealth-verify.py:86-99]
blocked_on: 
evidence: "MEASURED 2026-10-24, N=12 runs of tools/verify/suites/simhealth-verify.py against 127.0.0.1:5591 (964.6 s): the verdict does NOT depend on which mob was hit and does NOT depend on where the character stopped. 12/12 runs: BAD=0, exit 0, one verdict line, the unarmed half produced ZERO damage numbers, and the damage always landed on the very mob the character walked up to (the instance at x=2091, y=-25, the 2nd of the eleven by x), hp 1000->900. The one run whose positions differ is the SAME animal displaced 6.29 px by the knockback of the first hit. Two stop distances were measured (9.2537 px in 11 runs, 6.1545 px in 1) and the verdict did not follow either. Gate acceptance item 2: the lead's own gate run on 2026-10-24 21:44 printed '=== sim-health === ok / GREEN? BAD=0' and 'RED SUITES: 1 / RED_SUITE=attack-after' -- sim-health was NOT red. Report: C:\mbtest2\w102\report.md (44314 B, sha256 A5C8A7A43B8ABD302B370923B4ECAAF03E6730DB085F1E8C2EC47D9DCEE714F4)"
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

**Closed 2026-10-24, on step 1's evidence: the assertion is SOUND as it
stands.** Step 1 said "If the damage always lands on the mob it walked to,
the assertion is sound as it stands and this item closes with that
evidence." It does land there. No change was made to
`tools/verify/suites/simhealth-verify.py`.

Measured, N = 12 runs against `127.0.0.1:5591`, 964.6 s:

```
verdict lines   : {"calibration reproduced: unarmed cannot swing, armed can": 12}
exit codes      : {"0": 12}
BAD=0           : 12 of 12
unarmed damage numbers, summed : 0
armed damage numbers, summed   : 118
stop positions  : {(2091,-25): 11, (2078.4,-24.7): 1}
victim positions (the mob the damage names): {(2091,-25): 11, (2084.7,-24.6): 1}
victim ordinal among the eleven by x : {2: 11}
distance (victim x - stop mob x) per run : [0,0,0,6.29,0,0,0,0,0,0,0,0]
runs where the victim is within 20px of the stop mob : 12 of 12
```

So the answer to the question this item asked is: **it does not matter which
mob was hit, and it does not matter where the character stopped.** The
damage always landed on the mob the character walked up to, its HP fell
1000 -> 900, the unarmed half produced no damage number at all, and the
verdict was the same every time.

The one run whose victim position differs from its stop position is the
**same animal**, displaced 6.29 px by the knockback of the first hit: its HP
fell in one chain 1000 -> 900 -> 800 -> ... -> 200 while its x walked with
the knockback (2084.70, 2082.53, 2084.70, 2086.86, 2089.03, 2091.20,
2093.36, 2095.53).

Acceptance item 2 (the gate) is satisfied by the lead's own gate run on
2026-10-24 21:44, against a fresh instance built from the working tree:
`=== sim-health === ok`, `GREEN? BAD=0`, and the gate's only red was
`RED_SUITE=attack-after`.

### Two traps measured, for whoever designs a "which mob" filter

1. **The victim moves.** Up to 6.29 px between the stop frame and the hit
   frame. A position filter needs a tolerance, not equality.
2. **The hit X/Y is the victim's HEAD, not its body.** The hit Y was -75.6 /
   -78.6 while the body's y was -24.6 -- about 51 px above it
   (`geometry.HeadAt`). A filter that matches the hit point against the
   mobs' body positions would never match anything.

### Cross-check on this item's own list of mob positions

The eleven instances of `2220100` are at the x this item lists, but **only
two of them are on the character's floor**: x = 2091 and x = 2221, both at
y = -25. The other nine are at y = -145, -535 or -805, which is why the
suite's own `|m.y - f.y| <= 100` filter leaves only the two. "The mob it
walks up to" is therefore the instance at x = 2091, and the walk from
x = 1710 stops 9.2537 px short of it.

### The residual, stated honestly

In 11 of 12 runs the stop was **9.2537 px** from the mob -- 0.75 px inside
the suite's 10 px gate -- and the character's step is about 2.25 px
(measured in a recorded walk: x goes 1710 -> 1711.4995 -> 1712.7491), so the
stop distance is decided inside a ~2.25 px window. The swing connected at
both distances that occurred (6.15 px and 9.25 px). **The swing's REACH was
not measured**, so the margin on the far side is UNKNOWN: the verdict is a
property of the tree for every stop distance that was actually sampled,
and nothing more is claimed.

### What was NOT measured

- Stop distances other than 9.2537 px and 6.1545 px -- only two occurred,
  so the sampled range is narrow.
- Any other instance, map or server build: only `127.0.0.1:5591` and this
  map.
- The swing's reach.
