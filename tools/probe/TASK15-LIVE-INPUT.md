# TASK 15 — The simulator replays. It does not respond. This is the original problem.

## The user's original complaint, restated

The simulator was built to test whether a character can traverse a map, so it
replays a **precomputed polyline** of walk positions. That is not a client you
can walk around in. Everything built since — camera, Z order, mobs, avatar —
sits on top of a player that does not actually respond to input.

**Measured, right now, by me, on the live page:**

```
stand       map=109050001  action=walk  drawn=102316
walk-right  map=109050001  action=walk  drawn=102316   ← ArrowRight held 2400ms
walk-left   map=109050001  action=walk  drawn=102316   ← ArrowLeft held 2400ms
jump-map    map=109050001  action=walk  drawn=102316   ← Space held 900ms
sit-map     map=109050001  action=walk  drawn=102316   ← KeyS held 700ms
```

**Identical action state and identical pixel count across every input.** Holding
right for 2.4 seconds changes nothing. The keyboard events my harness dispatches
may not reach the handler — establish which it is before concluding the
simulator ignores input, and say which you proved it.

## The specification

    C:\MUWORK\GAME\MPSH\clean-client\src\Character\Char.cpp     35,430 B
    C:\MUWORK\GAME\MPSH\clean-client\src\Character\Player.cpp   69,622 B
    C:\MUWORK\GAME\MPSH\clean-client\src\Gameplay\Physics\       (Foothold.h and the
                                                               rest of the tree)
    C:\MUWORK\GAME\MPSH\clean-client\src\MapleStory.cpp           main loop

Read for:
- **The main loop.** Fixed 8ms step with an accumulator and alpha interpolation,
  decoupled from frame rate. Our `FixedUpdateDelta` is 1/60s. Reconcile these
  and say which you chose and why.
- **Input.** Is it edge-triggered (keydown) or level-triggered (key held)? Find
  the exact place. Level-triggered matters — a character that only moves while
  a key edge fires cannot walk.
- **Physics.** Gravity, jump impulse, wall collision, the foothold tree.
  `Gameplay\Physics\Foothold.h` is named in the NX-reading grep.
- **Stance selection.** How the current state maps to an animation. This is why
  every action reads `walk` above — the action never changes because nothing
  drives it.

## Requirements

1. **Live input drives the character.** Holding a direction moves it; the
   position changes while held and stops when released.
2. **Action follows state.** Standing still → `stand1`, moving → `walk1`, in the
   air → `jump`. The HUD and the sprite must agree with each other.
3. **Physics runs on a fixed timestep** with an accumulator, so behaviour does
   not change with frame rate.
4. Keep the existing traverse verification working — `/api/simulate/session`
   and its sweep are verified and must not regress.

## Verify — numbers first, screenshot second

1. Build clean, restart against the new build, confirm the DLL timestamp.
2. Open `Map.nx` and `Character.nx` from `C:\MUWORK\GAME\MPSHS`.
3. **The decisive measurement**: hold `ArrowRight` for 3 seconds and report the
   tick's x at t=0, t=1000ms, t=2000ms, t=3000ms. They must increase
   monotonically. Then release and confirm it stops. Then hold `ArrowLeft` and
   confirm x decreases. **If these numbers are flat, input still does not work
   and the task is not done.**
4. Report the action at each of those four sample points — it must change from
   `stand1` to `walk1`.
5. Screenshot mid-walk with the character visible, and a screenshot airborne.
6. Re-run the traverse check and report it did not regress.

Use `tools/visual-check/verify-scenes.mjs <cdpPort> <serverPort> [outDir]` — it
already drives the page, switches to the simulator section, holds keys and
measures. **Extend it rather than writing a new harness**, and fix its known
defect: it silently falls back to `maps[0]` when a requested map id is absent,
so it reported `mushroom map=0` while claiming to test `106020010`. A harness
that lies about which map it tested invalidates every number it prints.

## Constraints

- **Do NOT modify `C:\MUWORK\GAME\MPSH\`** — read-only reference.
- Do not change Z formulas, session semantics, or NX decoding — verified working.
- Do not weaken a check to make it pass.

## Scope limit

Finite budget. Priority: **(1) input moves the character, (2) action follows
state, (3) fixed timestep correctness, (4) frame-rate independence measured at
two different refresh rates.** The first two are the whole point of the task.
Report which you finished.

## A note on method

This project has repeatedly generalised from one sample and reported the
generalisation as a conclusion — three times in one day, each one confidently
wrong. Read the C++ first. And check your own arithmetic before reporting a
number: I just reported a phantom 9-pixel grounding error that was my own
measurement bug, not a defect in the code.