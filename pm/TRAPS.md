# TRAPS — every measured way this project has lied to itself.

Keep this file. A trap already paid for is cheap; the same trap met cold is
expensive. Each entry is a real incident with a date, not a hypothetical.

Format: **T-n** — the trap. ▸ *date* — what it cost. ✔ the control that catches it.

---

## The machine, not the code

**T-0 — A stale server on another port answers exactly like a current one.**
▸ *2026-10-17* A whole day of fixes — flat damage, 1000 HP mobs, facing from the
key, the corpse that would not stop walking, three of 66 skills dealing no
damage — built, deployed and gated on **5591**, green, 14 browser assertions. The
person had **5100** open, and 5100 was running a build from 11:37, before any of
it. A third instance sat on **5171**, six hours stale, holding another 8 GB.
Nothing in the UI, the gate or the journal said so: a stale process answers
`/api/about` identically.
Two things came out of it, both mechanical rather than advisory:
- `deploy.ps1` now **stops every other MapleBench before starting**, because
  three instances at ~8 GB each starved the machine and `Map.nx` (678 MB, ~13.5
  GB to convert) refused to open **anywhere** with "Not enough memory".
- `%LOCALAPPDATA%\hermes\cache\scratch\which-server.ps1` prints every instance,
  its port, its age and whether it is stale. **Run it before trusting anything
  you see in a browser.**
▸ The rule: **`http://127.0.0.1:5100` is the editor.** 5591 is the gate's port and
  runs the same binary; there is no third server.

---

## Harness self-deception

**T-1 — `@($null).Count == 1`.**
▸ *2026-10-16* `attack-test.ps1` reported `damage entries over 8 swings : 8`
for eight swings that hit **nothing**. The frame has no `damage` key at all
until a swing connects, and `@($null)` counted as one hit every time. The gate
had been green on a product that could not hit anything, for an unknown number
of runs.
✔ `@($r.damage | Where-Object { $_ })` — as `skill-test.ps1` already did.

**T-2 — A regex is not a verdict.**
▸ *2026-10-16* Three attempts at inferring red from suite output, three false
results. `$LASTEXITCODE` is not set by a PowerShell script (flagged 6 green
suites red); `\d+ failed` matches the `0 failed` inside `passed=5 failed=0`
(flagged 2 more); `(?m)^\s*FAIL` matched nothing useful.
✔ The gate reads **explicit markers only**. A suite that judges itself prints
`>>> RED`. Everything else is shown, not interpreted.

**T-3 — Selecting by one axis.**
▸ *2026-10-16* `attack-test.ps1` picked the mob at `x=1867 y=-535` — 510px above
the spawn — and walked past nothing. Same trap, three separate times: also
`nomove-acquire.py` and a mob-sweep, each producing a false "the swing is
broken".
✔ Filter on both axes, and print the chosen target's full position before
believing a distance.

**T-4 — A gap threshold below one frame.**
▸ *2026-10-15* A tolerance tighter than 1/60 s failed 0 of 55 skills that all
passed at a looser one. Fixed step is 16.67ms; a 5ms tolerance tests nothing.
✔ Express every tolerance in frames, then make it ≥1 frame.

**T-5 — Same-session priming contamination.**
▸ A measurement that set up the session and measured in it read its own setup.
✔ Separate the calibration call from the measurement call, and print the
control.

**T-6 — One-shot events need their delay frames.**
▸ *2026-10-16* The swing hit resolves 15–20 frames after the key. Pressing
attack on the next frame **resets** the countdown, so "swing every frame" never
resolves one hit, and the damage number appears on an *idle* frame.
✔ Idle frames are part of the measurement. See `TRAPS` companion rule R-10.

**T-7 — Chasing a stale coordinate.**
▸ *2026-10-16* A loop that re-picks "the nearest mob" each frame walked the
character to `x=2302 y=240` — 265px above the ground — chasing a mob that had
patrolled away.
✔ Close in once, then stand still. Re-close only against a live position.

---

## Reading the archive

**T-8 — Wrong node name, confident prose.**
▸ *2026-10-16* `DamageRoll.cs` documented the mob's defence as `info/wdef` and
explained its absence as a data gap. **No mob on this archive has a `wdef`
node.** The real node is `info/PDDamage`, on 342 of 343. The wrong name made
the defence term look unavailable when it was available the whole time.
✔ Before writing "the archive does not carry X", measure X's *neighbours* too.

**T-9 — The wrong field, three journal entries.**
▸ *2026-10-15* `info/attack1` was claimed 0/2053 in three separate entries.
`Mob.cpp:146` reads `src["attack1"]["info"]`. Correct measurement: 87 of 206 and
88 of 205 mobs.
✔ Read the *reference's* access path before measuring the archive.

**T-10 — Exact-case matching loses real data.**
▸ *2026-10-16* The archive spells speed and jump `incSpeed`/`incJump`, not the
C++ reference's `incSPEED`/`incJUMP` — 27 and 15 occurrences. And
`Glove/01082143.img` spells its LUK node `incLUk`, a lower-case k. Exact-case
reads returned 0 and dropped a real stat silently.
✔ Every node name compared `OrdinalIgnoreCase`; spellings that differ from the
common one are *reported*, not just accepted.

