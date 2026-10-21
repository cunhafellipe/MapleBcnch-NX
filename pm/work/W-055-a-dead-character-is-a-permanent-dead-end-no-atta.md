---
id: W-055
title: A dead character is a permanent dead end: no attack, no skills, no way back
state: done
priority: 1
owner: 
opened: 2026-10-16
closed: 2026-10-16
depends: []
refs: [Player.cpp:210-251, Stage.cpp:497-508, UIStateGame.cpp:548-556, Player.cpp:1252, UIStateGame.cpp:794]
blocked_on: 
evidence: revive-verify.py BAD=0; gate 25 suites RED SUITES: 0
---
## Intent

The user reported, on 2026-10-16: *"??蝣箏??桀????銝??啁?????拙振霈?銝?餅? ???拙振?賣??賭?璅?瘜??* ??the player has become unable to attack, and casting a skill does not work either.

It is the latest version. Both symptoms were one thing: the character was dead.
A corpse cannot swing and cannot cast ??that part is the reference's behaviour and
the guard that enforces it is correct ??but there was nothing on screen saying so
and nothing to do about it, so a death looked exactly like a broken build.

## Reference

- `Player.cpp:210-251` ??`Player::respawn(Point<int16_t> pos, bool uw)`. Its own
  comment is why this method clears rather than moves: *"Nothing about the old spot
  may carry over to the new one??Clear the whole movement state and let
  FootholdTree::update_fh re-derive the foothold from the new position"*, and
  *"Coming back from a death leaves the state behind otherwise, and a revived
  character would stay a tombstone."*
- `Stage.cpp:497-508` ??`Stage::respawn(int8_t portalid)`: the spawn portal, which
  is where a character comes back to.
- `UIStateGame.cpp:548-556` ??the revive prompt: `emplace<UIRevive>()`, reached
  from `Player.cpp:681` when HP notices it has reached zero.
- `Player.cpp:1252` / `UIStateGame.cpp:794` ??`can_attack()` and the damage path
  both test `is_invincible()`.

## Approach

1. `Services/MapEditor/CharacterPhysics.cs` ??`Respawn(foot, x, y)`: the body onto a
   foothold with every velocity, the input, the foothold handle and both pose locks
   cleared, per `Player.cpp:210-251`. And `ReleasePoses()`, for the corpse.
2. `Services/Simulate/PlayerStatus.cs` ??`Revive()`: alive, HP to `MaxHp`.
3. `Services/Simulate/MapSimSessionStore.cs` ??`Revive(sessionId)`: find the spawn
   portal, place the body `2px` above it exactly as `Start` does, clear the pending
   swing and the skill pose, revive the sheet. `SetInvulnerable(sessionId, value)`
   alongside it.
4. `Services/Simulate/MapSimSessionStore.cs` ??`Advance`: when the character dies,
   let the swing pose go once (`BodyDeadPoseCleared`). A corpse frozen mid-swing for
   the rest of the animation reports `attackStance` for a body that cannot swing.
5. `Api/SimulatorEndpoints.cs` ??`POST /session/{id}/revive` and
   `POST /session/{id}/invulnerable`.
6. `wwwroot/js/livewalk.js` ??`revive()` and `setInvulnerable()`; `openSession` now
   sends `invulnerable`, remembered across map re-opens.
7. `wwwroot/js/walkview.js` ??the dead banner and its button in the HUD
   (`renderHud`), and the invincibility checkbox in the controls row.
8. `wwwroot/css/simulator.css`, `wwwroot/js/locale.zh-Hant.js` ??the styles and the
   three strings.

## Acceptance

```
python %SCRATCH%\revive-verify.py
# 50 HP character; 4220000 (PADamage 135) summoned
#   frame 19: hit for 135, HP 0 / frame 19: DEAD
#   attack -> attackStance=''  /  skill -> skillStance=''
#   playerDead=False hp=50/50
#   attack -> action='attack' attackStance='swingO1'
#   skill  -> action='skill'  skillStance='bamboo'
#   x 0 -> 21
#   200 frames in contact with 4220000: playerDead=False
# BAD=0

powershell -File %SCRATCH%\gate.ps1
# =========== RED SUITES: 0 ===========
```

## Result

Shipped and gated. MEASURED, in order:

1. The character dies almost at once on a mob map. On
   `f4/Map/Map1/100000004.img` the mobs carry `info/PADamage` 90??50 and a level-1
   sheet has 50 HP, so one touch ends the walk.
2. A dead character reports `attackStance=''` and `skillStance=''` and will not
   walk ??correct, and indistinguishable from a broken build from the outside.
3. `/revive`, `/respawn` and `/portal` were all 404. There was no way back at all.
4. After the fix: revive returns the character at 50/50, and it swings, casts and
   walks.

Two things the dead-end was hiding, both found by measurement rather than by
reading:

- **A corpse was frozen mid-swing.** The pose lock holds the body for the whole
  animation, so a character killed three frames into its swing kept `attackStance`
  for the other thirty-four. `revive-verify.py` read `attackStance='stabO1'` off a
  DEAD character and reported a missing guard; the guard was there and the pose was
  what remained.
- **The revive put the character at y=0.** It used the spawn portal's raw Y; `Start`
  uses `portalY - 2` and a foothold probe. MEASURED: it came back on a ledge at the
  top of the map and walked happily along it.

**Invincibility was unreachable.** It existed only as a session-creation parameter
the browser never sent, so every walk on a mob map ended the same way. It is now a
checkbox in the walk view's controls, and it remembers itself across map re-opens.
Declared own-design at the call site: the reference's revive costs a scroll or the
free wheel (`UIRevive.cpp:143-158`, `:35-51`) and this has neither, so HP returns
in full.

`W-037` still stands for the visual confirmation of all of this; nothing here is a
screenshot claim.