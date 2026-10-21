---
id: W-054
title: A mob's projectile is invisible: the ball art is never published or drawn
state: done
priority: 2
owner: 
opened: 2026-10-16
closed: 2026-10-16
depends: [W-048]
refs: [MobAttack.cpp:163-164, MobAttack.cpp:36, Mob.cpp:1118-1126]
blocked_on: 
evidence: ballart-verify.py BAD=0 (9400708 ball published, 4 frames, named only past the delay, index 0..3); gate 23 suites RED SUITES: 0
---
## Intent

A mob that fires at range currently shows no projectile. It fires, the damage
lands, and nothing crosses the space between them — so the fight reads as the mob
taking an action rather than as the mob shooting something at you. That is the
difference the user asked for: not "the number went down" but "the monster did
what the monster does".

## Reference

- `MobAttack.cpp:163-164` — `attack.has_ball = bool(data["ball"]["0"]); attack.ball
  = data["ball"];`
- `MobAttack.cpp:36` — `for (auto& data : attacks) data.second.ball.update();`
- `Mob.cpp:1118-1126` — the draw, verbatim in shape:

```cpp
for (const auto& entry : attack_entries) {
    const auto& data = attacks.at(entry.index);
    if (data.geometry.type != 2 || !data.has_ball || entry.elapsed < entry.delay) continue;
    double t = std::min(1.0, double(entry.elapsed - entry.delay) / entry.lifetime);
    Point<int16_t> point{ viewx + entry.start.x() + (entry.finish.x() - entry.start.x()) * t,
                          viewy + entry.start.y() + (entry.finish.y() - entry.start.y()) * t };
    data.ball.draw(DrawArgument(point, entry.flipped), alpha);
}
```

## Approach

1. `Services/MapEditor/MobSpriteService.cs` — `Describe`: after the image's own
   actions, publish each attack's projectile art as a pseudo-action named
   `attackN/ball`, read from `attackN/info/ball` through the SAME
   `AnimationService.Describe` the other actions use, so its frames, origins and
   delays come from one reader.
2. `Services/Simulate/CombatService.cs` — `MobAttackSpec` gains `BallAction` and
   `BallFrameCount`, from the archive's own `info/ball` and its frame 0.
3. `Services/MapEditor/MobPatrol.cs` — `AttackEntry` gains `Flipped`; the
   `AttackEntries` snapshot gains the ball's action name, frame count, frame index
   and facing, and names the ball only once `elapsed >= delay`.
4. `Services/Simulate/MapSimSessionStore.cs` — `MapSimMobFrame` carries those four
   fields.
5. `wwwroot/js/mobsprite.js` — `MobSprite.stepNamed(mob, name, index)`: blit one
   NAMED action rather than the one the mob's state says it is playing. The
   existing preload loop already fetches every action's frames, so the ball art
   arrives with no new request.
6. `wwwroot/js/walkview.js` — `drawMobAttackBall`, called from `drawMob` after the
   body, mirroring about the ball's own origin when `attackEntryFlipped`.

## Acceptance

```
python %SCRATCH%\ballart-verify.py
# 9400708 (ball carries 4 frames)
#   woken by a landed hit: True
#   frame 0: the ball art is named (attack1/ball), frame 1/4, elapsed=1133ms of
#     delay=950ms, flipped=True
#   an entry is in flight at 17ms of a 950ms delay and the ball is NOT named
#   ball frame indices seen: [0, 1, 2, 3]
#   ok   walkview reads the entry position / the ball action name / the frame
#        index / the launch facing / blits it
#   ok   mobsprite can blit a named action / preloads named actions
# BAD=0

powershell -File %SCRATCH%\gate.ps1
# =========== RED SUITES: 0 ===========
```

## Result

Shipped and gated. MEASURED, the art was invisible for a reason that had nothing
to do with drawing it: the action list is built from an image's children, and
`ball` is not one of them — it hangs off the ATTACK's info at
`attackN/info/ball`, so nothing that enumerates actions ever saw it. MEASURED
`ball` is declared on 172 of 1,536 attack slots; the projectile suite's own mob,
`3220000 attack2`, now publishes a 4-frame `attack2/ball` and the flight frames
report `attack1/ball` / `attack2/ball` as soon as the entry passes its delay.

**Declared own-design, in `MobPatrol.AttackEntries`:** the reference advances
`data.ball` EVERY tick whether or not the attack is playing
(`MobAttack.cpp:36`), so its phase is effectively free-running. This port starts
the ball at frame 0 when the entry begins and runs it on the ball's own
delays. The consequence is bounded and named: which frame of a 2–3 frame ball is
showing, never whether the shot is there at all.

Three traps this item walked into, each of which produced a red that read like a
defect in the drawing:

- `WzPath.Join(imagePath, attackName, "info", "ball")` percent-ENCODED the
  slashes already inside `imagePath`, producing `f5%2F4220000.img`, and every
  ball 404'd on a file id that does not exist. `WzPath.Child` per level is the
  right call for a path that is already a path.
- `HasNumericCanvasChild` answered false for a node `AnimationService.Describe`
  reads as two 32x32 canvases. Two readers of the same question, and the second
  one was the wrong one; `Describe` is now the only test.
- `9300335 attack1/ball` is a single canvas, so "the frame index never advances"
  is true of a correct implementation. The suite now picks a ball with at least
  two frames when the archive offers one, and says so when it cannot.

Not covered here, and recorded rather than claimed: the mob's attack `effect` and
`hit` sprites (`MobAttack.cpp:142, 144, 224`) are still undrawn, and area attacks
(`geometry.type == 3/4`, `MobAttack.cpp:159-177`) are still not simulated.