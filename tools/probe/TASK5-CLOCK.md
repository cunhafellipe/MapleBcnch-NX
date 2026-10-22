# TASK 5 — Give the session its own clock

## The problem, stated exactly

Right now the world advances **only when the browser asks**:

    MapleBench/Services/Simulate/MapSimSessionStore.cs
        Step(...) is the sole mutator, reachable only from
        MapleBench/Api/SimulatorEndpoints.cs (POST /session/{id}/frame)

Close the browser tab and the character stops mid-stride. Throttle the network
and the walk runs in slow motion. The clock is on the other side of a socket.

The finished reference does not work this way. Read it:

    A = C:\Users\e7896\AppData\Local\hermes\cache\necro-ref\MapleNecrocer

`RenderFormDraw.cs` around lines 176-202 (at the repo root, NOT under
`Client/`). It has a fixed timestep and an accumulator. It ticks whether or
not not anyone asks. `FixedUpdateDelta = 0.016666668f`. Copy that value.

## What to build

A session that advances on its own.

1. `MapSimSessionStore` gains a per-session tick loop on the fixed timestep.
   Input arrives asynchronously and is applied to the session's pending-input
   state; the tick loop consumes it.
2. A client that stops stepping manually still sees the character move.
   `GET /session/{id}` must reflect the new position without the client having
   pushed a frame.
3. The existing `POST /session/{id}/frame` endpoint must keep working — MCP
   and `tools/probe/sim-walk.mjs` both drive it and must not break.
4. Idle sessions must still be swept. The sweep exists today; do not let a
   self-ticking session leak. This is the one part where "it ticks forever"
   turns a bounded leak into an unbounded one.

## How to decide the threading model — this is the real design question

Read how the server is hosted first (`Program.cs`) and pick the model the
existing code can actually support. A `BackgroundService` that ticks all live
sessions is one option; a per-session timer is another. **Say which you chose
and why, and what happens when two ticks would overlap.**

Do not invent an abstraction the codebase does not need. The session store is
already a singleton.

## Constraints

- **Do NOT modify the reference repo.**
- Do not change the physics constants — they were verified against the
  reference and match.
- Keep `sim-walk.mjs` passing. It is the acceptance check:
  `WZ_SERVER=... node tools/probe/sim-walk.mjs .simdata/Map.nx 910020202`

## Scope limit

Make the code edit in the first half of your budget. Where you must choose,
choose and write down why. Do not go spelunking for a "better" abstraction.

## Verify before reporting

1. Build clean.
2. Start a session, push input once, then poll `GET /session/{id}` several
   times with a gap between — position must keep changing **without** any
   further frame pushes. Paste the actual numbers.
3. Run `sim-walk.mjs` and paste the result.
4. Report how you proved idle sessions still get swept.

Report the diff per file, your threading-model choice with reasoning, what you
could NOT verify, and anything wrong you found but did not touch.

Do not claim it ticks because it compiles. Paste the poll output.