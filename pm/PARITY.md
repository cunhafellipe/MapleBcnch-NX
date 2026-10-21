# PARITY — the two references, and what has been verified

## The conflict rule

> **The C++ wins.** Studio rule 7.6.

The reason is not seniority. `clean-client` is the game client; `MapleNecrocer`
is a desktop tool that reads the archives. When they disagree about what the
game does, the client is right, and the tool is showing you something
convenient.

Record the disagreement in the work item. Do not average, and do not silently
pick — a reader who finds the C# formula in a C#-authored file has no way to
know it was overruled.

## Reference A — clean-client (C++), authority

```
C:\MUWORK\GAME\MPSH\clean-client\src\     598 files, 110,357 lines
```

The **continued Journey** client (GNU AGPL, Allendorf & Payton 2015-2019),
reading NX archives via `nlnx`, targeting the BeiDou server. Comments cite
v83/v95/v167 binary addresses.

Useful subsystems: `Gameplay/` 19,766 · `Character/` 13,612 · `Net/` 12,241 ·
`Data/` 3,160. `IO/` is 51,203 lines and almost entirely UI.

**Encoding.** Two different problems, and they need different answers:

| File | State |
|---|---|
| `Character/Inventory/Weapon.h`, `Character/Buffs/Buff.h`, `Buff.cpp` | genuinely GBK — recoverable via cp936 |
| `CharStats.cpp`, `Player.cpp`, `Mob.cpp` | valid UTF-8 whose Chinese is **already destroyed** with U+FFFD. Verified at byte level (`c4 ac ef bf bd cf b7`). **Unrecoverable.** ~28 comment lines. |

No formula depends on a destroyed comment. Do not budget time to recover them.

## Reference B — MapleNecrocer (C#), primary for tool behaviour

```
C:\MUWORK\GAME\_necrocer_src\MapleNecrocer\   4 projects, 367 .cs
```

A **stripped fork** of Kagamia's WzComparerR2, and this matters when you go
looking for something:

| Present | Absent — no source here at all |
|---|---|
| `CharaProp.GetSum` (CharaProp.cs:82-92) — stat aggregation | `close_totalstats` |
| the weapon damage table (Character.cs:576-626) | `get_attack()` |
| `Character` ctor, level-1 constants (Character.cs:14-25) | `Mob.calculate_damage` |
| the equip-stat node mapping (Gear.cs:840-854) | the mob-side damage formula |
| `MobTooltipRenderer` — the bestiary page | the hit-chance roll |

Searched and confirmed absent: `close_totalstats`, `calculateaccuracy`,
`totalstats`, `get_attack`, `struct Attack`, `mindamage`, `ignoredef`,
`calculate_damage`, `CalcDamage`, `hitRate`, `GetHitRate`, `LevelDiff` — all
zero matches across every `.cs`.

**Also:** `Character.cs:570` hard-codes `GearType.totem` in `CalcAttack`, so
the weapon table is unreachable from that call site. The table is correct; the
caller is not using it. That is why the C++ `CharStats.cpp:115-168` is the
formula this project implements.

UTF-8 **with BOM** — not GBK, contrary to first impression.

## Verified table — what has actually been ported, with its formula

| Behaviour | Reference | Status |
|---|---|---|
| Ladder / rope climb, top exit | MapleCharacter.cs:541-585 | verified, `W-001` |
| Forward jump horizontal term | MapleCharacter.cs:838-843 | verified |
| Ledge grab / hang | MapleCharacter.cs:541-585 | verified |
| Attack stance table | CharLook.cpp:648, :682-696 | verified, `W-002` |
| Weapon attack clock | WeaponData.cpp:32-35 | verified, `W-009` |
| Mob attack list execution | MobAttack.cpp:20-127 | verified, `W-002` |
| Touch damage = collide + roll | Stage.cpp:809-818 | verified, `W-015` |
| Mob pursues on `firstAttack` | Mob.cpp | verified, `W-015` |
| Mob element resistance digits | ElementDamage.h:9-15 (v95 0x7231e0) | measured, `W-004` |
| Mob element letters | Mob.cpp:2122-2128 | measured, `W-004` |
| Mob stat nodes (`level`/`maxHP`/`exp`/…) | Mob.cpp:35-90 | measured, `W-004` |
| Skill pose = body image delays | MapleCharacter.cs:1593-1600 | verified, `W-003` |
| Equip stat nodes | EquipData.cpp:43-57 + Gear.cs:840-854 | measured, `W-005` |
| Weapon → afterimage class | WeaponData.cpp:52 → Afterimage.cpp:39 | **fixed** `W-005` |
| Level-1 sheet | Character.cs:14-25 | **fixed** `W-005` |
| Beginner damage formula | CharStats.cpp:118, :172 | **implemented** `W-005` |
| Accuracy derivation | CharStats.cpp:186-192 | **implemented** `W-005` |
| Mob defence vs a swing | Mob.cpp:62, :1577-1601 | **implemented** `W-005` |
| Bestiary page | MobTooltipRenderer.cs:29-330 | **implemented** `W-004` |

