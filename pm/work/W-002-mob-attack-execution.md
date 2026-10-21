---
id: W-002
title: Mob attack list executes: available, random pick, attackAfter settle
state: done
priority: 10
owner:
opened: 2026-07-01
closed: 2026-10-16
depends: [W-001]
refs: "MobAttack.cpp:20-127, :88-118"
blocked_on: 
evidence: first-attack-verify PASS; mob-attack-verify: mob closed 102.5px -> 47.5px and landed two hits, player 50/50 -> 10/50
---

## Intent

A mob standing next to the player played an attack pose and did no damage. The attack list existed in data and was never run.

## Reference

`MobAttack.cpp:20-127, :88-118`

## Acceptance

1. `first-attack-verify.ps1  # PASS: it attacked without being struck`
2. `mob-attack-verify.ps1  # mob closes the gap on its own after one landed hit`

## Result

BuildAvailable / AdvanceAttack / AttackStance / PendingAttackPower / ConsumeAttackDamage. ReadInt had to gain WzLongProperty: under NX `type`, `PADamage`, `conMP`, `attackAfter` and every frame `delay` arrived as Long and read as 0 while `range` (a Vector) stayed correct — so a geometry spot-check passed and the damage and timing did not.
