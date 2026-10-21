---
id: W-012
title: Mobs play their death animation and stop colliding
state: done
priority: 50
owner:
opened: 2026-07-01
closed: 2026-10-16
depends: []
refs: "Mob.cpp:1830-1836 finish_delayed_kill"
blocked_on: 
evidence: mob-attack-verify and first-attack-verify both observe the death state
---

## Intent

Reported: killed mobs did not animate their death.

## Reference

`Mob.cpp:1830-1836 finish_delayed_kill`

## Acceptance

1. `first-attack-verify.ps1  # reaches the dying state`

## Result

MobAnimation stance priority: patrol.AttackStance > die1 > hit1 > move > stand.
