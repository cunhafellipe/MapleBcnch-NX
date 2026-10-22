# TASK 3 — Z-depth draw order

Draw the scene in depth order so the character is correctly occluded by
foreground geometry and draws over background.

## The reference implementation

Read the real one — do not invent a sort:

    A = C:\Users\e7896\AppData\Local\hermes\cache\necro-ref\MapleNecrocer

- `Client/SpriteEngine/SpriteEngine.cs` around lines 463-480 — the Z-sorted
  draw list, including the comparator at ~line 472 and how items are inserted.
- `Client/MapTile.cs:33` — the tile Z formula: `Layer * 100000 + wzZ + 1000`.
- `Client/MapleCharacter.cs:561` — the player's Z: `FH.Z * 100000 + 60000`.
  The character takes its Z from **the foothold it stands on**, not from a
  constant. That detail is the whole point of this task.
- `Client/MapParticle.cs:57` — particles forced to `10000000`.

Copy the arithmetic. Do not normalise it into something tidier.

## Where it goes in MapleBench

- `MapleBench/wwwroot/js/walkview.js` — line ~454-457 is a flat paint order.
  Make it depth-sorted.

## Requirements

1. Footholds and tiles sort by their own Z. The character sorts by the Z of
   the foothold under it, so walking behind a wall puts the wall in front and
   walking in front of it puts the character in front.
2. When the character changes footholds its Z changes — that transition must
   actually move it in the draw order.
3. Ladders, portals and any marker entities need a defined Z too. Pick the
   reference's approach; if it has none, choose a value and say so in a comment.
4. The existing stroke-figure character rendering must survive. Only the ORDER
   of the draw calls changes — not what is drawn or how it looks.
5. Performance: this runs at 60 Hz over a whole map's geometry. Do not sort on
   every frame if a cheaper ordering exists — a precomputed key, or sorting only
   when the set changes. Say which you chose and why.

## Constraints

- Vanilla JS, match the surrounding style.
- Touch only `walkview.js` unless you can justify another file.
- **Do NOT modify the reference repo.**
- Do not change physics, camera, session store, or input.

## SCOPE LIMIT — this matters

The previous attempt burned its whole budget reading data shapes and never
wrote code. **You have already read the reference. Write the code first, then
verify.** Do not go spelunking for DTO field names.

Concretely:

- If a foothold in the browser data lacks an explicit Z, use the field the
  reference uses; if it is absent there too, fall back to the foothold's own
  y or index and say so in a comment. Do not stall.
- If `page` or any other ladder field is unreachable, pick a ladder Z and note
  it. Do not stall.
- Anything you decide while writing is fine as long as you write down why.

Deadline discipline: **make the edit within the first half of your budget.**

## Verify before reporting

```
cd "C:/MUWORK/GAME/MAPLESOTRY/wz工具/MapleBcnch-NX-audit"
node --check MapleBench/wwwroot/js/walkview.js
```

Then report:

1. The Z formulas you took from the reference, each with file:line
2. Where the sort happens and how often — every frame, or on change?
3. How you proved the ordering is correct (synthetic geometry is fine; say so)
4. What you could NOT verify
5. Anything that looks wrong but you did not touch

State plainly what you ran. Do not claim it works because it parses.