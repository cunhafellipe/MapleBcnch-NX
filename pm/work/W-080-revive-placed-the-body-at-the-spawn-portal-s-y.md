---
id: W-080
title: revive placed the body at the spawn portal's Y, which is not the ground
state: done
priority: 88
owner: 
opened: 2026-10-17
closed: 2026-10-17
depends: []
refs: MapSimSessionStore.cs Revive; CharacterPhysics.cs:136-151 and :202-215; revive-verify.py; pm/decisions/ADR-0006
blocked_on: 
evidence: |
  session spawn x=0 y=95; after revive x=0 y=-2 onGround=True; 20 frames of Right moved 0 px.
  After the fix: after revive x=0 y=95; x 0 -> 49.
workstream: simulate
kind: defect
risk: med
---

## Intent

A character that dies and is revived must be standing on the map, able to
walk. Today, on any map whose spawn portal is not sitting on the ground, the
revived body is placed at the portal's own Y — in mid-air, `OnGround = true`,
with the foothold it claims 97 px below it. Every movement step is then
rejected by the foothold test and the character cannot walk at all.

## Reference

`Player::respawn` (`Player.cpp:210-251`) as recorded in
`CharacterPhysics.Respawn`'s own doc comment: *"Nothing about the old spot may
carry over to the new one."* The reference respawns onto a foothold; nothing in
it suggests the portal's raw Y is a body position.

## Approach

1. `CharacterPhysics.Respawn(foot, x, y)` takes the position verbatim
   (`CharacterPhysics.cs:202-207`), while the constructor derives it from the
   foothold's own end (`_y = start.Y1 / start.Y2`, `:147-148`). `Start` uses the
   constructor; `Revive` used `Respawn`. That asymmetry is the defect.
2. In `MapSimSessionStore.Revive`, keep the portal's X when it is over the
   foothold and take the Y from the foothold:

   ```csharp
   FootholdPhysics.Foot f = foot.Value;
   int placeX = x < f.X1 ? f.X1 : x > f.X2 ? f.X2 : x;
   int placeY = placeX <= (f.X1 + f.X2) / 2 ? f.Y1 : f.Y2;
   session.Body.Respawn(f, placeX, placeY);
   ```

   `Respawn` is not changed: portal transfer relies on the exact coordinates it
   is given.

## Acceptance

```
# on f4/Map/Map0/000000000.img
#   session spawns at (0, 95) and walks; after a revive the body is at (0, 95)
#   and 20 frames of Right move it ~49 px
python %LOCALAPPDATA%\hermes\cache\scratch\revive-verify.py   # BAD=0
```

## Result

Landed. `revive-verify` is green (`BAD=0`) and the full gate is at 1 red suite
(`ball-art`, W-070, open and unrelated).

One test defect was fixed alongside it, because it was masking the same
message: `revive-verify` measured "and it walks" immediately after
`step({"skillId": "10001009"})`, with no settle, while a skill pose locks
movement (the reference gates on `Skill.cs:321` `Skill.PlayEnded`, and
`MapSimSessionStore` zeroes `moveLeft` while `SkillPoseTicks > 0`). It gave the
swing 24 settle frames and the cast none. The settle was added; the assertion
itself is unchanged.
