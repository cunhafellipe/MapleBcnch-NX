---
id: W-011
title: The character is drawn, not occluded by a mob
state: done
priority: 50
owner:
opened: 2026-07-01
closed: 2026-10-16
depends: []
refs: "Layers/Obj.cpp z-order"
blocked_on: 
evidence: player-visible.mjs: the player sprite is on screen among mobs
---

## Intent

Reported: the player was covered by mobs.

## Reference

`Layers/Obj.cpp z-order`

## Acceptance

1. `# visual: see W-037`

## Result

Layer ordering in the walk view.
