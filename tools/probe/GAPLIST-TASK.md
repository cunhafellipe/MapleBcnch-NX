# GAP-LIST TASK — read-only reconnaissance

You are in codebase **B** (a WZ map editor, C#/ASP.NET). Codebase **A** is a
FINISHED open-source MapleStory client (MonoGame, C#) located at:

    A = C:\Users\e7896\AppData\Local\hermes\cache\necro-ref\MapleNecrocer

A is the SPECIFICATION. It is a real working client with a live character that
moves. B has a "simulator" page that currently only replays a precomputed
polyline of walk positions.

Read both trees and produce a ranked GAP LIST — what A's client does that B's
simulator does NOT.

## Check specifically

1. **GAME LOOP.** Where does A pump Update/Draw? Find the real accumulator /
   fixed timestep. Report exact `file:line` and the constant values.
2. **SPAWN.** How does A decide where the character starts on a map?
3. **PHYSICS.** A's foothold tree, gravity constant, jump, wall collision.
   Name the constants and their values.
4. **INPUT.** Is A edge-triggered (keydown) or level-triggered (key held)?
   Which file implements it?
5. **RENDER.** What draws the map tiles? What draws the character sprite?
   Does A load sprites from WZ, or is it placeholder art?
6. **CAMERA.** Which file? Does it follow the character?
7. **MOBS / NPCS.** Patrol logic, aggro, rendering.
8. **NETWORK.** Does A talk to a server at all, or is it fully offline?

## Output format

For EACH gap, one line:

    <what> | A: <path:line> | B: <path or MISSING> | effort: S/M/L

Rank biggest gap first. End with the single biggest structural difference
between "a client you can walk around in" and "a polyline player".

## Constraints

Read actual files. Do not guess from filenames. **Do NOT write code. Do NOT
modify any file.** Read-only reconnaissance.