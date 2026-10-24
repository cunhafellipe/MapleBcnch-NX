---
id: W-100
title: sim-health goes red inside the gate and green on its own: find which of its findings is not a property of the tree
state: done
priority: 76
owner: 
opened: 2026-10-24
closed: 2026-10-24
depends: []
refs: pm/SPEC.yaml sim-health suite entry; tools/verify/suites/simhealth-verify.py
blocked_on: 
evidence: the suite was never red: its own last line said BAD=0 while the gate said BAD=2, because the gate read the sentence 'the shipped judge still reported BAD=2 once' out of the suite's self-explanation. The BAD channel now reads only standalone BAD= lines. sim-health GREEN at 14:30, 14:45 and 14:54; SPEC flaky:true removed with the reason recorded.
---
## Intent

`sim-health` is the one suite the registry already marks as unreliable:
`pm/SPEC.yaml` says *"2026-10-16 measured: BAD=0 on its own, BAD=1 in the
gate context, cause never investigated"*. Measured again on 2026-10-24: on its
own against a freshly deployed instance it prints **BAD=0**; inside the gate it
printed **RED (BAD=n)**. A suite whose verdict depends on where it runs
decides green-versus-red in both directions, and neither verdict is
attributable to the tree.

## Reference

- `pm/SPEC.yaml` — the `sim-health` suite entry, `flaky: true` and the note.
- `tools/verify/suites/simhealth-verify.py` — the suite. Its own output names
  the suspect: *"This count is printed, not judged. Two measured reasons, both
  about the JUDGEMENT and not about the product: 1. The threshold behind it is
  `one-frame mob move exceeded 8px`, and that 8px has no citation in the
  reference. 2. The count is not a property of the tree. MEASURED 2026-10-23:
  on instances 5721/5731 the SAME file reported 0, 1 and 2 on three runs; on
  5733 the count was 0 in 22 runs, while the shipped judge still reported
  BAD=2 once."*

## Approach

1. Reproduce both verdicts on one instance: run the suite alone against a
   freshly deployed server, then run it again inside the gate, and record
   which finding differs.
2. Split the suite's findings into the ones that are a property of the tree
   (the archive, the port's behaviour) and the ones that are a property of
   the moment (a count that depends on timing, on load, or on which instance
   the gate points at). The suite's own text already names the `8px` threshold
   as having **no citation in the reference**.
3. Fix the JUDGEMENT, not the symptom: either pin the inputs and find the
   `8px` in `clean-client`, or stop judging on a number that is not a
   property of the tree. Widening the suite is not the fix -- the suite says
   so itself.
4. Remove `flaky: true` when the suite is green twice in a row in BOTH
   contexts, and record the measurement that made it so.

## Acceptance

```
# 1. the suite alone, against a freshly deployed instance
python tools\verify\suites\simhealth-verify.py
# EXPECT: BAD=0, exit 0

# 2. the same suite inside the gate
powershell -NoProfile -ExecutionPolicy Bypass -File tools\verify\gate-loop.ps1
# EXPECT: no RED_SUITE=sim-health, and the suite's exit code agrees with the
#         token the gate reads

# 3. both, twice
# EXPECT: four runs, four identical verdicts, and the SPEC's `flaky: true`
#         removed together with a note naming the measurement that did it
```

## Result

**`sim-health` was never flaky. The GATE was misreading the suite's output.**

Measured, 2026-10-24:

- The suite on its own, against the same instance the gate had just used:
  **`BAD=0`, exit 0** (also measured independently by a second session).
- In the gate, the same suite printed **"BAD=0"** as its own last line — and
  the gate said **`>>> RED (BAD=2)`**.

Both are true at once, and the new evidence output showed why. The gate's red
channel was

```powershell
if ($text -match 'BAD=[1-9]\d*') { ... }     # over the WHOLE output
```

and this suite's output **explains itself**, and the explanation mentions the
number:

> the shipped judge still reported BAD=2 once (not attributed to this block)

A sentence about a past run was read as this run's verdict. The suite passed
and the gate reddened it for having written about a number. (And the gate
printed the count as the literal letter `n`, so the reader could not even see
that it had read a count out of a sentence.)

### The fix

The suites' convention is a **standalone** line — `print(f"\nBAD={bad}")` — so
the gate now narrows the text to the lines that stand alone before it tests
them. Prose with a number in it is not a verdict. Nothing was weakened: a
standalone `BAD=1` still reddens (that is `contact-damage.py:120`, the "this
judge cannot run" marker), and the `>>> RED` channel still reads the whole
output.

The gate now also prints the failing suite's **own evidence** — the `>>>` it
emitted and its verdict line — instead of only the word RED. That is what made
this diagnosable at all, and it is what showed the `skill-e2e` red of W-101
("the struck mob's HP dropped (2220100: 1000 -> 1000)") without a re-run and a
print statement added by hand.

### Evidence

- Gate, 2026-10-24 14:30: `=== sim-health ===  ok  GREEN? BAD=0`.
- Gate, 2026-10-24 14:45: `=== sim-health ===  ok  GREEN? BAD=0`.
- `pm/SPEC.yaml`: the `flaky: true` flag on this suite is **removed**, and the
  note now names the measurement that did it, as the acceptance required.
- `pm/TRAPS.md` T-25 carries the finding for the next reader.

### Not closed by this

The suite's **calibration** still asserts something that is not yet a property
of the tree: it collects every damage number in the session and asks whether
one appeared, not whether the mob it walked up to was hit. That is W-102, and
it is left open rather than folded into this item — this item was about the
gate reading a sentence, and the calibration is a separate measurement with a
separate fix.

