# TASK 2 — Camera follow

MapleBench's walk viewer frames the entire map at once. The finished reference
client follows the character. Make the viewer follow.

## The reference implementation

Read the real one — do not invent camera maths:

    A = C:\Users\e7896\AppData\Local\hermes\cache\necro-ref\MapleNecrocer

`Client/MapleCharacter.cs` around lines 749-764. It computes a destination
offset so the character sits centred, then lerps the camera toward it, then
clamps the result to the map bounds. The two lerp rates differ per axis
(horizontal and vertical use different coefficients — copy them exactly).

`Client/SpriteEngine/SpriteEngine.cs:90` holds the camera as a plain
`Vector2` field. `Client/MapleMap.cs:42` sets the viewport size.

Read those files and port the behaviour, not a paraphrase of it.

## Where it goes in MapleBench

- `MapleBench/wwwroot/js/walkview.js` — the canvas renderer. Line ~382
  currently computes one fixed `scale = cssWidth / (maxX - minX)` for the whole
  map. That must become a camera transform, not a whole-map fit.
- `MapleBench/wwwroot/js/livewalk.js` — drives the frame-by-frame body. It
  already knows the character's position every tick, so it is what moves the
  camera.

## Requirements

1. The character must stay near the centre of the view, lerped — not snapped.
2. The camera must never show area outside the map bounds.
3. Existing behaviour must survive: if the map is smaller than the viewport on
   an axis, that axis must centre rather than clamp to a negative edge.
4. Keep the existing zoom/scale control working. Do not break the fit-to-width
   default that other code and the CLI checks depend on.

## Constraints

- Match the surrounding style (this is vanilla JS with a specific naming
  convention — read neighbouring functions before adding yours).
- Touch only the two files named above.
- **Do NOT modify the reference repo.**
- Do not change physics, do not change the session store, do not add features.

## Verify before reporting

```
cd "C:/MUWORK/GAME/MAPLESOTRY/wz工具/MapleBcnch-NX-audit"
node --check MapleBench/wwwroot/js/walkview.js
node --check MapleBench/wwwroot/js/livewalk.js
```

Both MUST pass. Then report:

1. The diff, file by file
2. The exact lerp coefficients you copied from the reference, with file:line
3. How you handled the smaller-than-viewport case, and where you put that code
4. What you could NOT verify and why
5. Anything that looks wrong but you did not touch

State plainly what you actually ran. Do not claim the camera "works" because
the syntax checks passed — say that it is unverified if it is.