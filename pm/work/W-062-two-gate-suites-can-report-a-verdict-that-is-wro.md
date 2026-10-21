---
id: W-062
title: Two gate suites can report a verdict that is wrong: mob-attack-verify dies mid-measurement, skill-test asserts nothing
state: done
priority: 75
owner: 
opened: 2026-10-16
closed: 2026-10-16
depends: []
refs: mob-attack-verify.ps1:129-148; skill-test.ps1:80-105; pm/RULES.md R-9 R-13; pm/TRAPS.md T-2; 工作室守則 2.9
blocked_on: 
evidence: skill-test.ps1 now carries 19 assertions and a VERDICT: line gate.ps1 reads; VERDICT: skill-test 19 assertions, 0 failed. -ForceFail proves the failure path: >>> RED emitted, exit 1. The first run of the new assertions caught a real defect: the suite walked the 50-HP character into mob 2220100 (info/PADamage=90) and asserted skills on a corpse (probe printed dead=True before the cast); fixed with invulnerable on New-Session plus control 1.0c. mob-attack-verify.ps1: invulnerable per attempt, player HP now printed every attempt, and death vs never-connected split into separate branches; 5 consecutive runs GREEN=5 RED=0 (was 1 red in 5).
workstream: infra
kind: audit
risk: med
verify:
  - cmd: powershell -NoProfile -File $S/skill-test.ps1
    expect: 19 assertions, 0 failed
---
## Intent

Two of the gate's own suites can state something untrue. `skill-test.ps1`
carries no assertion at all — its last four lines end in `<-- MUST be refused`,
which is a comment, not a check, so every skill behaviour is unverified.
`mob-attack-verify.ps1` reports `>>> RED: the mob never connected` for a mob
that connected repeatedly and killed the player: contact damage drains the
50 HP pool during the approach phase, which the script never records, so when
the pool empties mid-measurement the loop breaks on `status.dead` with zero
hits counted. Both are the same defect class as W-057, one level further out:
a measurement whose priming phase consumes the resource being measured.

## Reference

Own-design for the harness itself. The rules it must obey are already written:
`pm/RULES.md` R-9 (calibrate), R-13 (red is an explicit marker),
`pm/TRAPS.md` T-2 (a regex is not a verdict), and 工作室守則 §2.9 (the priming
frame must be inside the observation window) — this item is §2.9 applied to
the *approach* rather than to the *trigger frame*.

## Approach

1. `%LOCALAPPDATA%\hermes\cache\scratch\skill-test.ps1`
   - Replace the four `print` tails with real assertions, each guarded by a
     `Fail()` that records the failure and prints `>>> RED: …`:
     the ladder refusal (:104-105), the unknown-id refusal (:96-97), the empty-id
     refusal (:98-99), and the cast lock (:90-92, second cast 1 frame later must
     deal 0 damage). Section 1's MultiStrike=false cast must deal damage;
     section 2's MultiStrike=true cast must deal none.
   - End with `skill-test: N assertions, M failed` so the block has a verdict
     line for `gate.ps1` to show instead of a tail.
   - The two calibration controls at :26-38 stay, and stay first.
2. `%LOCALAPPDATA%\hermes\cache\scratch\mob-attack-verify.ps1`
   - Record `player hp` at the top of every Phase A attempt and print it, so a
     run that enters Phase B at 30/50 is visibly different from one at 50/50.
   - Turn the character invulnerable for Phase B only
     (`POST /session/{id}/invulnerable`), so the thing under test — pursuit —
     is not measured through the player's remaining HP. Declared in a comment:
     contact damage is already proven by the calibrate/attack-drop suites.
   - Split the verdict: `status.dead` while hits are 0 is **not** "never
     connected"; it is its own message with its own exit, and only a genuine
     zero-hit window prints `>>> RED`.

## Acceptance

```
# 1. skill-test now reports a verdict, and the gate shows it
python tools/pm.py gate
# EXPECT: the skill-test block prints "GREEN? skill-test: N assertions, 0 failed"
#         -- a verdict line, not "(read the tail)"

# 2. the new assertions can actually fail: a deliberately wrong expectation
#    must turn the suite red
powershell -NoProfile -File %LOCALAPPDATA%\hermes\cache\scratch\skill-test.ps1 -ForceFail
# EXPECT: ">>> RED:" appears, and the summary reads "M failed" with M > 0

# 3. mob-attack-verify is no longer flaky -- five consecutive green runs
$ok=0; 1..5 | % { $t = & "$env:LOCALAPPDATA\hermes\cache\scratch\mob-attack-verify.ps1" 2>&1 | Out-String
                 if ($t -notmatch '>>> RED' -and $t -match 'closed the distance') { $ok++ } }
# EXPECT: prints 5   (baseline: 1 red in 5, all from the same death-mid-measurement cause)

# 4. the death and the miss are distinguishable
# EXPECT: no run may print both "the mob never connected" and "died on that hit"
```

