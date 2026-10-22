# REVIEW TASK — MapleBench simulator. Report findings only. DO NOT EDIT ANY FILE.

You are reviewing a WZ (MapleStory v83 archive) editor/simulator written in C# (.NET 10,
ASP.NET Core) + vanilla ES modules. A working directory is already running at
http://127.0.0.1:5100 — you may curl it. Do NOT kill or restart it, do NOT run `dotnet build`,
do NOT modify any file. Report findings only.

## What was just fixed (these are PROVEN by measurement — do not re-litigate, but DO report
if you find a remaining defect in their vicinity)

1. `livewalk.js` had unbalanced braces at the `tick()` method (a `finally {}` closed the
   method early, orphaning `this.paint()` and the rAF re-arm). Browser refused to load the
   module: `SyntaxError: Unexpected token 'this'`. Fixed.
2. `MobPatrol.Roll()` used `switch (Random.Next(200))` with only 4 equality cases
   (50/100/150/199) — 196 of 200 outcomes were no-ops. Changed to an if/else chain.
3. Added `POST /api/simulate/session/{id}/frames` (batch physics, N steps in one round trip)
   in Api/Endpoints.cs + Services/Simulate/MapSimSessionStore.cs.
4. `app.js preloadClient()`: opens every `.nx` in a client folder once any one is opened,
   via `GET /api/files/listing` (new endpoint) + `POST /api/files/open-many`.
5. Monster mirror axis moved from feet (`px(mob.x)`) to the sprite's own originX.
6. `damagenumber.js`: glyph URL is `<fileId>/BasicEff.img/NoRed{1|0}/<digit>`.

## MEASURED facts (true as of today — trust these over anything you infer)

- Session holds 16 archives: Map/Base/Character/Effect/Etc/Item/Mob/Morph/Npc/Quest/
  Reactor/Skill/Sound/String/TamingMob/UI .nx at C:\MUWORK\GAME\MPSHS\
- Avatar compose on `f1/Map/Map1/100000000.img`: 10-11 parts incl. face 26x16, arm 10x19,
  zero parts missing a bitmap. In the `jump` stance: lHand 8x9, rHand 9x9.
- Physics: walk dx=1480px over 30 frames; jump arc y -34.5 -> peak -105.0 -> lands frame 28.
- 40/40 files in wwwroot/js parse as ES modules (verified with vm.SourceTextModule).
- Map `100000000.img` has 30 spawns all `kind=n` (NPC, Frozen — they do not move by design).
  Map `100000004.img` has 24 `kind=m` and they patrol at ~2.0px/frame.
- `/api/children?path=<fileId>/<inner path>` — the first path segment IS the open-file id.
- `/api/canvas?fileId=<id>&path=<id>/<inner>` — 200 on Character.nx, 404 on wrong root.

## What I want from you

Read the actual source and find REAL defects. Rank by severity. For each: file, line,
what breaks, and the mechanism. If you find nothing in an area, say so plainly — a clean
area is a useful result, a padded one is not.

Prioritise, in this order:
1. Any remaining syntax/structural defect in wwwroot/js/*.js that a browser would reject.
2. Correctness in Services/Simulate/*.cs — especially `BatchFrames`, `Advance`, `Step`,
   `MobPatrol`, spawn placement, and the batch-frame input handling (`First` vs `Then`).
3. The new endpoints in Api/Endpoints.cs (`/files/listing`, `/session/{id}/frames`) —
   input validation, clamping, failure modes.
4. `app.js preloadClient()` — races, re-entrancy, wrong-root cases, whether the guard can
   deadlock or fire twice.
5. Anything in walkview.js draw path (z-order, anchors, mirroring) that would make the
   rendered figure wrong even though the data is right.

Use grep/read freely. Run `curl` against the live server to check a hypothesis. You have
roughly 10 minutes of budget — spend the first half reading, and do not propose a patch
without a mechanism you can point at in the source.
