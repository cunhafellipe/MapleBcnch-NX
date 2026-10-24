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
- `tools/verify/deploy.ps1` now **prints every MapleBench process and the port
  it listens on**, and it compares the **listening socket's pid** with the pid
  it started. A stale server answers `/api/about` identically, so the pid is
  the only thing that tells them apart. **Read that block before trusting
  anything you see in a browser.**
▸ *2026-10-24* The instance census moved into `tools/verify/deploy.ps1`; the
  standalone `which-server.ps1` died with the pruned cache and is not coming
  back. The rule it encoded is now a line of the deploy output: `LISTENERS`.
▸ The rule: **`http://127.0.0.1:5100` is the editor.** 5591 is the gate's port and
  runs the same binary; there is no third server.
▸ *2026-10-24, measured* An instance that runs ELEVATED cannot be stopped by
  an unelevated tool ("Access is denied"), and its command line and loaded
  modules cannot be read either. `deploy.ps1` reports it as `STOP_FAILED` with
  the reason and still prints its listening port, because the honest answer
  ("there is an instance I cannot stop, and here is the port it holds") is
  more useful than a silent one. It does not fail the chain: the guard that
  matters is the listener-pid check, which compares the pid on the gate's port
  with the pid the deploy started.

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

**T-25 — The gate's red channel read a sentence, not a verdict.**
▸ *2026-10-24* `sim-health` was RED inside the gate and GREEN when run on its own,
on the same instance, and the gate said `BAD=2` while the suite's own last line said
`BAD=0`. The suite's output explains itself, and the explanation mentions the
number: "the shipped judge still reported BAD=2 once (not attributed to this
block)". The channel was `$text -match 'BAD=[1-9]'` over the WHOLE output, so a
sentence about a past run was read as this run's verdict. A suite that PASSED was
red for having written about a number — and the gate printed `BAD=n`, a letter,
because the number was never filled in.
▸ The suites' convention is a STANDALONE line (`print(f"\nBAD={bad}")`). The
channel now narrows to the lines that stand alone before it tests, so prose with
a number in it is not a verdict. Nothing is weakened: a standalone `BAD=1` still
reddens, and the `>>> RED` channel still reads the whole output.
▸ And the gate now prints the failing suite's OWN evidence — the `>>>` markers it
emitted and its verdict line. Before that it printed the red line and nothing
else, so the only thing a reader could learn was THAT it failed, not WHICH
assertion failed.

**T-26 — A child PowerShell inherits the caller's module path.**
▸ *2026-10-24* The gate chain was green when a shell started it and BROKEN when
`pm.py` started it, on the same machine and the same `build-gate.ps1`. The
difference was the environment, not the code: python does not rebuild
`PSModulePath`, so Windows PowerShell 5.1 inherited the path of the PowerShell 7
that started `pm.py`. PowerShell 7's `Microsoft.PowerShell.Utility` (7.0.0.0) was
then the first match in that path, it does not export `Get-FileHash`, and the
build gate died with "The term 'Get-FileHash' is not recognized as the name of a
cmdlet" — on a line with nothing to do with the build. Every other cmdlet of that
module was still there (`Select-Object`, `ConvertTo-Json`), so nothing in the
failure pointed at the environment.
▸ `Initialize-PowerShellModulePath` (tools/verify/lib/Common.ps1) pins
`$env:PSModulePath` to THIS PowerShell's own directories, and it is called at the
top of every script — a module is autoloaded when the command is first USED, so
pinning it there also repairs the script's own later cmdlet calls.
▸ And the md5 no longer depends on a cmdlet at all: `Get-RawMd5OfBytes` /
`Get-RawMd5OfPath` use `System.Security.Cryptography.MD5`. The normalised rule
(R-17) already did; the build-diff rule now does too.

**T-27 — The doctor's shape contract is a contract, and a comment can break it.**
▸ *2026-10-24* `pm.py`'s doctor reads the gate's red channels out of `gate.ps1`
by finding the pattern test that ADDS to `$bad` — so the shape of that line IS
the contract between the two tools. Rewriting it (the zero-check on a line of its
own) broke the contract and the doctor reported **20 suites** whose declared
marker the gate does not read — with the gate working perfectly. The doctor took
the FIRST match in the file, so a COMMENT that spelled the shape out was picked
up in preference to the code, and the marker it extracted became `<PATTERN>`
instead of `BAD=`.
▸ When one tool reads another tool's source, the pattern it matches is part of
that tool's contract. Change the pattern and the reader is changed too, and a
comment in the file is part of the source.