**T-11 — Encoding: GBK is not UTF-8.**
▸ `clean-client`: `Weapon.h`, `Buff.h`, `Buff.cpp` are GBK. `CharStats.cpp`,
`Player.cpp`, `Mob.cpp` are valid UTF-8 whose Chinese is **already destroyed**
with U+FFFD — unrecoverable, verified at byte level (`c4 ac ef bf bd cf b7`).
▸ `_necrocer_src` is UTF-8 **with BOM** and is not GBK, contrary to first
impression.
✔ Check before budgeting time to recover text. No formula depends on a
destroyed comment.

**T-12 — PowerShell flattens big arrays.**
▸ Reading a large `/api/children` through PowerShell silently loses elements.
✔ Python for anything that reads an archive.

---

## Building and shipping

**T-13 — The running server locks the output dll.**
▸ MSB3026 retried 10× then MSB3021 — reads like a compile error, is a file
lock.
✔ `build-gate.ps1` stops the instance first.

**T-14 — A stale artifact looks like a build difference.**
▸ *2026-10-16* `build-check/` carried a `MapleLib.deps.json` no current build
emits. Reported as "only in check".
✔ Diff the fresh build against a clean copy of the previous one, and treat an
extra file as a question, not a failure.

**T-15 — Path spelled with CJK in a `.ps1`.**
▸ *2026-10-16* `build-gate.ps1` and `deploy.ps1` both failed on a path that
plainly existed: `wz工具` decoded as `wz撌亙आ`.
✔ Discover the path with a glob; never type it.

**T-16 — Server sockets exhaust.**
▸ `WinError 10048` after a few thousand requests. That is the harness, not the
product. Restart and continue.

**T-17 — A measurement that self-corrected was trusted.**
▸ *2026-10-16* A `C:\` read reported a full disk that then showed 32 GB free.
Never let a measurement's own conclusion stand without a second observation.

**T-18 — A canvas draw judge that filters the wrong prefix reports zero.**
▸ *2026-10-17* Recording every `drawImage` source and filtering with `/^f8\//`
answered "0 skill draws" while the caster's art was demonstrably on screen. The
client's URL is `/api/canvas?path=f8/112.img/...` — it never starts with `f8/`.
▸ Every prefix-filtered count is suspect. Before reporting a zero, print the
DISTINCT sources and look at them; a judge that cannot name what it expected to
see is not evidence that the thing is absent.

**T-19 — The client polls its OWN session; a judge that opens a second one measures nothing.**
▸ *2026-10-17* Driving `/session/{id}/frames` from the harness against a session
the page did not open produced a correct server frame and zero client draws, for
every skill. The same script against `sim.live.sessionId` drew 120-170 decals.
▸ Read the id out of the page (`window.app.ensureSimulator()` → `live.sessionId`)
and use that one, exactly as `tools/browser-verify.mjs` does.

**T-20 — `deploy.ps1` does not build.**
▸ *2026-10-17* It restarted the server and reported success while `bin\…\MapleBenchX.dll`
was 17 seconds OLDER than the source that had just been edited, so a fix appeared
to do nothing. Verify `dll.LastWriteTime -gt source.LastWriteTime` after deploying,
or kill the server and `dotnet build` first — the running instance holds the dll
(T-13) and must be stopped anyway.

**T-21 — A judge that passes only because what it measures is broken.**
▸ *2026-10-17* `browser-verify` filtered recorded draws with `src.includes('112.img')`
— the whole skill book — which also matched the mob's hit sprite. It passed for as
long as a hit effect never reached the canvas, and went red the moment W-084 made
it draw. A filter broad enough to catch two different animations is not a filter.
▸ Narrow every selector to the exact art path the assertion is about, and re-ask
what a previously-green assertion was actually looking at when it first turns red.

**T-22 — Comparing a screen coordinate with a world offset.**
▸ *2026-10-17* A recorded `drawImage` argument is `px(x) = (x - view.minX) * s`. An
assertion comparing it with the archive's `-origin` is off by the camera position,
and asking `view.minX` returns `undefined` because `view` is rebuilt every paint.
▸ MEASURED as a 1700 px offset. Have the renderer publish the world rectangle it
filled (`walkview.js` `lastEffectRect`), or capture the camera with the sample.

**T-23 — `/api/clientlog` is append-only and outlives a redeploy.**
▸ *2026-10-17* The "client log holds no error" assertion kept failing on a
`ReferenceError` from a build that had been fixed in the previous run. DELETE the
log at the start of a run before reading it.

**T-24 — A harness that fixes its measurement by moving the subject.**
▸ *2026-10-17* To make a draw measurable on one branch, `browser-verify` walked the
body three frames to the right — away from the mob it had just approached — and the
cast then missed: "1000 -> 1000". Fix the measurement, not the position.
▸ Same trap as making a test pass by relaxing it: if a precondition changes what
the test is about, the precondition is the defect.