---
id: W-099
title: The mob-sprite endpoint answers 404 for a mob whose image exists, against its own contract of 200 with hasArt:false
state: done
priority: 78
owner: 
opened: 2026-10-24
closed: 2026-10-24
depends: []
refs: MapleBench/Api/SimulatorEndpoints.cs:662; MapleBench/Services/MapEditor/MobSpriteService.cs:70-75; MapleBench/Api/ApiErrorFilter.cs:48-50
blocked_on: 
evidence: mob-sprite?mobId=9500135 now answers 200 (mobId 7130010, linkedFrom 9500135, imagePath f5/7130010.img, hasArt true, actions attack1/die1/hit1/move/stand/attack1-ball); the cause was the link moving the NODE without moving the PATH. ball-art GREEN BAD=0 in the gate at 14:30 and 14:45; ballart-verify.py BAD=0 exit 0.
---
## Intent

`GET /api/simulate/mob-sprite?mobId=9500135` answers **404** with a raw
WZ-node error message, for a mob whose image **exists** and is open. The
endpoint's own contract says it answers **200 with `hasArt:false`** in that
situation, so a caller (the browser's mob renderer) gets an exception where it
expects a body. The gate's `ball-art` suite hit it and died on it; the same
404 would hit any map that spawns that mob.

## Reference

- `MapleBench/Api/SimulatorEndpoints.cs:662` — the contract: *"200 with
  hasArt:false rather than 404. 'No open archive holds this mob' is the
  ordinary state for a map open without Mob.nx, and the caller has to draw a
  placeholder AND say which -- which needs a body to read, not an exception to
  catch."*
- `MapleBench/Services/MapEditor/MobSpriteService.cs:70-75` — the same promise
  on the service: *"Returns an empty list -- not an error -- for a mob with no
  art."*
- `MapleBench/Api/ApiErrorFilter.cs:48-50` — `KeyNotFoundException` becomes a
  404, and **that branch writes nothing to stderr** (only the catch-all at
  :94-99 does), so the 404 arrives with no trace of where it was thrown.

## Approach

1. Get the stack. The 404 branch of `ApiErrorFilter` swallows the stack;
   log `ex.ToString()` there (or reproduce the call in a scratch harness) and
   find the throw site.
2. Find why the action list contains `attack1` for an image that has no
   action node. Measured: `f5/9500135.img` has exactly **one** child, `info`
   (`/api/inspect` reports `childCount: 1`, and `/api/animation/audit` says
   *"no animation at 'f5/9500135.img'"*), yet the endpoint tries to resolve
   `f5/9500135.img/attack1` -- and that path does not resolve
   (`/api/inspect?path=f5/9500135.img/attack1` answers the same 404). So the
   NODE walk and the PATH walk disagree about the same image, which is the
   risk `MobSpriteService.cs:222-227` already warns about for the ball loop.
3. Make `MobSpriteService.Describe` answer as it promises. The ball loop
   already has the right shape (`:215-229`: catch, skip the optional art,
   keep the rest); the main action loop (`:170-180`) has no such guard, and
   `AnimationService.Describe` resolves by PATH with `_session.Resolve`, not
   `TryResolve`.
4. Keep the suite honest: `tools/verify/suites/ballart-verify.py` now counts
   this as a defect of the port's response shape and keeps running, so the
   gate says **what** is broken instead of dying. That change is a harness
   fix and stays; this item is the product half.

## Acceptance

```
# 1. the endpoint answers 200 for the mob that made the suite die
GET /api/simulate/mob-sprite?mobId=9500135
# EXPECT: 200, a body with "mobId":"9500135", and hasArt reflecting the truth
#         (no attack art -- the archive carries only an info node)

# 2. the suite that died on it now reports a verdict
python tools\verify\suites\ballart-verify.py
# EXPECT: "BAD=0", exit 0, and the line
#         "0 mob(s) the port answered 404 for" -- no traceback

# 3. the whole gate
powershell -NoProfile -ExecutionPolicy Bypass -File tools\verify\gate-loop.ps1
# EXPECT: no RED_SUITE=ball-art
```

## Result

**The endpoint answers 200. The 404 is gone, and the cause was a stale path,
not a missing node.**

Measured, against the instance the chain deployed (`/api/about` →
`{"version":"1.1.0","license":"GPL-3.0"}`, 16 archives open):

```
GET /api/simulate/mob-sprite?mobId=9500135   -> 200
  mobId=7130010  linkedFrom=9500135  imagePath=f5/7130010.img  hasArt=True
  actions=['attack1', 'die1', 'hit1', 'move', 'stand', 'attack1/ball']
```

Before the fix the same request answered **404** with
`{"message":"'attack1' not found under 'f5/9500135.img'."}`.

### The root cause, and why the acceptance's assumption was wrong

The acceptance above expected `hasArt:false` on the grounds that "the archive
carries only an info node". That is true of the mob's **own** image and false of
the mob: `/api/inspect?path=f5/9500135.img` lists exactly one child, `info`,
and `info/link` is the string **7130010**. The art belongs to `f5/7130010.img`,
which carries `attack1`, `die1`, `hit1` and `stand` — measured, not assumed.
So the truthful answer for mob 9500135 is `hasArt:true` with `linkedFrom:9500135`,
which is what the endpoint now says.

The defect was that following the link moved the **node** but not the **path**:

```csharp
// MapleBench/Services/MapEditor/MobSpriteService.cs
dto.MobId = linked.ToString();
dto.ImagePath = targetPath;
image = target;          // the LINKED image
// imagePath was still "f5/9500135.img"  <-- the ORIGINAL
```

The action list was enumerated off the linked image, and every action's path
was built from the original image's path, so `AnimationService.Describe` was
handed `f5/9500135.img/attack1` — a path that does not exist — and threw
`KeyNotFoundException`, which `ApiErrorFilter` maps to 404. **Every mob that
carries a link was broken this way**, and the same stale path fed the ball
loop, so a linked mob's projectile art (`attack1/ball`) was never published
either. The fix is one line: the path moves with the node.

### Evidence

- `python tools/verify/suites/ballart-verify.py` — `BAD=0`, exit 0, and the
  suite's own line "0 mob(s) the port answered 404 for" (the suite had been
  changed earlier to count this as a defect and keep going, instead of dying
  on the first 404 and hiding the other 59 measurements).
- The gate, 2026-10-24 14:30 run: `=== ball-art ===  ok  GREEN? BAD=0`, and
  `RED SUITES: 1` — the remaining one being `skill-e2e`, which is W-101, not
  this item.
- The harness change stays: the suite now reports the defect instead of
  aborting the whole suite on the first 404.
