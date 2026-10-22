# TASK 6 — A session that only ticks is never swept

## The bug, confirmed by measurement

A session opened and then left completely alone is still alive after 130
seconds, with `IdleTimeoutMs = 120_000`. Measured on a clean build:

```
  session 6cb3d7bda5ab created
  +8s   alive, frame=477
  +16s  alive, frame=923
  ...
  +128s alive, frame=7042      <-- never swept
```

## Root cause

    MapleBench/Services/Simulate/MapSimSessionStore.cs:775

`Peek()` refreshes `session.LastTouchedMs`. So a client that polls `GET
/session/{id}` — which is the *normal* thing for a client with a server-side
clock to do — keeps the session alive forever. The session ticks whether or
not anyone looks, and looking resets its idle timer.

That is self-defeating: the whole point of TASK 5 was that the character moves
because time passed, not because someone looked. But looking still keeps it
alive indefinitely, so a browser tab left open overnight holds its session all
night.

## What to decide — and why this is not just "delete the line"

Two defensible designs:

1. **Idle means idle.** `Peek()` must not refresh the timer. A session lives
   for `IdleTimeoutMs` after its last *input*, and reads are free. This is one
   line, and it is correct for the "leave the tab open" case.
2. **Idle means unattended.** Separate the two clocks: an input lease and a
   read lease, with the read lease much shorter. More machinery.

Pick one, say why, and implement it. If you pick (1), consider whether a client
that legitimately watches for a long time now gets killed mid-watch — if so,
that is the argument for (2) and you should say it.

## Requirements

1. After TASK 6, a session that is only *read* is eventually swept.
2. A session that is actively stepped or held still behaves sensibly.
3. `sim-walk.mjs` must still pass **25/25**. It polls; do not break it.
4. Do not change `IdleTimeoutMs` to make the test pass faster. If the number
   is wrong, argue it separately — do not quietly shorten it.

## Constraints

- Touch `MapSimSessionStore.cs` and nothing else unless you justify it.
- **Do NOT modify the reference repo.**
- Do not change physics, camera, Z order, or mobs.

## Verify before reporting

1. Build clean.
2. Open a session, never touch it, poll every ~15 s, and paste the output
   through the moment it disappears. **Use the real timeout — do not shorten
   `IdleTimeoutMs` to make this quick.**
3. Run `sim-walk.mjs`, paste the result.
4. State how long step 2 took in wall-clock seconds.

Report the diff, your design choice with reasoning, the raw poll output, and
anything you could not verify.

Do not claim it sweeps because the constant is right. Paste the output.