**T-28 — The artifact is compared against sources it does not contain.**
▸ *2026-10-24* The build gate refused to build with "the built dll is OLDER
than the newest product source" — because someone had edited
`MapleMcp/Program.cs`, a doc-comment change in the MCP server's instructions.
`MapleBenchX.csproj` has exactly ONE ProjectReference, to `MapleLib`:
`MapleBenchX.dll` cannot contain that change. The dll was not stale; the
comparison was against a source the artifact does not contain, so a change to a
different assembly reddened the build of this one.
▸ A minute later the same refusal came from `MapleBench/tools/soak.py` — a
python helper, not compiled into anything. The file list was built with
`Get-ChildItem -LiteralPath <dir> -Recurse -File -Include '*.cs'`, and
`-Include` is INERT when the path is given with `-LiteralPath`: it filters the
leaf only when the path carries a wildcard. So "the newest product source" was
the newest file of ANY kind under those roots. The extension has to be tested
in the filter itself.
▸ Compare an artifact with the sources it is BUILT from, and say what those
sources are where the comparison is made (`Get-NewestProductSourceTime`). A
file that cannot end up in the artifact cannot make the artifact stale.

**T-29 — A pipe the server inherits never reaches EOF.**
▸ *2026-10-24* `deploy.ps1` starts the server with `Start-Process` and a
redirected stdout. A child inherits EVERY inheritable handle of its parent, not
just the three standard ones, so the server kept the CALLER's pipe write end
open and the reader waited for an EOF that never came. `python tools/pm.py gate |
Tee-Object log.txt` hung for ever, with the gate already finished and no visible
progress.
▸ Every step's output goes to a FILE and is tailed; a file has no EOF to wait
for. `pm.py gate` does the same for the whole chain: it writes the child's
output to a file and prints it as the file grows.

**T-30 — A step's log is written in the machine's code page, not UTF-8.**
▸ *2026-10-24* The chain's logs came back as mojibake: the CJK repository path
as `wz???` and the SDK's Chinese build summary as unreadable bytes. A step
started with `Start-Process` and a redirected stdout has NO console, so
`[Console]::OutputEncoding` cannot be set on it and its output lands in the file
in the machine's ANSI code page (950 here). Reading that as UTF-8 is what turned
it into replacement characters.
▸ Decode strictly as UTF-8 first and fall back to the machine's code page.
The decision is made on the WHOLE file, so a chunk boundary cannot split a
character and change the answer half way.

**T-31 — MapleLib writes its scratch file into the working directory, named after the output alone.**
▸ *2026-10-24* `WzFile.SaveToDisk(path, …)` derives a scratch name from the
OUTPUT FILE NAME ONLY (`Path.GetFileNameWithoutExtension(path) + ".TEMP"`) and
writes it into the **current working directory**, not next to the output
(`dependencies/MapleLib/WzLib/WzFile.cs:591`). It deletes it afterwards
(`:658-662`), but between those two points the name belongs to whoever is
writing a file with that name. Two containers with the same name created at the
same time truncate each other's scratch, and the resulting file is corrupt in a
way that looks like a writer bug.
▸ The app already guards this for saves (`WzSaveService.cs:1050-1055`) and the
empty-container creator does the same: the scratch name carries a GUID stem, so
two creations cannot collide. The working directory of the server is
`%TEMP%\MapleBench\<pid>` (Program.cs), so the scratch never lands in a client's
folder either.

**T-32 — An empty .wz reads back at EVERY game version.**
▸ *2026-10-24* A classic WZ stores a version hash derived from the game
version, and `WzFile.ParseWzFile` checks it (`WzFile.cs:524-528`). An EMPTY
archive has nothing under that hash, so it parses at game version -1, 0, 95, 112
and 230 alike — measured, all five `Success`. "It opened" is therefore NOT
evidence that the version hash in the container is the one you meant, and a
container that opens with the wrong game version will be re-written under a
different hash the next time it is saved.
▸ The create answer carries the `gameVersion` it was written with, and that is
the number to open it back with. A container that will hold real data needs the
game version pinned, not left at -1.

**T-33 — A suite declared in the registry but not wired into the gate is a red doctor, not a silent no-op.**
▸ *2026-10-24* `pm/SPEC.yaml` declared a new suite and `tools/verify/gate.ps1`
did not run it. The doctor's registry check (`tools/pm.py:1665-1672`) compares
the registry against the `Run '<id>'` lines in the gate, and answered
`DRIFT empty-archive registry=True gate=False` — so the doctor exited 1, the
gate refused to run ANY suite ("the doctor is not green -- no suite was run"),
and the whole chain stopped. The new suite itself was green when run by hand.
▸ Declaring a suite in the registry is half the work. The other half is the
`Run '<id>'` line in the gate, and until that line exists the doctor reads the
declaration as a promise the gate does not keep.
