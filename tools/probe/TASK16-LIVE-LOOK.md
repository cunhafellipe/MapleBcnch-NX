# TASK 16 — Live input, and the composed avatar (TASK 11 resumed + TASK 15)

This merges two dispatches. TASK 11 timed out mid-write and its work was lost —
the look composer never hit disk. TASK 15 is the user's original complaint.

**Budget discipline: TASK 15 first.** The composed avatar is worthless if the
character cannot be moved.

---

## PART 1 — Live input *(do this first)*

### The measurement that says it is broken

```
stand       map=109050001  action=walk  drawn=102316
walk-right  map=109050001  action=walk  drawn=102316   ← ArrowRight held 2400ms
walk-left   map=109050001  action=walk  drawn=102316   ← ArrowLeft held 2400ms
jump-map    map=109050001  action=walk  drawn=102316   ← Space held 900ms
sit-map     map=109050001  action=walk  drawn=102316   ← KeyS held 700ms
```

Identical action state and identical pixel count under every input. The
simulator replays a precomputed polyline. Hold right for 2.4 seconds and
nothing moves.

**My harness dispatches `KeyboardEvent` on `window`. Establish first whether
those events reach the handler at all** — it may be my probe that is wrong
rather than the app. Report which you proved, and how.

### Already built, do not rebuild

`SimulatorEndpoints.cs` has session routes from TASK 6, verified working:

```
POST /api/simulate/session
POST /api/simulate/session/{id}/frame
GET  /api/simulate/session/{id}
POST /api/simulate/session/{id}/input
```

The server-side stepping machinery exists. What is missing is the **client**
driving it from real input, and the character actually responding.

### The specification

    C:\MUWORK\GAME\MPSH\clean-client\src\MapleStory.cpp        main loop
    C:\MUWORK\GAME\MPSH\clean-client\src\Character\Char.cpp    35,430 B
    C:\MUWORK\GAME\MPSH\clean-client\src\Character\Player.cpp  69,622 B
    C:\MUWORK\GAME\MPSH\clean-client\src\Gameplay\Physics\      foothold tree

Read for:
- **The main loop**: fixed **8ms** step with an accumulator and alpha
  interpolation, decoupled from frame rate. Ours is `FixedUpdateDelta = 1/60s`.
  Reconcile, and say which you chose and why.
- **Input is level-triggered** (key held), not edge-triggered. Find the exact
  place. A character that only moves on a key edge cannot walk.
- **Physics**: gravity, jump impulse, wall collision, the foothold tree.
- **Stance selection**: state → animation. This is why every action reads `walk`
  above — nothing drives it.

### Verify — numbers first

1. Hold `ArrowRight` 3s. Report the tick's **x** at t=0, 1000, 2000, 3000ms.
   They must increase monotonically. Release → confirm it stops. Hold
   `ArrowLeft` → confirm x decreases. **Flat numbers mean not done.**
2. Report the **action** at those four points. It must go `stand1` → `walk1`.
3. Screenshot mid-walk with the character visible; screenshot airborne.
4. Re-run the traverse check; it must not regress.

---

## PART 2 — The composed avatar *(TASK 11, resumed)*

### What TASK 11 found before it timed out

Its own numeric proof, which I did not verify — **re-derive it yourself**:

> reference feet at y+0, ours at y+22, and the head 47px low

The single-layer `body` canvas grounds correctly at `feetΔ=0`. The **composed**
look — head, hair, face, arms — does not. And `Body.cpp` uses
`get_head_position`, not a zero offset.

**The work was lost.** The look composer was written but never hit disk. Start
it from the C++, not from that draft.

### The specification

    C:\MUWORK\GAME\MPSH\clean-client\src\Character\Look\CharLook.cpp

1. **Anchoring** — `CharLook.cpp:98`: *"MSW's marker is the part's navel, not
   the character's ground origin."* Align it with the same per-pose body anchor
   used by normal clothing. The **face uses a separate offset** —
   `drawinfo.getfacepos()` — not the body's.

2. **Layer order** — `CharLook.cpp:175-179`:

   ```cpp
   // Back to front: the highest zmap value first.
   std::stable_sort(sprites.begin(), sprites.end(), [](const Sprite& a, const Sprite& b) {
       return a.part->z > b.part->z;
   });
   ```

   Descending, and **stable** — equal `z` keeps collection order.

3. **Slot arbitration** — `CharLook.cpp:155-173`. A 64-bit mask; where two parts
   claim the same slot, only the lowest `priority` survives. Sources:
   `AvatarLayers.h` / `AvatarLayers.cpp`.

4. **NX `z` is a string**, not an integer — a previous probe got this wrong.
   Sort it correctly or the order is arbitrary.

### Decide where to compose

TASK 11 chose **server-side** composition, mirroring `CharLook` in C#. That is
sound — one place, matches the reference. If you keep it, keep it consistent
between the endpoint and the JS.

### Verify

1. Screenshot with the composed avatar visible: **head, hair, face and arms all
   present, correctly stacked**, feet on the foothold line. Give the path.
2. Report the reference feet Y vs ours, as numbers. TASK 11 claimed +22 and a
   47px-low head — **confirm or refute it**.
3. Report the real `z`, `priority` and `slots` values read from `Base.nx` /
   `Character.nx` for `00002000.img` `stand1`, and show the resulting order.
4. Compare against the C++ for one concrete pose, side by side.

---

## Harness — extend, do not rewrite

    tools/visual-check/verify-scenes.mjs <cdpPort> <serverPort> [outDir]

It switches to the simulator section, holds keys, measures `placement()` and
takes screenshots. **Fix its known defect first:** it silently falls back to
`maps[0]` when a requested id is absent — it printed `mushroom map=0` while
claiming to test `106020010`. A harness that lies about which map it tested
invalidates every number it prints. Make a missing map a hard failure.

## Constraints

- **Do NOT modify `C:\MUWORK\GAME\MPSH\`** — read-only reference.
- Do not change Z formulas, session semantics, or NX decoding — verified working.
- Do not weaken a check to make it pass.

## Scope limit

Finite budget. **Part 1 is the priority.** If only one part completes, complete
Part 1 — a character you can actually walk is worth more than a perfectly
composed sprite you cannot move. Report which you finished and which you did not.

## A note on method

Three tasks have timed out mid-write and lost their work. **Make the first code
edit in the first half of the budget**, and commit to disk early.

This project has repeatedly generalised from one sample and reported the
generalisation as fact — three times in one day. Read the C++ first, and check
your own arithmetic before reporting a number: I published a phantom 9-pixel
grounding error that was my measurement bug, not a defect.