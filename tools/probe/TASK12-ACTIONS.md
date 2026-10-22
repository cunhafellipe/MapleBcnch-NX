# TASK 12 — Full action set, interpolation, and the real frame model

Depends on TASK 9C and 11 landing first. If the feet are not on the ground yet,
finish that before starting this — every visual judgement made on a misplaced
character is worthless.

## The specification

    C:\MUWORK\GAME\MPSH\clean-client\src\Graphics\Animation.cpp

`Frame::Frame(nl::node src)` at **:58-102** reads, per frame:

| key | meaning | fallback |
|---|---|---|
| `delay` | frame duration in ms | **0 → 100** |
| `dir` | obstacle/facing direction | — |
| `head` | head-over marker node | — |
| `a0` / `a1` | opacity pair, **alpha-blended over the frame's lifetime** | see `:72-89` |
| `z0` / `z1` | scale pair | see `:91-100` |

The `a0`/`a1` handling has **three distinct cases** and they are not
interchangeable — `:72-89`:

```cpp
if (hasa0 && hasa1)      opacities = { src["a0"], src["a1"] };
else if (hasa0)          opacities = { a0, 255 - a0 };
else if (hasa1)          opacities = { 255 - a1, a1 };
else                     opacities = { 255, 255 };
```

The `z0`/`z1` equivalent is `:91-100`. Get these wrong and fades and scaling
subtract from the wrong end.

`Animation::update(uint16_t timestep)` at **:265+** shows the frame walk
including the **zigzag** bounce, where reaching the last frame reverses
`framestep` instead of wrapping. Read it and match it.

`Animation::draw(args, alpha)` at **:243-263** computes an `interframe` and
interpolates opacity and scale between adjacent frames — it does not snap.

`CharLook.cpp` selects stances; the action names (`stand1`, `walk1`, `swing1`,
`jump`, `prone`, `sit`, `alert`, and the ladder/rope poses) are spread across
`Character\Char.cpp` and `Gameplay\`. Ladder and rope are special-cased at
`CharLook.cpp:89`.

## What exists

`Character.nx` has **158 action nodes** per character image. We currently
animate `stand1` and `walk1` and nothing else. Verify how many you can drive.

## Requirements

1. Read the real property values for at least `stand1`, `walk1`, `jump`, `sit`,
   `swing1` out of `Character.nx` and report what is actually there — including
   which frames carry `a0`/`a1`/`z0`/`z1` at all. **Do not assume every action
   uses them.**
2. Implement `delay` with the 100 ms fallback. We have this; confirm it matches.
3. Implement opacity and scale interpolation across a frame's lifetime, with
   the three-case fallback exactly as above.
4. Implement the zigzag frame walk.
5. Implement frame interpolation on draw (`interframe`), not frame snapping.
6. Stance selection: pick the action from the character's actual state —
   moving, airborne, sitting, on a ladder or rope. Freezing facial animation on
   stationary ladder/rope poses is a real requirement (`CharLook.cpp:256`).

## Verify

1. Build clean, restart against the new build, confirm the DLL timestamp.
2. **Screenshots of at least four distinct actions** — `stand1`, `walk1`,
   `jump` mid-air, `sit` — with the character visible in each. Give the paths.
3. Report the measured frame timing for one action: the delay values you read,
   the resulting cycle duration, and the observed frame index over time.
4. For one action carrying `a0`/`a1`, show the measured opacity at two points
   in the frame and confirm it interpolates rather than snapping.

Use `tools/visual-check/verify-char.mjs <cdpPort> <out.png>` to drive the page.
Do not write a new harness.

## Constraints

- **Do NOT modify `C:\MUWORK\GAME\MPSH\`** — read-only reference.
- Do not change physics, camera, Z formulas, session semantics, or NX decoding.
- Do not weaken a check to make it pass.

## Scope limit

Finite budget. Order: (1) action coverage — get jump and sit animating at all,
(2) frame interpolation, (3) opacity/scale pairs, (4) zigzag. Report which you
finished. A character that jumps with snapping frames is more useful than one
that stands perfectly still with smooth interpolation.