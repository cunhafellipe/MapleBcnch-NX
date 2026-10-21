---
id: W-006
title: Map layer order: art z was flattened to zero
state: done
priority: 40
owner:
opened: 2026-07-01
closed: 2026-10-16
depends: []
refs: "MapEditorService.cs CanvasInt()"
blocked_on: 
evidence: artZ now resolves to 4 distinct values {-4:38, -3:100, -2:18, 0:97} over the test map; VISUAL confirmation is W-037, not this item
---

## Intent

Flat tiles drew over the walls and floor objects standing on them.

## Reference

`MapEditorService.cs CanvasInt()`

## Acceptance

1. `python ...\sweep-layers.py  # artZ histogram has >1 distinct value`

## Result

The tile depth was read with an `as WzIntProperty` cast that flattened every value to 0. Caught by fixing the layer bug, not by looking at it — the number was right and the picture was wrong.
