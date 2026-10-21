---
id: W-088
title: Five questions about the skill hit box — one real defect, three measured correct, one a published lie
state: done
priority: 94
owner:
opened: 2026-10-18
closed: 2026-10-18
depends: [W-083, W-087]
refs: MapSimSessionStore.cs ApplySkillHits; SkillCastService.cs Plan/DamageWaitTime; Combat.cpp:605-830,1226-1272,660-679; Point.h:60-65,80-83; Mob.cpp:1875-1884; MapleBench/tools/probe-A1..A5
blocked_on:
evidence: |
  A1 5101002 DOES hit. 60-row distance sweep, facing RIGHT: HIT at dist -125,-84,-55,-29,-25,+2,+22;
     MISS at -205,-165,+36,+53,+83. Our box and Combat.cpp:660-679 agreed in 60 of 60 rows.
  A2 REAL DEFECT. 1111008, mobCount 6, 8 summoned mobs all inside the box:
     hit slots [24,25,26,27,28,29] = the six lowest session indices, contiguous;
     NOT hit: run 1 — slot 30 at refDist 1 and slot 31 at refDist 27;
             run 2 — slot 30 at refDist 0 (standing ON the player) and slot 31 at refDist 97.
     A mob 0px away stood untouched while a mob 94px away was struck. Same contiguous
     run on 10001009 (24..49, with slots 53 and 55 in range and untouched) and 2121007 (24..37).
  A3 hRange acts on the NEAR edge, as the reference says. Measured near-edge brackets:
     4121008 (hRange 1.5) (-328,-263]; 1111008 (hRange 1.0) (-256,-154]; 1121008 (-190,-144].
     Separation constrained to [7,173]px, contains the predicted 100px. 0 scored-row mismatches.
  A4 Damage lands on the CAST FRAME for 6 of 6 skills, including damageWaitTime 25 and 70.
     The reference also has no delay: apply_move calls find_closest and sends the attack
     in one call. So the BEHAVIOUR is right and the published plan tick is wrong.
  A5 collisionCount !== mobCount in 0 of 66 castable skills. It is ASSIGNED, not read:
     SkillCastService.cs:1086 `int collisionCount = mobCount;`
workstream: simulate
kind: defect
risk: high
---

## Scope

Five questions about one method, `MapSimSessionStore.ApplySkillHits`. Authoritative reference is
`C:\MUWORK\GAME\MPSH\clean-client\src` (598 files); `MapleNecrocer` was consulted only to find
out where two of our fields came from, and is not treated as authority.

Write scope was `MapSimSessionStore.cs` alone. Every C# change below is in that file.

## A1 — 5101002 (回马) "never hits": the premise is wrong, the box is correct

**Not a defect.** The box in `ApplySkillHits` is a line-for-line transcription of the reference and
agrees with it on every one of 60 measured rows.

Reference, `Combat.cpp:660-679`:

```cpp
int16_t hrange = static_cast<int16_t>(range.left() * attack.hrange);
if (attack.toleft) {
    range = { origin.x() + hrange, origin.x() + range.right(), ... };
} else {
    range = { origin.x() - range.right(), origin.x() - hrange, ... };
}
```

Ours, `MapSimSessionStore.cs:2197-2205`:

```csharp
int near = haveBox ? (int)(left * plan.HRange) : left;
bool facingRight = session.Facing >= 0;
float bx0 = facingRight ? body.X - right : body.X + near;
float bx1 = facingRight ? body.X - near : body.X + right;
```

Identical. `probe-A1` prints our box and the reference box side by side for every row; they match in
all 60.

The real reason 5101002 "misses" is that its authored box points **backwards** relative to the
facing. `lt=(0,-50) rb=(140,0)` is authored on the RIGHT, so:

- facing RIGHT → box `[X-140, X]` → hits mobs to the **left**
- facing LEFT  → box `[X, X+140]`   → hits mobs to the **right**

Measured, one mob, swept by walking the body (probe-A1 output, verbatim rows):

```
-- facing RIGHT --            | ourBox L..R | refBox L..R |  hpAfter
 -120  playerX 1754 mobX 1629 |  -140..0    |  -140..0    |    900  HIT
  -50  playerX 1794 mobX 1739 |  -140..0    |  -140..0    |    900  HIT
    0  playerX 1728 mobX 1730 |  -140..0    |  -140..0    |    900  HIT
   32  playerX 1636 mobX 1672 |  -140..0    |  -140..0    |   1000  miss
-- facing LEFT --
   32  playerX 1777 mobX 1806 |    0..140   |    0..140   |    900  HIT
   50  playerX 1680 mobX 1736 |    0..140   |    0..140   |    900  HIT
```

