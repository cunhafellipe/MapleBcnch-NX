---
id: W-016
title: Summon a mob from an icon picker
state: done
priority: 50
owner:
opened: 2026-07-01
closed: 2026-10-16
depends: []
refs: "MobForm.cs:107-124"
blocked_on: 
evidence: 2053 mobs listed, 1543 with a stand pose; the picker now also shows the bestiary
---

## Intent

Reported: the summon UI had no picker.

## Reference

`MobForm.cs:107-124`

## Acceptance

1. `# visual: see W-037`

## Result

`info/icon` is absent on 0 of 2053 mobs, so the tiles show `stand/0`. That is the honest substitute; an invented icon sheet would not be.
