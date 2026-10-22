# ROADMAP — MapleBench simulator, remaining work to "a client you can walk around in"

## Where we are

| # | Task | State |
|---|---|---|
| 1 | Physics defects fixed | ✅ verified |
| 2 | Camera follow | ✅ verified |
| 3 | Z ordering | ✅ verified |
| 4 | Mob patrol | ✅ verified |
| 5 | Clock | ✅ verified |
| 6 | Session sweep | ✅ verified |
| 7 | Avatar module | ⚠️ code written, never seen draw |
| 8 | NX canvas decoding | ✅ verified — 21×31 character + 90×60 tile, real PNGs |
| 9 | Map art on screen | ✅ verified — grass, stone, palms, ticket booth |
| 9C | `placement()` NaN | 🔄 running |
| 11 | Sprite layering | 📋 queued |

**The single biggest change since task 8: the specification moved.** We had been
guessing at rendering behaviour from single observations. The real v83 client
source is on this machine at `C:\MUWORK\GAME\MPSH\clean-client\src` — 594
files, 125,988 lines. It is read-only and authoritative.

## The rule from here on

`MPSH` is the specification. When our C# and their C++ disagree, the C++ is
right and we have the bug. Read before writing.

    Graphics\Animation.cpp     frame delay, interframe, zigzag, a0/a1, z0/z1
    Graphics\Texture.cpp       origin handling, texture rect
    Character\Look\CharLook.cpp       layer sort, slot arbitration, anchors
    Character\Look\AvatarLayers.*     z / priority / slots tables
    Character\Char.cpp         stance and action selection
    AGENTS.md                  rules the author wrote after their own failures

## Remaining work, in dependency order

### R1 — Feet on the ground *(blocks everything visual)*
`placement(x, y, dir)` is called with one argument. Then the deeper issue:
`CharLook.cpp:98` says the marker is the part's **navel**, not the ground
origin, and the face uses a separate offset (`getfacepos()`). Until this is
right, every later visual claim is unjudgeable.
Success: screenshot, feet on the foothold line, numbers for the delta.

### R2 — Layer order and slot arbitration
`CharLook.cpp:175-179` stable-sorts by descending `z`. `:155-173` resolves 64
slots by lowest `priority` wins. Read the real values out of `Base.nx` /
`Character.nx` and compare against the C++ for one concrete pose.
Success: the two orders side by side, matching.

### R3 — Full action set *(currently only stand1/walk1)*
`Animation.cpp:58-102` reads per frame: `delay`, `dir`, `head`, `a0`/`a1`
opacity pair, `z0`/`z1` scale pair, with `delay == 0` falling back to **100**.
`CharLook.cpp` selects stances across 40+ action references.
Success: jump, sit, swing1, prone, alert all animate. Interpolation and zigzag
work.

### R4 — Mobs with real art *(patrol works, pixels do not)*
`Mob.nx` is 817 MB and we only opened geometry. `Gameplay\MapleMap\Objects\Mob.cpp`
has 94 delay/frame references — the real mob animation model.
Success: mobs on screen with correct art and animation, Z-sorted against terrain.

### R5 — NPCs *(66.7 MB unread)*
`Npc.nx` exists, nothing consumes it. `IO\UITypes\World\` has the NPC UI.

### R6 — MiniMap *(70 refs in the client)*
`IO\UITypes\Hud\UIMiniMap.cpp`.

### R7 — Weather *(27 refs)*
`Gameplay\MapleMap\MapWeather.cpp` — real files, read but not drawn.

### R8 — Audio *(235 refs, 368 MB Sound.nx)*
Lower priority: correctness first, sound last.

### R9 — Player-facing features *(out of simulator scope)*
Skills (220 refs), quests (112), buffs (421), vehicles (267), expressions
(144), cash shop. These belong to the client, not the map simulator. Park them
— do not let them expand the simulator into a game.

## Method that got us here, and the failure it corrects

Nine tasks in, the failure mode has been consistent: **observe one thing,
generalise, write code.** Three times that produced a confident wrong
conclusion — "the archive has no art" (the data was in `MPSHS`), "the node is
not a canvas" (I probed a container), "the avatar draws" (omp's own harness,
not the page).

The corrections that worked: read the reference, and measure the live page
instead of trusting a report. `tools/visual-check/verify-char.mjs` is the
measurement harness — it found the `"[object Object]"` that six tasks of
reading code had missed.

**Rule: screenshot or measured numbers, every time. No exceptions.**

## Verification harness — reuse, do not rewrite

    tools/visual-check/verify-char.mjs <cdpPort> <out.png>

Drives the real page over CDP: opens the simulator, walks a real map with real
art, reads canvas pixels, holds a key to walk, screenshots. Globals are
`app.simulator.viewer`; Node's built-in `WebSocket` needs `addEventListener`,
not `.on()`.

    C:\MUWORK\GAME\MPSH\AGENTS.md   read this, it is rules not a template

## Constraints for every task in this roadmap

- **`C:\MUWORK\GAME\MPSH\` is read-only.** Including its two git repos.
- Do not weaken a check to make it pass.
- Confirm the DLL timestamp before reporting — a stale process makes every
  measurement a lie. This bit us three times.
- If it cannot be done, say so plainly with the numbers. A truthful "this needs
  X, here is X" beats a green check on something that draws nothing.