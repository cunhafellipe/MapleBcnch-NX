---
id: W-094
title: The gate's skill-shape judge rounds the hit box where the server truncates it, and the damage array is omitted on a miss so a PowerShell count of it reads 1
state: todo
priority: 92
owner:
opened: 2026-10-21
closed:
depends: []
refs: MapSimSessionStore.cs 2394-2406; MapSimSessionStore.cs 2447-2479; scratch/skillshape-verify.mjs 273-287; scratch/attack-test.ps1 83-99
blocked_on:
evidence: |
  measured 2026-10-22 on a backend built from HEAD f79669a, port 5645, with
  C:/mbtest2/Map.nx, C:/mbtest2/Mob.nx and C:/mbtest2/Skill.nx open read-only.
  16 arms: 4 skill ids x 2 mob ids x 2 facing directions, 60 frames each.
  THE REAL DEFECT - the harness rounds where the server truncates:
    harness : scratch/skillshape-verify.mjs:279 (before this item)
              const near = haveBox ? Math.round(left * (plan.hRange ?? 1)) : left;
    server  : MapleBench/Services/Simulate/MapSimSessionStore.cs:2398
              int near = haveBox ? (int)(left * plan.HRange) : left;
    the server's own comment at :2394-2397 says the cast truncates and that
    rounding "is a different box whenever left * HRange is not a whole number
    -- a skill at range 0.75 over lt=-200 reached -150 where the reference
    reaches -149".
    MEASURED over the real corpus, every distinct lt.x in Skill.nx crossed with
    every distinct hRange (= range/100) in Skill.nx:
      68 distinct lt.x values (min -1220, max 100)
      63 distinct range values -> 63 distinct hRange values
      4284 pairs compared, 1102 pairs DISAGREE (25.7%)
      e.g. left=-1220 hRange=1.34 -> round -1635, trunc -1634
           left=-1090 hRange=0.15 -> round  -164, trunc  -163
    FIXED in the scratch suite on 2026-10-22 (Math.round -> Math.trunc, with
    the server's line quoted above it); `node --check` passes. The fix must
    ride into the repository with the migration (task-26).
  THE HAZARD - the damage array is OMITTED on a miss, and PowerShell counts
  an absent key as one:
    the /frames response omits `damage` entirely when nothing was hit
    (MEASURED: the session response carries `damage`, the frames response
    does not) -> `$f.damage` is $null -> `@($f.damage).Count` is 1
    (MEASURED: @($null).Count = 1, @(@()).Count = 0)
    so a PowerShell judge that counts the damage array without filtering sees
    ONE event on a miss.
    NO SUITE GETS THIS WRONG TODAY. All four that touch it handle it:
      scratch/attack-test.ps1:88,99   @($r.damage | Where-Object { $_ })
      scratch/skill-test.ps1:64        Count-Damage filters Where-Object { $_ }
      scratch/skillshape-verify.mjs:191 Array.isArray(f.damage) && f.damage.length
      scratch/deadplayer-verify.py:60  fr.get("playerDamage"), the hp-derived field
    attack-test.ps1:83 already writes the trap down in a comment:
      "@($r.damage).Count is 1 for a NULL damage -- PowerShell wraps $null in an"
    That knowledge is in one file only. It fooled reviewer-nx's own probe on
    2026-10-22, which reported "dmgEvents=1 on a miss" and would have become
    a second false finding.
workstream: simulate
kind: defect
risk: high
---

## Intent

The gate's skill-shape judge decides "did the cast land" against a box it
computes itself. That box must be the box the server used, or the judge
answers a different question from the one the product was asked.

Two things about the wire shape make that hard to get right, and one of them
is already a live defect.

## The live defect: the harness rounds where the server truncates

`scratch/skillshape-verify.mjs:279` computed the near edge of the skill box
with `Math.round`. The server computes it with a C# cast to int
(`MapSimSessionStore.cs:2398`), which truncates toward zero.

The server's own comment at :2394-2397 names the difference and gives the
worked example, and the harness's own comment at :242-244 transcribes the
reference line the rounding changes.

**1,102 of the 4,284 real (lt.x, hRange) pairs in the corpus disagree.** The
box the harness judged against was a different box for a quarter of the real
combinations.

FIXED on 2026-10-22 in the scratch suite (`Math.round` -> `Math.trunc`, with
the server's line quoted above it). `node --check` passes. The fix has to
ride into the repository together with the migration (task-26) - a suite
migrated from the scratch copy as it was before this item would carry the
wrong arithmetic into version control.

## The hazard: an absent `damage` key counts as one event in PowerShell

The /frames response **omits** `damage` when nothing was hit. In PowerShell
`$f.damage` is then `$null`, and `@($null).Count` is **1** - so a judge that
counts the array without filtering reports one damage event on a miss, on
every miss.

**No suite does this today.** All four that touch the array handle it, and
`attack-test.ps1:83` already writes the trap down. The problem is that the
knowledge lives in one file's comment, and it fooled a measurement made on
2026-10-22: reviewer-nx's probe counted `@($cf.damage).Count` and reported
"dmgEvents=1 on a miss" as a second false finding, when the array was in fact
empty.

## What is not done

1. **The truncation fix exists only in the scratch copy.** It has not been
   carried into the repository, and the repository has no copy of the suite.
2. **The null-wrapping hazard is not a known-answer case.** Nothing in
   `tools/verify/self-test.ps1` asserts that an absent field reads as zero
   elements rather than one, and `require-fields.ps1` checks presence, not
   count.
3. **The `damage` key's absence on a miss has not been asserted by a test.**
   It is a wire-shape fact, measured once, recorded only in this item.

## How to check a fix

- For every (left, hRange) pair the corpus produces, compare the harness's
  near edge with the server's and show they are equal.
- Cast a skill that misses and show the frame carries no `damage` key, and
  that a PowerShell consumer that filters nulls counts zero while one that
  does not counts one.

## acceptance

1. The harness box is the server box.

   ```
   node -e "<loop over the corpus's (lt.x, hRange) pairs, printing the pairs
   where Math.trunc(left*hRange) differs from the server's (int) cast>"
   ```

   must print `disagreements=0` for the harness's own comparison, AND the
   corpus check must print at least one pair where `Math.round` and
   `Math.trunc` differ (so the check is not vacuous). The corpus figures as
   measured on 2026-10-22 are 4284 pairs, 1102 differing.

2. A miss is a miss, in every language.

   ```
   powershell -File tools/verify/self-test.ps1
   ```

   must print a case named `DAMAGE-NULL-COUNT-IS-NOT-ONE` whose expectation
   reads "an absent damage array counts as ZERO elements, not one", and that
   case must PASS.

3. No suite decides a hit by counting damage events.

   ```
   grep -rn "damage" <the suites' new repository path>
   ```

   must show every hit decision reading the target's hp. A hit decision that
   reads an array length without filtering out nulls is a failure of this
   item.
