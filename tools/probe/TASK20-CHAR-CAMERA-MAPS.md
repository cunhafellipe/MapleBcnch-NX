# TASK 20 — Character, camera, and the maps that break

Three defects the user reported directly. **46% of maps are broken** — I
measured it. Fix them against the reference, not by guessing.

    A = C:\Users\e7896\AppData\Local\hermes\cache\necro-ref\MapleNecrocer

## The measurement

`tools/visual-check/scan-fallout.mjs 5510 400` over 400 maps:

```
400 maps → 184 work, 155 HTTP 400 (cannot open), 61 "fell off"
```

**"Fell off" means the spawn point itself is already off the map** —
`spawnY` of 2100-2400, not something the player walked into. Every one of the
worst is a `Map1/1xxxxx00.img` town map.

So: two of the three defects are about **map loading**, not gameplay.

---

## DEFECT 1 — 155 maps return HTTP 400

Reproduce: `POST /api/simulate/session {"path":"f1/Map/Map1/100020101.img"}`

That path is listed by `/api/simulate/maps` and is a real town map. Get the
**actual error text** from the server log and from the exception — this project
has repeatedly guessed at a cause and been wrong three times in one day.

### CORRECTION — I chased the error down. It is not an exception.

The 400 body is:

```json
{"error":"nothing to stand on at x=, y="}
```

Note the **empty** x and y — the spawn point was never computed, so the error
message itself is malformed.

I then checked what those maps actually contain:

```
f1/Map/Map1/100020101.img  children = ['info']          <- no footholds at all
f1/Map/Map1/100020102.img  children = ['info']          <- same
f1/Map/Map1/109050001.img  children = ['0','1','2',...] <- real footholds
f1/Map/Map1/102020100.img  children = ['0','1','2',...] <- real footholds
```

A 60-map random sample: **29 have footholds, 31 have none.**

**So the 155 HTTP 400s split into two different things, and they are not the
same bug:**

1. **Maps with no footholds at all** (`100020101`) — these are empty/incomplete
   in the archive itself. `info` has 20 keys, and no `VRLeft`/`VRTop`/`centerY`/
   `MapHeight` (nor does the working map `109050001` expose those through
   `/api/node/facts`, so facts is not the right way to read them — check how
   the working map reads its own bounds). **Do not treat these as broken.
   Filtering them out of `/api/simulate/maps` is correct**; a real client
   cannot render a map with no geometry. What must not happen is a 400 with an
   empty-coordinate error string.

2. **Maps WITH footholds that spawn off-map** (`102020100`, `102020200`,
   `101010000`) — spawnY already 2100-2379 before any input. **This is the real
   defect and it is in DEFECT 2.**

Confirm the split by measuring it, then act on each half correctly.

Reference candidates, in order:

- `MapleMap.cs:175-194` reads `Info["VRLeft"]`, `Info["VRTop"]`,
  `Info["VRRight"]`, `Info["VRBottom"]`, `Info["centerY"]`,
  `Info["MapHeight"]`, `Info["MapWidth"]`. **A map missing one of these, or
  holding a string where we expect an int, is a candidate.** Report which
  property is missing on the maps that fail and which is present on the maps
  that work — that is a measurement, not a theory.
- Whether the map's footholds are empty, which would make every later stage
  throw.

**Report the first map that opens cleanly and the first that fails, with the
differing property.** Then fix and re-run the scan; the number must go down.

---

## DEFECT 2 — the spawn point is off the map (61 maps)

The reference spawns at a **portal**, and `MapleCharacter.cs` uses portal type
0 as the start. If no portal qualifies, our code appears to be falling back to
something arbitrary — and on those 61 maps the fallback lands at y=2100.

Read the reference for portal selection and reproduce it exactly:
- which portal type is the start
- how a portal's position becomes a spawn position
- what happens when the map has no qualifying portal (**do not invent a
  fallback that lands off-map** — clamping into the footholds, or reporting
  "no spawn", are both better than a spawn at y=2300)

Verify: re-run `scan-fallout.mjs`. Every map must spawn on a foothold, or say
it has none.

---

## DEFECT 3 — the camera

`MapleCharacter.cs:749-764`, verbatim:

```csharp
DestX = DisplaySize.X / 2 - X + Engine.Camera.X;
if (Math.abs(DestX) > 1)
    Engine.Camera.X -= DestX * (12f / 800f);

DestY = (DisplaySize.Y / 2) + (Map.OffsetY + 60) - Y;
if (Math.abs(DestY + Engine.Camera.Y) > 1)
    Engine.Camera.Y -= 0.01f * (DestY + Engine.Camera.Y);

if (Engine.Camera.X < Map.Left)                       Engine.Camera.X = Map.Left;
if (Engine.Camera.X > Map.Right - DisplaySize.X)      Engine.Camera.X = Map.Right - DisplaySize.X;
if (Engine.Camera.Y < Map.Top)                        Engine.Camera.Y = Map.Top;
if (Engine.Camera.Y > Map.Bottom - DisplaySize.Y)     Engine.Camera.Y = Map.Bottom - DisplaySize.Y;

if (Map.Right - Map.Left < DisplaySize.X)
    Engine.Camera.X = Map.Left - ((DisplaySize.X - Map.Info["MapWidth"]) / 2);
```

