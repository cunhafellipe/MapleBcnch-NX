# TASK 9C — The avatar placement call is passing one argument to a three-argument function

## I found the break. It is not subtle.

`avatar.js:390` takes three positional arguments:

```js
placement(x, y, dir) {
  const frame = this.part.frames[this.part.index];
  const flip = dir < 0;
  const anchorX = this.part.anchorX ?? frame.originX;
  const anchorY = this.part.anchorY ?? frame.originY;
  const ox = flip ? anchorX - frame.width : -anchorX;
  const oy = -anchorY;
  return { x: x + ox, y: y + oy, w: frame.width, h: frame.height, flip };
}
```

`walkview.js:1320` calls it with the tick object, not with the three numbers:

```js
const p = avatar.part.placement(cur);        // <-- wrong: one arg
drawImage(img, px(p.x), py(p.y), p.w * s, p.h * s);
```

So inside `placement`, `x` is the whole tick object, `y` and `dir` are `undefined`.
`x + ox` string-concatenates into the literal string `"[object Object]-21"`,
and `y + oy` is `NaN`. Both blits land at `NaN`, which `drawImage` treats as
"draw nothing".

**Nothing about the character is wrong. The canvas data is real, the bitmaps
decode, and the whole draw is being skipped by two missing arguments.**

## Measured, from the live page

```
viewer:   hasAvatar=true  hasPart=true  frames=4  bitmaps=8
          bitmaps naturalWidth: 12x17, 27x32, 13x17, 26x32   (real decoded PNGs)
placement → { x: "[object Object]-21", y: null, w: 27, h: 41, flip: false }
canvas:   998x406, 399,677 non-blank pixels  (the map art renders fine)
```

That `"[object Object]-21"` is the whole bug, visible from outside the page.

## Task

Fix the call so the character actually draws.

1. Decide the correct contract. Either pass `(cur.x, cur.y, dir)` at the call
   site, or make `placement` take the tick. **Whichever you pick, make every
   call site agree** — search for all of them, including any in `livewalk.js`
   and the MCP-driven path. A partial fix leaves one caller drawing and one not.
2. The feet must land on the foothold line. `y` is the character's feet
   position by the contract in the docstring above — that is what a foothold's Y
   means. Report the numbers that prove it: avatar placement y vs the tick's y,
   and where the bottom edge of the drawn rect sits relative to the foothold.
3. Confirm the layer stack still composites correctly and Z ordering from TASK 3
   is unaffected.

## Verify — and this time, look for the character in the screenshot

1. Build clean. Restart the server against the new build. Confirm the DLL
   timestamp so nobody measures a stale process.
2. Open `C:\MUWORK\GAME\MPSHS\Map.nx` and `Character.nx`.
3. Load map `109050001` (it has real art: grass, stepped stone, palms, ticket
   booth), walk it, and **take a screenshot where the character sprite is
   clearly visible on the map.** Give the path.
4. Report, from the live page:
   - the value of `placement()` after the fix (must be numbers, not a string)
   - canvas pixel count
   - the avatar's screen rect and the foothold's screen Y, so the feet contact
     is visible as a number and not an impression.

If the character still does not appear, **report what the numbers now say** —
a fixed `placement` that still does not draw points at the next link in the
chain, and that is worth more than a guess.

## Reproduce it yourself

`tools/visual-check/verify-char.mjs <cdpPort> <out.png>` drives the real page
over CDP. It opens the simulator, walks a real map, and reads canvas pixels. I
already fixed its globals (`app.simulator.viewer`) and its WebSocket usage, so
it runs as-is. Use it — do not write a new harness.

## Constraints

- **Do NOT modify the reference repo.**
- Do not change physics, camera, Z formulas, session semantics, NX decoding, or
  the `placement` arithmetic. Only the call is wrong.
- Do not weaken a check to make it pass.

## Scope limit

Finite budget. This is a small fix — spend the budget proving it visually, not
padding the diff.