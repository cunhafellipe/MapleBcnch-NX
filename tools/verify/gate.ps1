# The suites live in the repository. They used to live in an agent cache under
# the user profile, and that cache pruned them on 2026-10-21; they survived only
# because a shadow copy taken an hour earlier still held them. A directory that
# prunes itself is not a place to keep the thing that decides whether the code
# is correct.
$Root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$S = Join-Path $Root 'tools\verify\suites'
$env:PYTHONIOENCODING = 'utf-8'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$fail = 0

# RED is an EXPLICIT marker a suite prints, never an inference. Three attempts at
# inferring red from prose each produced a false result: $LASTEXITCODE is not set
# by a PowerShell script (flagged six green suites), and `\d+ failed` matches the
# "0 failed" in "passed=5 failed=0" (flagged two more). A gate that cries wolf is
# worse than no gate, so this one only ever looks for these strings.
$RED = @('>>> RED', 'NO damage number')
# Overridable so the gate can be pointed at another instance, but the default is
# the port the suites have always used.
$Base = if ($env:GATE_BASE) { $env:GATE_BASE } else { 'http://127.0.0.1:5591' }
$Port = ([uri]$Base).Port

# ...and "the suite printed no red marker" is NOT the same as "the suite passed".
#
# MEASURED 2026-10-17: the backend died of OutOfMemoryException partway through a
# run (16 archives open plus a browser judge holding several GB). Every suite after
# it printed a Python traceback ending in
#   URLError: <urlopen error [WinError 10061] connection refused>
# which contains no '>>> RED' and no 'BAD=n' -- so the gate printed "ok" for six
# suites that had measured NOTHING. A green that means "nothing ran" is the exact
# failure this gate exists to prevent.
function Alive($why) {
  try { $null = Invoke-RestMethod "$Base/api/about" -TimeoutSec 20; return $true }
  catch { "   >>> RED (backend unreachable: $($_.Exception.Message)) -- $why"; return $false }
}

function Run($name, $block) {
  "=== $name ==="
  if (-not (Alive $name)) { $script:fail++; return }
  $text = (& $block 2>&1 | Out-String -Width 200)
  # a traceback in the middle of a suite is a failed suite, whatever it printed
  if ($text -match 'URLError|Connection refused|WinError 10061|Traceback \(most recent call last\)') {
    "   >>> RED (the suite could not reach the backend)"; $script:fail++
    ($text -split "`r?`n" | Where-Object { $_.Trim() } | Select-Object -Last 3) |
      ForEach-Object { "      $_" }
    return
  }
  $lines = $text -split "`r?`n" | Where-Object { $_.Trim() }
  $verdict = $lines | Where-Object {
    $_ -match 'VERDICT|ALL LADDERS PASS|passed,|BAD=|PASS:' } | Select-Object -Last 3
  if ($verdict) { $verdict | ForEach-Object { "   GREEN? $($_.Trim())" } }
  else { "   (read the tail):"; ($lines | Select-Object -Last 4) | ForEach-Object { "      $_" } }
  $bad = @($RED | Where-Object { $text.Contains($_) })
  if ($text -match 'BAD=[1-9]') { $bad += 'BAD=n' }
  if ($bad) { "   >>> RED ($($bad -join ', '))"; $script:fail++ }
  else { "   ok" }
}

Run 'ladder-all'      { & "$S\ladder-all.ps1" }
Run 'attack-test'     { & "$S\attack-test.ps1" }
Run 'skill-test'      { & "$S\skill-test.ps1" }
Run 'mob-attack-verify' { & "$S\mob-attack-verify.ps1" }
Run 'first-attack-verify' { & "$S\first-attack-verify.ps1" }
Run 'npc-label-check' { & "$S\npc-label-check.ps1" -Port $Port -Map '000000002.img' }
Run 'sim-skills-check' { Push-Location $Root; node 'MapleBench/tools/sim-skills-check.mjs'; Pop-Location }
Run 'npc-chat'     { python "$S\npcchat-verify.py" }
Run 'sim-health'   { python "$S\simhealth-verify.py" }
Run 'npc-chat-frame' { python "$S\npcchat-frame-verify.py" }
Run 'knockback'    { python "$S\knockback-verify.py" }
Run 'attack-drop'  { python "$S\attackdrop-verify.py" }
Run 'skill-key'   { python "$S\skillkey-verify.py" }
Run 'projectile'  { python "$S\projectile-verify.py" }
Run 'hold-key'    { python "$S\holdkey-verify.py" }
Run 'npc-name'    { python "$S\npcname-verify.py" }
Run 'mob-fly'     { python "$S\mobfly-verify.py" }
Run 'ball-art'    { python "$S\ballart-verify.py" }
Run 'attack-after' { python "$S\attackafter-verify.py" }
Run 'revive'    { python "$S\revive-verify.py" }
Run 'mobinfo-verify'  { python "$S\mobinfo-verify.py" }
Run 'equipstats-verify' { python "$S\equipstats-verify.py" }
Run 'equipset-verify' { python "$S\equipset-verify.py" }
Run 'sheetfields-verify' { python "$S\sheetfields-verify.py" }
Run 'sheet-verify'    { python "$S\sheet-verify.py" }
Run 'node-verify'    { python "$S\node-verify.py" }
Run 'skill-e2e'    { python "$S\skill-e2e.py" }

# W-089. The 66-skill SHAPE sweep, resident.
#
# W-087 found that 22 of the 66 castable skills dealt no damage, and the reason
# was not per-skill logic: it was one reader that only knew one node name. A
# one-off measurement of that would have gone stale the next day, and "today 64 of
# 66" is not a property a person can check by reading.
#
# The suite prints BAD=n, and `Run` recognises the same `BAD=[1-9]` shape every
# other suite uses, so it needs no new gate rule. A skill that is genuinely out of
# reach is listed in the suite's own WHITELIST, and the whitelist entry is
# re-checked on every run -- if the reason for it stops being true, the suite
# fails instead of quietly passing.
Run 'skillshape'   { node "$S\skillshape-verify.mjs" }

# The only suite that can see a canvas. Everything above it is HTTP, and HTTP is
# blind to drawImage, to focus, and to the key handler -- which is how a skill that
# "works" server-side can be un-castable by a person. It drives real Chrome through
# playwright-core, the copy DSH already ships, so there is nothing to install.
$env:DSH_PROFILE_DIR = if ($env:DSH_PROFILE_DIR) { $env:DSH_PROFILE_DIR } else { Join-Path $HOME '.dsh\profiles\desktop' }
Run 'browser-verify' { Push-Location $Root; node 'MapleBench/tools/browser-verify.mjs'; Pop-Location }

# save-audit is NOT wired in here, on purpose, and that is a finding rather than
# an omission. It needs to copy an archive, and once the editor has that archive
# open the file is memory-mapped for the life of the process -- there is no
# POST /api/files/close -- so the copy fails with Errno 22 on every run after the
# first. Measured: run 1 green (archives 16 -> 17), runs 2-4 all failing at the
# copy. A gate that goes red on its second run cries wolf, which is worse than
# not having it (pm/GATE.md:78-83, TRAPS T-2).
#
# Run it on a FRESH server, the one the documented loop already produces:
#   build-gate.ps1 (stops the server) -> deploy.ps1 -> open-archives.py
#   python tools\verify\suites\save-audit.py
# W-066 tracks the underlying gap: no endpoint to close an archive.

"`n=========== RED SUITES: $fail ==========="