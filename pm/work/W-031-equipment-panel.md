---
id: W-031
title: An equipment panel that feeds the sheet
state: done
priority: 9
owner: 
opened: 2026-10-16
closed: 2026-10-16
depends: [W-005]
refs: EquipSlot.cs:27-60; CharaEquip.cs:11-16
blocked_on: 
evidence: equipset-verify.py BAD=0 — bare 50hp/acc6/dmg1-1, +sword watk17/dmg2-11, +longcoat+glove hp55 str16 acc9 def90-20 dmg3-15, stripping restores all; gate 12/12 green; three-way md5 equal
---
## Intent

GET /api/simulate/equip-stats reads one item; nothing lets a person choose which items are on.

## Reference

`EquipSlot.cs:27-60; CharaEquip.cs:11-16`

## Acceptance

1. `python ...\equipstats-verify.py  # per-item node reading`
2. `python ...\equipset-verify.py   # the SET moves the sheet, and strips correctly`
3. `# two slots filled, sheet reflects both`

## Result

The picker and the 14-slot equipment bar already existed and already changed
what the character LOOKS like. What was missing: only `Weapon-1` reached the
server, so every other piece was cosmetic. Measured why that matters — over 840
sampled items: `incPDD` on 326, `incDEX` 134, `incMDD` 129, `incSTR` 123,
`incLUK` 105, `incINT` 70, `incMMP` 62, `incEVA` 42, `incMHP` 39, `incACC` 31.

- `POST /api/simulate/session/{id}/equip` — the whole set, as ONE REPLACEMENT.
  A patch could not work: the server could not tell "removed" from "never
  equipped", and the sheet is `base + sum over the inventory`
  (Player.cpp:309-313), so a stale entry is a wrong number.
- `/session/{id}/weapon` stays as the one-slot convenience and now delegates to
  the same path, so the two cannot disagree about what "equipped" means.
- `MapSimSession.EquippedItems` holds the set; `BuildStatus` applies each item in
  turn, the reference's own shape (`Inventory::recalc_stats` then
  `Player::recalc_stats`).
- Client: `LiveWalk.setEquipment(slots)`; `chooseEquip` posts the whole slot map
  for every non-weapon slot, keeping `setWeapon` for `Weapon-1` because the
  weapon is also a physics fact (`info/attack` selects the stance group).

**Measured (equipset-verify.py BAD=0), items read from the archive first:**

| | hp | str | dex | luk | watk | acc | damage | def |
|---|---|---|---|---|---|---|---|---|
| bare | 50 | 12 | 5 | 4 | 0 | 6 | 1-1 | 0/0 |
| + sword 1302000 | 50 | 12 | 5 | 4 | 17 | 6 | 2-11 | 0/0 |
| + longcoat 01050000 + glove 01082143 | 55 | 16 | 8 | 7 | 17 | **9** | 3-15 | **90/20** |

Accuracy 9 is `trunc((5+3)*0.8 + (4+3)*0.5)` — the glove's `incDEX 3` plus its
`incLUk` 3, read case-insensitively. Defence 90 is the longcoat's 60 plus the
glove's 30. Stripping the armour restores every one of those, which is the
replacement semantics being observable. `Head`/`Body`/`Hair-1`/`Face-1` are
filtered and contribute nothing.

**Three defects found while building it, all fixed:**

1. **`MaxHp`/`MaxMp` did not accumulate.** `ApplyEquip` assigned
   `MaxHp = BaseMaxHp + stats.Hp`, an ABSOLUTE value, so the second item reset
   the first's contribution — every other stat uses `+=`. Measured before the
   fix: `maxHp 50 → 50 → 50` across bare / +sword / +longcoat. Now computed over
   `GearHp`/`GearMp` accumulators, as `Accuracy` already is.
2. **`Start` wiped the weapon it had just been given.** The new `SetEquipped`
   call at the end of `Start` replaced the whole set with the (absent) `items`
   map, so `attack-test.ps1` reported `attackStance was EMPTY on every swing` on
   the very next run. The weapon now seeds the set and an explicit `items` map
   replaces it wholesale.
3. **The gate did not catch #2.** `attack-test.ps1` printed a negative VERDICT
   and `gate.ps1` still reported the run green, because no red marker matched —
   the exact failure ADR-0003 warned about, happening immediately. Every failing
   branch in that suite now prints `>>> RED` explicitly.

Not proven here: that the sheet moves on screen. That is W-037.
