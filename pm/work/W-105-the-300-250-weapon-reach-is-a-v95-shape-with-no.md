---
id: W-105
title: The port's 300/250 weapon reach is a v95 shape with no v83 basis: a known divergence
state: todo
priority: 40
owner: analyst-reverify
opened: 2026-10-25
closed:
depends: []
refs: C:\mbtest2\task78-v83-combat-truths.md; MapleBench/Services/Simulate/CombatService.cs; MapleBench/Services/Simulate/MapSimSessionStore.cs; MapleBench/Services/Simulate/PlayerStatus.cs; pm/decisions/ADR-0007-the-skill-hit-logic-came-from-the-wrong-program.md; docs/SIMULATOR-VERIFICATION.md; clean-client/src/Character/Stats/CharStats.cpp:54-57
blocked_on:
evidence: "Measured 2026-10-25. The reference source's own comment attributes the 300 to v95 (clean-client CharStats.cpp:54-56, read verbatim, 16282 bytes, decodes as UTF-8). task-70 measured that the v83 client has no reach table at all: 1220 weapon images with 0 reach-named nodes, the whole v83 string pool with 0 reach-ish names, and all 9 functions holding both 250 and 300 read (none is a reach table); 0x5CA659 is a price/production-cost table, not a range function. Behaviour unchanged: the ProjectileRange body hashed 58610CD2B1A4EDF5D45D996B7D9B6BD09552D87C0F63590E92F5298302F5DAD3 (352 bytes) before and after the comment change, and the file diff has 0 changed lines that are not /// comments. Build: 0 warnings, 0 errors. pm.py check: 80 items, 6 problems before and after (the 6 are pre-existing). Still open: two files outside this card's write scope still present the split as a reference rule."
---

## Intent

`pm/` had no field, no file and no convention for a KNOWN DIVERGENCE -- a place
where the port deliberately differs from the reference, the difference is
recorded instead of closed, and a later reader sees it in one place.
Measured (task-71): `grep -i divergence` over `pm/SPEC.yaml`, `pm/TRAPS.md`
and `pm/RULES.md` returns **0** hits.  `pm/decisions/` holds decisions that
were taken, `pm/work/` holds defects and features; neither is the thing this
card needs.  This file is that record, for one divergence: the port's 300/250
weapon reach.

## The divergence

**The port returns a 300/250 projectile reach.  The v83 client has no such
rule, and the number has no v83 first-hand basis.**  The port keeps it for
compatibility; this record exists so that no future change is built on the
false premise that it is a v83 rule.

**(1) The reference source says so itself.**  Read verbatim from
`C:\MUWORK\GAME\MPSH\clean-client\src\Character\Stats\CharStats.cpp`
(16282 bytes, no BOM, decodes as UTF-8), lines 54-57:

```
  54|     // v95 CSkillInfo::GetShootSkillRange (0x709650) gives bow/crossbow shoot
  55|     // attacks a base reach of 300 and adds skill 3000002's range bonus
  56|     // (远程箭, +15 per level). Other weapons keep the previous 250 fallback.
  57|     projectilerange = Weapon::is_bow_or_crossbow(weapontype) ? 300 : 250;
```

The source's own comment attributes the **300 to v95** and calls 250 "the
previous fallback".  A v95 number sitting in a v83-shaped client is not a v83
number.  (The three CJK characters on line 56 are intact; the two U+FFFD on
line 57 are after the semicolon and are not quoted.)

**(2) task-70 measured that there is no v83 reach table to find.**  Receipt:
**`C:\mbtest2\task78-v83-combat-truths.md`** (task-70, analyst-nx), sections 1
and 7 -- three complete censuses, all agreeing:

- **data**: 1,220 weapon images under `Character/Weapon`, 0 nodes named
  range / reach / projectile / dist / distance;
- **names**: the whole v83 string pool, 0 hits for reach / projectile / melee /
  attackrange / shootrange / weaponrange;
- **numbers**: all 9 functions holding both 250 and 300 were read; not one is a
  weapon-type to reach table.

The function this port's comment used to cite, `0x5CA659`, is **not a range
function**: it computes a price / production-cost value and compares it against
a meso limit ("You do not have enough mesos.").  task-70's ruling was to
close path A (implement a v83 rule) and keep the port's numbers.

## What is still NOT established

**The v83 client's own character reach is unknown.**  The censuses above say
there is no reach TABLE in the client; they do not say what the client does
instead.  The closest candidate is the 250 in three v83 area builders
(0x933990, 0x9537D5, 0x981211), which shrink a box by 250 on one side and 100
on the other -- **not proven** to be the reach.  Also not established, and not
guessed here: whether the v83 client derives the weapon TYPE the same way
(the equip id prefix, `WeaponData.cpp:28-29`); where the v83 client consumes
the skill level's `range` node (`0x75F464` stores it in a separate field, the
consumer was not located); whether the v83 box is scaled by `range` at all
(task-70 measured that the v83 box builder 0x75CD52 -> `SetRect` does NOT
scale it, while the port scales the near edge -- a v95 shape, `Combat.cpp:660`).

## The ruling: NO BEHAVIOUR CHANGE

**`CombatService.ProjectileRange` still returns 300 for a bow or crossbow and
250 otherwise.**  This item changes a COMMENT, not a number.

