---
id: W-007
title: Player does not fall through walls at a low ledge boundary
state: blocked
priority: 45
owner:
opened: 2026-10-16
closed: 
depends: []
refs: "Physics.cpp, FootholdTree.cpp"
blocked_on: a map id that reproduces it (see W-038)
evidence: 
---

## Intent

Reported: walking off a low flat area drops the character out of the map instead of being stopped by a higher screen.

## Reference

`Physics.cpp, FootholdTree.cpp`

## Acceptance

1. `# reproduce first: python ...\wallblock.py against a map that shows it`
2. `# the debug fields footholdAt / nearestLadder make the diagnosis one call`

## Result

Swept 73 walkable spans on the test map and found no fall-out, so the reported map is not the one under test. BLOCKED ON: a map id that reproduces it.
