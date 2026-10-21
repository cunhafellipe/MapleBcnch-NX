---
id: W-067
title: attack-after cries wolf: it calls the CORRECT behaviour wrong and reports BAD=2
state: done
priority: 72
owner: 
opened: 2026-10-16
closed: 2026-10-16
depends: []
refs: [attackafter-verify.py:150-176; pm/work/W-040]
blocked_on: 
evidence: The suite measured the offset from the first frame the probe OBSERVED and compared it to delayMs/STEP_MS, printing 'became testable 7 frames in, not 96.0'. The run's own output said 'created on frame 0 at elapsed=1483ms' - the entry was already 89 frames old before anything saw it. Correct answer is (delay-age)/frame: measured 13.0, observed 13. attackAfter was honoured exactly all along. Second check was self-contradictory (printed 'not testable' then counted BAD because it WAS testable) and is replaced by one invariant checked from the frame data: no frame before the delay was testable. Now BAD=0 and the suite reports its own verdict.
workstream: infra
kind: audit
risk: med
verify:
  - cmd: python $S/attackafter-verify.py
    expect: BAD=0
---
## Intent

One paragraph. What changes for the user when this is done.

## Reference

`path:line` — the exact construct being ported. If there is none, say so
explicitly and mark the item own-design.

## Approach

Ordered steps. Each names a file and a symbol.

## Acceptance

```
python %LOCALAPPDATA%\hermes\cache\scratch\attackafter-verify.py
# EXPECT: "attackAfter is honoured exactly"
# EXPECT: "ok    no frame before the delay was testable"
# EXPECT: BAD=0
#   and the printed arithmetic must agree with itself:
#     "it was already <age>ms old when first observed, so the correct answer is
#      (delay - age) / 16.67 = <N> frames"
#     followed by "first testable frame: <N>"   -- same N, or the suite is wrong again
```

The point of the second clause: the expected offset is computed from the entry's
own age at first observation, so the number the suite prints as a result must
equal the number it derived. A suite that only prints its own output proves
nothing about it.

## Result

Filled in when closed: what actually landed, and the evidence.
