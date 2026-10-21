---
id: W-001
title: Assertion surface: foothold / ladder / mob boxes / attack data
state: done
priority: 10
owner:
opened: 2026-07-01
closed: 2026-10-16
depends: []
refs: "MapSimSessionStore.cs debug/* endpoints"
blocked_on: 
evidence: GET /api/simulate/debug/mob-swing returns mobBoxes, footholdAt, nearestLadder, mobAttacks, clientArt; npc-label-check and every mob harness read them
---

## Intent

The mob and map simulations were unfalsifiable from outside: a caller could not ask where the geometry is, so every conclusion was an inference from a frame's motion.

## Reference

`MapSimSessionStore.cs debug/* endpoints`

## Acceptance

1. `python %USERPROFILE%\AppData\Local\hermes\cache\scratch\npc-label-check.ps1 -Port 5591 -Map 000000002.img`
2. `# prints per-mob boxes and the foothold under the character`

## Result

Endpoints live; 10 gate suites read them. Four of the traps in TRAPS.md were only diagnosable because these fields exist.
