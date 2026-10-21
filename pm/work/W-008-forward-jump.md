---
id: W-008
title: Jump carries horizontal motion
state: done
priority: 35
owner:
opened: 2026-07-01
closed: 2026-10-16
depends: []
refs: "MapleCharacter.cs:838-843"
blocked_on: 
evidence: jump-check / sweep-jumpup: a jump from flat ground advances along x as well as y
---

## Intent

Jump went straight up. Pressing a direction and jumping moved the character vertically only.

## Reference

`MapleCharacter.cs:838-843`

## Acceptance

1. `python ...\jump-check.py  # a jumping frame advances x as well as y`

## Result

StepCore's rising branch returned immediately after integrating vertical motion, skipping the foothold-angle horizontal term.
