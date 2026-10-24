---
id: W-103
title: first-attack-verify forbids the plain mob from MOVING, but the product's plain mob patrols its foothold chain
state: done
priority: 78
owner: 
opened: 2026-10-24
closed: 2026-10-24
depends: []
refs: [tools/verify/suites/first-attack-verify.ps1:95-117; MapleBench/Api/SimulatorEndpoints.cs:222-227]
blocked_on: 
evidence: "reproduced standalone 1 in 3 runs; the plain 2220100 moved 1666->2068 and 1662->1501 over 400 frames. FIXED 2026-10-24: the suite now asserts NO PURSUIT via a three-way conjunction (gapN <= 40 px AND tailMax <= 40 px AND closureFrac >= 0.10) instead of forbidding movement; 10/10 runs exit 0 with ONE distinct verdict string (148.9 s); offline, the same discriminator over the 9 recorded traces gives 6 plain = PURSUIT False and 3 firstAttack = PURSUIT True, zero overlap; the check fires every time (the firstAttack mob gives up 60-80.8 percent of its starting gap, threshold is 0.10). Report: C:\mbtest2\w103\report.md (18985 B, sha256 d7afb93b112d50075f8e030e60c31108e2d1166f99817dc304b022cd4b9c2ff4)"
---
## Intent

The gate said `first-attack-verify` was red with

> it moved 62.981 px toward a player it was never told about

and the suite is red roughly **one run in three**, on its own, against a tree
that is not changing. The tree is fine: the plain mob **patrols its foothold
chain**, which is what the product documents, and the suite's stage 1 forbids
the mob from moving AT ALL. A suite that reports a defect that is not there
costs the same as one that misses a defect that is: both make the verdict
untrustworthy.

## Reference

- `tools/verify/suites/first-attack-verify.ps1:114-117` — the assertion:
  `if ([Math]::Abs([double]$m.x - $startX) -lt 1) { "PASS: it did not walk toward the player" }`
  else `">>> RED: it moved X px toward a player it was never told about"`.
  Two things are wrong with it. It forbids **any** movement, and it claims
  "toward the player" while never checking a direction.
- `MapleBench/Api/SimulatorEndpoints.cs:222-227` — the product's own words:
  "Mobs and NPCs patrol their own foothold chain. Seeded, so a map's answer
  is reproducible."
- `MapleBench/Api/SimulatorEndpoints.cs` (the summon endpoint) — "N mobs at a
  **random x within 100px** of the character", so the starting position of the
  mob under test is a random draw.

## Approach

1. **Measure the plain mob's behaviour** (done, 2026-10-24). Summoned
   `2220100` (no `firstAttack`) and stepped 400 frames, three times, with the
   player standing at x=1710:

   | run | mob x start | mob x end | min distance to the player | max distance |
   |---|---|---|---|---|
   | 1 | 1666 | 2068.17 | **0.1 px** | 358.2 |
   | 2 | 1662 | 1501.55 | **0 px** | 208.5 |
   | 3 | 1736 | 1736 (did not move) | 26 px | 26 px |

   The mob walks its chain — in run 1 it walked 400 px **past** the player and
   away again. That is a patrol, not a pursuit: a pursuit converges and stays.
   The random spawn decides whether the chain the mob lands on is long enough
   to move at all, which is why the failure comes and goes.

2. **Decide what the assertion has to be**, because "it did not move" and
   "it did not move toward the player" are both wrong here: a patrol crosses
   the player by luck (run 1 reached 0.1 px), so neither "X did not change"
   nor "the distance did not decrease" can be the property.
   The property the item actually wants is **no pursuit**: the mob must not
   CONVERGE on the player and stay. That needs a measurement that separates
   "walked past" from "walked up to and stayed", and it needs the random spawn
   out of the way — the same map, the same mob, the same window, and a
   comparison against the mob that DOES have `firstAttack` (stage 2 already
   proves that one attacks).

