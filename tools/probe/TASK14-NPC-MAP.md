# TASK 14 — NPCs and the minimap

Depends on TASK 9C, 11, 12, 13 landing. Do not start until the player character
is correctly anchored — otherwise you cannot tell an NPC misplaced from an NPC
correctly placed.

## Two independent pieces. They can ship separately; do not let one block the other.

### Part A — NPCs (`Npc.nx`, 66.75 MB, never opened)

Nothing in this project has read `Npc.nx`.

The specification is on this machine, read-only:

    C:\MUWORK\GAME\MPSH\clean-client\src\IO\UITypes\World\
      UINpcTalk.cpp   72,440 bytes — the NPC interaction UI
      UINpcTalk.h     11,898 bytes
    C:\MUWORK\GAME\MPSH\clean-client\src\Data\   (quest + npc data)

Read for: how an NPC is placed on a map (its position and which foothold it
stands on), how its sprite is anchored — note that `CharLook.cpp:98` established
that markers are **navels**, not ground points, so an NPC's marker is probably
not its feet either — its facing direction, its name plate, and whether NPCs
animate at all.

Questions to answer from the real data before writing code:
- What is the shape of an NPC node in `Npc.nx`? Report it.
- How many NPCs are on a real map? Report the count for one specific map.
- Do NPCs have frames and delays, or are they single still images? **Report
  what you find rather than assuming.**

### Part B — Minimap

    C:\MUWORK\GAME\MPSH\clean-client\src\IO\UITypes\Hud\UIMiniMap.cpp   27,833 bytes
    C:\MUWORK\GAME\MPSH\clean-client\src\Configuration.cpp              (70 refs)

Read for: what data the minimap is built from (map image? tile colours?
player position only?), where the player's dot and the viewport rectangle come
from, and whether it is minimap art from `UI.nx` (519 MB, also never opened) or
generated from map geometry.

That distinction decides the whole approach — **report which it is before
building anything.**

## Requirements

1. NPCs drawn on real maps, correctly anchored and Z-sorted. Name plate if
   the C++ has one.
2. Minimap showing the player's position within the map bounds, and the
   viewport rectangle if the C++ draws one.
3. If a piece turns out to need art that is not in the archives we have open,
   say so with the node path — that is a finding, not a failure.

## Verify

1. Build clean, restart against the new build, confirm the DLL timestamp.
2. Open `Map.nx`, `Character.nx` and `Npc.nx` from `C:\MUWORK\GAME\MPSHS`.
3. **Screenshots with NPCs visible** on a map that has them. Give paths and the
   map id.
4. **Screenshot with the minimap visible** showing a moving player dot.
5. Report the PNG byte count for one NPC layer, fetched by the browser.

Use `tools/visual-check/verify-char.mjs <cdpPort> <out.png>` to drive the page.
**Two things it already got wrong that you must not repeat:** it must call
`app.sections.open('simulator')` (not just `sim.open()`) or the map simulates
off-screen and the screenshot is evidence of nothing; and it asserts
`sections.active === 'simulator'` before capturing. Do not write a new harness.

## Constraints

- **Do NOT modify `C:\MUWORK\GAME\MPSH\`** — read-only reference.
- Do not change physics, camera, Z formulas, session semantics, or NX decoding.
- Do not weaken a check to make it pass.

## Scope limit

Finite budget. If you can only finish one part, finish **Part A** — an NPC
standing correctly on the map is more visible to a user than a minimap corner
widget. Say which you did and which you did not.

## A note on method

The recurring failure in this project has been observing one thing, generalising,
and writing code — and it produced three confident wrong conclusions before the
C++ source was found. `MPSH` has the answer in source form. Read it first.