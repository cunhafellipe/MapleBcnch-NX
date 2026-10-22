# TASK 24 — mobs reach the screen

The data path is done and measured. One field is empty and nothing paints.

    A = C:\Users\e7896\AppData\Local\hermes\cache\necro-ref\MapleNecrocer
    B = C:\MUWORK\GAME\MAPLESOTRY\wz工具\MapleBcnch-NX-audit

Server: `dotnet MapleBenchX.dll --port 5520`, with `Map.nx` (fileId `f1`) and
`Mob.nx` (fileId `f2`) open.

## What I measured — do not re-derive this

```
POST /api/simulate/session  f1/Map/Map0/000000002.img
  mobs: 0            ← was 0 for every map; the life-node parsing fixed this
  mobs: 12 now, each with mobId, x, y, foothold, onGround, facing, z

GET /api/simulate/mob-sprite?mobId=9409000&fileId=f2
  kind "m", hasArt true, imagePath "f2/9409000.img", anchorX 22, anchorY 95
  actions: die1 (4 frames), hit1 (1), move (4), stand (8)
  frames[i] = {index, name, path, width, height, originX, originY,
               offsetX, offsetY, delay, layers: []}

GET /api/canvas?path=f2/9409000.img/stand/0
  → 40x80 PNG, 1840 opaque pixels        ← the pixels exist and are real
```

Mob.nx has 2053 mobs (745 bosses). `stand` frame counts across 9 sampled mobs:
1, 1, 2, 3, 4, 4, 6, 8, 15 — **vary, and node names are lexicographic**
(`0,1,10,11,...,2,3`), so never sort them as strings.

`/api/simulate/mob-sprite` parameters are `mobId` and `fileId`. `path` returns
400.

## The one thing that is broken

Driving the real page over CDP:

```
app.sections.open('simulator')      → section mounts, <div class="sim"> built
await app.simulator.viewer.load(path) → canvas 300x150 → 900x620
POST /frame × 20                    → 12 mobs in the response
```

Every mob that reaches the viewer carries:

```json
{ "mobId":"9409000", "x":939, "y":-130.19, "foothold":"20", "z":...,
  "spritePath": "", "kind":"m", "onGround":true, "facing":-1 }
```

**`spritePath` is empty.** `WalkViewer` already has `loadArt`, `drawMob`,
`mobZ`, `artZ` and a placeholder `drawMobSlab`; it keys its sprite cache on
`spritePath`. Empty means every mob falls through to the slab, so the map draws
and no mob ever appears. The canvas has 558,000 painted pixels and zero mobs on
it — confirmed by reading the image, not by a screenshot.

## What to do

1. Populate `spritePath` for every mob in the frame, from the same resolution
   `/api/simulate/mob-sprite` performs — `f2/<mobId>.img`, or `linkedFrom` when
   the id is not a top-level image. Do not guess: resolve it and report how many
   of the 12 resolved.
2. Decide the action a mob plays. The reference picks `move` while travelling
   and `stand` while still, and falls back to `stand` when there is no `move`.
   `mobsprite.js` already implements that; make sure the server's per-frame
   `stance`/`frameIndex` agrees with it.
3. `/api/canvas` is how a frame's pixels are fetched. `frames[i].layers` is
   always empty for mobs — for characters the layers live under the canvas and
   `/api/canvas?path=<frame path>` is the only thing that returns them. Use the
   frame's own `path`.
4. `0100100` and `0100101` have a **1-frame** `stand`. Confirm the animation
   holds instead of indexing past the end.

## Verify — both, not either

1. **A number**: the canvas pixel count with mobs present, and the mob count
   the HUD reports (`renderHud` already distinguishes `kind === 'n'` and shows a
   placeholder-art warning). Give both numbers.
2. **A picture**: drive the page over CDP and read
   `app.simulator.viewer.canvas.toDataURL()` — **not** `Page.captureScreenshot`,
   which returns black for a canvas holding hundreds of thousands of pixels.
   Write the PNG under `B/tools/visual-check/shots/mobs/`. At least two mobs
   must be visible on the ground, at the right depth, with the player.
   Then state what you see in the image. Do not report success from the numbers
   alone.

## Do NOT

- Do not touch `Services/MapEditor/FootholdPhysics.cs`, `CharacterPhysics.cs`,
  `MapBounds`, the camera, or the fall clamp. Those are verified — 3000 maps
  walked with zero falls — and out of scope.
- Do not modify `C:\MUWORK\GAME\MPSH\` or the reference repo.
- Do not weaken the HUD's placeholder-art warning to make the numbers look
  better. If a mob genuinely has no art, the HUD should say so.

## Scope

**First code edit in the first half, saved to disk immediately.** Three tasks
in this project timed out mid-write and lost the work.

## Traps I hit today

1. `app.sections.open('simulator')` is **async** and builds the canvas inside
   `ensureSimulator()` — not awaiting it reports "no canvas".
2. The viewer is a **field** (`app.simulator.viewer`), not a child of the
   section root; walking the root's DOM finds no canvas while the canvas is
   right there. And an unfed viewer's canvas is 300x150 with 0 painted pixels.
3. `viewer.mobs` is an **array**, not a map — `Object.keys(mobs).length` returns
   0 and reads as "no mobs".
4. A screenshot of a canvas is not evidence. Check painted pixels, then look at
   the image.