3. **Do not let the contact-damage path stand in for an attack.** The map's
   own mob is within contact range of the character and hurts it
   (`Stage.cpp:809`, and the suite says so at :89-90) — a plain mob that
   happens to walk into the player can deal contact damage, so "the plain mob
   dealt no damage" is not a clean assertion either. The discriminator has to
   be the pursuit itself.

## Acceptance

```
# 1. the suite is not flaky any more
for ($i=0; $i -lt 10; $i++) { powershell -NoProfile -ExecutionPolicy Bypass -File tools\verify\suites\first-attack-verify.ps1 }
# EXPECT: 10 runs, 10 x exit 0, and the same verdict every time

# 2. the gate
powershell -NoProfile -ExecutionPolicy Bypass -File tools\verify\gate-loop.ps1
# EXPECT: no RED_SUITE=first-attack-verify
```

## Result

**Closed 2026-10-24.** The tree was never broken: the plain mob patrols, the
spawn is random by design, and the old assertion forbade movement without
checking direction. The suite now measures **no pursuit** instead.

The discriminator is a three-way conjunction, and each clause kills a
different false positive:

```
pursuit  <=>  gapN <= 40 px  AND  tailMax <= 40 px  AND  closureFrac >= 0.10
```

- "it ended up close" alone is false: a patrolling mob can stand at 11 px
  for all 400 frames.
- "the gap got smaller" alone is false: a patrolling mob can walk THROUGH
  the player (44 -> 0.1 -> 358 px).
- "it moved" alone is false: a patrolling mob can walk 529 px AWAY
  (closure -2784%).

Three further repairs to the old assertion:

1. It forbade ANY movement; the plain mob patrols its foothold chain (the
   product's own words, `MapleBench/Api/SimulatorEndpoints.cs:222-227`).
2. It said "toward the player" and never checked a direction.
3. Its 400-frame loop broke out as soon as `gap <= 2` -- that is, it stopped
   measuring at the moment the mob arrived. The window now runs to the end.

The damage channel is NOT used as the discriminator: a plain mob standing at
11 px dealt 463 damage (the map's own mob is within contact range --
`Stage.cpp:809`), so "the plain mob dealt no damage" is not a clean
assertion.

The spawn is a random draw within 100 px of the character
(`SimulatorEndpoints.cs:451-455`; the endpoint accepts no position), so a
draw that lands INSIDE the engagement band cannot show "it gave up gap".
Such a draw is discarded and re-drawn (at most 8 times, and the output says
"drawing again") -- that is the step that makes the measurement a property
of the TREE rather than of a lottery.

Measured:

- `tools/verify/suites/first-attack-verify.ps1`, 10 consecutive runs:
  10 x exit 0, **one distinct verdict string**, 148.9 s.
- The same discriminator applied offline to the recorded traces
  (`C:\mbtest2\w103\plain-3x400.json`, `fa-3x400.json`, plus the three rows
  in the table above): **6 plain traces -> PURSUIT False, 3 firstAttack
  traces -> PURSUIT True, zero overlap.**
- The check HAS TEETH: in all 10 runs the mob that carries `firstAttack`
  gave up **60%-80.8%** of its starting gap (threshold 0.10, six times the
  margin) and settled at 18.83-20.41 px with the tail motionless. It fires
  every time.
- Run 7 is the case the old assertion would have got wrong: the plain mob's
  minimum gap was **0.08 px** (it walked straight through the player to
  714.25 px, closure -998.8%), and the new assertion still says "did not
  pursue".

## Not covered

- Only one instance (port 5591), one map, one player position (x=1710).
  No second map and no second player x were tried.
- The discriminator is one-dimensional (x only), which matches the product's
  own description of `firstAttack`.
- The re-draw path was not exercised in the 10 runs (every first draw landed
  at 46-98 px); that code is written but was not observed running.
- **Residual misjudgement risk:** a patrol chain that happens to end the
  window within 40 px of the player, having given up more than 10% of its
  starting gap, and motionless for the tail, would be misjudged as a
  pursuit. It did not occur in 6 recorded traces or 10 live runs, and it
  cannot be proved impossible.
- `pm/SPEC.yaml` still carries `flaky: true` for this suite. It must come
  off together with the measurement that removed it.
