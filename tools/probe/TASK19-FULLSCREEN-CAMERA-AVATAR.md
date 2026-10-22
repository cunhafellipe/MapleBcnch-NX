# TASK 19 — Full-screen simulator, a correct camera, and a real avatar

The user has redirected the project. **The reference simulator is the target;
`C:\MUWORK\GAME\MPSH` is only a data source that makes it faster.** Build to
the reference's feature set.

    A = C:\Users\e7896\AppData\Local\hermes\cache\necro-ref\MapleNecrocer

## Four problems, in the user's order

### 1. The simulator must be full-screen, with the rest collapsed

Clicking it takes over the window: no file tree, no left nav, no right
inspector, no map-picker column eating width. One prominent **exit button**
returns to the editor.

The map picker moves **inside** the simulator as an overlay — openable on
demand, not a permanent column.

### 2. The camera is wrong

The user says it should be the player's own view window.

**The reference, `MapleCharacter.cs:749-764`:**

```csharp
DestX = DisplaySize.X / 2 - X + Engine.Camera.X;
if (Math.abs(DestX) > 1)
    Engine.Camera.X -= DestX * (12f / 800f);

DestY = (DisplaySize.Y / 2) + (Map.OffsetY + 60) - Y;
if (Math.abs(DestY + Engine.Camera.Y) > 1)
    Engine.Camera.Y -= 0.01f * (DestY + Engine.Camera.Y);

if (Engine.Camera.X < Map.Left)      Engine.Camera.X = Map.Left;
if (Engine.Camera.X > Map.Right - DisplaySize.X)  Engine.Camera.X = Map.Right - DisplaySize.X;
if (Engine.Camera.Y < Map.Top)       Engine.Camera.Y = Map.Top;
if (Engine.Camera.Y > Map.Bottom - DisplaySize.Y) Engine.Camera.Y = Map.Bottom - DisplaySize.Y;

if (Map.Right - Map.Left < DisplaySize.X)
    Engine.Camera.X = Map.Left - ((DisplaySize.X - Map.Info["MapWidth"]) / 2);
```

**Our `walkview.js:653` has the bug:**

```js
const destY = half.y + 60 - y;     //   <-- missing Map.OffsetY
```

The reference is `Map.OffsetY + 60`. **We copied `60` and dropped `OffsetY`.**

And `MapleMap.cs:279`: `Map.OffsetY = (Map.DisplaySize.Y - 600) / 2;`

**Where the bounds come from — `MapleMap.cs:175-194`:**

```
VR mode:    Left = VRLeft   Right = VRRight
            Top  = VRTop    Bottom = VRBottom + 15
non-VR:     Left  = FootholdTree.MinX1.First()
            Right = FootholdTree.MaxX2.Last()
            Top    = -Info["centerY"] + 50
            Bottom = -Info["centerY"] + Info["MapHeight"] - 55
```

**I measured: our session frame reports NO map bounds at all.** The camera
clamps against nothing. `VRLeft`/`VRTop`/`centerY`/`MapHeight` are not read
from `info`.

Also `Camera.cs` has `AdjustRectEnabled` — snap the rect to integer boundaries
so sprites do not shimmer on half-pixels. Match that.

### 3. The avatar is not properly assembled

The user compared ours against the reference and it loses. The reference has a
full equipment system — read these before designing anything:

    A\AvatarForm.Designer.cs    17 slots
    A\CharaSim\Character.cs     35,033 B   the assembly
    A\CharaSim\CharaEquip.cs      8,027 B
    A\CharaSim\CharacterStatus.cs 9,669 B

**The 17 slots, verbatim from the designer file:**

```
Head, Body, Weapon-1, Weapon-2, Cap-1, Cap-2, Coat, Pants,
Longcoat, Cape, Shield, Glove, Shoes, Hair-1, Hair-2,
Face-1, Face-2, FaceAcc, Glass
```

**The action set, verbatim from `AvatarForm.cs`:**

```
stand1, stand2, walk1, walk2, alert, heal, rope, ladder, fly,
prone, proneStab, jump, sit, shoot1, shoot2, shootF,
swing1, swing2, swing3, swingT1..T3, swingO1..O3, swingTF, swingOF
```

Our character composes **6 parts and cannot change any of them**. The reference
swaps every slot and picks any action.

Priorities: the avatar must (a) assemble completely, (b) accept an equipment
override per slot, (c) animate any action in that list. Start with the slot
plumbing — an endpoint that takes a slot map and composes from it. Showing a
hat must not require new architecture.

### 4. Scope discipline

**Every feature the reference simulator has is in scope.** Read the reference
and enumerate what it can do, then work down that list. Do not stop at
"walking works".

---

## Verify

1. Build clean, restart, **confirm the DLL timestamp against the server start
   time** — a stale process has produced three false readings in this project.
2. **Full-screen**: screenshot showing the simulator with no editor chrome,
   with the exit button visible. Then screenshot after clicking exit, showing
   the editor back.
3. **Map picker as an overlay**: screenshot with it open over the canvas, and
   with it closed.
4. **Camera**: report the character's on-screen position as a fraction of the
   viewport, at rest and after holding a direction for 3 seconds. It must be
   near the centre, not pinned to a corner. Report the clamp bounds and confirm
   they come from `VRLeft`/`VRTop` or the foothold range as `MapleMap.cs`
   specifies. **Screenshot the same spot at two zoom levels** — the character
   must stay centred.
5. **Avatar**: screenshot with equipment on at least four different slots
   (e.g. Cap + Coat + Shoes + Weapon), correctly layered. Confirm via the
   endpoint that the arbitration from TASK 18 actually hid the covered layers.
6. **Action**: screenshot or frame capture for `stand1`, `walk1`, `jump`, `sit`.

Use `tools/visual-check/verify-scenes.mjs <cdpPort> <serverPort> [outDir]`.
Extend it — do not write a new harness.

**Known harness defect to fix first:** `feetΔ` compares the look's **local**
coordinates (`bounds.maxY`) against the **world** tick Y. They are different
coordinate spaces and must not be subtracted. Report grounding from rendered
pixels or not at all.

## Constraints

- **Do NOT modify `C:\MUWORK\GAME\MPSH\` and do NOT modify the reference repo.**
  Both are read-only. The reference is the specification.
- Do not weaken a check to make it pass.

## Scope limit

Finite budget. **Make the first code edit in the first half and save to disk
immediately** — three tasks have timed out mid-write here and lost everything.

Order: (1) full-screen + exit, (2) camera bounds from real map info, (3) the
avatar equipment slot plumbing. Report which you finished.

## A note on method

Everything above was measured or read from the reference — you do not need to
re-derive it. This project produced three confident wrong conclusions in one day
by generalising from a single sample. Cite `file:line` when you claim
something, and say plainly when something could not be verified.