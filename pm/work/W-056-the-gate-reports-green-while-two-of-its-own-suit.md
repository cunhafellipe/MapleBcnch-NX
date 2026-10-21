---
id: W-056
title: The gate reports green while two of its own suites fail
state: done
priority: 70
owner: 
opened: 2026-10-16
closed: 2026-10-16
depends: []
refs: [GATE.md, TRAPS.md T-2, Keyboard.cpp:25-43, Keyboard.cpp:78-81, livewalk.js:1024]
blocked_on: 
evidence: 4 suites made able to report red. sim-skills-check 23/1 -> 27/0 (was FAILING silently; walk-key set now DERIVED from keyFor() as its comment always claimed, plus 3 assertions pinning Keyboard.cpp:78-81; stale livewalk.js skillFor comment corrected). mobfly-verify KeyError fixed - cached 'have' covered only the 7-mob revalidation sample while 24 were judged; 18 of 24 now read live, BAD=0. mob-attack-verify + ladder-all emit >>> RED on their existing failure branches (proved: old 'NO DAMAGE' string scores gate=ok, new '>>> RED' scores RED). GATE.md marker table corrected. Gate: RED SUITES: 0, 25 suites.
---
## Intent

`python tools/pm.py gate` printed `RED SUITES: 0` on a tree where
`sim-skills-check` was running **23 passed, 1 failed** and `mob-fly-verify` was
**crashing with a `KeyError` before it reached its own verdict**. Both were
scored `ok`.

That matters more than the two bugs themselves: every other work item closed
today cited this gate as its evidence, and the gate was not looking at two of
the twenty-five suites at all. A gate that reports green over a failing suite
protects nothing — it is `TRAPS.md` T-2's other direction. T-2 warns that a
gate which cries wolf gets ignored; this one was crying *wolf-free*.

## Reference

This is own-design — it is the project's own gate, not a client behaviour.

The rule it must satisfy is the project's own, `pm/GATE.md`:

> Red is an explicit marker a suite prints. Nothing else.
> If you add a suite, it must print its own verdict. **Do not add a pattern to
> `gate.ps1`.**

`gate.ps1:14` honours that by looking for exactly three strings:
`>>> RED`, `NO damage number`, and `BAD=[1-9]`. So the defect is never in
`gate.ps1` — it is in a suite that fails without printing one of them.

## The blind spots, measured

Read off a real `gate.ps1` run on 2026-10-16, then each suite run alone.

| Suite | What it does on failure | Gate sees it? |
|---|---|---|
| `sim-skills-check.mjs` | prints `FAIL <name>` + `N failed` | **NO** — neither is a marker |
| `mobfly-verify.py` | `KeyError`, dies before `BAD=` at line 160 | **NO** — crashed, so printed nothing |
| `mob-attack-verify.ps1` | prints `NO DAMAGE over 400 frames` | **NO** — gate looks for `NO damage number`; .NET `String.Contains` is case-sensitive, so `NO DAMAGE` does not match |
| `ladder-all.ps1` | prints `SOME LADDERS FAILED` | **NO** — not a marker |
| `skill-test.ps1` | prints observations, **has no assertion at all** | **NO** — it cannot fail |
| `npc-label-check.ps1` | prints counts, **has no assertion at all** | **NO** — it cannot fail |

Two of these were not merely blind — they were **actively failing or dead** while
the gate said `ok`.

## Two real defects the blind spot was hiding

### 1. `sim-skills-check` — a stale expectation, and a stale doc comment

MEASURED: `check('no walk key can be bound', shadowed, [])` returned
`["a","A","d","D","w","W","s","S"]`. The suite asserted that WASD must be
un-bindable; the product had deliberately stopped binding them.

The product is right and the test was wrong. `clean-client/src/IO/Input/Keyboard.cpp`:

- `:25-43` — the `Keytable` lists `InputKey::A, S, D` (line 32) and `W` (line 30).
- `:78-81` — the only `keymap[InputKey::…] = Mapping(…, KeyAction::…)` lines in
  the file bind `LEFT`, `RIGHT`, `UP`, `DOWN`. No letter is bound to an action.

So reading the Keytable as the bindings is the mistake, and `keyFor()` no longer
makes it. Per `PARITY.md` the C++ wins, so the product behaviour stands.

