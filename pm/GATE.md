# GATE — what "green" means, exactly

```powershell
python tools/pm.py gate        # build, deploy, every suite
python tools/pm.py check       # structural audit of the work items
```

`pm.py gate` runs the **whole chain** — build, deploy, reopen the archives,
every suite — and it is the same chain `gate-loop.ps1` runs, so there is one
place where the steps are defined. The entry is read from `pm/PROJECT.yaml`
(`startup_chain_entry`), not written into the tool.

The chain's output goes to a **file** and is printed as the file grows, not
down a pipe. A child process inherits every inheritable handle of its parent,
and the server the chain starts inherits the caller's pipe write end, so a
reader waiting on that pipe waits for an EOF that never comes:
`pm.py gate | Tee-Object log.txt` hangs for ever with the gate already
finished (`pm/TRAPS.md` T-29).

## The build gate

Run in this order. The order is not stylistic: each step is the precondition
for the next, and skipping one produces a failure that reads like a code error.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\verify\gate-loop.ps1
```

That one command runs all four steps below, in this order, and chooses the
instance it deployed. The steps individually:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\verify\build-gate.ps1   # stop server, build Release
powershell -NoProfile -ExecutionPolicy Bypass -File tools\verify\deploy.ps1       # three-way md5, start
python tools\verify\open-archives.py                                             # reopen archives
powershell -NoProfile -ExecutionPolicy Bypass -File tools\verify\gate.ps1        # every suite
```

These four scripts used to live in `%LOCALAPPDATA%\hermes\cache\scratch`. That
directory pruned itself on 2026-10-21 and took them with it — the gate and its
suites were rescued into the repository, the startup chain was not
(`pm/TRAPS.md` T-0, `tools/pm.py:876`). They are in the repository now, because
a directory that prunes itself is not a place to keep the thing that decides
whether the code is correct.

| Condition | Value |
|---|---|
| Build | `net10.0-windows`, SDK `10.0.401`, **0 errors 0 warnings** |
| Output | `bin/Release/net10.0-windows`; the previous output is kept in `%TEMP%\maplebench-build-prev` and **diffed**, so an extra file is a question and not a failure. (This table said `%TEMP%\maplebench-verify\build-prev` until 2026-10-24; the code is the authority — `tools/verify/build-gate.ps1:131` does `Join-Path $env:TEMP 'maplebench-build-prev'`, and a repo-wide search finds that path only here and in that line.) |
| wwwroot | source ≡ build output ≡ **served**, every file |
| `avatar.js` md5 | `ba96b2d9727203279b8b784d256430b5` — unchanged, always. This is the **CRLF→LF-normalised** digest, which is the one `pm/SPEC.yaml` declares and the one all three legs are compared with. (`a1eb2eb47af42e07ee048762c2592456` is the same content in CRLF form, and it was quoted here until 2026-10-24; `pm/SPEC.yaml:480-483` records both and why the byte-level raw invariant was removed on 2026-10-23.) |
| Test instance | **The gate and the doctor take the instance from `$env:GATE_BASE`; the chain that STARTS one does not.** Set `$env:GATE_BASE = 'http://127.0.0.1:<port>'` before running the gate or the doctor, or they refuse: the **gate** prints `>>> RED (GATE_BASE is not set: the gate refuses to choose an instance -- there is no default)`, sets `RED_SUITES=1` and `GATE_STATE=refused`, and exits 1; every **suite** prints `>>> RED` and exits non-zero. The **doctor** prints `no instance probed: GATE_BASE is not set.` and then **ADDS ONE PROBLEM**: the doctor's `bad += 1` (the line `bad += 1` in `cmd_doctor` — `tools/pm.py:2053` as of 2026-10-24), so it prints `DOCTOR_SUITES_PROBLEMS=1` and exits 1 (`return 1 if bad else 0`, `tools/pm.py:2127` as of 2026-10-24). Measured: `python -X utf8 tools/pm.py doctor` with `GATE_BASE` unset printed `other=1 (registry drift, orphans, invariants, empty registry, no instance probed)  TOTAL=1`, `DOCTOR_SUITES_PROBLEMS=1`, `doctor: 1 problem(s)`, exit code 1. **The chain has a default instance, and it is the declared one, not yours:** `gate-loop.ps1:57,:62` read `test_port` from `pm/PROJECT.yaml` (5591, `pm/PROJECT.yaml:67`) and `:75` overwrites `$env:GATE_BASE` with it, `deploy.ps1:47` never reads `GATE_BASE` at all, and `open-archives.py:77-81` falls back to `test_port`. Launch flags: `--allow-multiple --no-browser`. |
| Port 5480 | **NOT IMPLEMENTED — this line is a rule, not a check, and a deploy can stop whatever is on 5480.** `tools/verify/lib/Project.ps1` decides by PROCESS IDENTITY, never by port: `:262` matches a command line naming `MapleBenchX` or the process image being `MapleBenchX.exe`, and `:335` calls `Stop-Process -Id $c.Id -Force`. Nothing in that stop path reads `banned_ports`; `gate-loop.ps1:67` checks only whether the DECLARED `test_port` is itself banned (`pm/PROJECT.yaml:68` declares `banned_ports: [5480]`). `deploy.ps1:85` calls `Stop-MapleBenchInstances -RepoRoot $Root` **without** `-OnlyThisRepository` — `build-gate.ps1:66` passes it, and `Project.ps1:323` skips another checkout's instance only when that switch is present — so a deploy stops every MapleBenchX on the machine, including other checkouts' instances and any instance listening on 5480. Until card **W1-F** implements the ban, read this row as "do not run a deploy while something you need is on 5480", not as a protection. |
| Server sockets | `WinError 10048` after a few thousand requests is the harness, not the product |

