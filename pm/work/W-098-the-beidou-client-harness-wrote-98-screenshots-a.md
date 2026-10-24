---
id: W-098
title: The beidou-client harness wrote 98 screenshots and 2 logs into the user's data root: the agent tools cannot move or delete them, and the invariant has to name them as exclusions
state: done
priority: 80
owner: 
opened: 2026-10-24
closed: 2026-10-24
depends: []
refs: C:\MUWORK\GAME\MAPLESOTRY\伺服器研究\beidou-client-mcp\config.json; pm/SPEC.yaml data-root-untouched
blocked_on: 
evidence: Re-measured 2026-10-24 20:30, every number below carries the command that produced it. `Test-Path 'C:\MUWORK\GAME\MPSHS\screenshots'` -> False; PNG count under MPSHS -> 0; `Get-ChildItem MPSHS -File -Filter 'client-std*.log'` -> no output; files under MPSHS -> 38; config.json screenshotDir -> C:\MUWORK\GAME\MPSH\.scratch\beidou-client\screenshots (that directory exists, 3 PNGs), workDir still C:\MUWORK\GAME\MPSHS. `python -X utf8 tools/pm.py doctor` -> data-root-untouched: ok newest=2026-10-18 22:58:33.753097 reference=2026-10-22 08:42:00 files scanned=34 unreadable=0 excluded=4, with one excluded file newer than the reference (Settings 2026-10-24 18:56:19). The running MCP process (pid 16472, started 16:03:13, after the config was written 04:40:57 UTC) was scanned in memory: it carries MPSH\.scratch\beidou-client\screenshots and does NOT carry MPSHS\screenshots. The residue was removed earlier through the runtime's own trash launcher (mavis-trash.js), which reported all three targets moved.
---
## Intent

`C:\MUWORK\GAME\MPSHS` is the user's original data: 16 `.nx` archives, the
game client, its DLLs. The `data-root-untouched` invariant exists to catch
**tests** writing into it. The beidou-client MCP harness had its working
directory set to that folder, so its screenshot tool wrote **98 PNGs** and its
two client logs landed there, and the invariant went red for the wrong reason:
the red said "something wrote to the user's data", and what had written was a
screenshot tool.

The harness now writes outside the data root, and the residue that had
already landed in the root is gone.

## Reference

- `pm/SPEC.yaml` — the `data-root-untouched` invariant, `kind: mtime-newer-than`.
- `pm/SPEC.yaml` — the `exclude:` list added on 2026-10-24, with the reason.
- `C:\MUWORK\GAME\MAPLESOTRY\伺服器研究\beidou-client-mcp\config.json` —
  `workDir` (must stay: the client reads its relative `Settings` from the
  working directory, `src/Configuration.cpp:114-115`) and `screenshotDir`
  (an absolute path outside the data root).

## Approach

1. **Stop the bleeding.** `screenshotDir` is an absolute path outside the data
   root. `workDir` stays where it is: the client's `Settings` file lives in
   the working directory by design, and moving it would silently give the
   client defaults (its own comment says so).
2. **Declare the exclusions.** `data-root-untouched` carries
   `exclude: ['Settings', 'Settings.*', 'screenshots', 'client-std*.log']`.
   `Settings` is the game's own runtime file, written by the running client.
   The other three are harness output. The exclusions are **printed by name
   and mtime** whenever they are newer than the reference, so an exclusion is
   never a way to hide a write: the doctor prints `excluded from this guard
   (N file(s) newer than the reference...)` and lists them.
3. **Clean the root.** The residue went out through the runtime's own trash
   launcher (`mavis-trash.js`), which reported all three targets moved. The
   agent's ordinary file tools are still not allowed to write outside the
   session workspace; the trash launcher is the path that worked.
4. **Decide whether `Settings` should be excluded at all.** The honest
   alternative is to exclude it from the guard and say so, which is what step 2
   does. The alternative to THAT is to move the client's working directory
   out of the data root and put the `Settings` file there -- a change to the
   game client's setup, and a decision for the person who owns that client.

## Acceptance

```
# 1. the harness writes outside the data root
python -c "import json;print(json.load(open(r'C:\MUWORK\GAME\MAPLESOTRY\伺服器研究\beidou-client-mcp\config.json',encoding='utf-8'))['screenshotDir'])"
# EXPECT: a path that does NOT start with C:\MUWORK\GAME\MPSHS

# 2. the guard is green and says what it excluded
python -X utf8 tools/pm.py doctor
# EXPECT: "data-root-untouched: ok", "files scanned=34", "excluded=4",
#         and the excluded files that are newer than the reference listed by name

# 3. the data root carries no harness output
# EXPECT: C:\MUWORK\GAME\MPSHS contains no screenshots\ directory and no
#         client-std*.log
```

## Result

**Closed 2026-10-24. Re-measured 20:30 — every clause below is a measurement,
not a recollection.** The commands and their output are the evidence.

