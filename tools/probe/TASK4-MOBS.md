# TASK 4 — Mobs in the live session

Mobs exist as a model but are not part of the session the character walks
through. Put them in it.

## Current state — read these first

- `MapleBench/Services/MapEditor/MobPatrol.cs` — the model already exists.
  `RollRange=200`, `DefaultMoveSpeed=2f`. It is wired to `/simulate/mob` only.
- `MapleBench/Services/Simulate/MapSimSessionStore.cs` — the live session. It
  has no mobs.
- `MapleBench/wwwroot/js/walkview.js` — draws the map and the character. Does
  it draw mobs today? Find out and report the answer either way.

## The reference implementation

Read the real one — do not invent mob behaviour:

    A = C:\Users\e7896\AppData\Local\hermes\cache\necro-ref\MapleNecrocer

- `Client/Mob.cs` — the whole thing, 836 lines. `Client/Npc.cs` (306) for
  comparison.
- Line ~354: the wander roll, `Random.Next(200)` — the probability a mob
  changes direction on a given tick. Read what the roll compares against.
- Lines ~158, ~806-822: HP, collision damage, death. Read the numbers.
- ~166-175: `MoveType` comes from the mob's own animation set, not a constant.
- Patrol bounds `rx0`/`rx1` — read how they are derived.

**Note for calibration:** the reference has no aggro radius. Do not invent one
because it feels natural. If the reference does not have it, neither do we.

## Requirements

1. A mob must appear in the live session, at a position on the map a character
   can reach, on a foothold.
2. Mobs must move on their own ticks while the character walks. Their
   behaviour must come from the roll in `Mob.cs`, not from a loop.
3. Where the Z-order work from TASK 3 put the character, mobs must respect the
   same ordering rule. Reuse `Z_*`; do not introduce a parallel scheme.
4. Keep `MobPatrol.cs` as the model — wire it in, do not fork it.
5. The existing `/simulate/mob` endpoint must keep working. Do not break it.

## Scope limit

You have a finite budget. **Make the code edit in the first half.** If a field
you want does not exist in the DTO or the map data, fall back to a reasonable
value, write down why in a comment, and move on. Do not go spelunking.

## Constraints

- Match surrounding style.
- **Do NOT modify the reference repo.**
- Do not change physics, camera, or the session endpoints that already exist.

## Verify before reporting

```
cd "C:/MUWORK/GAME/MAPLESOTRY/wz工具/MapleBcnch-NX-audit"
"/c/MUWORK/app/scoop/apps/dotnet-sdk/current/dotnet.exe" build MapleBench/MapleBenchX.csproj -c Release --nologo -v q
node --check MapleBench/wwwroot/js/walkview.js
```

Report:

1. The diff per file
2. The mob constants you took from the reference, each with file:line
3. **Answer plainly: does walkview.js draw mobs today, before your change?**
4. How you verified mobs actually move (not just that it compiles)
5. What you could NOT verify
6. Anything wrong you found but did not touch

Do not claim it works because it builds.