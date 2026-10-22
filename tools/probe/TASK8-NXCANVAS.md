# TASK 8 — Decode NX canvas data so art actually draws

## The real blocker

Everything above the pixels works. Nothing at the pixels works.

Node trees read fine — `f2/00002000.img` has 158 action nodes, `stand1` has 3
frames, `stand1/0` has `arm`, `body`, `delay`, `face`. Map geometry reads
(`mapCount=5366`). Physics, sessions, camera, Z order, mobs are all done and
verified.

But every canvas query comes back empty:

```
GET /api/node/facts?path=f2/00002000.img/stand1/0/body
  → {"applicable": false, "scanned": 4, "total": 4, "facts": {}}

GET /api/animation?path=f2/00002000.img/stand1
  → {"isAnimation": false, "width": 0, "height": 0, "anchorX": 0,
     "anchorY": 0, "totalMs": 0, "frames": []}

GET /api/node/facts?path=f1/Map/Map9/910020202.img/0
  → {"applicable": false, "scanned": 3, "total": 3, "facts": {}}
```

**Map tiles are empty too.** This is not avatar-specific.

## The data is real

```
C:\MUWORK\GAME\MPSHS\
  Map.nx        678.9 MB
  Character.nx  186.4 MB
  Mob.nx        817.6 MB
  Npc.nx         66.7 MB
  UI.nx         519.5 MB
  String.nx, Item.nx, Effect.nx, Skill.nx, Reactor.nx, ...
```

Open several at once with `POST /api/files/open-many`. NX opens by memory
mapping, so a 679 MB archive costs almost nothing to open.

## What exists already

- `MapleBench/Services/Nx/NxCanvasDecoder.cs`
- `MapleBench/Services/Nx/NxCanvas.cs`
- `MapleBench/Services/Nx/NxPngProperty.cs`
- `MapleBench/Services/Nx/NxImageBuilders.cs`
- `MapleBench/Services/Nx/NxNode.cs`, `NxNodeOrigin.cs`, `NxArchive.cs`

`NxNodeOrigin` is the one to read first — it suggests the tree knows how to
reach the bytes and something downstream is not asking.

The reference also exists, and it is NOT what I assumed:

    A = C:\Users\e7896\AppData\Local\hermes\cache\necro-ref\MapleNecrocer

MapleNecrocer uses **WzComparerR2** (`using WzComparerR2.WzLib`) — an external
library, not code vendored in this repo. There is no NX reader to copy from
there. Read `Client/MapleCharacter.cs` and `Client/SpriteEngine/SpriteEngine.cs`
for the *consumption* side — how a canvas is asked for its bitmap, its origin,
and its frame timing — and implement the NX side here.

## Task

Make a canvas node in an NX archive return real image data through the existing
endpoints.

1. `/api/node/facts` on a canvas node must return its real dimensions, origin
   and format. `applicable: false` is wrong for a node whose parent image is an
   animation frame.
2. `/api/animation` on an NX action node must return real frames with real
   widths, heights, anchors and per-frame delays.
3. The browser must be able to fetch the decoded bitmap. Find how WZ canvases
   are served today and route NX through the same path.

## Verify before reporting — with real numbers, from a real server

1. Build clean.
2. Start the server against a build that contains your change. Confirm which
   DLL you are testing — a stale process makes every measurement a lie.
3. Open `C:\MUWORK\GAME\MPSHS\Map.nx` AND `Character.nx`.
4. Paste the full JSON from `/api/node/facts?path=f2/00002000.img/stand1/0/body`
   and from `/api/animation?path=f2/00002000.img/stand1`. **If the widths are
   still 0, you have not solved it — say so.**
5. Paste the full JSON for `f1/Map/Map9/910020202.img/0` — a map tile.
6. Confirm the browser can fetch the image and it is non-zero bytes. Paste the
   byte count.

If you cannot get real pixels out of NX, **say that plainly and report what you
found about why**, rather than reporting partial success. A truthful "NX canvas
decoding needs X and here is X" is worth more than a green check on something
that draws nothing.

## Constraints

- **Do NOT modify the reference repo.**
- Do not change physics, camera, Z order, mobs, or session semantics.
- Do not weaken a check to make it pass. If a node has no canvas, that is a real
  answer, not a failure to work around.
- Prefer wiring the existing decoder over writing a new one.

## Scope limit

Finite budget. Make the code edit in the first half. When a format detail is
undocumented, reverse-engineer it from the bytes with a hex dump and say so.

Report the diff per file, what you found about the NX canvas layout, the real
JSON from step 4-6, and everything you could not verify.