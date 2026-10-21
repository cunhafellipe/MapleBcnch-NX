---
id: W-061
title: Skill damage is a hardcoded 50000..750000 random, not the archive's own level/n/damage
state: done
priority: 70
owner: 
opened: 2026-10-16
closed: 2026-10-16
depends: []
refs: DamageRoll.cs:84-103; Mob.cpp:1577-1600; MapSimSessionStore.cs:1613,1843-1844; SkillCastService.cs:345-356
blocked_on: 
evidence: SkillCastPlan gains SkillLevel+Damage read from level/<clamp(Status.Level,1,maxLevel)>/damage OrdinalIgnoreCase; ApplySkillHits now calls DamageRoll.Roll with MobLevel and MobDefence, the identical call shape as the swing. Measured: plan 1121008 -> skillLevel=1 damage=135; cast damage 90801 -> 103; mob 2220100 hp 350 destroyed -> 247; plan 10001009 -> damage null not 0. build exit 0, 0 errors 0 warnings; deploy 59 wwwroot files identical, avatar.js md5 unchanged; gate RED SUITES: 0.
workstream: simulate
kind: defect
risk: med
verify:
  - cmd: powershell -NoProfile -File $S/skill-test.ps1
    expect: 19 assertions, 0 failed
---
## Intent

A skill that lands currently removes between 50,000 and 750,000 from a mob,
whatever the character, whatever the skill, whatever the mob's defence. The
archive carries each skill's own `level/<n>/damage` — measured, see below —
and nothing reads it. After this item a skill's damage comes from that node and
goes through the same `DamageRoll` a swing uses, so the mob's `info/PDDamage`
and the level difference finally mean something for skills, and a level-1
skill against a level-200 mob correctly does nothing.

## Reference

**Partial.** The roll is the reference's, verbatim, and already exists:
`DamageRoll.Roll` (`DamageRoll.cs:84-103`) implements `Mob::calculate_mindamage`
/ `calculate_maxdamage`, `Mob.cpp:1577-1600`, and the swing path already calls it
at `MapSimSessionStore.cs:1843-1844`.

**Own-design, declared:** where the attacker-side number comes from. The C++
client computes no skill damage at all — real MapleStory is server-authoritative
and the client only renders the number the server sends (`SkillCastService.cs`
remarks, :131-134). There is therefore no reference line for "skill damage" and
this item does not invent one: it feeds the archive's own `level/<n>/damage`
into the reference's roll, which is the only formula in this codebase that can
cite a reference for the part that is arithmetic.

### Measured, 2026-10-16, through `/api/inspect` only

| Fact | Value |
|---|---|
| skill entries census | 535 |
| carrying `level/1/damage` | **175** |
| carrying no `level` node at all | 0 |
| `level/1/damage` values (n=175) | min 1, p25 72, median 104, p90 265, max 1210 |
| `f5/<mob>.img/info/PDDamage` (n=409 of 411 sampled) | min 0, p25 40, median 170, p90 950, p99 2000, max 30000 |
| `f5/<mob>.img/info/level` | min 1, median 59, max 200 |
| roll outcome for damage=135 vs median PDDamage 170 | min 33, max 50 — not floored |
| roll outcome for damage=135 vs p90 PDDamage 950 | min 1, max 1 — **floored** |

The floor is the reference's own (`Mob.cpp:1590-1601` puts `max(1.0, …)` around
both lines), and it already applies to swings today. This item does not change
it; it records that a level-1 character and a mid-level skill will read as
"1 damage" on a high-level map, and that is arithmetic, not a defect.

## Approach

1. `Services/Simulate/SkillCastService.cs` — `SkillCastPlan` gains
   `int? SkillLevel` and `int? Damage`. In `SkillCastPlan`'s sibling
   `Plan(string skillId, int characterLevel)`… (the method that currently takes
   only the id, at `SkillCastService.cs:229`): after the existing `maxLevel`
   scan at `:345-356`, resolve `level = clamp(characterLevel, 1, maxLevel)` and
   read `level/<n>/damage` **OrdinalIgnoreCase** (TRAPS T-10 — `incLUk` is a
   real lower-case-k precedent on this archive). Null when absent.
2. `Services/Simulate/SkillCastService.cs:157-163` — the caller passes
   `session.Status.Level`, the same level source `ResolveSwingHit` already uses
   for a swing (`MapSimSessionStore.cs:1843`). Skill level therefore follows the
   character, capped by the skill's own `maxLevel`.
