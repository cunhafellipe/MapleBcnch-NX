---
id: W-005
title: Equipment -> attributes -> damage, and the attack box that was never the weapon's
state: done
priority: 5
owner:
opened: 2026-07-01
closed: 2026-10-16
depends: []
refs: "CharStats.cpp:62,:118,:172,:186-192; EquipData.cpp:43-57; WeaponData.cpp:52; Afterimage.cpp:39; RegularAttack.cpp:49; Character.cs:14-21"
blocked_on: 
evidence: equipstats-verify.py BAD=0 over 6 cases; sheet-verify.py BAD=0 — base 12/5/4/4, 50/50, 10/10, unarmed 1/1, sword incPAD=17 -> max 11 min 2, observed swings 1..3 against a mob with PDDamage=10, mob HP 350 -> 340
---

## Intent

Equipping a weapon changed the pose and the clock and nothing else. The damage roll read two flat constants and the mob's HP, and the attack box was the bare-hands one — 37x15, entirely behind the character — so NO swing ever connected.

## Reference

`CharStats.cpp:62,:118,:172,:186-192; EquipData.cpp:43-57; WeaponData.cpp:52; Afterimage.cpp:39; RegularAttack.cpp:49; Character.cs:14-21`

## Acceptance

1. `python ...\equipstats-verify.py  # BAD=0, incLUk read, incRMAI ignored`
2. `python ...\sheet-verify.py       # BAD=0, 8 swings -> 8 damage numbers`
3. `attack-test.ps1  # damage numbers on the DELAYED frames: 8`

## Result

Five defects: (1) base attributes were 4/4/4/4, the reference's level-1 is 12/5/4/4 (Character.cs:14-21) and MaxMP was 0 not 10; (2) AttackBox built its path from the weapon ID and `Afterimage/01302000.img` does not exist — the folder holds 15 weapon CLASSES, so every lookup fell through to barehands; the mapping was in the data all along as `info/afterImage = swordOL`; (3) AttackStancesFor accepted only the caller's id spelling, so `1302000` returned an empty group -> no pose -> the hit never settled; (4) SetEquippedWeapon rebuilt the sheet and dropped Invulnerable; (5) DamageRoll's documented 'no wdef node' was wrong — the node is PDDamage, on 342 of 343 mobs.