**But the test's own comment was false too.** It claimed *"The list is read OFF
the prototype's own `keyFor` rather than restated"* — while the code below it
restated a literal `WALK_KEYS`. That is how the list went stale behind a comment
that promised it could not. The same drift reached `livewalk.js`'s `skillFor`
doc comment, which still said `keyFor()` *"maps WASD, the arrows, space and Z
(livewalk.js:783-800)"* — both halves false, and `783-800` is not where
`keyFor()` lives.

### 2. `mobfly-verify.py` — a cache that silently narrowed the measurement

The cached path (`:61-78`) builds `have` from **only the re-validation sample** —
at most 7 ids. But the simulation loop judges up to 24 of the 70 mobs in
`fly_only` (`:112`, `:128`), so `have[mob_id]` raised `KeyError: '3230304'` and
the suite died at `:138`, never reaching its `BAD=` line at `:160`.

MEASURED after the fix: **18 of the 24** judged mobs were not in the
re-validation sample. The suite was not merely crashing — before the crash it
was judging a set almost entirely composed of ids it had no data for.

## Approach

1. `sim-skills-check.mjs` — derive `WALK_KEYS` from `body.keyFor()` over a
   candidate set, so the block does what its comment always claimed. Keep the
   intent, do not delete the assertion (`R-8`).
2. `sim-skills-check.mjs` — add three assertions that pin the facts the
   derivation rests on: the four arrows are owned, WASD is **not**, plain letters
   stay free. A future `keyFor()` edit must confront them.
3. `sim-skills-check.mjs` — print `>>> RED (N failed)` when `fail > 0`, the
   marker `gate.ps1` already reads.
4. `mobfly-verify.py` — fill `have` for every mob the loop is about to judge,
   and **print how many came off the wire rather than out of the cache**.
5. `mob-attack-verify.ps1` / `ladder-all.ps1` — emit `>>> RED` on the existing
   failure branches. No new pattern in `gate.ps1`.
6. `livewalk.js` — correct the `skillFor` doc comment.

## Acceptance

```
# the two suites that were failing or dead
node MapleBench/tools/sim-skills-check.mjs
#   the walk owns: ["ArrowLeft","ArrowRight","ArrowUp","ArrowDown"," ","Spacebar","Control","z","Z"]
#   PASS  the walk does NOT own WASD (Keyboard.cpp:78-81 binds arrows only)
#   27 passed, 0 failed
python %SCRATCH%\mobfly-verify.py
#   18 of the 24 judged mobs were not in the re-validation sample; read from the live server
#   BAD=0

# the two that could not report red at all
powershell -File %SCRATCH%\gate.ps1
#   ladder-all   -> ALL LADDERS PASS   (and would print >>> RED on a failure)
#   mob-attack   -> per-hit lines      (and would print >>> RED on zero hits)
#   =========== RED SUITES: 0 ===========
```

## Result

Four suites made able to report red; one stale doc comment corrected.

**The `mob-attack-verify` blind spot, demonstrated rather than argued.** Feeding
its own failure line through `gate.ps1`'s exact detection logic:

```
Contains('NO damage number') = False
Contains('>>> RED')          = True
gate verdict for OLD output: ok  <-- BLIND
gate verdict for NEW output: RED (>>> RED)
```

So the case-sensitivity claim is not a reading of the docs — the old string is
demonstrably invisible to the gate and the new one is demonstrably visible.

**Cross-boundary check, which found nothing.** `livewalk.js`'s `keyFor()` and
`MapSimSessionStore.MovementKeys` are two halves of one rule, and one side had
just gone stale. Checked rather than assumed:
`MapSimSessionStore.cs:2066-2067` is `new HashSet<…>(StringComparer.OrdinalIgnoreCase) { "Z" }`
— arrows are not letters, so only `Z` needs excluding server-side, and its
comment at `:2045-2050` already records that WASD was removed and why. The two
halves agree. The staleness was confined to `sim-skills-check.mjs`'s restated
`WALK_KEYS` and to the one `skillFor` doc comment; both are fixed.

**Left open, deliberately.** `skill-test.ps1` and `npc-label-check.ps1` contain
no assertions at all — they print observations and cannot fail by construction.
Giving them real assertions is new work, not a marker fix, so it is **W-057**
rather than a quiet widening of this item (`R-7`).

**Also open, and not explained.** `ball-art` reported `BAD=1` on its first run
inside `gate.ps1` (as suite 18 of 25) and `BAD=0` on every one of six
subsequent solo runs and on a full gate re-run — 1 red in 7. No root cause was
found, so no fix is claimed. Recorded rather than smoothed over, because a
suite that is red one time in seven is not a suite that has been verified.