## Result

Both suites now carry assertions and the whole gate is green.

### skill-test.ps1 — 19 assertions, and the first run caught a real defect

Every former `print` tail is now a `Check()`, and the block ends with
`VERDICT: skill-test 19 assertions, 0 failed` — `VERDICT` is one of the five
patterns `gate.ps1` greps for, so the block shows a verdict instead of
`(read the tail)`.

The first run of the new assertions **failed, and was right to**:

```
CAST -> action=walk skillId='' skillStance='' damage=0
FAIL  1.1 a MultiStrike=false cast deals damage -- damage entries=0
FAIL  1.3 the pose lasts more than one frame -- only 0 of 80 frames held the pose
```

Probed with `%TEMP%\skill-cast-probe.py`, replicating the suite's own sequence:

```
walked to x=2057.4773 y=-25 facing=1 action=walk onLadder=False dead=True
```

**The character was already dead before the cast.** The target mob 2220100 has
`info/PADamage = 90` and a fresh character has 50 HP, and the walk from spawn to
the target sits inside its contact box for ~140 frames. `CastSkill`'s first gate
is `if (session.Status.Dead) return;` (`MapSimSessionStore.cs:1472`) — **the
product was correct; the suite was walking into a 90-damage mob and blaming the
skill.** The endpoint's own comment already said it: *"a level-1 character
(50 HP) on a mob map is dead within seconds: `info/PADamage` is 90..150 there"*
(`SimulatorEndpoints.cs:305-307`).

Worse, section 2's *"MultiStrike=true deals no damage"* **passed for the wrong
reason**: nothing can damage a corpse. A green that proves nothing is the exact
failure this item exists to remove.

Fixed by making `New-Session` set `invulnerable` (`{"value": true}` — the
endpoint takes `value`, not `invulnerable`), and by adding the control that
would have caught it: `1.0c CONTROL: the character survived the approach`.

### mob-attack-verify.ps1 — the flake is gone, and the verdict can no longer lie

- `invulnerable` is set per attempt, so the approach phase can no longer spend
  the HP pool Phase B measures against.
- Every attempt now prints `player 50/50 after the approach`, so a run that
  enters Phase B weakened is visibly different from one that does not. The red
  run never printed this, which is why it could not diagnose itself.
- "died" and "never connected" are now separate branches. Death with zero
  recorded hits is **INCONCLUSIVE** and prints its own `>>> RED: inconclusive` —
  `gate.ps1` cannot see an exit code (T-2), and a run that proved nothing must
  not read as `ok`. Only a genuine zero-hit window on a living character prints
  `>>> RED: ... the mob never connected`.

**Five consecutive runs, five green** (was 1 red in 5). The mob's closest
approach also tightened from 50-69 px to 20-27 px, which is what the unspent HP
pool buys: the mob now walks the whole way in.

### 2.4 The same suite, later the same day: a second defect in its own mob lookup

The hardened suite then went red in a later gate run —
`VERDICT: skill-test 19 assertions, 1 failed` — on an assertion written in this
very item:

```
FAIL  2.2 a MultiStrike=true cast leaves the mob untouched -- hp 350 ->
```

It located the mob with `|mobs[i].x - seedMob.x| < 3`. **This map carries eleven
instances of 2220100** (measured from the live frame: x = 2091, 2221, 2767, 2553,
2681, 2847, 3043, 2006, 2210, 2451, 2435), so a position-pinned filter matches an
ordinal, not an animal. The mob walked a few pixels, the filter went empty,
`[int]$null` read as 0, and the assertion failed. `knockback-verify.py` carried
the identical trap on the identical map and was fixed as W-064 the same hour.

The assertion was also asking the wrong question: it claimed **"the cast dealt
no damage"** while measuring **"the mob did not walk"**. A mob that walks has
still not been damaged. It now reads hp through a `Nearest` helper that picks by
id and then by distance to the body, so absence from the frame means *destroyed*
and nothing else — which a no-damage cast cannot cause.

Three runs after the fix: `19 assertions, 0 failed` each, with the mob's hp
stable at 350 -> 247 (section 1, damaged) and 350 -> 350 (section 2, untouched).

**This map's duplicate mob ids have now caused three defects, two of them in the
harnesses rather than the product.**

### Verification

```
skill-test.ps1             -> VERDICT: skill-test 19 assertions, 0 failed   (x3)
skill-test.ps1 -ForceFail  -> >>> RED: skill-test -- zz self-test ... ; exit 1
5x mob-attack-verify.ps1   -> GREEN=5  RED=0
python tools/pm.py gate    -> RED SUITES: 0
```