The reported observation was "character x=1710, mob x=1742, misses both ways". Facing right at that
position is a **correct miss** — the box is `[1570, 1710]`. Facing left it hits (row `32` above).
So one of the two facings was never actually facing left when the cast landed; the held direction is
the facing (`MapSimSessionStore.cs:2200`), and a cast sent with `{Left:true, SkillId:...}` does turn
the body, so this is a measurement artefact of the original probe, not a product bug.

The `**MISMATCH**` markers in probe-A1's raw output are all at `dx ≈ 0` and are an artefact of that
script's simplified model, which used the mob's centre `x` rather than its animation bounds. The
box comparison columns are the load-bearing ones and agree everywhere.

## A2 — mobCount > 1: real defect, fixed

**The defect.** `ApplySkillHits` bounded the SCAN instead of the OUTPUT:

```csharp
// OLD
for (int i = 0; i < session.Mobs.Count && hits.Count < want; i++)
```

The reference applies `objcount` to the sorted result, never to the loop
(`Combat.cpp:1263-1269`):

```cpp
for (auto& iter : distances) { if (targets.size() >= objcount) break; targets.push_back(iter.second); }
```

So the old code took the **first `mobCount` mobs in session order** — a map's own `life` entries
first (indices 0..23 on the test map), then whatever was summoned — and the sort that followed could
not undo it, because it only reordered mobs that had already been chosen.

**Measured, `probe-A2-mobcount.mjs`, 1111008 (mobCount 6, box ±200, 8 summoned, all 8 inside).**
Two independent runs, because summon placement is random and only the STRUCTURE of the
answer is invariant:

Run 1 —
```
  slot    x      y   refDist  inBox | hp after cast
    24   1790    -25        78     IN |      900  <- HIT
    25   1775    -25        63     IN |      900  <- HIT
    26   1690    -25        21     IN |      900  <- HIT
    27   1798    -25        86     IN |      900  <- HIT
    28   1693    -25        18     IN |      900  <- HIT
    29   1722    -25        10     IN |      900  <- HIT
    30   1713    -25         1     IN |     1000
    31   1684    -25        27     IN |     1000

  hit slots           : [24,25,26,27,28,29]  (6 of 8 summoned)
  NEAREST-6 (reference) : [25,26,27,28,30,31]   nearest-ok=false
  FIRST-6-BY-SESSION    : [24,25,26,27,28,29]   matches-observed=true
```

Run 2 —
```
    24   1806    -25        94     IN |      900  <- HIT
    25   1759    -25        47     IN |      900  <- HIT
    26   1694    -25        17     IN |      900  <- HIT
    27   1620    -25        91     IN |      900  <- HIT
    28   1803    -25        91     IN |      900  <- HIT
    29   1703    -25         8     IN |      900  <- HIT
    30   1712    -25         0     IN |     1000     <- refDist 0: standing ON the player
    31   1809    -25        97     IN |     1000

  hit slots           : [24,25,26,27,28,29]  (6 of 8 summoned)
  NEAREST-6 (reference) : [25,26,27,28,29,30]   nearest-ok=false
  FIRST-6-BY-SESSION    : [24,25,26,27,28,29]   matches-observed=true   predictions differ=true
```

Run 2 is the sharper statement: a mob at **refDist 0** — standing on the player — was left
untouched while a mob **94 px away** was struck. Run 1 has the same shape at 1 px vs 78 px.

The invariant across every run and every skill is the signature itself: the hit set is a
**contiguous run starting at the lowest session index**. 1111008 → slots 24..29. 2121007 →
24..37. 10001009 → 24..49, with slots 53 and 55 sitting in range and untouched. A
distance-ordered selection cannot produce a contiguous run from the bottom for four
independent random layouts.

**The fix**, `MapSimSessionStore.cs:2231-2281`:

1. The scan is now unbounded — `for (int i = 0; i < session.Mobs.Count; i++)`.
2. The distance is the reference's metric, not `|dx|`. `Combat.cpp:1243` →
   `Point::distance` → `static_cast<int16_t>(std::sqrt(a*a + b*b))` (`Point.h:60-65,80-83`):
   **both axes, truncated**. It was `Math.Abs(mobPatrol.X - body.X)`, horizontal only, which ties
   two mobs at the same `|dx|` and different heights that the reference separates.
