---
id: W-003
title: Skill poses load the character's own image
state: done
priority: 20
owner:
opened: 2026-07-01
closed: 2026-10-16
depends: [W-001]
refs: "MapleCharacter.cs:1593-1600 DataS[skill/action]"
blocked_on: 
evidence: skill-lock-verify: 55/55 skills lock to their declared pose length
---

## Intent

Casting a skill played the character's standing pose instead of the skill's.

## Reference

`MapleCharacter.cs:1593-1600 DataS[skill/action]`

## Acceptance

1. `node MapleBench/tools/sim-skills-check.mjs  # 24 passed, 0 failed`
2. `skill-lock-verify.py  # 55/55`

## Result

SkillCastService gained Action / ActionOf / PoseMs. PoseMs reads body-image frame delays where `delay` is a CHILD of the frame node. The cast lock uses PoseMs, not plan.EffectMs: the sprite length was wrong for 44 of 55 skills — 17 too short (cut off) and 27 too long (frozen pose, next cast refused).
