---
id: ADR-0007
title: The skill hit logic was ported from the wrong program and is now rewritten from clean-client
status: DECIDED
date: 2026-10-17
supersedes: []
superseded_by: []
relates_to: [ADR-0005, ADR-0006]
---

# ADR-0007 — the skill hit logic came from MapleNecrocer, not from the reference

## Status

DECIDED, 2026-10-17. This is a correction, not a design decision.

## What happened

`ApplySkillHits` — the whole "which mobs does a cast hit, and how" method — was
ported from `Skill.cs`, cited as `Skill.cs:168-178`, `:208`, `:231`, `:441-458`,
`:486`, `:491`, `:509-510`. **`Skill.cs` is not a file in `clean-client`.** It is
MapleNecrocer, the C# tool, which `pm/PARITY.md` explicitly does not accept as
authority.

Four separate reports came out of it, and I answered each one with another patch
on top of the wrong port instead of noticing that the port itself was wrong:

| symptom | what I did | what was actually wrong |
|---|---|---|
| "skills deal no damage" | removed the `MultiStrike` guard | `MultiStrike` never existed in the reference |
| "casting twice shows no animation" | added `SkillCastSeq` | that one was a real bug, but unrelated |
| "the origin / per-frame offset is wrong" | could not reproduce | not caused by this |
| — | — | the reach box was read from the wrong nodes |

Three skills out of 66 could damage, because `MultiStrike(skillId)` is true for
every id except three and the method opened with `if (plan.MultiStrike) return;`.

## What the reference actually does

All of it in `clean-client`:

```cpp
// Combat.cpp:659   Rectangle range = attack.range;
// Combat.cpp:660   int16_t hrange = range.left() * attack.hrange;
// Combat.cpp:662   if (attack.toleft)
// Combat.cpp:664       range = { origin.x + hrange,  origin.x + range.right(), ... }
// Combat.cpp:673   else
// Combat.cpp:674       range = { origin.x - range.right(), origin.x - hrange, ... }

// CharStats.cpp:468  get_range() { return Rectangle<int16_t>(-projectilerange, -5, -75, 25); }
// CharStats.cpp:57   projectilerange = is_bow_or_crossbow ? 300 : 250;

// SkillData.cpp:69   uint8_t mobcount = (uint8_t)sub["mobCount"].get_integer(1);
// SkillData.cpp:77   float hrange = (float)sub["range"].get_real(100.0) / 100;

// Combat.cpp:1226-1272  find_closest: alive mobs whose animation bounds overlap
//                       the range box, in a multimap keyed by uint16 distance
//                       from the character, then the first `mobcount`.
// Mob.cpp:1875-1884    is_in_range == range.overlaps(bounds)
```

So:

1. **The reach is the CHARACTER's**, not the skill's: 250 forward starting 5 px
   out, 100 tall. A skill's own `range` node is a NUMBER that scales the near
   edge — not a collision box.
2. **`mobCount` comes off the skill**, default 1. There is no single-strike /
   multi-strike split. Measured on this archive: `1111008` is 6, `11111006` is 3,
   most are 1, `hrange` is 1 everywhere.
3. **Targets are the NEAREST first**, as many as `mobCount` — not "the first
   overlapping mob, then stop".
4. **The client does not compute damage at all.** `CharStats.cpp:435` reads
   `// TODO: Random stuff, need to find the actual formula somewhere`; the server
   owns the number. So ADR-0006's fixed 100 is closer to the reference than
   `DamageRoll.Roll` was, not further from it.

## Decision

`ApplySkillHits` is rewritten from `Combat.cpp:659-679` + `find_closest`.
`SkillCastPlan` carries `MobCount` and `HRange` read from the skill's own level
node. `PlayerStatus.ProjectileRange` carries the 250.

What survived from the old port and was correct all along: the box-vs-box test
(`Mob::is_in_range` is `range.overlaps(bounds)`), the flat test damage, and the
"no damage node, no damage" rule.

## Why this is recorded as a decision at all

Because the failure was not a bug — it was me citing a source `PARITY.md` had
already ruled out, and patching the consequence instead of checking the premise.
A reader who finds `Skill.cs` cited anywhere in this codebase should treat every
sibling citation as suspect.

## Measured after the rewrite

```
1121008   mobCount=1 hRange=1  damage=135
11111006  mobCount=3 hRange=1  damage=46
1111008   mobCount=6 hRange=1  damage=11
11111004  mobCount=1 hRange=1  damage=115

mob BEHIND (character faces right, gap 93), cast -> mobHp 1000   miss
turn to face it (gap 95),                cast -> mobHp  900   hit
```

## Version note (2026-10-25) -- the 300:250 quoted above is a v95 shape, not a v83 rule

The `CharStats.cpp:57` line quoted under "What the reference actually does" was
read, when this ADR was written, as the reference's rule.  It is not one.

Two measurements, and they are the two the code now carries:

- The reference source's own comment at `CharStats.cpp:54-56` attributes the
  300 to v95 (`CSkillInfo::GetShootSkillRange`, 0x709650) and calls 250 "the
  previous fallback".  A v95 number sitting in a v83-shaped client is not a
  v83 number.
- task-70 (`C:\mbtest2\task78-v83-combat-truths.md`) measured, with three
  complete censuses, that the v83 client has no reach table at all -- 1,220
  weapon images with no reach-named node, the whole v83 string pool with no
  reach-ish name, and all 9 functions holding both 250 and 300 read, none of
  them a weapon-type to reach table.  The function the port's comment used to
  cite here, `0x5CA659`, is a price / production-cost table, not a range
  function.  **The v83 client's own reach mechanism is therefore UNKNOWN.**

**The decision above is not overturned.**  It was about the hit BOX -- which
rectangle, which multiplier, how many targets -- and that is a separate
question task-70 did not overturn.  What changes is the status of the 300:250
pair: it is a **v95 shape kept for compatibility**, and the port's numbers are
unchanged (`CombatService.ProjectileRange` still returns 300 for a bow or a
crossbow and 250 otherwise).  The divergence is recorded in
`pm/work/W-105-the-300-250-weapon-reach-is-a-v95-shape-with-no.md`.

The historical text above is left exactly as it was written.  An ADR records
what was decided and why; rewriting it would hide the mistake this note
exists to record, and the mistake is the useful part.
