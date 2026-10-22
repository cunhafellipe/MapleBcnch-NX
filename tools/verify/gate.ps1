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

# The names of the suites that went red, in the order they ran.
#
# The COUNT alone is not a verdict. MEASURED 2026-10-22 on a clean clone:
# three consecutive runs, each of them ending in "RED SUITES: 1" -- and each
# of those 1s was a DIFFERENT suite. A caller that reads only the count sees
# "1" three times and concludes nothing changed, while the content changed
# completely. So the count is reported AND the names are reported.
$script:redSuites = @()

# The verdicts file, TRUNCATED HERE, before a single suite runs.
#
# The truncation is first on purpose. A gate killed half way through must not
# leave the PREVIOUS run's suite names in the file for a reader to believe.
# MEASURED 2026-10-22: a gate killed 20 s in left the previous run's
# "RED_SUITE=knockback" and its "GATE_STATE=complete" untouched, because the
# write was at the END of the script and the script never got there.
# Truncating at the start means a killed run leaves the format marker and
# nothing else, so the reader can tell it did not finish.
$verdictsPath = Join-Path $env:TEMP 'maplebench-gate-verdicts.txt'
$verdictsOk = $true
try {
  Set-Content -LiteralPath $verdictsPath -Value 'GATE_VERDICTS_FORMAT=1' -Encoding ASCII
} catch {
  # The file is a convenience for one kind of caller; the stdout lines and
  # the exit code are the verdict. A failure here is reported by name and
  # does not change the gate's own result.
  "GATE_VERDICTS_WRITE_FAILED: $($_.Exception.Message)"
  $verdictsOk = $false
}

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
# The reason the last check failed, kept OUT of the return value.
#
# A PowerShell function returns everything it writes, so a function that both
# printed a message and returned $false returned a TWO-ELEMENT ARRAY, and
# `-not` on a non-empty array is $false. MEASURED 2026-10-22: with the backend
# down, every suite printed ">>> RED (backend unreachable)" and the gate still
# finished with "RED SUITES: 0" and an exit code of 0. The message was on the
# screen and the count did not know about it.
$script:aliveError = ''

function Alive($why) {
  try { $null = Invoke-RestMethod "$Base/api/about" -TimeoutSec 20; return $true }
  catch { $script:aliveError = $_.Exception.Message; return $false }
}

function Run($name, $block) {
  "=== $name ==="
  if (-not (Alive $name)) {
    "   >>> RED (backend unreachable: $script:aliveError) -- $name"
    $script:fail++; $script:redSuites += $name
    return
  }
  $text = (& $block 2>&1 | Out-String -Width 200)
  # a traceback in the middle of a suite is a failed suite, whatever it printed
  if ($text -match 'URLError|Connection refused|WinError 10061|Traceback \(most recent call last\)') {
    "   >>> RED (the suite could not reach the backend)"; $script:fail++; $script:redSuites += $name
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
  if ($bad) { "   >>> RED ($($bad -join ', '))"; $script:fail++; $script:redSuites += $name }
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

# ADR-0005 criterion 1, "position accuracy": the foothold the feet rest on,
# against the reference's get_fhid_underneath. It reads the foothold geometry
# from the ARCHIVE and applies the reference's own formulas, so a port that
# changes its physics does not pass by agreeing with itself.
Run 'position-accuracy' { python "$S\position-accuracy.py" }

# ADR-0005 criterion 2, "mechanism accuracy": the attack's frames and their
# timing, against the reference's own rules (Animation.cpp:64-67, Mob.cpp:151-153,
# Mob.cpp:48-57). It reads the mob data from the ARCHIVE and applies the
# reference's formulas to it, so a port that reads the wrong node cannot pass
# by agreeing with itself. It needs Mob.nx open, which the documented loop does.
Run 'attack-frame-timing' { python "$S\attack-frame-timing.py" }

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

# --------------------------------------------------------------------------- #
# The verdict, in a form a caller can use.
# --------------------------------------------------------------------------- #
#
# The exit code carries the verdict, because a verdict that lives only in the
# text is carried by nothing: every caller in this project checks the exit
# code, and until now the gate answered 0 whether it was green or red.
#
# The COUNT alone is not enough either, so the names go with it: one
# `RED_SUITE=<name>` line per red suite, in a form that can be grepped
# exactly. The count line keeps its exact bytes, because 35 places in this
# repository quote that string.
#
# The same content also goes to a file, for the caller that only has the exit
# code. It is TRUNCATED at the start of the run, so a gate that dies part way
# through leaves a file with no RED_SUITE lines in it rather than the
# previous run's names for a reader to believe.
$verdictsPath = Join-Path $env:TEMP 'maplebench-gate-verdicts.txt'
$verdictsOk = $true
try {
  Set-Content -LiteralPath $verdictsPath -Value 'GATE_VERDICTS_FORMAT=1' -Encoding ASCII
} catch {
  # The file is a convenience for one kind of caller; the stdout lines and
  # the exit code are the verdict. A failure here is reported by name and
  # does not change the gate's own result.
  "GATE_VERDICTS_WRITE_FAILED: $($_.Exception.Message)"
  $verdictsOk = $false
}

"`n=========== RED SUITES: $fail ==========="
if ($fail -gt 0) {
  foreach ($suite in $script:redSuites) {
    "RED_SUITE=$suite"
  }
}
if ($verdictsOk) {
  $lines = @('GATE_VERDICTS_FORMAT=1', "RED_SUITES=$fail")
  foreach ($suite in $script:redSuites) { $lines += "RED_SUITE=$suite" }
  $lines += 'GATE_STATE=complete'
  try {
    Set-Content -LiteralPath $verdictsPath -Value $lines -Encoding ASCII
  } catch {
    "GATE_VERDICTS_WRITE_FAILED: $($_.Exception.Message)"
  }
  "GATE_VERDICTS=$verdictsPath"
}

if ($fail -gt 0) { exit 1 }
exit 0