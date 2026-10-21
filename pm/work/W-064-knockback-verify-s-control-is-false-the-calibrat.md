---
id: W-064
title: knockback-verify's control is false: the calibration mob patrols, so 'an unhit mob does not move' is not true
state: done
priority: 78
owner: 
opened: 2026-10-16
closed: 2026-10-16
depends: []
refs: 
blocked_on: 
evidence: knockback-verify.py selected a mob by proximity in setup then re-found it by mobId while measuring; the test map carries 11 instances of 2220100 (x=2091,2221,2767,2553,2681,2847,3043,2006,2210,2451,2435), so the control compared two different mobs and reported 194.850px 'drift' - impossible in one frame at MaxWalkSpeed 2.5px/frame. Patrol and aggro-chase hypotheses were measured and rejected (0.000px idle movement either way). Now pinned by list index, the pattern mob-attack-verify.ps1 already uses. 3/3 standalone and BAD=0 in the failing gate context; recoil |impulse| = 2.167 = EXPECT_PX with exactly one impulse.
workstream: infra
kind: audit
risk: med
verify:
  - cmd: python $S/knockback-verify.py
    expect: BAD=0
---
## Intent

`knockback-verify.py` went red in a gate run and green in every standalone run.
The suite selects a mob by **proximity** during setup and then re-identifies it
by **mob id** while measuring — and this map carries eleven instances of that id.
The control was therefore comparing two different animals and reporting the
distance between them as drift.

## Reference

Own-design for the harness. The pattern it must use already exists in this
project's own harness family: `mob-attack-verify.ps1` pins `indexes[0]` and
indexes the frame's mob list, and has never had this failure.

## Approach

1. `new()` returns the **list index** of the mob it approached, not the mob.
2. The control and the recoil loop both read `fr["mobs"][idx]` instead of
   `[x for x in fr["mobs"] if x["mobId"] == MOB][0]`.

## Acceptance

```
python %LOCALAPPDATA%\hermes\cache\scratch\knockback-verify.py
# EXPECT: BAD=0, and the line "instance #0 at x=2091.00, ... drift: 0.000px"

# run after suites 1..10 of gate.ps1, which is the context that produced BAD=1
# EXPECT: BAD=0
```

## Result

**The recoil was never wrong.** In the failing run all three recoil assertions
passed (`+2.167px`, `knockbackFacing=-1`, exactly one impulse); the red came from
the calibration line above it.

The measurement that identified the cause:

```
=== calibration: an UNHIT mob must not drift ===
  mob x=2221.00, largest single-frame drift over 40 idle frames: 194.850px
```

194.85px **in one frame** is not movement — `MaxWalkSpeed` is 2.5px/frame. Then,
from the live frame:

```
frame 0: 11 instance(s) of 2220100 ->
  (2091,-25) (2221,-25) (2767,-805) (2553,-145) (2681,-145)
  (2847,-145) (3043,-145) (2006,-535) (2210,-535) (2451,-535) (2435,-805)
```

`x=2221` is the **second** instance. The control read `ms[0]` — index 0, the one
at x=2091 — and compared it against a baseline taken from whichever instance the
proximity walk had reached. Which instance the walk landed on decided green or
red, which is exactly why it passed three times alone and failed inside the gate.

Two hypotheses were checked and **rejected** before this one, rather than
assumed:

- *"the mob patrols"* — measured 0.000px of idle movement over 40 frames, both
  with and without contact. It does not patrol.
- *"contact damage sets aggro and the mob chases"* — measured with the walk into
  contact: still 0.000px, and `knockbackPx` 0.000 throughout. With
  `invulnerable=False` the player ends at 0/50, so the contact damage **is**
  happening — it simply does not move this mob.

### Verification

```
knockback-verify.py standalone x3   -> BAD=0, BAD=0, BAD=0   (was 1 red in 5 in gate context)
knockback-verify.py after suites 1..10 (the failing context) -> BAD=0
every run: "instance #0 at x=2091.00, drift: 0.000px"  and  |impulse| = 2.167 = EXPECT_PX, 1 impulse
```
