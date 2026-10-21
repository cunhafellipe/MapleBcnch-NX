---
id: W-015
title: Mobs engage the player
state: done
priority: 50
owner:
opened: 2026-07-01
closed: 2026-10-16
depends: [W-002]
refs: "MobAttack.cpp:20-127; Mob.cpp:1848-1858"
blocked_on: 
evidence: mob-attack-verify: mob closed 102.5px -> 47.5px and landed two hits
---

## Intent

Reported: mobs did not attack.

## Reference

`MobAttack.cpp:20-127; Mob.cpp:1848-1858`

## Acceptance

1. `mob-attack-verify.ps1  # the mob closes the distance on its own`

## Result

AttackPlayer was split into Engage (pursuit, no damage) and ContactDamage. Touch damage is `find_colliding` + `create_touch_attack` with NO aggro step at all (Stage.cpp:809-818) — the original design had an invented aggro gate.