3. The sort is stable-by-index. `std::multimap<uint16_t,int32_t>` keeps equal keys in insertion
   order, and `List<T>.Sort` is not stable, so the index is written into the comparison:

   ```csharp
   hits.Sort((a, b) => a.Dist != b.Dist ? a.Dist.CompareTo(b.Dist) : a.Index.CompareTo(b.Index));
   ```

4. The cut is applied after sorting:

   ```csharp
   if (hits.Count > want) hits.RemoveRange(want, hits.Count - want);
   ```

A `mobCount` larger than the crowd in range now hits the whole crowd, as the reference does.

**Not verified end-to-end.** The server on :5591 is still running the pre-fix binary — I am not
allowed to build or deploy. Re-run `probe-A2-mobcount.mjs` after the rebuild; the 1111008 block
above must flip to `nearest-ok=true`. The script refuses to report PASS unless at least one skill
has the two predictions disagree, so it cannot silently pass on a fixture that no longer
discriminates.

## A3 — hRange = 1.5: measured correct, one rounding fix

`hRange` multiplies the **near edge** (`range.left()`) and nothing else, exactly as
`Combat.cpp:660` says. Ours was already `(int)(left * plan.HRange)` in spirit; the one difference was
rounding.

**Fix**, `MapSimSessionStore.cs:2197`:

```csharp
// OLD
int near = haveBox ? (int)Math.Round(left * plan.HRange) : left;
// NEW
int near = haveBox ? (int)(left * plan.HRange) : left;
```

`static_cast<int16_t>` truncates toward zero. `Math.Round` rounded, which is a different box
whenever `left * HRange` is not a whole number. No skill on this archive is affected today
(`4121008` is `-200 * 1.5 = -300`, exact), so this is a correctness fix with **no measured
behaviour change** — I am not claiming a number for it.

**Measured boundary, `probe-A3-hrange.mjs`** (facing LEFT, one fresh session per cast; the distance
is measured, not commanded, because the mob patrols):

```
4121008  lt=-200 rb=200 hRange=1.5  near edge -300  bracket (-328, -263]  0 scored-row mismatches
1111008  lt=-200 rb=200 hRange=1.0  near edge -200  bracket (-256, -154]  0 scored-row mismatches
1121008  lt=-150 rb=-10 hRange=1.0  near edge -150  bracket (-190, -144]  0 scored-row mismatches

boundary separation (|4121008 - 1111008|) constrained to [7,173]px; predicted 100px
AGREES — hRange moved the NEAR edge by exactly lt*(hRange-1), as Combat.cpp:660 does.
```

An earlier run of the same probe bracketed 4121008 at `(-337, -300]` and 1111008 at `(-220, -194]`,
separation `[80, 143]` — also containing 100. Both runs put the predicted boundary inside the
measured bracket with zero scored-row mismatches across 25/26 rows.

The mob's own half-width is 4..16 px, measured not assumed (1111008 hits at dist -201 and misses at
-228, which bounds it above and below); the probe uses 12 and marks rows within 8 px of an edge
`AMBIGUOUS` rather than scoring them.

## A4 — damageWaitTime: the behaviour is right, the published plan is wrong

**The behaviour already matches the reference, so nothing was changed.**

The reference has no delay at all. `Combat::apply_move` (`Combat.cpp:605`) builds the `Attack`,
calls `find_closest` at `:724` and sends the attack at `:826` inside one call — no queue, no timer,
no deferred pass. `grep -rn damageWaitTime clean-client/src` returns **zero** hits.

Our `SkillCastService.DamageWaitTime` (`SkillCastService.cs:566-591`) is a hardcoded id→ms table
transcribed from MapleNecrocer `Skill.cs:53-150` — the secondary reference. It feeds
`plan.DamageWaitTime`, `plan.TotalTime = wait + 42` and the `mobTarget` timeline ticks, and nothing
else. `ApplySkillHits` has never read it.

**Measured, `probe-A4-damage-timing.mjs`, frame-by-frame HP after the cast:**

```
skill      mobCount damageWaitTime totalTime  planFirstMobTargetTick  planDelayFrames  hpDropsOnCastFrame
1121008           1              25        67                      31              5.2                      0
5101002           3              15        57                      21              3.5                      0
5211004           2              15        57                      21              3.5                      0
2121007          15              70       112                      76             12.7                      0
2321008          15              70       112                      76             12.7                      0
1111008           6              15        57                      21              3.5                      0

6 of 6 in-box skills settle damage on the cast frame.
```

