# TASK 7 — Real WZ avatar art

The character is a stroked stick figure. The reference composes a real avatar
from WZ sprites. This is the last big "looks like a client" gap.

## Read the reference first — the compositing order matters

    A = C:\Users\e7896\AppData\Local\hermes\cache\necro-ref\MapleNecrocer

- `Client/MapleCharacter.cs:200` — `Wz.GetNodeA("Character/00002000.img")`,
  the base body node.
- `Client/MapleCharacter.cs:272` — `("Character/" + Dir + EquipID + ".img")`,
  the equipment layer built from direction and equipment id.
- `Client/MapleCharacter.cs:142-143` — the animation states it reads
  (`stand1`, `walk1`). Read how the frame is picked from the sprite's own
  timing; do not assume a fixed rate.
- The character's own canvas size — a WZ avatar is NOT a 55x79 box. Find the
  real dimensions and origin (the origin is usually not the centre, and getting
  it wrong puts the head in the floor).

**This is the part people get wrong:** the current figure is a55x79 stroked
skeleton centred on its own anchor. A WZ avatar has an origin and a canvas that
do not match that assumption. Find both in the reference and use them.

## What already exists in MapleBench

- The map art pipeline works — tiles and objects already load from the archive
  and draw. Whatever fetches and caches those is the machinery to reuse for
  avatar parts. Find it, do not build a second one.
- TASK 3 introduced Z ordering and TASK 4 put mobs in the session. The avatar
  must draw through the same path so it sorts against geometry and mobs.

## Requirements

1. Draw the real avatar when the archive has it. When it does not, fall back to
   today's stick figure **and say so on screen** — a silent fallback that looks
   like success is worse than the current state.
2. The origin must come from the reference/WZ data, not from a guess about
   where the centre is.
3. Frame selection follows the sprite's own timing, and it must follow the
   character's actual action — standing still is `stand1`, walking is `walk1`,
   and the animation must not run while the character is idle.
4. Direction must work if the data supports it. Read the reference and follow
   what it actually does; if a direction is missing, fall back and say so.
5. The avatar must respect the Z order from TASK 3.

## Scope limit

Finite budget. **Make the code edit in the first half.** When a piece of the
avatar is missing from the data — no equipment node, no named frame — use the
body alone, write down why in a comment, and move on. Do not go spelunking for
a richer fallback.

## Constraints

- **Do NOT modify the reference repo.**
- Do not change physics, camera, Z formulas, mobs, or the session.
- Match the surrounding style.

## Verify before reporting

1. Build clean; `node --check` on what you touched.
2. Prove the frame selection actually changes over time while walking and
   STOPS while idle. Synthetic geometry is acceptable — say that it is.
3. Prove the origin is used: show the character standing on a foothold line
   with its feet on the line, not its centre. Numbers or the assertion you ran.
4. State plainly which parts were exercised against real archive data and which
   were not.

Report the diff per file, the WZ node paths you read, the canvas size and
origin you found with file:line, and everything you could not verify.

Do not claim it looks right because it renders. Say what you measured.