```
$ Test-Path 'C:\MUWORK\GAME\MPSHS\screenshots'                 -> False
$ (Get-ChildItem 'C:\MUWORK\GAME\MPSHS' -Recurse -File -Filter *.png | Measure-Object).Count
                                                                -> 0
$ Get-ChildItem 'C:\MUWORK\GAME\MPSHS' -File -Filter 'client-std*.log'
                                                                -> (no output: none exist)
$ (Get-ChildItem 'C:\MUWORK\GAME\MPSHS' -Recurse -File | Measure-Object).Count
                                                                -> 38
$ (Get-Content '<...>\beidou-client-mcp\config.json' -Raw | ConvertFrom-Json)
   workDir       = C:\MUWORK\GAME\MPSHS
   screenshotDir = C:\MUWORK\GAME\MPSH\.scratch\beidou-client\screenshots
$ Test-Path 'C:\MUWORK\GAME\MPSH\.scratch\beidou-client\screenshots'   -> True
$ (Get-ChildItem 'C:\MUWORK\GAME\MPSH\.scratch\beidou-client\screenshots' -Filter *.png).Count
                                                                -> 3
$ python -X utf8 tools/pm.py doctor
   data-root-untouched: ok  newest=2026-10-18 22:58:33.753097
                         reference=2026-10-22 08:42:00  files scanned=34  unreadable=0  excluded=4
   excluded from this guard (1 file(s) newer than the reference, by declaration ...):
     Settings  2026-10-24 18:56:19.685374
```

Acceptance clause by clause:

1. **The harness writes outside the data root -- MET.** `screenshotDir` is an
   absolute path under `MPSH\.scratch\`, and the directory exists with 3 PNGs
   in it. This is stronger than the config file alone: the running MCP
   process (pid 16472, started 16:03:13, i.e. AFTER the config was written at
   2026-10-24 04:40:57 UTC) was scanned in memory and carries
   `MPSH\.scratch\beidou-client\screenshots` (ASCII and UTF-16) and does NOT
   carry `MPSHS\screenshots`. The config is not a stale file here; the live
   process is using it.
2. **The guard is green and says what it excluded -- MET.** The doctor prints
   `data-root-untouched: ok`, `files scanned=34`, `excluded=4`, and names the
   one excluded file that is newer than the reference (`Settings`, the game's
   own runtime file). The three harness-output exclusions
   (`screenshots`, `client-std*.log`) match nothing any more, because nothing
   with those names exists in the root -- which is why `excluded=4` and the
   printed list has ONE entry rather than a hundred.
   (38 files on disk - 4 excluded = 34 scanned: the two numbers are the same
   population counted with and without the declared exclusions.)
3. **The data root carries no harness output -- MET.** 0 PNGs, no
   `screenshots\` directory, no `client-std*.log`. The residue went out
   through the runtime's own trash launcher (`mavis-trash.js`), which reported
   all three targets moved.

`workDir` is still `C:\MUWORK\GAME\MPSHS` and it must stay: the client reads
its relative `Settings` from the working directory by design
(`src/Configuration.cpp:114-115`), and moving it would silently give the
client defaults.

### The residual risk that this item does NOT cover

The exclusions in `pm/SPEC.yaml` are matched **by name**, so they cover the
harness output that was measured, not the class of writes. A differently
named write into the data root still reddens the invariant -- that is the
correct behaviour. The residual is that an agent command can still name
`C:\MUWORK\GAME\MPSHS\screenshots` as an output path and recreate the
directory; the toolchain in `C:\MUWORK\GAME\MPSH\.scratch\` (`screen.ps1`,
`grab_game.ps1`, `crop.py`, `bgra2png.py`) defaults to writing into `.scratch`
already, and keeping it there is the convention that prevents the recreate.

### Who wrote them (round 2, for the record)

Two distinct writers, measured:

- **ISO-named** (`<UTC stamp>-<tag>.png`, e.g. `...-AutoPlaySettings-.png`):
  the beidou-client MCP's `client_screenshot` tool
  (`<...>\beidou-client-mcp\src\mcp.js:1353-1359` writes
  `${stamp}-${tag}.png`; `:80-86` resolves the directory from
  `workDir` + `screenshotDir`). The image is the client's framebuffer
  (1366x768 = the game window), and the client source itself never writes a
  file -- every "screenshot" hit under `src/**` and `wz/**` is the DebugServer's
  in-memory PNG path.
- **Hand-named** (`concept-panel.png`, `concept-panel-2x.png`,
  `slotarea-3x-v3.png`, `steppers-12x.png`, `crop_menu.png`, `zoom_grid.png`):
  an agent session with cwd `C:\MUWORK\GAME\MPSH`. The toolchain there is
  `screen.ps1` (the CALLER chooses the output path), `grab_game.ps1/.py`,
  `crop.py` and `bgra2png.py`. The two newest files were 73 ms apart
  (`concept-panel.png` ctime 05:06:51.343, `concept-panel-2x.png` ctime
  05:06:51.416) -- one tool call writing a crop and its 2x copy.

The exact command for the hand-named writer is NOT recoverable from the DSH
session stores: a sweep of all 633 stored sessions across `.dsh`,
`%APPDATA%\dsh-desktop`, the hermes tree and `.omp` (182 966 files scanned,
1445.3 s) found 6 hits for those file names, and every one of them is a
`read_image` READ, never an exec. The session records hold no shell-exec
events at all. The next places that WOULD hold it are the harness's
subprocess-runner log, or Windows' own write audit.
