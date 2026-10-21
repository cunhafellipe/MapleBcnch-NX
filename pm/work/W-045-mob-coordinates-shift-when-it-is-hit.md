---
id: W-045
title: Mob coordinates shift when it is hit
state: done
priority: 4
owner: 
opened: 2026-10-16
closed: 2026-10-16
depends: []
refs: [MobPatrol.cs; Mob.cpp apply_damage]
blocked_on: 
evidence: logical coordinates do NOT move: hit frame x 2091.00->2091.00 delta +0.00, y -25.00->-25.00 delta +0.00, largest single-frame movement in a 6-swing run 0.00 px; the visible shift is the HIT POSE'S OWN ORIGIN (2220100 stand origin (27,58) vs hit1 (28,65); 9500302 stand (51,95) vs hit1 (44,91))
---
## Intent

Reported: a mob's coordinates shift when it is hit.

## Reference

None specific — this is a measurement question about this project's own
simulation. The drawing side uses each frame's own `originX/originY`, which is
the reference's `draw_origin`.

## Approach

1. Record the mob's x and y on EVERY frame around a landed hit.
2. If they do not move, the shift is in the DRAWING, so compare each stance
   frame's origin.

## Acceptance

1. `python ...\mobdefect4.py  # the hit frame's dx and dy, armed`
2. `GET /api/simulate/mob-sprite?mobId=` — each action's frame-0 origin

## Result

**The logical coordinates do not move.** Armed, against `2220100` on
`f4/Map/Map1/100000004.img`, six swings with the mob's position sampled on every
frame:

```
mob 2220100 hp=350 at (2091.00, -25.00)
first hit: hp 350 -> 347
  x 2091.00 -> 2091.00    delta +0.00
  y  -25.00 ->  -25.00    delta +0.00
  stance 'stand' -> 'hit1'
largest single-frame movement anywhere in the run: 0.00 px
stances during the run: {'stand': 150, 'hit1': 120, 'move': 6}
```

The hit is real — HP drops, and the pose changes `stand -> hit1` as it should.

**The visible shift is the hit pose's own draw origin**, which differs from
`stand`'s by design (a flinching creature recoils):

| mob | action | frame 0 | origin |
|---|---|---|---|
| 2220100 | stand | 63×58 | (27, 58) |
| 2220100 | **hit1** | 62×65 | **(28, 65)** |
| 9500302 | stand | 109×100 | (51, 95) |
| 9500302 | **hit1** | 109×98 | **(44, 91)** |
| 9500302 | attack1 | 109×100 | (51, 95) |

Drawing at `x - originX, y - originY`, `2220100` is drawn **7px higher and 1px
further left** while it flinches, and snaps back afterwards. That is what a hit
looks like on screen while the position is untouched.

**Nothing was changed for this item**, because the measurement found no defect
in the simulation and the reference draws from the same origin. Whether a 7px
recoil reads as correct or as a glitch is a visual judgement (W-037), and the one
question I cannot answer from here is which mob and which moment — the number is
the same for all of them.

**Left open:** if the user wants the feet planted during a flinch, that is a
deliberate deviation from `draw_origin` and would need its own item and its own
justification, not a quiet change here.
