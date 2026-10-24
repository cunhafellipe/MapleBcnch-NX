---
id: W-095
title: The gate's startup chain is gone with the pruned cache: put build, deploy and open-archives back in the repository so the gate can measure the current tree
state: done
priority: 95
owner: 
opened: 2026-10-24
closed: 2026-10-24
depends: []
refs: 
blocked_on: 
evidence: the chain is back in the repository (build-gate/deploy/open-archives/gate + gate-loop.ps1) and pm.py gate now runs it, reading the entry from pm/PROJECT.yaml. Measured five runs of python tools/pm.py gate on 2026-10-24: 14:30 RED SUITES 1 (skill-e2e, W-101), 14:45 RED SUITES 0, 14:54 RED SUITES 1 (first-attack-verify, W-103), 15:13 RED SUITES 0, 15:23 RED SUITES 0 -- two consecutive green runs, GATE_LOOP_OK, exit 0, GATE_SUITES_DECLARED=34 OWNED=33 RAN=33 every time. doctor 0 problem(s) exit 0; check 5 pre-existing problems.
---
## Intent

The gate could not be run against the current source at all. The three scripts
that build the product, start it and open the archives lived in
`%LOCALAPPDATA%\hermes\cache\scratch`, a directory that pruned itself on
2026-10-21 and took them with it. What was left was a gate that could only be
run against whatever server happened to be running — and the one that was running
had been alive since 2026-10-21, serving a dll built 37 product-source commits
before the tree under test.

After this, **one command** builds the current source, starts it, opens the
declared archives and runs every suite against that instance, and the output
says which pid, which build and which digest it measured. A gate verdict now
means "the tree under test passed", not "some server from last week passed".

## Reference

**Own-design.** The originals are gone; there is no file to port line by line.
What survives of them is:

- `pm/GATE.md:14-17` — the four steps, written down, pointing at the cache.
- `tools/pm.py:876` — the lesson this repository already wrote for itself:
  a directory that prunes itself is not a place to keep the thing that decides
  whether the code is correct.
- `tools/verify/gate.ps1:553-554` — the loop, named in a comment:
  `build-gate.ps1 (stops the server) -> deploy.ps1 -> open-archives.py`.
- `pm/TRAPS.md` T-0, T-13, T-14, T-20 — the four behaviours the chain has to
  keep, and the reasons they are not obvious.

The gate and its suites were rescued into the repository in commit `3c7b307`.
The startup chain was not: the migration took the judge and left the thing
that runs the judge.

## Approach

1. **`tools/verify/lib/Project.ps1`** — the values the chain needs, read from
   `pm/PROJECT.yaml` so no value is typed twice (`Get-ProjectScalar`), the
   md5 tripwires read from `pm/SPEC.yaml` (`Get-ProjectMd5Invariants`), the
   instance census (`Get-MapleBenchInstanceCandidates`,
   `Stop-MapleBenchInstances`), the lock probe (`Test-FileLocked`), and the
   three legs of a tripwire (`Get-InvariantLegs`).
2. **`tools/verify/build-gate.ps1`** — stop the instance that holds the dll
   (R-14/T-13), keep the previous output so the build can be **diffed**
   (T-14), build with `TreatWarningsAsErrors=true` so the exit code carries
   the warning count instead of a localized summary line, check the built dll
   is newer than the newest product source (T-20), and compare the source and
   build legs of every md5 tripwire (R-17).
3. **`tools/verify/deploy.ps1`** — stop every other MapleBench instance
   (T-0), start one on the declared `test_port`, wait for `/api/about`,
   compare the **listening socket's pid** with the pid it started, and compare
   all three legs of every md5 tripwire: source, build output, **served**.
4. **`tools/verify/open-archives.py`** — open the declared archives in the
   declared order and confirm each one **by name** against what the instance
   reports (rule 16: never hard-code an archive id).
5. **`tools/verify/gate-loop.ps1`** — the four steps in order, and the choice
   of instance (rule 17: the caller chooses; this script is the caller,
   because it deployed the instance).

## Acceptance

```
# 1. the chain exists in the repository, and every step is runnable
powershell -NoProfile -ExecutionPolicy Bypass -File tools\verify\build-gate.ps1
# EXPECT: BUILD_OK, exit 0, and the tripwire shows source == build == declared

powershell -NoProfile -ExecutionPolicy Bypass -File tools\verify\deploy.ps1
# EXPECT: DEPLOY_OK, exit 0, and the tripwire shows source == build == served

python tools\verify\open-archives.py
# EXPECT: OPEN_ARCHIVES_OK, exit 0, 16 archives listed by NAME with their ids,
#         and NO "extra" archive

# 2. the whole thing, in one command
powershell -NoProfile -ExecutionPolicy Bypass -File tools\verify\gate-loop.ps1
# EXPECT: every step exits 0 and the chain prints GATE_LOOP_OK

# 3. the gate measures the current source, twice
# EXPECT: "RED SUITES: 0" on two consecutive runs
```

## Result

**The chain is back, in the repository, and it measures the tree under test.**
`python tools/pm.py gate` builds the current source, deploys it, opens the
declared archives, and runs every suite against **the instance it just
deployed** — and the exit code says what it measured.

### What landed

