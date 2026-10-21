---
id: W-030
title: Show the character sheet in the walk view
state: done
priority: 8
owner: 
opened: 2026-10-16
closed: 2026-10-16
depends: [W-005]
refs: Character.cs:174-195
blocked_on: 
evidence: sheetfields-verify.py BAD=0, 18/18 fields on the wire; unarmed acc 0 -> 6 (calculateaccuracy now runs gear or not); walkview/livewalk/simulator.css three-way md5 equal; gate 10/10 green
---
## Intent

The sheet is correct and reaches the client as `frame.status`, but nothing draws it. A person cannot see that equipping a weapon changed anything.

## Reference

`Character.cs:174-195`

## Acceptance

1. `python ...\sheetfields-verify.py  # BAD=0 — all 18 rendered fields on the wire`
2. `python ...\sheet-verify.py  # must stay BAD=0`
3. `# W-037: the row is visible in the walk view and moves when a weapon is equipped`

## Result

The sheet is drawn in its OWN row, above the HUD's `·`-joined run-on line. Not
folded into the HUD: that line is a list of facts about the MAP and the VIEW,
and a reader who wants to know what a weapon just did should not find it
between "missing tile art" and a foothold count.

`renderSheet()` in walkview.js formats `frame.status` and computes nothing.
`LiveWalk.paint()` hands the sheet over beside `mobs`, for the same reason it
hands over mobs: a viewer that recomputed the sheet would show a character the
simulation is not running.

**Two defects found while wiring it up, both fixed:**

1. **Accuracy was 0 on an unarmed character.** `ApplyEquip` derived it
   (`CharStats.cpp:186-192`), but the derivation only ran when an item was
   found. `CharStats.cpp:73` runs `totalstats[ACC] += calculateaccuracy()` on
   every `close_totalstats`, gear or not, so a level-1 character has
   `trunc(5 * 0.8 + 4 * 0.5) = 6`. `Accuracy` is now a computed property over
   `GearAccuracy`, so it cannot be zero on a bare character and cannot compound
   when a second weapon is applied.

2. **`movementSpeed` is not a field.** The C# property is `MoveSpeed`, and
   `System.Text.Json` camel-cases it to `moveSpeed`. The renderer asked for a
   name the server never sends and would have drawn a dash forever.

**Measured, 18/18 fields on the wire** (sheetfields-verify.py BAD=0):
`hp 50/50 · mp 10/10 · str 12 · dex 5 · int 4 · luk 4 · watk 0 · acc 6 ·
damage 1-1 · def 0/0 · lv 1 · speed 100 · jump 100 · exp 0`, and equipping
`1302000` moves it to `watk 17 · damage 2-11`.

Not proven here: that the row looks right on screen. That is W-037.
