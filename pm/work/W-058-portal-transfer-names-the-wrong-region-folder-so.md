---
id: W-058
title: Portal transfer names the wrong region folder, so no door works
state: done
priority: 75
owner: 
opened: 2026-10-16
closed: 2026-10-16
depends: []
refs: [Stage.cpp:315-318]
blocked_on: 
evidence: MapSimSessionStore.cs:1638 Map{ToMap % 100} -> Map{ToMap / 100000000} per Stage.cpp:315-318. Measured both spellings against the archive for the test map's 2 real exits: Map1/100000003 -> 200, Map3/100000003 -> 404; Map1/100000005 -> 200, Map5/100000005 -> 404. Every portal transfer previously built a 404 path, LoadMap threw into a catch, PendingTransfer stayed null: walking through a door did nothing. Gate RED SUITES: 0. NOT claimed: end-to-end transfer unexercised - the frame payload exposes no portal field and a body settles ~21px off the portal, so TakePortal never fires in this harness.
---
## Intent

One paragraph. What changes for the user when this is done.

## Reference

`path:line` — the exact construct being ported. If there is none, say so
explicitly and mark the item own-design.

## Approach

Ordered steps. Each names a file and a symbol.

## Acceptance

The region folder the code builds must be one the archive answers, and it must
be the one the reference builds:

```
# for each real exit portal on the test map, both spellings, measured
GET /api/inspect?path=f4/Map/Map1/100000003.img   -> 200      # first digit
GET /api/inspect?path=f4/Map/Map3/100000003.img   -> 404      # ToMap % 100
GET /api/inspect?path=f4/Map/Map1/100000005.img   -> 200
GET /api/inspect?path=f4/Map/Map5/100000005.img   -> 404
```

## Result

**Found by the `W-041` id-width sweep, which flagged it as adjacent and out of
scope; confirmed and fixed here.**

`TakePortal` built the folder as `Map{portal.ToMap % 100}` — the last **two**
digits. The reference builds the first:

```cpp
// clean-client/src/Gameplay/Stage.cpp:315-318
std::string strid  = string_format::extend_id(mapid, 9);
std::string prefix = std::to_string(mapid / 100000000);
nl::node src = nl::nx::map["Map"]["Map" + prefix][strid + ".img"];
```

`mapid / 100000000` on a nine-digit id *is* its first digit, and the
`:000000000` the code already applied is the same `extend_id(mapid, 9)`. Only the
folder was wrong.

MEASURED on the two real exits the test map carries (`portal/tm` = 100000003 and
100000005, both `pt` = 2), re-derived from `/api/inspect` rather than assumed:

| `tm` | first digit | `ToMap % 100` |
|---|---|---|
| 100000003 | `Map1/100000003.img` → **200** | `Map3/100000003.img` → **404** |
| 100000005 | `Map1/100000005.img` → **200** | `Map5/100000005.img` → **404** |

So every portal built a path the archive does not hold. `LoadMap` threw, the
`catch` at `:1644` swallowed it into `LastPortalFailure`, and `PendingTransfer`
was left `null` — **walking into a door did nothing, with no error anywhere.**
Reachable on every frame the player holds Up while over a portal (`:1263-1274`).

Two halves of one rule had drifted: `MapEditorService.Minimap.cs:118` already
used `mapId.ToString()[0]`.

**Fixed:** `ToMap % 100` → `ToMap / 100000000`, with the measurement and the
citation in the comment at the site.

### What is NOT claimed

**The transfer was never exercised end to end.** The frame payload carries no
portal field at all (`action, attackStance, blockedByWall, bounds, clamped,
expGained, facing, foothold, footholdChanged, frame, levelsGained, mobs,
onGround, onLadder, playerDamage, playerDead, sessionId, skillEffects, skillId,
skillStance, speed, status, ticksRequested, ticksRun, velocityY, x, y`), and a
body spawned on a portal settles ~21 px off it, so `TakePortal` never fires in
this harness. `/debug/portal` reporting no failure is consistent with *either*
"never triggered" or "triggered and loaded cleanly", and it cannot tell them
apart.

What is verified is the thing the fix changes — the path it builds, measured
against the archive, and the rule it now follows. Closing the loop needs a way to
park a body on a portal and observe that the map changed; that harness capability
does not exist yet and is not faked here.
