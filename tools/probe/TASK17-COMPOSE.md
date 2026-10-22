# TASK 17 — Compose the avatar: head, hair, face, arms

Input is fixed and verified. `LastAction` now follows speed
(`CharacterPhysics.cs:219`). Hold-then-poll moves the character, release stops
it, `action` switches `stand`/`walk`. **Do not touch that; it is measured.**

This task is purely about the avatar being *composed* correctly rather than a
single `body` canvas blitted at one anchor.

## The defect, from TASK 11 before it timed out

Its numbers — **unverified by me, re-derive them yourself**:

> reference feet at y+0, ours at y+22, and the head 47px low

The single-layer `body` canvas grounds correctly at `feetΔ=0` — I measured that.
The **composed** look does not: head, hair, face and arms each need their own
anchor offset, and we blit them all at one position.

Also found, and load-bearing: **NX `z` is a string, not an integer.** A previous
probe sorted on it as an int and got an arbitrary order.

## The specification — read this before writing anything

    C:\MUWORK\GAME\MPSH\clean-client\src\Character\Look\CharLook.cpp

1. **Anchoring.** `CharLook.cpp:98`:

   > MSW's marker is the part's navel, not the character's ground origin.

   Align with the same per-pose body anchor normal clothing uses. The **face
   uses a separate offset** — `drawinfo.getfacepos()` — not the body's. This is
   almost certainly the 47px head error.

2. **Head position.** `Body.cpp` provides `get_head_position` — a non-zero
   offset. Do not treat the head as sitting at the body's anchor.

3. **Layer order.** `CharLook.cpp:175-179`:

   ```cpp
   // Back to front: the highest zmap value first.
   std::stable_sort(sprites.begin(), sprites.end(), [](const Sprite& a, const Sprite& b) {
       return a.part->z > b.part->z;
   });
   ```

   Descending, and **stable** — equal `z` keeps collection order. `z` is a
   string in NX; sort it the way the data requires, or the order is arbitrary.

4. **Slot arbitration.** `CharLook.cpp:155-173` — a 64-bit mask; where two parts
   claim the same slot, only the lowest `priority` survives. Sources:
   `AvatarLayers.h` / `AvatarLayers.cpp`, and the real values in `Base.nx`.

## Where to compose

TASK 11 chose **server-side** composition mirroring `CharLook`, which is sound —
one place, matches the reference, keeps the JS dumb. Keep that decision. If
you change it, keep the endpoint and the JS consistent.

The look composer TASK 11 wrote was lost to a timeout. **Start from the C++,
not from any draft.**

## Verify

1. Build clean, restart against the new build, **confirm the DLL timestamp
   against the server start time** — a stale process has produced three false
   readings in this project.
2. Open `Map.nx` and `Character.nx` from `C:\MUWORK\GAME\MPSHS`.
3. **Screenshot with the composed avatar visible**: head, hair, face and arms all
   present, correctly stacked, feet on the foothold line. Give the path and the
   map id. **Zoom in** — "present" and "correctly anchored" are different claims
   and at full-frame scale they are indistinguishable.
4. Report reference feet Y vs ours, as numbers. **Confirm or refute** TASK 11's
   +22 / 47px claim.
5. Report the real `z`, `priority`, `slots` values read from the archives for
   `00002000.img` `stand1`, and show the resulting order.
6. Compare against the C++ for one concrete pose, side by side.
7. Re-run the input check from TASK 16 and confirm it still works.

## Harness

    tools/visual-check/verify-scenes.mjs <cdpPort> <serverPort> [outDir]

Both known defects are fixed: a missing map id is now a hard skip rather than a
silent fallback to `maps[0]`, and key events carry the bare key name the
handler's `keyFor()` expects. Extend it; do not write a new harness.

## Constraints

- **Do NOT modify `C:\MUWORK\GAME\MPSH\`** — read-only reference.
- Do not change physics, Z formulas, session semantics, NX decoding, or the
  `LastAction` fix.
- Do not weaken a check to make it pass.

## Scope limit

Finite budget. **Make the first code edit in the first half and save to disk
immediately** — three tasks have timed out mid-write and lost everything.

Priority order: (1) head anchored correctly — a head 47px low is instantly
visible and looks broken, (2) face offset, (3) layer sort with string `z`,
(4) slot arbitration. Report which you finished.