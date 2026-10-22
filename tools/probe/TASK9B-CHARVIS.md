# TASK 9B — Map art renders, the character does not

## What works, verified on a clean build against the real archives

Screenshot: `tools/visual-check/shots/sim-walk-art.png`

Map `109050001` draws with real art — grass, stepped stone platforms, palm
trees, a ticket booth. The Z overlays, footholds and HUD all render. So TASK 9's
tile work landed.

**The character is not drawn.** No sprite is visible on the canvas.

## The backend is fine — I checked it directly

```
GET /api/animation?path=f2/00002000.img/stand1
  → isAnimation:true width:23 height:41 anchorX:18 anchorY:31 totalMs:1500
    frames[0].delay=500
    frames[0].layers = [ arm 10×19 origin(5,9) | body 21×31 origin(16,31) ]

GET /api/canvas?path=f2/00002000.img/stand1/0/arm   → 200, 280 bytes, real PNG
GET /api/canvas?path=f2/00002000.img/stand1/0/body  → 200, 505 bytes, real PNG
```

Both archives must be open: `Map.nx` and `Character.nx` from
`C:\MUWORK\GAME\MPSHS`.

## The client code looks correct — so find out why it does not run

`walkview.js:1306` `drawAvatar()` handles the layer stack properly:

```js
const layers = frame.layers?.length ? frame.layers : [frame];
...
for (const layer of layers) {
  const img = avatar.bitmaps.get(layer.path);
  if (!img) continue;
  ctx.drawImage(img, px(lx), py(ly), layer.width * s, layer.height * s);
}
```

And `walkview.js:1145`:

```js
if (avatarFrame !== null && avatarFrame !== undefined) {
  this.drawAvatar(ctx, px, py, s, cur, avatarFrame);
```

`drawAvatar` reads `avatar.part?.frames[index]` and **returns early if
`part` is null**. So the most likely break is that `avatar.part` was never
populated — but **do not assume that**. Prove which link in the chain is
broken.

## Method — instrument, do not guess

Drive the real page over CDP with a real browser and report, in order:

1. Is `viewer.avatar` present?
2. Is `avatar.part` present? If not, why — was `/api/animation` called at all?
   With what path? What did it return?
3. Is `avatar.bitmaps` populated? How many entries, and are their `.naturalWidth`
   values non-zero (a decode that failed leaves a truthy `Image` with width 0)?
4. Is `stepAvatar()` advancing the frame index?
5. What is `avatarFrame` at the moment of the draw call?
6. Is `avatar.placement()` returning a non-zero rect, or does `w < 0.5` at the
   current zoom (line 1321 returns early on a sub-pixel draw)?

Report the actual values. Then fix the broken link and re-check.

## Verify

1. Build clean. Restart the server against the new build; confirm the DLL
   timestamp.
2. **A screenshot with the character visible.** Save it and give the path. A
   screenshot without the character is not evidence of success.
3. Report the PNG byte count and non-transparent pixel count the browser
   actually received for the avatar layers.
4. Confirm the feet land on the foothold line with numbers.

## Constraints

- **Do NOT modify the reference repo.**
- Do not change physics, camera, Z formulas, session semantics, or NX decoding —
  those are verified working.
- Do not weaken a check to make it pass.

## Scope limit

Finite budget. Instrument first, then fix. If the answer is "the map has no
character art after all", say so with the path you queried — that is a valid
finding, and better than a fix for the wrong reason.