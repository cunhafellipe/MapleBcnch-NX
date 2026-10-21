---
id: W-039
title: Investigate a ladder 1px away that does not attach
state: blocked
priority: 55
owner:
opened: 2026-10-16
closed: 
depends: []
refs: "MapleCharacter.cs:541-585"
blocked_on: an explanation for nearestRope.dx=1 with onLadder=False
evidence: 
---

## Intent

`Map/Map1/105050107.img` reports nearestRope.dx = 1 with onLadder = False — the mirror of the reported 'attaches to anything'. Swept 25 random maps: 0 off-rope attachments, so the reported bug is not reproduced either.

## Reference

`MapleCharacter.cs:541-585`

## Acceptance

1. `# the nearestRope.dx=1 / onLadder=False case, explained or fixed`
2. `# 25-map sweep still reports 0 off-rope attachments`

## Result

BLOCKED ON: an explanation for dx=1 not attaching. This is the harder half and nobody has done it.
