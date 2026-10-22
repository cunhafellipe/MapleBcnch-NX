# TASK 9 — Put the decoded pixels on screen

The server now returns real NX pixels. Verify they actually reach the canvas
in the simulator, end to end, on a real map with real art.

## Confirmed working (do not rebuild this)

Verified on a clean build against `C:\MUWORK\GAME\MPSHS`:

```
GET /api/node/facts?path=f2/00002000.img/stand1/0/body
  → canvas:true width:21 height:31 originX:16 originY:31 format:"Format2" layer:"body"

GET /api/animation?path=f2/00002000.img/stand1
  → isAnimation:true width:23 height:41 anchorX:18 anchorY:31 totalMs:1500
    frames[0]: delay=500
      layers: [ arm 10×19 origin(5,9) | body 21×31 origin(16,31) ]

GET /api/canvas?path=f2/00002000.img/stand1/0/body
  → real PNG (magic 89504e470d0a1a0a), 505 bytes, 435/651 opaque px, 22 colours

GET /api/node/facts?path=f1/Tile/ThemeGL.img/bsc/0
  → canvas:true 90×60
GET /api/canvas?path=f1/Tile/ThemeGL.img/bsc/0
  → real PNG, 3135 bytes, stone-tile texture
```

## The NX layer model — this is what the client must honour

Four levels: `img → action → frame → canvas`. At the frame level there are
several canvases (`arm`, `body`, …) that must be **stacked**, not drawn one at
a time. `z` is a string on the canvas and its semantics are: **higher = further
back = drawn first.** The layer table lives in `Base/zmap.img`.

A frame's own `originX/originY` are 0 — the drawn position comes from the
**server-computed frame anchor** (`anchorX:18, anchorY:31`), not from any one
canvas origin. Getting this wrong puts the head underground.

## Where the client code is

- `MapleBench/wwwroot/js/avatar.js` — TASK 7's 322-line module
- `MapleBench/wwwroot/js/walkview.js` — `drawAvatar()`, `stepAvatar()`, the
  tile/object draw path, Z ordering from TASK 3
- `MapleBench/wwwroot/js/livewalk.js` — the per-frame driver

## Requirements

1. Load `Map.nx` AND `Character.nx` from `C:\MUWORK\GAME\MPSHS`.
2. On a real map, the ground tiles draw as real art, not empty boxes.
3. The character draws as the real stacked avatar: feet on the foothold line,
   animated by the frame's own `delay`, animation stopped while idle.
4. The avatar sorts against geometry and mobs using the Z work from TASK 3.
5. When an archive lacks art, say so on screen rather than silently drawing
   nothing.

## Scope limit

Finite budget. Make the code edit in the first half. If one layer of the stack
cannot load, draw the others and say which.

## Verify before reporting — evidence, not assertion

1. Build clean, restart the server **against the new build**, confirm the DLL
   timestamp so nobody measures a stale process.
2. **Take a screenshot of the simulator with real art on screen.** Save it and
   give the path. This is the first time in this project's history that a
   screenshot is the right proof rather than a synthetic fixture — do not skip
   it.
3. Report the PNG byte counts the browser actually received, and how many
   pixels were non-transparent.
4. Confirm the character's feet land on the foothold line. Give numbers.

If the screen is still empty, **say so and report why**. Do not substitute a
synthetic fixture for the real thing — that has been the failure mode of the
previous seven tasks.

## Constraints

- **Do NOT modify the reference repo.**
- Do not change physics, camera, Z formulas, session semantics, or NX decoding.
- Do not weaken a check to make it pass.