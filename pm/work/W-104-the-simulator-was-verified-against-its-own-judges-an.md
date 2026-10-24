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

- **`knockback-verify`'s control is now the channel the assertion uses**
  (card W9-KB, commit 413cc65). The old control required an unhit mob not to
  move -- the same false premise as first-attack-verify's stage 1 -- AND it
  measured `fr["mobs"][idx]`'s x, while the assertion reads the frame's own
  `knockbackPx` (`knockback-verify.py:141`, and the file's own header at
  :127-129 already said "read from the frame's own knockbackPx, NOT from the
  mob's x"). The drift distribution over 27 phases is not zero-or-not: **22
  phases ZERO, 1 PATROL (2.099 px -- one patrol step at MaxWalkSpeed
  2.5 px/frame, which is the 2.100 px the gate reported), 4 INDEX-SHIFT**
  (the frame's mob list changed shape and the index named a different
  animal). The old control would go red in **5 of 27** phases (18.5%); the
  new one in **0**. It now scans every mob's `max |knockbackPx|` and carries
  a positive control arm that must fire: the same channel reads **+2.167 px**
  on a mob that WAS hit. The knockback assertion itself is untouched.
  **Still open inside this item:** the recoil loop still identifies its mob by
  index, so the INDEX-SHIFT mode is not fixed -- giving the eleven instances
  of mob id 2220100 a stable identity is a bigger change than this card.
- **`eol-check.ps1` was reporting a false green** (card W5-EOL, commit
  413cc65). MEASURED on a controlled fixture (a scratch git repo outside
  this one: the HEAD blob has 400 bare-LF endings, the working copy has the
  same text with every 7th line's ending turned CRLF -- 58 CRLF among 342
  bare-LF -- and one line edited; `.gitattributes` says `* text=auto eol=lf`):
  ```
  git diff HEAD --numstat           -> 1  1
  git diff HEAD --ignore-all-space  -> 1  1     <- EQUAL
  HEAD blob    : 3092 bytes  CR= 0  LF=400
  WORKING copy : 3157 bytes  CR=58  LF=400       <- the endings DID change
  ```
  The attribute makes git CLEAN the working copy's CRLF back to LF before
  diffing, so the CR difference never reaches the diff. The detector that
  shipped before 2026-10-25 answered that file with `RESULT: PASS` and exit
  0. It now decides on the bytes (a KIND test) and answers
  `fail_reason=endings-normalised`. **And `tools/verify/AGENTS.md` rule 6's
  second half -- "Only a difference between the two numstat runs is real
  churn" -- was INCOMPLETE and has been corrected with the fixture above.**
- **The maker's "+44/-1" for `MapleBench/tools/soak.py` is REFUTED**: that
  file has exactly two commits in its history -- added 428/0, rewritten
  137/11 -- and no base produces 44/1. The checker's 137/11 is the numstat
  of the rewrite commit. The tree has moved since: `soak.py` is now committed
  with the LF form, so `git diff HEAD --numstat -- MapleBench/tools/soak.py`
  is empty and HEAD, INDEX and the working copy are all 22 261 B, CR=0.
- The gate now prints which binary it is judging (card W8-STALE). The
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
  The last line was supposed to be the one that matters, and **it was the
  wrong measure**: a binary is built from the WORKING TREE, so a commit
  dated after the build can already be INSIDE that binary. MEASURED: the
  repo's newest product source is
  `MapleBench/Services/EmptyArchiveService.cs` at 2026-10-24 15:46:17, and
  the 5591 instance's `MapleBenchX.dll` was built at **15:46:31 — fourteen
  seconds later**, so it contains it. The commit-date rule called both 5591
  and 5602 "stale"; under the source-mtime rule **no instance on this
  machine is stale**. The decision is now the source-mtime comparison, and
  the commit counts are printed as INFORMATION with their source line saying
  so. The rule has a positive control: a deliberately two-day-old binary
  (2026-10-22 20:24:52) makes the gate print `GATE_INSTANCE_STALE=yes`.
  **⇒ The 5591 `RED_SUITE=empty-archive` was NOT staleness.** Its text is
  `status=403 body={"message": "Access denied.", "detail": "Access to the
  path '...\\empty-....wz' is denied."}` — and **403 rather than 404 is
  itself the evidence that the route WAS in that binary**. It is a
  filesystem-permission refusal by that process, and the difference between
  the two instances is **not established**. The lead's earlier claim that
  "the same tree is green against a fresh instance and red against a stale
  one" was the card's premise repeated as if it were a measurement; it was
  refuted, and the correction is recorded in `FAILURES.md`.
- **The gate's suites are not isolated from each other, and that is the root
  of every red in this audit.** MEASURED 2026-10-25 03:0x, gate run against
  a FRESH instance (`GATE_INSTANCE_STALE=no`, `GATE_PRODUCT_COMMITS_AFTER_BINARY=0`):
  ```
  === skill-test ===
     >>> RED: skill-test -- 2.2 a cast takes the mob's hitCount x 100 HP (hp 1000 -> 1000; expected -100 for hitCount=1)
  === contact-damage ===
     >>> RED: contact-damage -- 1210103 (80): the hits are not all one number (every hit was 74; the reference's range holds 16 numbers)
  ```
  Both were green in the previous gate run (commit 413cc65, `RED SUITES: 0`).
  The same shape as W-102 (the stop lands inside a ~2.25 px window), W-103
  (the phase decides the verdict) and W9-KB (5 of 27 phases red): **a mob
  is summoned at a RANDOM x within 100 px of the character
  (`SimulatorEndpoints.cs:451-455`; the endpoint accepts no position), so a
  suite that summons a mob measures a random placement.** A placement at the
  edge of the range produces a different verdict — a cast that does not
  reach, hits that all read one number instead of the reference's range of
  16. Card W10-SHARED measures the distribution before anything is changed.

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
