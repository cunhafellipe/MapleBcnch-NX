---
id: W-014
title: Damage numbers appear where the swing lands
state: done
priority: 50
owner:
opened: 2026-07-01
closed: 2026-10-16
depends: [W-005]
refs: "Combat/DamageNumber.cpp:107-113"
blocked_on: 
evidence: attack-test: 8 delayed frames carry 8 damage numbers [1,1,1,1,1,1,1,1]
---

## Intent

Reported: no damage numbers.

## Reference

`Combat/DamageNumber.cpp:107-113`

## Acceptance

1. `attack-test.ps1  # damage numbers on the DELAYED frames: 8`

## Result

The numbers were already computed and already on the wire; nothing was reaching them because no swing connected (W-005). The gate that claimed otherwise was T-1.
