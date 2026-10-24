---
id: W-104
title: The simulator was audited against its own judges: six rounds, twenty-one cards, and every red turned out to be a judge rather than the tree
state: done
priority: 90
owner: lead
opened: 2026-10-24
closed: 2026-10-24
depends: []
refs: pm/work/W-097..W-103; C:\mbtest2\chk-a.md; C:\mbtest2\chk-b.md; C:\mbtest2\w4-attack-after.md; C:\mbtest2\w102\report.md; C:\mbtest2\w103\report.md; C:\mbtest2\routes\routes-report.md; C:\mbtest2\w5-handshake.md; C:\mbtest2\w6-readme-routes.md; C:\MUWORK\GAME\MPSH\PROJECT\FAILURES.md
blocked_on: 
evidence: "The whole gate, run against an instance BUILT from the working tree (a fresh build into %TEMP%\mb-build, started on port 5601, 16 archives open): before the fixes 'RED SUITES: 1 / RED_SUITE=attack-after / GATE_EXIT=1', after the fixes 'GATE_SUITES_RAN=34 / RED SUITES: 0 / GATE_EXIT=0' (elapsed 14:01). self-test.ps1: CASES_PASS=38 CASES_FAIL=0 CASES_SKIP=2 RESULT=PASS, exit 0. Build: 0 warnings, 0 errors. The census's two instruments agree: 'static=92 handshake=92 AGREE=True' (exit 0), and the refusal path 'server binary missing' exits 1."
---
## Intent

The owner asked for a rigorous audit of the simulator system: not "does the
gate go green" but "is the green a property of the TREE". This item is the
index of that audit, so a later reader can see every card, what it changed,
what it proved, and what is still open. The per-card reports live under
`C:\mbtest2\` and the per-defect write-ups live in `pm/work/W-097..W-103`.

## What the audit found

**Every red in the simulator area was a JUDGE, not the tree.** Three separate
cases, all with the same mechanism -- the judge mistook "not observed" for
"not true":

1. `attack-after` printed "no shot ever reached its delay" and went red.
   The product's delay was 1600 ms; the entry was last seen at 1583.3333 ms
   and the very next tick is exactly 1600.0000 ms -- the frame in which the
   hit test REMOVES the projectile (it was born 5.4 px from the player, so it
   is removed the instant it becomes testable). The suite recorded only the
   frames in which the entry still existed, so the one frame that could show
   `elapsed >= delay` was never recorded, and "not observed" was read as
   "not achieved". W-067's family, again.
2. `first-attack-verify` forbade the plain mob from MOVING and claimed
   "toward a player" without checking a direction, while the plain mob
   patrols its foothold chain by design. It is now a three-way conjunction
   (`gapN <= 40 px AND tailMax <= 40 px AND closureFrac >= 0.10`), each
   clause with its own falsifier, 10/10 runs green with one verdict string,
   and offline 6 plain traces False against 3 firstAttack traces True with
   no overlap.
3. `sim-health`'s calibration asserts that SOME damage number appeared. The
   measurement (12 runs) shows the damage ALWAYS landed on the mob the
   character walked up to, so the assertion is sound as it stands -- and two
   traps were measured for whoever designs a stricter one: the victim moves
   up to 6.29 px, and the hit X/Y is the victim's HEAD, ~51 px above its body.

**The verification tools themselves had false greens and false refusals.**

- `mcp-tool-census.mjs --handshake <missing exe>` printed `AGREE=unknown`
  and exited 0. It now exits 1.
- The handshake's 120 s stall was the probe waiting on a child that had
  already DIED (the .NET 8 apphost trap); it now answers in 0.8 s and names
  the exit code and stderr. Three real bugs in the probe came with it: a
  dead child that was waited on anyway, a `spawn()` that throws
  synchronously (EFTYPE), and an EPIPE on writing to a dead stdin.
- `page-check.mjs` answered "is the simulator working?" with 9/9 green on a
  DEAD simulator. It now reports BAD=1 / exit 1 on that same fixture and
  BAD=0 / exit 0 against a live instance with 5366 maps.
- `pm.py`'s SPEC reader truncated the whole `areas:` list at a comment line
  without a colon: 16 rows became 1. It now skips comments and folded
  continuation lines.
- `pm/GATE.md` said the doctor "exits 0" and that "the instance block adds no
  problem to it". The code says the opposite (the doctor adds 1 and exits 1
  when no instance is probed). The document now matches the code, and the
  document's `pm.py` line numbers were corrected after a checker found them
  47 lines stale.
- `pm/GATE.md` said "port 5480 banned". Nothing implemented it. It is now
  code: the stop path reads `banned_ports` out of `pm/PROJECT.yaml` and
  refuses to stop a process LISTENING on a banned port, with a known-answer
  test (`tools/verify/banned-port-check.ps1`) that cannot kill anything and
  carries a positive control.
- `self-test.ps1`'s cases 5 and 6 asserted absolutes, so a DEAD detector
  passed them. They are relative to the control arm now, and the control arm
  itself now requires the doctor to have ANSWERED.
- Every suite depended on the caller's `PYTHONIOENCODING`; standalone, a
  suite that prints an archive name died with UnicodeEncodeError on the
  big5 console code page. One guard in `archivelib.py` (which every suite
  imports) fixes all 27.

**And the hand-typed numbers in the documents were wrong.** The census said
91 tools when the tree had 92. `MapleMcp/README.md` said 234 routes; the
measured populations are 237 (method, path) registrations, 227 distinct
paths, 218 raw literals -- and the tree the 234 came from never entered
version control. `pm/SPEC.yaml`'s header quoted 34/34/33 for a file that
declares 35.

### The SPEC's `areas:` block (commit be4f52a)

The 6b block could NOT be pasted verbatim: `pm.py`'s scalar reader is
one-value-per-line (`pm.py:291-296`), so a multi-line array makes `routes`
a 46-character STRING and `cmd_coverage` then prints one backticked
CHARACTER per route (measured: 228 characters where 132 belong). The
block's CONTENT was applied in the SPEC's own single-line `routes:` shape
instead, with its 17 comment lines verbatim.

**The lead's own count, independent of the maker's:**

```
HEAD~1   areas=16  visible=74 (distinct 74)  comment_paths=0  (distinct 0)   union=74
HEAD     areas=16  visible=62 (distinct 62)  comment_paths=24 (distinct 24)  union=86
worktree areas=16  visible=62
```

**And then the checker refuted the lead's own reconciliation (chk-c, claim 3).**
The lead wrote "Zero were dropped". That is true of the DECLARATIONS and FALSE
of the PATH STRINGS, and the difference is the whole point:

```
OLD declared (HEAD~1)          : 74
  still machine-visible now    : 40
  named in a COMMENT line      : 12
  rewritten (old name GONE)    : 22      <- the lead's first count missed this
  OLD NAMES NOT NAMED ANYWHERE : 22 before the fix, 0 after it
