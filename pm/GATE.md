# GATE — what "green" means, exactly

```powershell
python tools/pm.py gate        # build, deploy, every suite
python tools/pm.py check       # structural audit of the work items
```

## The build gate

Run in this order. The order is not stylistic: each step is the precondition
for the next, and skipping one produces a failure that reads like a code error.

```powershell
& "$env:LOCALAPPDATA\hermes\cache\scratch\build-gate.ps1"   # stop server, build Release
& "$env:LOCALAPPDATA\hermes\cache\scratch\deploy.ps1"       # copy wwwroot, md5, start
python "$env:LOCALAPPDATA\hermes\cache\scratch\open-archives.py"   # reopen archives
python "$env:LOCALAPPDATA\hermes\cache\scratch\gate.ps1"    # every suite
```

| Condition | Value |
|---|---|
| Build | `net10.0-windows`, SDK `10.0.401`, **0 errors 0 warnings** |
| Output | `bin/Release/net10.0-windows`, and a byte-identical copy in `<scratch>/build-check` |
| wwwroot | source ≡ build output ≡ **served**, every file |
| `avatar.js` md5 | `a1eb2eb47af42e07ee048762c2592456` — unchanged, always |
| Test instance | `http://127.0.0.1:5591`, `--allow-multiple --no-browser` |
| Port 5480 | **banned.** Do not stop whatever is on it. |
| Server sockets | `WinError 10048` after a few thousand requests is the harness, not the product |

Three of these are load-bearing for reasons that are not obvious:

- **Stop the server before building.** It holds `MapleBenchX.dll`; the build
  retries ten times and then fails with `MSB3027`, which reads like a compile
  failure and is a file lock.
- **Reopen the archives after every restart.** Archive ids `f1..fN` depend on
  open order. Never hard-code "f5 is Mob".
- **`avatar.js` is the tripwire.** This project does not otherwise touch it. A
  changed md5 means something reached into a file it had no business in.

## The suites

Twenty-five, each covering one thing. `gate.ps1` runs them in this order. The
first ten are the original set; the rest were added as their work items landed.

| Suite | Covers | Pass marker |
|---|---|---|
| `ladder-all` | every ladder on the test map: up, down, bounds | `ALL LADDERS PASS` |
| `attack-test` | attack stance on the wire; swings that land | `VERDICT: …` + `damage numbers on the DELAYED frames: N` |
| `skill-test` | multiStrike deals 0; the cast lock; four guards | **no assertion — prints only, see `W-057`** |
| `mob-attack-verify` | a mob closes the gap and lands hits on its own | per-hit lines |
| `first-attack-verify` | `firstAttack` mobs pursue unprovoked | `PASS: it attacked without being struck` |
| `npc-label-check` | NPC names, functions, hide flags | **no assertion — prints only, see `W-057`** |
| `sim-skills-check` | client-side skill pose routing (node) | `27 passed, 0 failed` |
| `mobinfo-verify` | the bestiary endpoint against archive truth | `BAD=0` |
| `equipstats-verify` | equipment stat nodes against archive truth | `BAD=0` |
| `sheet-verify` | equipment → sheet → swing → damage number | `BAD=0` |

Four more suites print `>>> RED` on failure but carry no `BAD=`/`VERDICT:`
marker, so `gate.ps1` shows their tail instead of a verdict line:
`attack-drop`, `mob-fly`, `ball-art`, `attack-after`. `W-056` is the item that
made them able to report red at all — before it, four of them could fail
silently.

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