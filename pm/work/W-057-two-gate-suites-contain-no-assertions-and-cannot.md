---
id: W-057
title: Two gate suites contain no assertions and cannot fail
state: done
priority: 60
workstream: infra
kind: audit
risk: med
opened: 2026-10-16
closed: 2026-10-16
depends: []
refs: skill-test.ps1; npc-label-check.ps1; pm/GATE.md:64-83; pm/RULES.md R-13; pm/TRAPS.md T-2
blocked_on: 
evidence: BOTH suites assert now and both were made to fail on purpose. skill-test.ps1 (W-062): 19 assertions + VERDICT line + -ForceFail proves >>> RED. npc-label-check.ps1 (W-069): 3 assertions over NPC labels, and the 'no region holds an NPC' case that used to end the loop and pass silently now reports >>> RED (measured with -Map 999999999.img, exit 1). pm/SPEC.yaml carries asserts: per suite; pm.py coverage prints 'suites that cannot report red: (none)'.
verify:
  - cmd: powershell -NoProfile -File $S/npc-label-check.ps1 -Port 5591 -Map 000000002.img
    expect: 3 assertions, 0 failed
  - cmd: powershell -NoProfile -File $S/skill-test.ps1
    expect: 19 assertions, 0 failed
---
## Intent

Two suites in `gate.ps1` printed observations and asserted nothing, so the gate
reported them `ok` unconditionally. A suite that cannot fail is worse than no
suite: it occupies a line in the gate and teaches everyone that the gate is
covering something.

## Reference

`pm/GATE.md:64-83` (red is an explicit marker, nothing else) · `pm/RULES.md` R-13 ·
`pm/TRAPS.md` T-2 (a regex is not a verdict). No product code is involved; both
suites are harness.

## Approach

1. `skill-test.ps1` — W-062. Every printed tail became a `Check()`; the block ends
   with a `VERDICT:` line `gate.ps1` already greps for; `-ForceFail` proves the
   failure path emits `>>> RED`.
2. `npc-label-check.ps1` — W-069. Three assertions over NPC labels, plus the
   control that the map actually contained NPCs.
3. `pm/SPEC.yaml` — every suite now declares `asserts: true|false`, and
   `pm.py coverage` names the ones that cannot report red. The list is now empty,
   so a future suite that forgets cannot pass unnoticed.

## Acceptance

```
powershell -NoProfile -File %LOCALAPPDATA%\hermes\cache\scratch\skill-test.ps1
# EXPECT: VERDICT: skill-test 19 assertions, 0 failed
powershell ...\skill-test.ps1 -ForceFail
# EXPECT: ">>> RED:" and exit 1

powershell -NoProfile -File %LOCALAPPDATA%\hermes\cache\scratch\npc-label-check.ps1 -Port 5591 -Map 000000002.img
# EXPECT: VERDICT: npc-label-check 3 assertions, 0 failed
powershell ...\npc-label-check.ps1 -Port 5591 -Map '999999999.img'
# EXPECT: ">>> RED: npc-label-check -- a map with NPCs was actually examined" and exit 1
#   -- the run verified nothing and used to pass

python tools/pm.py coverage
# EXPECT: "suites that cannot report red: (none)"
```

## Result

**Both suites assert now, and both have been made to fail on purpose.**

- `skill-test.ps1`: 19 assertions; `-ForceFail` emits `>>> RED` and exits 1.
- `npc-label-check.ps1`: 3 assertions, green 3/3. It also had a *third* failure
  mode this item did not name: the loop `break`s on the first region holding NPCs
  and `continue`s past the others, so a run where no region holds an NPC printed
  nothing, asserted nothing, and **passed**. Measured with `-Map 999999999.img`:
  `FAIL a map with NPCs was actually examined`, `>>> RED`, exit 1.

The regression that made it possible is now structural rather than a convention:
`pm/SPEC.yaml` gives every suite an `asserts:` flag, `pm.py doctor` cross-checks
the registry against `gate.ps1`, and `pm.py coverage` prints

```
suites that cannot report red: (none)
```

so the next suite that forgets has to be declared as `asserts: false` on purpose,
in the one file that says what that means.