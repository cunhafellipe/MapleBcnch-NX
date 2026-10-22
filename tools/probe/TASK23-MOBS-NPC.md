# TASK 23 — Mobs and NPCs: make the map inhabited

The map renders beautifully and is empty. Mob.nx is open and has **2054 mob
images**; not one pixel of any of them has ever been drawn. That is the single
largest visual gap.

    A = C:\Users\e7896\AppData\Local\hermes\cache\necro-ref\MapleNecrocer

## What I measured, so you do not have to

**Mob.nx opens in 0 seconds** alongside Map.nx in one process (both lazy-load;
the earlier "13.4 GB needed" was four stale servers eating 18.5 GB, not the
file). Open both in the SAME process.

```
Mob.nx  top level : 2054 entries, e.g. 0100100.img, 0100101.img, 0100120.img
Map.nx  109050001 : mobs = 0   ← the gap
geometry returns : footholds, ladders, portals, bounds   ← no lives
```

**Life nodes exist and are populated.** `f2/Map/Map0/000000002.img/life` has
twelve entries (`0`..`11`), each with children:

```
cy  f  fh  hide  id  mobTime  rx0  rx1  type  x  y
```

`id` and `type` are `String`; the rest are `Long`. `/api/node` reports
`hasValue` but not the value, and `/api/node/value` returned null for all of
them in my probe — **check the existing node-reading helper in
`WzSessionService` rather than trusting either endpoint.** Read the scalar the
way the rest of the codebase does.

`type` distinguishes a mob from an NPC; `rx0`/`rx1` are the patrol range, `fh`
the foothold it stands on, `cy` the vertical offset. `MobPatrol.cs` already
exists — find out what it expects and feed it these.

## Requirements

1. **Mobs appear.** `/api/simulate/session` returns `mobs: []` for every map.
   Populate it from the `life` nodes whose `type` is a mob, with at least
   `id`, `x`, `y`, `z`, and the art path.
2. **Art resolves.** `/api/simulate/mob-sprite` exists. Prove it returns a
   real PNG for a mob id found in a life node — report the PNG magic and the
   byte length, and how many distinct opaque pixels it has.
3. **NPCs appear** alongside, from the other `type` value. Same shape.
4. **Patrol works** if `MobPatrol.cs` is wired: report a mob's x over 300
   frames. It must move between `rx0` and `rx1` and not outside them.

## Do NOT

- Do not rewrite the footholds, bounds, camera or fall logic. That work is
  done and verified — `MapBounds`, `FallOutSlack`, `FindWall(z:)`. Stay out of
  `Services/MapEditor/FootholdPhysics.cs`, `CharacterPhysics.cs` and
  `MapBounds`.
- Do not modify `C:\MUWORK\GAME\MPSH\` or the reference repo.
- Do not weaken a check to make it pass.

## Verify

1. **A number**: `POST /api/simulate/session` on a map known to have lives →
   `mobs.length > 0`, and the same map today returns 0. Quote both.
2. **A pixel count**: for one mob, count non-transparent pixels in
   `/api/simulate/mob-sprite`'s PNG.
3. **A screenshot**: drive the page over CDP, `app.sections.open('simulator')`,
   load a map with mobs, and read the canvas with `toDataURL()` — NOT
   `Page.captureScreenshot`, which returned a black frame for a canvas holding
   577,589 pixels. A mob must be visible, at the right place, at the right Z.
4. **Patrol**: a mob's x across 300 frames, first and last, plus the rx0/rx1
   bounds.

If a screenshot is genuinely unobtainable, say so and ship the numbers — but
try `toDataURL` first.

## Scope limit

Finite budget. **First code edit in the first half, saved to disk
immediately.** Three tasks have timed out mid-write in this project and lost
everything.

Ship mobs before NPCs. Report which of the two you finished.

## Traps I hit today, so you do not repeat them

1. **`/api/children` does not reflect the real node structure.** For
   `000030000.img`, `foothold` appeared as a top-level node and its children
   disagreed with `BuildFootholds` (`MapDocument.cs:342`), which walks
   `foothold/<layer>/<group>/<foothold>`. **Read the parsing code before
   querying the API.**
2. **A grep that finds nothing is not proof of absence.** I searched for
   `map-overlay|sim-overlay|simPicker` and concluded the map picker did not
   exist; the class is `.sim-picker` and it works. Search the CSS and the
   renderer before claiming a feature is missing.
3. **An endpoint silently ignores an unknown property.** Posting
   `{"slots": {...}}` to `/api/simulate/look`, whose DTO declares `Equip`,
   returned `200 OK` with a payload identical to the no-argument call. Read the
   request DTO's exact property names first.
4. **Memory.** One server at a time. Four stale instances once made a working
   archive fail to open with an out-of-memory error that reads exactly like a
   corrupt file.