Five of these are load-bearing for reasons that are not obvious:

- **Stop the server before building.** It holds `MapleBenchX.dll`; the build
  retries ten times and then fails with `MSB3027`, which reads like a compile
  failure and is a file lock. `build-gate.ps1` stops it, and then **probes the
  lock** rather than assuming it is gone: if the dll is still locked it says so
  and stops, instead of letting the build fail with a message that reads like
  a compile error.
- **The instance answering on the port has to be the one you deployed.** A
  stale server answers exactly like a current one, so `deploy.ps1` compares the
  **listening socket's pid** with the pid it started, and refuses if they
  differ. It also prints every MapleBench process and the port it listens on.
- **Reopen the archives after every restart.** Archive ids `f1..fN` depend on
  open order. Never hard-code "f5 is Mob".
- **`avatar.js` is the tripwire.** This project does not otherwise touch it. A
  changed md5 means something reached into a file it had no business in.
- **The 5480 ban is a rule in this file, not a check in the code.** A deploy stops
  every MapleBenchX on the machine by process identity
  (`tools/verify/lib/Project.ps1:262,:335`), so an instance on 5480 and other
  checkouts' instances are stopped too. See the table row above, and card W1-F.

## The suites

Thirty-four, each covering one thing. `gate.ps1` runs them in this order. The
first ten are the original set; the other 24 were added as their work items
landed. The table below lists those first ten only.

A suite that the registry declares but the gate does not run is not a harmless
extra: the doctor reads the registry against the gate's `Run '<id>'` lines and
answers `DRIFT`, which stops the chain before a single suite runs (`pm/TRAPS.md`
T-33). Declaring a suite is half the work; the `Run` line is the other half.

| Suite | Covers | Pass marker |
|---|---|---|
| `ladder-all` | every ladder on the test map: up, down, bounds | `ALL LADDERS PASS` |
| `attack-test` | attack stance on the wire; swings that land | `VERDICT: …` + `damage numbers on the DELAYED frames: N` |
| `skill-test` | multiStrike deals 0; the cast lock; four guards | `VERDICT: skill-test N assertions, M failed` |
| `mob-attack-verify` | a mob closes the gap and lands hits on its own | per-hit lines |
| `first-attack-verify` | `firstAttack` mobs pursue unprovoked | `PASS: it attacked without being struck` |
| `npc-label-check` | NPC names, functions, hide flags | `VERDICT: npc-label-check N assertions, M failed` |
| `sim-skills-check` | client-side skill pose routing (node) | `N passed, M failed` (35 checks) |
| `mobinfo-verify` | the bestiary endpoint against archive truth | `BAD=0` |
| `equipstats-verify` | equipment stat nodes against archive truth | `BAD=0` |
| `sheet-verify` | equipment → sheet → swing → damage number | `BAD=0` |

The gate prints a verdict line for a suite only when the suite's output contains
one of the five tokens `VERDICT`, `ALL LADDERS PASS`, `passed,`, `BAD=`, `PASS:`
— the list in `gate.ps1:460`. A suite that prints none of them gets no verdict
line: the gate prints `(read the tail):` and the last four lines instead, which
is a readability defect and not a verdict one (`tools/verify/AGENTS.md` rule 14
— only the red markers decide the verdict). All thirty-three suites the gate
runs declare a verdict token from that list; the recomputation is
`python tools/pm.py doctor`, which prints per declared suite whether the gate
runs it and the `verdict=` it declares, and is red when a declared token is
missing from the suite's source.

`W-056` is the item that made `sim-skills-check`, `mob-fly`, `mob-attack-verify`
and `ladder-all` able to report red at all — before it they could fail without
the gate seeing it.

## How the gate decides

**Red is an explicit marker a suite prints. Nothing else.**

This is not a stylistic choice; it is a repair. Three attempts at inferring red
from output text each produced a false result:

- `$LASTEXITCODE` — **not set by a PowerShell script.** Six green suites
  flagged red.
- `(?im)\d+ failed` — matches the `0 failed` inside `passed=5 failed=0`. Two
  more green suites flagged red.
- `(?im)^\s*FAIL` — matched nothing useful.

So: a suite that can judge itself prints `>>> RED` or `NO damage number` or
`BAD=n`. The gate looks for those strings and shows everything else without
interpreting it. **A gate that cries wolf is worse than no gate**, because it
gets ignored, and then it protects nothing.

If you add a suite, it must print its own verdict. Do not add a pattern to
`gate.ps1`.

## Calibrate before you trust

Every harness prints a known-answer control before its measurements, and the
run is void if the control does not produce the known answer. This is R-9 and
it has been violated six times in one session.

The controls currently in place:

| Suite | Control |
|---|---|
| `attack-test` | 30 walk-frames must move at 2.5 px/frame |
| `attack-test` | `?attack=1` must return a non-empty stance group |
| `mob-attack-verify` | prints the calibration mob's `PADamage` / `bodyAttack` / `firstAttack` |
| `sheet-verify` | computes the expected max/min from the formulas *first*, then compares |

## What the gate does not prove

- **That the picture is right.** Six items have code-level proof and no
  screenshot behind them: `W-037` tracks them. The gate cannot close them.
- **That a blocked report is bogus.** `W-007` and `W-039` are blocked on a map
  id and an explanation, not on effort.
- **That a measurement was calibrated.** That is the author's obligation (R-9);
  the gate cannot detect it from the outside.