and `MapleMap.cs:279`: `Map.OffsetY = (Map.DisplaySize.Y - 600) / 2;`

**Our `walkview.js:653` is `half.y + 60 - y` — the `Map.OffsetY` term is
missing.** The camera clamps against nothing because **the session frame
reports no map bounds at all** (I checked: no Left/Top/Right/Bottom fields).
`Camera.cs` also has `AdjustRectEnabled` — snap to integer boundaries so
sprites do not shimmer on half-pixels.

Verify: report the character's on-screen position as a fraction of the viewport,
at rest and after 3 seconds of held input. It must sit near the centre.

---

## DEFECT 4 — the avatar does not assemble, and cannot be changed

The user compared ours against the reference and it loses. **All the data is
already in `Character.nx` — this is missing plumbing, not missing assets.**

I measured the top-level folders in our own archive:

```
Cap 908   Hair 1518   Face 540   Accessory 503   Weapon 1220
Coat  Pants  Shoes  Glove  Cape  Shield  Longcoat   (+ Afterimage, PetEquip, Ring, Dragon)
```

**The 19 slots, verbatim from `AvatarForm.Designer.cs`:**

```
Head, Body, Weapon-1, Weapon-2, Cap-1, Cap-2, Coat, Pants, Longcoat, Cape,
Shield, Glove, Shoes, Hair-1, Hair-2, Face-1, Face-2, FaceAcc, Glass
```

**Folder and layer name per slot, verbatim from `AvatarForm.cs:494-645`:**

| slot | folder | layer within the image |
|---|---|---|
| Head | `Character/` (root) | `front` |
| Body | `Character/` (root) | `stand1/0/body` |
| Hair | `Character/Hair/` | `hairOverHead` |
| Face | `Character/Face/` | `face` |
| Weapon | `Character/Weapon/` | `icon` |
| Cap / Coat / Pants / Shoes / Glove / Cape / Shield / Longcoat | same name | `icon` |
| FaceAcc / Glass | `Character/Accessory/` | `icon` |

ID filters, also verbatim (`AvatarForm.cs:557-620`): Cap-1 is imgId/1000 in
1000..1003, Cap-2 1004..1006, Hair-1 30..56, Hair-2 57..85, Face-1 20..23,
Face-2 24..65, FaceAcc `0101`, Glass `0102`, Weapon-1 everything except `0170`,
Weapon-2 only `0170`.

**Requirement:** an endpoint that accepts a per-slot item id and composes from
it, using the zmap-derived `z`/`slots`/`priority` that already work
(`/api/simulate/look` returns real values: z 76/51/45, mask 1/1/2,
prio 150/150/149). Putting on a hat must not require new architecture.

---

## Verify

1. Build clean, restart, **confirm the DLL timestamp against the server start
   time**.
2. Re-run `tools/visual-check/scan-fallout.mjs` over **all 5366 maps**, not a
   sample. Paste the totals. **The HTTP-400 count must reach 0.**
3. For one of the previously-failing maps, show the spawn position and the
   foothold it lands on.
4. Camera: character at rest and after 3s of input, as a fraction of viewport.
5. Avatar: screenshot with equipment in at least four slots at once, correctly
   layered. Confirm via the endpoint that arbitration hid the covered layers.
6. Full-screen regression: `tools/visual-check/verify-fullscreen.mjs` must still
   report the exit button and the overlay picker.

## Harness notes — two traps already hit

- The server serves the **build output's** `wwwroot`, not the source tree.
  Rebuild; do not just `cp` JS and assume.
- `Page.captureScreenshot` returned a **black frame** while the canvas had
  577,589 non-background pixels. When a screenshot disagrees with a pixel
  count, take the canvas's own `toDataURL()` — that produced a 910 KB real
  frame where the screenshot was black.

## Constraints

- **Do NOT modify `C:\MUWORK\GAME\MPSH\` and do NOT modify the reference repo.**
  Both read-only. The reference is the specification.
- Do not weaken a check to make it pass.

## Scope limit

Finite budget. **First code edit in the first half, saved to disk
immediately** — three tasks timed out mid-write here.

Order: (1) the 155 HTTP 400s, (2) the off-map spawns, (3) camera bounds,
(4) equipment slots. A map you can open beats an avatar with a hat. Report
which you finished.

## A note on method

I have read the reference for everything above and cited line numbers — do not
re-derive it. When you report, cite `file:line` for anything read from the
reference and mark anything measured separately. This project produced four
confident wrong conclusions in one day, three of them mine.