So the judgement in the brief ("如果參考是延遲結算而我們是立即結算，那要修") resolves the other way:
the reference is immediate, we are immediate, nothing to fix. What is wrong is the **plan**, which
publishes a `mobTarget` tick 3.5 to 12.7 frames after a hit that lands on frame 0 — a tester
reading `/api/skill-cast/plan` is told the hit is delayed when it is not. That is the file-level
finding; see below.

## A5 — collisionCount vs mobCount: always identical, and neither exists in the reference

**Never different.** `probe-A5-collision-vs-mobcount.mjs`, all 66 castable skills:

```
castable skills reported: 66
plans read: 66 (errors: 0)
skills where collisionCount !== mobCount : 0
skills where totalTime !== damageWaitTime + 42 : 0
```

They are identical **by construction**, not by coincidence — `SkillCastService.cs:1086`:

```csharp
int collisionCount = mobCount;      // assigned, never read from the archive
int totalTime      = wait + 6 * 7;  // assigned, never read from the archive
```

Neither name exists in the authoritative reference: `grep -rn collisionCount clean-client/src` → 0
hits, and neither does `damageWaitTime`. The reference's count is `SkillData.cpp:69
sub["mobCount"].get_integer(1)` → `Skill.cpp:394 attack.mobcount = stats.mobcount` →
`Combat.cpp:1265 if (targets.size() >= objcount) break`. `ApplySkillHits` reads only
`plan.MobCount`, so `collisionCount` and `totalTime` are dead fields that cannot disagree with
anything because they were never measured.

## Cross-file findings — NOT changed, `SkillCastService.cs` is another workstream's file

Three things belong in `SkillCastService.cs` and I did not touch them:

1. **`collisionCount` (`SkillCastService.cs:242`, assigned at `:1086`)** — a MapleNecrocer name with
   no reference counterpart, always equal to `mobCount`. Suggest deleting the field and its
   `SkillCastEndpoints.cs` projection rather than carrying two names for one number.
2. **`TotalTime` (`SkillCastService.cs:243`, assigned at `:1087` as `wait + 42`)** — synthetic. The
   doc comment at `:285-307` already says the cast lock must NOT use it, and it does not; the
   remaining consumer is the plan JSON.
3. **`DamageWaitTime` (`SkillCastService.cs:566-591`) and the `mobTarget` timeline entries
   (`:1089-1092`)** — published as if they were the hit schedule. A tester is told
   `2121007` targets at tick 76; the hit is on frame 0. Either drop the `mobTarget` entries or
   rename the field to say plainly that it is a MapleNecrocer-derived cast-time constant with no
   counterpart in the reference.

None of the three can change behaviour — `ApplySkillHits` reads none of them — so they are
documentation defects, not mechanism defects.

## What is NOT verified

- **The A2 fix has not been run.** The server is on the pre-fix binary and I am not permitted to
  build or deploy. `lsp_diagnostics` reports no diagnostics on the edited file, which is a parse
  check and not a build. Re-run `probe-A2-mobcount.mjs` after the rebuild.
- The A3 truncation change has **no measured behaviour change** on this archive, so no number is
  claimed for it.
- Probe A3's per-row distances are measured, not commanded: the mob patrols (one sample wandered
  x=630 → x=3620 while the body stood still). Rows where the map's own mobs filled `mobCount` are
  printed `INCONCLUSIVE` and excluded.
- The Y axis of the box is not swept. Every measurement held the mob and the body on the same
  foothold, so `top`/`bottom` is unverified by measurement; it is a verbatim transcription and the
  source agrees with the reference.

## Re-running

```
node MapleBench/tools/probe-A1-hitbox-5101002.mjs    # 60-row box comparison, ours vs Combat.cpp
node MapleBench/tools/probe-A2-mobcount.mjs           # exactly N, and the N nearest
node MapleBench/tools/probe-A3-hrange.mjs             # near-edge boundary for hRange 1.5
node MapleBench/tools/probe-A4-damage-timing.mjs     # which frame the hp drops on
node MapleBench/tools/probe-A5-collision-vs-mobcount.mjs
```

All print numbers, none print `ok`. Server must be on `http://127.0.0.1:5591`
(override with `MB_BASE`).