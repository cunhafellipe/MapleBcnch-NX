---
id: W-087
title: One shared cast path, and 22 of 66 skills read no damage node at all
state: done
priority: 96
owner:
opened: 2026-10-18
closed: 2026-10-18
depends: [W-083]
refs: SkillCastService.cs DamageNodes/TryReadReal; MapSimSessionStore.cs ApplySkillHits; scratch/skill-test.ps1 section 2
blocked_on:
evidence: |
  before: 44 of the 66 castable skills dealt the test hit; 22 dealt ZERO
  after:  64 of 66 deal 100; the 2 that do not are a heal and one box case
  22 -> 9 -> 7 -> 2 across four builds, each step measured before the next
  by skill school: every mage, rook and night lord skill in the list
  2321008 level/1 = hs lt mad(430) mastery mobCount mpCon rb -- NO `damage`
  10001009 level/1 = damagepc lt mobCount rb -- no `damage` at all
  5211004 level/1 carries a child NAMED damage and the reader still found none,
          because that node is not an Int, Short or Long
workstream: simulate
kind: defect
risk: high
---

## Intent

A challenge to a claim, and the claim was wrong.

Asked whether the skill logic is one system, the answer is yes -- `SkillCastService.Plan`
reads the archive, `ApplySkillHits` uses the plan's box, `drawSkillEffectPass` draws the
plan's sprites, and there is no per-skill code path. So the useful question is not
"is each skill right" but "does the one path survive every data shape the archive
contains".

Ran all 66 through the shared contract and 22 of them dealt nothing.

## Why one path was not the same as one behaviour

`ApplySkillHits` opens with

```csharp
if (plan.Damage is null) return;
```

and the plan read exactly one node name. The archive does not agree that there is one.

| node | who carries it | count |
| --- | --- | --- |
| `damage` | physical skills | 44 dealt |
| `mad` | mage / rook / night lord | 12 |
| `damagepc` | the player's own number, where it is split from the mob's | 4 |
| none | heals and buffs — correctly no damage | 2301002 群体治愈 carries only `hp(10)` |

`SkillData.cpp:58,65,66` reads `damage`, `mad` and `fixdamage` off the same level node,
which is the shape the port had already seen and still narrowed to one name.

Two more shapes the reader could not see at all:

- **`damagepc` / `maddpc`** — the player-side number. `10001009`'s `level/1` is exactly
  `damagepc, lt, mobCount, rb`. Since this simulator only ever casts as a player, that
  is the node that governs them.
- **Numeric width.** The reader listed `Int`, `Short` and `Long`, so a `Float` or
  `Double` node read as an ABSENT node rather than as a value of another type.
  MEASURED: `5211004`'s `level/1` carries a child named `damage` — the endpoint renders
  it as 73 — and the skill still dealt zero. `TryReadReal` now accepts any width, and
  `SkillCastPlan.LevelKeys` reports what the reader actually saw so this class of
  disagreement is answerable from the plan instead of a debugger.

## What is left, and why

- **2301002 群体治愈** — `level/1` is `hp(10)`. It heals; it deals nothing. Correct.
- **5101002 回马** — the plan reads `damage: 88` and the box is `0,-50 .. 140,0`, so a
  mob 32 px ahead is inside it horizontally and the cast still connects with neither
  facing. UNRESOLVED: a hit-box mapping case, not a damage-node case. `LevelKeys`
  proves the reader is not involved.

`SkillData.cpp:61-64` also documents a fifth shape — `3101005` has no `damage` and its
rate lives in `x`. The port reads `x` as a last resort, gated on no damage node
existing, which brings in `4211006` 金钱炸弹 (`x(500)`), the only other skill of that
shape on this archive.

## The suite that was asserting the defect

`skill-test.ps1` section 2 read

```
=== 2. MultiStrike = TRUE must deal NO damage ===
Check "2.1 a MultiStrike=true cast deals NO damage" ($hits2 -eq 0)
```

That is the W-083 defect written down as a contract. It stayed green only because the
skill it picked, `10001009`, was one of the four whose damage node was never being read
— a skill that deals nothing for an unrelated reason is indistinguishable, from HP
alone, from a skill the rules say cannot deal anything. T-21 again.

Corrected to assert what ADR-0006 and W-083 actually decided: a `MultiStrike` cast
deals the same 100 test hit as any other. The new section also checks the premise the
old one never did — that the plan found a damage node — so the suite can tell "this rule
says no damage" from "this reader found nothing".

## Verification

- All 66 castable skills swept through the shared path: **64 deal 100**, 0 errors.
- Full gate: **RED SUITES 0**, skill-test 20 assertions 0 failed.
- Hit effect and ground decals are unaffected — those were verified in W-084/W-085 and
  browser-verify still reports 14 assertions 0 failed.