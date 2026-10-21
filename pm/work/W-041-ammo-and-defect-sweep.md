---
id: W-041
title: Sweep every open mob and item for defects of the classes already found
state: building
priority: 65
owner: 
opened: 2026-10-16
closed: 
depends: [W-005]
refs: [Mob.cpp:59-61, Mob.cpp:160, Animation.cpp:58-61, Rectangle.h:30, Extension.cs:47-50, Npc.cs:67]
blocked_on: 
evidence: Clause 1 DONE: 37 sites swept, 33 already correct, 4 defects fixed and re-measured - IconService.cs:127 and database.js:96 item entry D8-only (f7/Special is 4/584 eight-digit; 0910.img is 538 SEVEN-digit, 9100000 -> 200 vs 09100000 -> 404), database.js:84-87 skill book/entry (now 535/535 resolve, was 510/535, per PassiveBuffs.cpp:229-238), ItemCategories.cs:146 pet 0500.img (0 of 155 exist). Clause 2 NOT DONE and left open: the sweep attributes a node name to the archive its comment mentions, so weapon nodes read as absent from Mob.nx - spot checks show info/attackSpeed and info/afterImage are f1/Weapon/01302000.img nodes, both 200.
---
## Intent

W-005 found three id-width defects and one wrong-node defect in one sitting. The same classes almost certainly exist elsewhere and nobody has swept for them.

## Reference

The sweep is data work against the archive, but each candidate is judged against
the client:

- `Mob.cpp:59-61` — `watk = info["PADamage"]; matk = info["MADamage"];`
- `Mob.cpp:160` — `attack.power = attack.magic ? (data["MADamage"] ?
  data["MADamage"].get_integer() : matk) : (data["PADamage"] ?
  data["PADamage"].get_integer() : watk);`
- `Animation.cpp:58-61` — `Frame::Frame(nl::node src) { texture = src; bounds =
  src; ... }`
- `Rectangle.h:30` — `Rectangle(nl::node source) : left_top(source["lt"]),
  right_bottom(source["rb"]) {}`
- `MapleNecrocer/Extension.cs:47-50` — `IntID()` is `Number.ToInt().ToString()`
- `Npc.cs:67` — `"String/Npc.img/" + ID.IntID()`

## Acceptance

1. `# every id-width lookup in the codebase tries all widths, or is documented as not`
2. `# every archive node name in a comment is verified against /api/inspect`

## Result

### Clause 1 — DONE. Four defects found, all fixed, all re-measured.

A full census (not a sample) of every image-name and key-width in the open
archives, then every site that builds a key from an id: 37 sites, 33 already
correct. Raw: `scratch\idwidth\FINDINGS.md` + ~30 scripts.

Measured archive facts, 100 % censuses:

| pool | n | widths |
|---|---|---|
| mob images `f5` | 2053 | **7 = 2053** |
| npc images `f6` | 7198 | **7 = 7198** — the npc *id* is 4-digit, the *image* is 7 |
| equip images `f1` | 7176 | **8 = 7176** |
| map images `f4` | 5366 | **9 = 5366** |
| `f3/MonsterBook.img` | 344 | 6 = 11, 7 = 333 |
| `f8/<book>.img/skill` | 535 | **7 = 353, 8 = 182** |

**D1 — `IconService.cs:127`, item entry key was D8-only. 543 ids missed.**
`f7/Special` is 4 of 584 eight-digit: `0900.img` holds 4, but `0910.img` holds
**538 seven-digit** keys and `0911.img` five. Verified by hand:
`f7/Special/0910.img/9100000` → **200**, `…/09100000` → **404** — and 404 is
exactly the key the line asked for. Now tries D8 then bare.
Reference: `UICashShop.cpp:1111` uses `std::to_string(commodity.itemid)` —
unpadded; `ItemTooltipRender2.cs:78` formats `{1}` with no width. Contrast
`MapDrops.cpp:40-45`, whose literal 8-digit keys belong to `0900.img`, the one
Special image that *is* 8-digit. So both widths, not a swap — D8 still wins for
5,450 of 5,554.

**D2 — `database.js:96`, the same key, second implementation.** Same fix. It
degrades rather than failing (falls back to the bare `.img`), so it was silent.

**D3 — `database.js:84-87`, skill book and entry. 25 of 535 unreachable.**
The archive's book stems are 3-digit for 45 of 76 books, and the entry is
zero-padded to 7 below 10⁷ and bare at/above — 353/182, which is exactly what
`PassiveBuffs.cpp:229-238` computes (`extend_id(id,7)` below 1e7 else
`to_string`; book `extend_id(id/10000,3)`). The old candidates tried `0.img` and
`0000.img`; verified `f8/000.img/skill/0000008` → **200**, `f8/0000.img/…` →
**404**, `f8/0.img/…` → **404**, and entry `0000008` → **200** while `8` → **404**.

> **VERIFIED AFTER THE FIX: `535 / 535` skill entries resolve** (was 510/535).
> And in `f7/Special`, **547/547** numeric entries are now named by the two
> spellings (D8 alone named 4).

**D4 — `ItemCategories.cs:146`, pets got a group image that does not exist.**
`(5000000/10000).ToString("D4")` = `"0500.img"`; measured **0 of 155** Item.nx
images is named that, while all 54 pets are 7-digit per-pet files. No caller
exists (repo-wide grep for `DataImageName` returns only the definition), so this
is a trap for the next caller rather than a live bug. Fixed anyway, with the
measurement in the comment.

### Clause 2 — NOT DONE. The sweep cannot answer the question as asked.

A second sweep enumerated node names from comments and measured them against
Mob.nx `info`. It is **not** usable as a verdict list, and the reason is worth
more than the list:

> It attributes a name to the archive its *comment* seems to mention. Comments
> routinely name a node belonging to a **different archive**, and then the
> name reads as absent. Spot-checked, both of the load-bearing-looking hits are
> false:
>
> - `info/attackSpeed` — "ABSENT 0/2053" in Mob.nx. It is a **weapon** node:
>   `f1/Weapon/01302000.img/info/attackSpeed` → 200, Long, value 4.
> - `info/afterImage` — same story: `f1/Weapon/01302000.img/info/afterImage` →
>   200, String, `swordOL`.
>
> Same for `ball` (documented as `attackN/info/ball`, not `info/ball`) and
> `bgm` (Map/Sound cites, not Mob).

So clause 2 stays open. Doing it honestly means resolving each name's archive
from the code that *reads* it rather than from prose near it, and that is a
different sweep. Recorded rather than closed, because a green tick here would
mean the same thing this gate used to mean.

### Also found, and split out (`R-7`)

- **`W-058`** — `MapSimSessionStore.cs:1638` built the region folder as
  `Map{ToMap % 100}` (last two digits) where `Stage.cpp:315-318` says
  `mapid / 100000000` (first digit). Measured: `Map1/100000003.img` → 200,
  `Map3/100000003.img` → **404**. Every portal transfer silently failed. Fixed.
- **`W-059`** — `/api/cashshop/icon/{id}` answers 404 for **every** id, while
  the icons are present under those ids. Pre-existing, not caused by D1.
- **`W-056`** — the gate could not see two of its own suites fail.