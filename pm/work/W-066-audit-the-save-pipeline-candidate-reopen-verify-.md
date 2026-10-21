---
id: W-066
title: Audit the save pipeline: candidate -> reopen -> verify -> swap, and what 'verified' actually means
state: building
priority: 90
owner: 
opened: 2026-10-16
closed: 
depends: []
refs: WzSaveService.cs:365 Save dispatch, :409 SaveNx, :146 node-count check, :142 WriteAllBytes, :175-178 two File.Move swap, :219-224 EncodingErrors; WzSaveService.SaveArchive :881,:1022,:1079,:1150; SimulatorEndpoints.cs:437
blocked_on: 
evidence: Audit complete; suite built and green but deliberately NOT wired into gate.ps1. 15 assertions green on a copy: edits stay in memory until save (disk md5 unmoved), the backup is byte-identical to the pre-save original, the saved archive reopens carrying the edit, and the live data root is untouched. Confirmed SaveResult.ReopenMs is 0 on every path. HEADLINE: every archive in MPSHS is .nx, so SaveNx is the only path this user's saves take and SaveArchive (the README's pipeline) is dead code for them; SaveNx verifies node count only, does not fsync, has no free-space guard, no CheckWritable, and swaps with two File.Moves leaving the destination absent in between; EncodingErrors>0 writes canvases empty and only warns. Not wired in because there is no POST /api/files/close: every opened archive stays mapped, so the suite is one-shot per server (measured run 1 green, runs 2-4 fail with Errno 22) and a per-run path leaks one archive each time (36 open, then sim-health went BAD=1).
workstream: save
kind: audit
risk: high
verify:
  - cmd: python $S/save-audit.py
    expect: assertions, 0 failed
---
## Intent

The README makes one safety promise — "MapleBench verifies its output before
replacing a destination and normally creates timestamped backups" — and the save
transaction is the one place where a defect costs the user their original data.
Establish what that promise is actually kept by, on the data this user has, and
give it a verdict that can go red.

## Reference

Own audit, no port. `WzSaveService.Save` (`WzSaveService.cs:365`) dispatches on
`file.Kind`: `split` (:377), `img-folder` (:385), `nx` -> `SaveNx` (:409),
read-only (:419), `.ms` (:430-437), then `SaveArchive` (:881) or
`SaveLooseImage` (:1531).

## Approach

1. Map both save paths, line by line, and establish what each one verifies.
2. Establish which path this user's archives actually take.
3. Build a black-box suite that exercises the promise on a COPY, with a
   known-answer control, and that reports its own red.
4. Prove the suite is not flaky before it is allowed anywhere near the gate.

## Acceptance

```
# on a FRESH server (build-gate stops the old one)
python %LOCALAPPDATA%\hermes\cache\scratch\save-audit.py
# EXPECT: VERDICT: save-audit 15 assertions, 0 failed, and the live data root's
#         md5 identical before and after

python %LOCALAPPDATA%\hermes\cache\scratch\save-audit.py --force-fail
# EXPECT: ">>> RED: save-audit --" and exit 1  (a judge that cannot fail proves nothing)
```

## Result

### The promise holds on the happy path, and that is now measured

`%LOCALAPPDATA%\hermes\cache\scratch\save-audit.py`, 15 assertions, green:

| The claim | Measured |
|---|---|
| the copy is byte-identical to the original | ok (control) |
| the edit is in memory and reads back | ok (control, before any save) |
| **"edits remain in memory until you save"** | **ok — the file on disk did not move one byte** |
| the save names the file and a backup | ok |
| **"the previous file is kept"** | **ok — the backup's md5 equals the pre-save original** |
| the destination changed and is non-empty | ok |
| **the saved archive reopens and carries the edit** | **ok** |
| `C:\MUWORK\GAME\MPSHS` is byte-identical throughout | ok |

It also confirmed a code finding empirically: **`SaveResult.ReopenMs` is `0` on
every path** — a documented field that is never set.

### The bigger finding: this user's saves never take the audited path

**Every archive in `C:\MUWORK\GAME\MPSHS` is `.nx`** (16 of them; there is no
`.wz` anywhere). `Save()` routes `file.Kind == "nx"` to `SaveNx`
(`WzSaveService.cs:409`). So the candidate -> flush -> verify -> swap pipeline the
README describes — `SaveArchive` — **is not the pipeline any of this user's saves
take.** `SaveArchive` is, for them, dead code.

| | `SaveArchive` (what the README describes) | `SaveNx` (what actually runs) |
|---|---|---|
| verification | header length + image count + `BlockSize`/`Checksum` + rewritten images parse non-empty | **node count only** (`:146`) |
| fsync | yes, `:1022`, with a comment saying why | **no** (`File.WriteAllBytes`, `:142`) |
| free-space guard | yes | **no** |
| destination locked | yes, Restart Manager exclusive-open probe (`:1079`) | **no** |
| swap | `File.Replace`, one atomic call | **two `File.Move`s** (`:175`, `:178`) — **the destination does not exist in between** |
| backup pruning | 3 kept (`:307`) | **never** |

Two paths that lose data quietly:

- `:219-224` — when `report.EncodingErrors > 0` those **canvases are written
  empty**, it becomes one line of free text in `warnings`, and the save proceeds.
- `:175-178` — between the two moves the destination is absent; the `catch`
  restores it only for `IOException`, and only while the process is alive.

And in the audited path itself: `VerifyParseBudget` (20 s, `:334`) **silently
truncates** the rewritten-image check and puts the notice in `warnings`, not
`defects`, so it does not abort. Rewritten images are checked for **presence,
never content** — an image that lost 800 of 900 properties verifies clean.

### Why the suite is not wired into the gate

Because it cannot be. **There is no `POST /api/files/close`** — measured, the only
`/files/*` routes are `open`, `open-img-folder`, `open-many`, `save` and
`readonly`. Every archive the editor opens stays memory-mapped for the life of
the process, and a mapped file cannot be reopened for writing by anything else:
a plain `open(f, 'wb')` on the file the previous run saved fails with `Errno 22`.

So a per-run unique path leaks one archive per run — measured **36 open after a
handful of runs**, after which `sim-health` reported `BAD=1` in the gate while
passing 3/3 standalone. And a fixed path works **exactly once per server
lifetime**: measured, run 1 green (archives 16 -> 17), runs 2-4 all failing at the
copy.

`gate.ps1` therefore does NOT run it, with the reason recorded in the file. A gate
that goes red on its second run cries wolf, which `pm/GATE.md:78-83` calls worse
than having no gate.

### What is left, and it is not small

- **No way to close an archive.** The gap underneath all of the above.
- `SaveNx` needs at minimum the `FlushToDisk` the other path has, a free-space
  guard, and a `CheckWritable` probe. Each is a small diff against a function
  that already exists for the other path.
- `EncodingErrors > 0` writing empty canvases should refuse the save, not note it.
- `SaveArchive`'s rewritten-image check should compare content, and the parse
  budget should abort rather than warn.