1. There is no v83 rule to implement, so "fixing" the numbers would mean
   inventing a rule -- worse than keeping a documented compatibility value.
2. The 250/300 pair is what the rest of the port is calibrated against (the
   near edge of a skill's box, `MapSimSessionStore.cs:2402`), so changing it
   is a behaviour change with its own regression surface.
3. So the divergence is recorded here, and any change to the numbers gets its
   own card with its own evidence.

## What this card changed

1. `MapleBench/Services/Simulate/CombatService.cs`, the `<remarks>` of
   `ProjectileRange` -- the only file and segment this card was allowed to
   touch.  The old text presented `CharStats.cpp:57` as the reference rule;
   the new text says the v83 mechanism is **not established**, that the
   300/250 is a **v95 shape kept for compatibility**, quotes the source's own
   v95 comment, cites the task-70 receipt, and points at this item.
2. This file.

The function body is byte-for-byte unchanged -- hashed before and after from
the signature line to the closing brace:

```
BEFORE body sha256 : 58610CD2B1A4EDF5D45D996B7D9B6BD09552D87C0F63590E92F5298302F5DAD3  352 bytes
AFTER  body sha256 : 58610CD2B1A4EDF5D45D996B7D9B6BD09552D87C0F63590E92F5298302F5DAD3  352 bytes
$ git diff -U0 -- MapleBench/Services/Simulate/CombatService.cs | grep -c '^[+-]'
70
changed lines that are NOT '///' comments : 0
```

## What is still open (measured)

**CLOSED by task-175 (2026-10-25).**  The four sites listed as "still present
it as a reference rule" below were fixed: `MapSimSessionStore.cs` (both
comment blocks), `PlayerStatus.cs` (the citation block), `docs/SIMULATOR-VERIFICATION.md`
(the verbatim quote KEPT, with the port's status and "v83's reach mechanism
is unknown" added next to it), and `ADR-0007` (a DATED VERSION NOTE APPENDED,
its historical text byte-for-byte unchanged -- an ADR records what was decided
and why, and the wrongness is the useful part of that record; the DECISION
itself, which was about the hit BOX, is not overturned).

The stale citation found while reading those files is closed too:
`PlayerStatus.cs`'s "This port uses 250 -- the fallback, not the v95 number"
was a false statement about the product's own behaviour (the value comes from
`CombatService.ProjectileRange`, which returns **300** for a bow or crossbow:
`MapSimSessionStore.SetEquipped` sets it, `BuildStatus` sets it) and now
describes what the code actually does.

`git grep -n 'CharStats.cpp:57'` after task-175, whole repo, and what each
survivor is:

```
MapSimSessionStore.cs:2396   port comment, now carries the version note
MapSimSessionStore.cs:3156   port comment, now carries the version note
MapSimSessionStore.cs:3251   port comment, now carries the version note
PlayerStatus.cs:90           a verbatim source quote inside a <code> block, with the note adjacent
ADR-0007:51                  the HISTORICAL original, left byte-for-byte alone
ADR-0007:108                 the appended dated version note
W-105 (this file)            its own record, which quotes the old text on purpose
```

⇒ **No site left in the repository presents the 300/250 split as the
reference rule.**  The `CombatService.cs` remark (the site this item was
opened for) carries the note too.

### STILL OPEN

1. **The v83 reach mechanism itself is still unknown.**  That is the real
   open question, and no amount of editing a comment closes it: task-70's
   three censuses (1,220 weapon images, the whole name string pool, and the
   nine functions containing both 250 and 300) all found no v83 range table,
   and `0x5CA659` is a price / production-cost value.  The reference's own
   comment attributes the 300 to v95.  ⇒ A v83-shaped mechanism may exist
   and has not been found yet.
2. **Six reach tags still say `W-084.`** -- `CombatService.cs:951` and
   `SkillCastService.cs:319/399/879/1661/1839`.  `pm/work/W-084-*.md` is
   "A mob's hit effect was published for one frame" -- a different subject --
   and the tag never named a reach item (W-084 was ADDED, not renamed, in
   `dfff4d4`).  These point at nothing.  A separate card (W11-TAGS) owns
   them, because task-175's write scope forbade both files.

## The priority number

`40`, with the rule measured rather than guessed: `pm.py:467-474` says "Lower
is sooner".  The open items' priorities, measured over the 80 items in
`pm/work/`, are 7, 25, 30, 35, 40, 45, 55, 65, 75, 88, 90, 92 -- so 40 puts
this ahead of the general backlog and behind the live product defects (7-30).
It is a judgement; the numbers above are measurements.

## How to reproduce

`python C:\mbtest2\task79-read-charstats.py` (the source, verbatim),
`python C:\mbtest2\task79-bytes.py` (the byte facts),
`python C:\mbtest2\task79-fnbody.py` (the two body hashes),
`git grep -n 'CharStats.cpp:57'` and `git grep -n 'projectilerange'` (the
census), `python C:\mbtest2\task79-census.py` (the work-item conventions).

## Still open

- The v83 reach mechanism itself: unknown, and task-70 closed the search.
- The three citations above that still need the version note.
- The two stale/broken citations.
- task-70's section 6: the `(a)` vs `(b)` question for the v83 hit box, and
  where the v83 client consumes the skill level's `range` node.