- `tools/verify/lib/Project.ps1` — the values the chain needs, read from `pm/PROJECT.yaml` and `pm/SPEC.yaml` (no value typed twice).
- `tools/verify/build-gate.ps1` — stop the instance holding the dll, keep the previous output so the build can be **diffed** (T-14), build, T-20, R-17.
- `tools/verify/deploy.ps1` — stop every other instance, start ONE on the declared port, compare the **listening socket's pid** with the pid it started, and compare source == build == **served**.
- `tools/verify/open-archives.py` — open in the declared order, confirm each one **by name** (rule 16: never hard-code an archive id).
- `tools/verify/gate-loop.ps1` — the four steps in order, and the choice of instance (rule 17: the caller chooses — this script deployed it).

And `pm.py gate` now runs the chain, not the runner alone. It used to run
`tools/verify/gate.ps1` and nothing else, while `pm/GATE.md:4` and
`pm/WORKFLOW.md` step 3 both document that command as "build, deploy, every
suite" — so the one entry point that claims to measure the tree was measuring
whichever instance happened to be running, and an instance running an old build
answers the suites just as well as a current one. The entry is read from
`pm/PROJECT.yaml` (`startup_chain_entry`), not written into the tool.

### The defects the chain had on its way back (all measured, all fixed)

1. `ArrayList.Add()` returns an index, so it polluted every function's output.
2. The instance could not be identified: `Get-CimInstance` gives some processes
   no command line, and `$p.Modules` is null for others. It now matches on
   name, command line **or** loaded modules, and says UNKNOWN when it cannot
   tell.
3. `deploy.ps1` read `$outTask.Result` in its failure branch — the server never
   closes stdout, so that waits for ever. It reads the log file now.
4. The server inherited the caller's stdout pipe, so the caller waited for an
   EOF that never came. Every step's output goes to a **file** and is tailed.
5. `Start-Process -PassThru`'s `ExitCode` is null unless `$proc.Handle` is
   touched first, and `[int]$null` is 0 — a failed step read as success. The
   handle is touched, and a step that cannot report an exit code is RED.
6. The tripwire's three legs were assembled from three different path shapes;
   they come from one function now, and a missing leg is RED.
7. The lock probe ran before the stopped process had actually exited.
8. `Get-FileHash` **disappeared** in one launch path and not the other: a child
   PowerShell inherited PowerShell 7's `PSModulePath`, whose
   `Microsoft.PowerShell.Utility` (7.0.0.0) does not export it. The module path
   is pinned now, and the md5 uses `System.Security.Cryptography.MD5` — no
   module involved at all. See `pm/TRAPS.md` T-26.
9. `pm.py gate` inherited the same class of failure from the other side: a pipe
   the server inherits never reaches EOF. Its output goes to a file too
   (T-29).
### Evidence

`python tools/pm.py gate` — the agent's own path, run five times on
2026-10-24:

| run | started | result |
|---|---|---|
| 1 | 14:30 | `RED SUITES: 1` — `skill-e2e` (a suite measuring the wrong animal: W-101) |
| 2 | 14:45 | **`RED SUITES: 0`** — `GATE_LOOP_OK`, exit 0 |
| 3 | 14:54 | `RED SUITES: 1` — `first-attack-verify` (the plain mob patrols; W-103) |
| 4 | 15:13 | **`RED SUITES: 0`** — `GATE_LOOP_OK`, exit 0 |
| 5 | 15:23 | **`RED SUITES: 0`** — `GATE_LOOP_OK`, exit 0 |

**Acceptance clause 3 is met: two consecutive runs with `RED SUITES: 0`**
(15:13 and 15:23). Every run printed
`GATE_SUITES_DECLARED=34 / OWNED=33 / RAN=33`, and the chain reported
`GATE_LOOP_OK` with exit 0.

The two runs that were red are as informative as the green ones, because they
are what a trustworthy gate is FOR: it named the suite and the failing
assertion instead of saying "something is wrong somewhere" —

```
=== skill-e2e ===
   >>> RED (>>> RED)
      >>> RED: skill-e2e -- the struck mob's HP dropped (2220100: 1000 -> 1000)
```

— and neither of those reds was a defect of the tree. They were two suites
measuring something that is not a property of the tree (W-101, W-103), which
is the class of bug the gate exists to expose.

Also measured against the instance the chain deployed:

- `pm.py doctor`: `DOCTOR_SUITES_PROBLEMS=0`, `doctor: 0 problem(s)`, exit 0;
  `data-root-untouched: ok ... files scanned=34 excluded=166`, with every
  excluded-but-newer file printed by name.
- `pm.py check`: 5 problems, all pre-existing (W-085, W-086, W-092).

### What this did NOT fix, on purpose

- The two suites that were red are still red sometimes, and they are **not**
  part of this item: `first-attack-verify` forbids the plain mob from moving
  at all while the product's plain mob patrols its foothold chain (W-103,
  measured), and `sim-health`'s calibration still asserts "a damage number
  appeared" rather than "the mob I walked up to was hit" (W-102). Both are
  harness measurements, both are registered with their evidence, and
  `first-attack-verify` is marked `flaky: true` in `pm/SPEC.yaml` with the
  reason. Neither was papered over to make the gate green.
- The chain is still a **chain**: it measures the tree under test and refuses
  to build while something else holds the dll (T-13). During this work a
  second agent and, later, an outside editor kept starting instances and
  touching files; the chain said so and stopped, every time, rather than
  measuring the wrong thing.
