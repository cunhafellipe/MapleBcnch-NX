---
id: W-009
title: Swing speed follows the weapon
state: done
priority: 35
owner:
opened: 2026-07-01
closed: 2026-10-16
depends: [W-002]
refs: "WeaponData.cpp:32-35; Char.cpp:607-613"
blocked_on: 
evidence: attack-test: pose 616.67ms and delay 15-20 ticks on the Str sword, against a flat 48-tick fallback before
---

## Intent

Every weapon swung on the same 1.7x clock regardless of the weapon's own `info/attackSpeed`.

## Reference

`WeaponData.cpp:32-35; Char.cpp:607-613`

## Acceptance

1. `attack-test.ps1  # swingPoseMs differs per weapon`

## Result

CombatService.WeaponAttackSpeed reads the node, SwingPoseTicks/SwingHitDelayTicks consume it. The id-width bug that made it return 0 for every weapon is documented there and is the reason all three id widths are now tried in AttackStancesFor and AfterimageName too.