## Known dead ends — do not re-walk

- **`ignoredef` / Ignore Defense.** The C++ *writes* it (Player.cpp:1429) and
  reads it only in the stat window (UIStatsInfo.cpp:343). `Mob::calculate_*`
  never references it, so it has no effect on any damage that client computes.
  It is a dead zero. There is nothing to port.
- **Level-up from EXP.** Never computed client-side in either reference.
  `ExpTable` is a hardcoded 201-entry array used *only* for the XP bar
  percentage. This project's `ExpToNextLevel` is declared own-design.
- **Drop tables.** No drop table exists in the C++ client at all. `MapDrops` is
  presentation; the server authors every drop. Nothing to port.
- **The C# inventory / EXP forms.** `AfrmItem` (1116 lines), `AfrmStat` (1022),
  `AfrmEquip` (572) and `CharaSim.Character` (919) have **zero** construction
  sites. A gap analysis that listed their systems as missing features was
  wrong, and the correction is recorded in `JOURNAL/2026-10-16-0630`.
- **`Player::change_level` does not call `recalc_stats`.** Level-up does not
  recompute min/max damage in the reference. Do not "fix" this.

## Archive facts, measured

Read only through `/api/inspect`, 2026-10-16.

| Fact | Value |
|---|---|
| `Mob.nx` images | 2,053 |
| `String.nx/MonsterBook.img` entries | 344 (333 seven-digit keys, 11 six-digit) |
| `info/level` / `maxHP` / `exp` | 343/343 · 343/343 · ~320/343 |
| `info/PDDamage` / `MDDamage` | 342/343 |
| `info/elemAttr` | 60/343 |
| `info/category` | **14/343**, values 0/1/7/8 only |
| `info/icon` | **0/2053** |
| attribute-EXP nodes (`charismaEXP` …) | **0/343** |
| `Character.nx` equip folders | 17; equip images 8-digit (`01002000.img`) |
| `Afterimage/` weapon classes | 15 (axe, barehands, swordOL, …) — by CLASS, never by item id |
| equip `inc*` nodes | 840 sampled: PDD 326, DEX 134, MDD 129, STR 123, LUK 105, INT 70, PAD 69, MMP 62, EVA 42, MHP 39, ACC 31, Speed 27, MAD 18, Jump 15 |
| `r`-suffixed rate nodes, merged keys, `incHANDS` | **0** |
| `incLUk` (lower-case k) | 1 glove — reads must be case-insensitive |
| `incRMAI` / `incRMAL` | 1 wand — in neither reference's table; ignore |
---

## Citations that name a file outside clean-client — CHECK THE PREMISE

**`Skill.cs`, `MapleCharacter.cs`, `PlayerStates.cs` are NOT in `clean-client`.**

`MapleCharacter.cs` / `PlayerStates.cs` belong to MapleNecrocer (the C# tool).
They were accepted for a long time and several of their rules turned out to
disagree with the reference — the already-settled example is the skill effect's
per-frame delay, recorded in W-076.

`Skill.cs` is worse: the ENTIRE skill hit path here was ported from it, and it
was wrong in the ways ADR-0007 lists. Measured: 63 of 66 castable skills dealt
no damage, the reach box came from nodes that are numbers rather than boxes, and
targets were picked by "first overlap, then stop" where the reference picks the
NEAREST first.

**Rule:** a `path:line` citation must name a file that exists under
`clean-client/src`. If it does not, the citation is a defect in itself, and every
sibling citation from the same source is suspect until checked. Do not patch the
symptom a bad citation produces — check whether the citation is load-bearing for
the whole method.

`pm/PARITY.md` already says the C++ wins on conflict. What went wrong was that a
port was written and accepted without that check ever being applied to the
citations inside it.