3. `Services/Simulate/MapSimSessionStore.cs:1613` — replace
   `mobPatrol.TakeHit(50000 + session.CombatRandom.Next(700000))` with
   `DamageRoll.Roll(session.CombatRandom, session.Status.Level,
   _combat.MobLevel(mobId), _combat.MobDefence(mobId), plan.Damage.Value,
   plan.Damage.Value)` — the identical call shape as the swing at `:1843`.
4. `Services/Simulate/MapSimSessionStore.cs:1568` — a plan with no `Damage`
   deals no damage and says so in a comment. 360 of 535 skills carry no damage
   node; the large majority are buffs, and "a buff does no damage" is the
   honest reading of an absent value (R-5: report absent as null, never zero).
5. Replace the four mojibake comments in this file at :1538, :1545, :1558-1559
   (`??six`, `??this`) — they are the U+FFFD residue of a mangled em dash.

## Acceptance

```
# 1. the plan now carries the archive's own damage, and the level it was read at
curl -s "http://127.0.0.1:5591/api/skill-cast/plan?skillId=1121008"
# EXPECT: "damage": 135, "skillLevel": 1      (measured lv/1/damage = 135)

# 2. a cast's damage number is bounded by the skill, not by a 750000 random
python %TEMP%\w061-verify.py
# EXPECT: prints "cast damage=NN  skill=135  in-range=True  legacy-random=False"
#   where in-range is 1 <= NN <= 135 and legacy-random is NN >= 50000

# 3. a skill with no damage node deals none, and the plan says null not 0
curl -s "http://127.0.0.1:5591/api/skill-cast/plan?skillId=10001009"
# EXPECT: "damage": null   (never 0 — R-5)

# 4. the whole gate is still green, and skill-test now ASSERTS
python tools/pm.py gate
# EXPECT: RED SUITES: 0, and the skill-test block shows its assertion summary
```

## Result

A skill's damage is now the archive's own number, put through the same roll a
swing uses. Measured before and after, same skill, same mob, same map:

| | before | after |
|---|---|---|
| `/api/skill-cast/plan?skillId=1121008` | no damage field at all | `skillLevel=1, damage=135` |
| damage number over mob 2220100 | **90,801** | **103** |
| that mob's hp 350 | destroyed outright | **247** |
| `?skillId=10001009` (MultiStrike=true) | 50,000-750,000 random, suppressed by the MultiStrike branch | `damage: null` |

The 135 is not a number anyone chose: it is `level/1/damage` for 1121008,
measured through `/api/inspect` before a line of code was written.

**What landed**

1. `SkillCastPlan` gained `SkillLevel` and `Damage`; `Plan` takes
   `characterLevel` (default 1, so the read-only endpoints are unchanged) and
   reads `level/<clamp(characterLevel,1,maxLevel)>/damage` OrdinalIgnoreCase.
2. `CastSkill` passes `session.Status.Level` — the same level source the swing
   already hands to `DamageRoll` (`MapSimSessionStore.cs:1843`).
3. `ApplySkillHits` calls `DamageRoll.Roll(CombatRandom, Status.Level,
   MobLevel, MobDefence, damage, damage)`. The mob's `info/PDDamage` now applies
   to a skill; before this it applied to a swing and not to a skill, which is not
   a game anyone has played.
4. A plan with no `Damage` deals none. 175 of 535 skills carry the node; the 360
   that do not are mostly buffs, and null is reported as null (R-5).
5. Four mojibake comments in the same file repaired (`??six`, `??this` → em dash).

**Two honest limits, both stated in code rather than discovered by a user**

- The floor is the reference's (`Mob.cpp:1590-1601`), not a bug: `info/PDDamage`
  measures median 170 / p90 950 / max 30000 (n=409), so a level-1 character with a
  level-1 skill floors against a mob much stronger than it. It already behaved
  this way for swings.
- A `long` damage node is clamped, not cast — a cast past `int.MaxValue` would
  wrap to a *negative* damage.

### Verification

```
GET /api/skill-cast/plan?skillId=1121008   -> skillLevel=1 damage=135 multiStrike=False maxLevel=30
python %TEMP%\w061-verify.py                -> cast damage=[103] in-range=True legacy-random=False
                                                mob 2220100 hp 350 -> 247
                                              W-061 acceptance 1-3: PASS
GET /api/skill-cast/plan?skillId=10001009  -> damage=None
dotnet build -c Release                    -> exit 0, 0 errors 0 warnings
deploy.ps1                                 -> 59 wwwroot files identical; avatar.js md5 unchanged
python tools/pm.py gate                    -> RED SUITES: 0
```