```

The maker's own accounting was wrong in the same direction ("52 carried
verbatim" is 40 + 12), and the 22 old flat names -- `/api/maps`, `/api/walk`,
`/api/items`, `/api/cast`, `/api/icon` ... -- left the file entirely, because
the real routes carry a group prefix (`/api/mapedit/maps`, `/api/simulate/walk`,
`/api/cashshop/items`, ...). The new names are correct -- the checker derived
the router's own table itself and found 41 exact routes + 21 real family
prefixes + **0 pointing at nothing** -- but the OLD names were simply gone.

**So the SPEC now names them again.** A table of the 22 pairs was written into
the header, explicitly labelled "not a declaration" so that "which paths does
this file declare" still has exactly one answer, and the header's own
statement that "/api literals appear in only three places" was corrected to
four (with the reason). Measured after the fix: **all 74 old names are named
somewhere in the file, 0 missing; 62 machine-visible, 22 distinct old names
in the mapping table, 22 distinct new names, every right-hand side a path.**

**The acceptance ruling (the lead's, recorded because the wording had two
readings):** the invariant is **"the DECLARED population is unchanged"** --
74 declarations before and after, 0 dropped, the same 16 area ids, and the
doctor reading exactly as before (`DOCTOR_SUITES_DECLARED=35`,
`DOCTOR_SUITES_PROBLEMS=1`, exit 1 with no instance; 0 problems with one).
The machine-visible count falls from 74 to 62 **by design**: the block moves
12 declarations into comments, and putting them back into `routes:` lines
would be exactly the silent cross-area move the block refuses to make. Neither
number was fudged -- and the one that WAS wrong (the lead's "zero dropped",
which ignored the 22 renamed) was caught by the checker, not by the lead.

## The method that produced this

- A fresh instance was BUILT from the working tree for the gate run
  (rule 17: a judge pointed at an instance running a stale binary is not a
  measurement of the tree).
- Every number was measured with the command that measured it, and the
  command is next to the number in the file it is in.
- Two independent checkers, each on files it had NOT authored, refuted
  several maker claims: "the doctor's output is byte-identical before and
  after" (it is not -- the registry's content changed), "pm/COVERAGE.md is
  byte-identical to HEAD" (it is not in HEAD at all; it is gitignored, so
  that check can never fail), "+44/-1" (it does not reproduce; every
  comparison gives 137/11), and the card's own prescription for the
  duplicate case (`AND` of two relative conditions) would have made the case
  permanently red, because the control arm is itself red.

## Still open

- **`knockback-verify` is still context-sensitive: the gate's verdict depends
  on which suites ran before it.** MEASURED 2026-10-25 01:44, on HEAD
  1713ad0 against an instance built from that tree:
  - inside the gate: `=== knockback === >>> RED (BAD=1) / >>> the control
    moved 2.100px, so 'it moved' would prove nothing`, and the gate ended
    `RED SUITES: 1 / RED_SUITE=knockback / GATE_EXIT=1`;
  - standalone, the SAME tree and the SAME instance, six consecutive runs:
    every one `exit=0`, `BAD=0`, `impulses seen in 45 frames: 1`.
  The suite's control (:114, :118, :121) requires an unhit mob not to move
  -- the same false premise as first-attack-verify's stage 1, because the
  plain mob PATROLS its foothold chain by design
  (`MapleBench/Api/SimulatorEndpoints.cs:222-227`). W-064 already turned
  the lie into an honest refusal; what is left is that the refusal still
  makes the gate red, and only in the gate's context. Card W9-KB.
- **The gate now prints which binary it is judging** (card W8-STALE). The
  five numbers rule 17 asks for, each with its source:
  ```
  GATE_INSTANCE=http://127.0.0.1:5602
  GATE_INSTANCE_PORT=5602
  GATE_INSTANCE_PID=64972
  GATE_INSTANCE_PID_SOURCE=Get-NetTCPConnection -State Listen -LocalPort 5602 -> OwningProcess
  GATE_INSTANCE_START=2026-10-24 23:24:30
  GATE_INSTANCE_START_SOURCE=Win32_Process.CreationDate for ProcessId=64972
  GATE_INSTANCE_BINARY=C:\Users\e7896\AppData\Local\Temp\mb-build\MapleBenchX.dll
  GATE_INSTANCE_BINARY_SOURCE=the loaded module MapleBenchX.dll of ProcessId=64972
  GATE_INSTANCE_BINARY_BUILT=2026-10-24 21:32:20
  GATE_COMMITS_AFTER_BINARY=5
  GATE_PRODUCT_COMMITS_AFTER_BINARY=0
  ```
  The last line is the one that matters: the five commits after that
  binary's build are all documentation and verification tooling, and NO
  product commit is missing from the instance -- which is exactly the
  distinction rule 17 asks for and which the gate could not previously make.
  It also explains how this session produced a RED that was not the tree's
  fault: the same gate, pointed at the owner's old instance on 5591 (dll
  built 15:46 against a tree with 5 later commits), reported
  `RED_SUITE=empty-archive` with `status=403 ... Access to the path ... is
  denied` -- green against a fresh instance, red against the stale one.
- `tools/verify/eol-check.ps1`: a checker measured that HEAD's and the
  index's blobs of `MapleBench/tools/soak.py` are CRLF while the working
  tree is LF, and that the two numstat runs are EQUAL. If that holds,
  `tools/verify/AGENTS.md` rule 6's "only a difference between the two
  numstat runs is real churn" is incomplete, because a `text=auto eol=lf`
  attribute hides the change from the diff. Card W5-EOL, re-dispatched
  twice; the first attempt ran out of room.
- The census's output SHAPE changed (two backend lines and a new refusal
  classification). No consumer of the old wording was looked for.
- No suite was re-verified on a second instance, a second map, or a second
  player position.

## Reference

- `C:\mbtest2\chk-a.md` (20004 B), `C:\mbtest2\chk-b.md` (30476 B) -- the two
  checkers.
- `C:\mbtest2\w4-attack-after.md`, `w103\report.md`, `w102\report.md`,
  `routes\routes-report.md`, `w5-handshake.md`, `w6-readme-routes.md`,
  `w1-a-selftest.ps1.md`, `w1-a-gate-base-census.md`, `w1-b-pagecheck.md`,
  `w1-c-soak.md`, `w1-e-gate-md.md`, `w1-g-census-docs.md`, `w1-h-pm-py.md`.
- `C:\MUWORK\GAME\MPSH\PROJECT\FAILURES.md` -- the failures ledger, including
  the lead's own mistakes in this audit.
