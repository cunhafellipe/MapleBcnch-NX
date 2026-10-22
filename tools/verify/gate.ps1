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

# The pre-gate: the doctor, asked once, before a single suite runs.
#
# MEASURED 2026-10-22: gate.ps1 ran a suite file that was not tracked. python
# printed "can't open file ... No such file or directory" and exited 2. That
# text contains neither '>>> RED' nor 'BAD=[1-9]', so $bad stayed empty and the
# gate printed "ok" -- a suite that never ran, scored as a suite that passed.
# A registry that cannot be trusted makes every suite verdict untrustworthy, so
# the registry is checked FIRST, and a red doctor stops the run.
#
# The question asked is the SUITE one: "do the declarations in SPEC.yaml still
# match the code?" The doctor's other sections are about this project's own
# bookkeeping -- work items, invariants -- and must not be able to stop the
# product from being measured. So the gate reads one machine-readable token,
# DOCTOR_SUITES_PROBLEMS, and a MISSING token counts as red: a doctor that
# could not be measured is not a doctor that found nothing.
#
# The exit code is read from ProcessStartInfo.ExitCode, never from
# $LASTEXITCODE: which copy of that automatic variable a native call updates
# depends on how this script was entered (incident 14), and the wrong copy
# reads 0 for a child that failed. Common.ps1's Invoke-CheckedProcess does
# this correctly, but it is not dot-sourced here: it sets
# Set-StrictMode -Version Latest and $ErrorActionPreference = 'Stop', and
# those would then apply to the whole gate -- the one tool that must not
# start throwing halfway through a run.
#
# The repository root is never written into this file. It is derived from
# $PSScriptRoot at run time, because the path contains non-ASCII characters
# and a literal in a .ps1 gets mangled on write (incident 1). The child is
# started with a RELATIVE argument and the root as its working directory, so
# the non-ASCII path never passes through a command line.
$doctorExit = $null
$doctorOut = ''
$doctorErr = ''
$doctorProblem = ''
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = 'python'
$psi.Arguments = 'tools/pm.py doctor'
$psi.WorkingDirectory = $Root
$psi.UseShellExecute = $false
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.CreateNoWindow = $true
$psi.StandardOutputEncoding = New-Object System.Text.UTF8Encoding($false)
$psi.StandardErrorEncoding = New-Object System.Text.UTF8Encoding($false)
$proc = New-Object System.Diagnostics.Process
$proc.StartInfo = $psi
try {
  [void]$proc.Start()
} catch {
  $doctorProblem = 'the doctor could not be started: ' + $_.Exception.Message
}
if ($doctorProblem -eq '') {
  $outTask = $proc.StandardOutput.ReadToEndAsync()
  $errTask = $proc.StandardError.ReadToEndAsync()
  $exited = $proc.WaitForExit(300000)
  if (-not $exited) {
    try { $proc.Kill() } catch { }
    $doctorProblem = 'the doctor did not finish within 300 s'
  } else {
    $doctorOut = $outTask.Result
    $doctorErr = $errTask.Result
    $doctorExit = $proc.ExitCode
  }
  $proc.Dispose()
}
# The token is read as a WHOLE LINE, and exactly one such line must be
# present.
#
# MEASURED 2026-10-22: a plain -match takes the FIRST occurrence, and the
# doctor prints each suite's DECLARED verdict -- a free-form string from
# SPEC.yaml -- on its own line, before the token line. A suite row whose
# verdict is "DOCTOR_SUITES_PROBLEMS=0" therefore put the token text
# earlier in the doctor's output than the token itself: the gate read the
# forged 0, and ran 32 suites on a tree the doctor had just reported as
# 20 problems. A whole-line match cannot be forged that way -- every other
# line of the doctor's output carries other text on it.
$tokenLines = @([regex]::Matches($doctorOut, '(?m)^DOCTOR_SUITES_PROBLEMS=(\d+)\s*$'))
$suiteProblems = $null
if ($tokenLines.Count -eq 1) {
  $suiteProblems = [int]$tokenLines[0].Groups[1].Value
} elseif ($tokenLines.Count -gt 1) {
  $doctorProblem = 'the doctor reported DOCTOR_SUITES_PROBLEMS ' + $tokenLines.Count + ' times -- the answer is ambiguous'
}
# The doctor's OWN EXIT CODE is the primary threshold. It is the doctor's
# verdict and it cannot be forged by anything printed in the output, which
# is exactly why it is the threshold and the token is the second check.
#
# MEASURED 2026-10-22: the doctor exited 1 on a fresh export of HEAD
# while every suite was fine, because the avatar-md5 invariant hashed a
# text file whose bytes differ between a working tree (CRLF) and a fresh
# checkout (LF). That invariant now normalises the line endings before
# hashing, so the doctor is green on a clean clone and its exit code means
# what it says. Both checks are kept: the exit code says whether the
# doctor found anything, the token says whether the SUITE declarations are
# sound.
$doctorRed = $false
$doctorReason = ''
if ($doctorProblem -ne '') {
  $doctorRed = $true
  $doctorReason = $doctorProblem
} elseif ($doctorExit -ne 0) {
  $doctorRed = $true
  $doctorReason = 'the doctor exited ' + $doctorExit
} elseif ($null -eq $suiteProblems) {
  $doctorRed = $true
  $doctorReason = 'the doctor did not report DOCTOR_SUITES_PROBLEMS'
} elseif ($suiteProblems -gt 0) {
  $doctorRed = $true
  $doctorReason = 'the doctor found ' + $suiteProblems + ' suite declaration problem(s)'
}
if ($doctorRed) {
  ">>> RED (the doctor is not green -- no suite was run)"
  "GATE_DOCTOR_EXIT=$doctorExit"
  "GATE_DOCTOR_REASON=$doctorReason"
  if ($doctorOut -ne '') { $doctorOut }
  if ($doctorErr -ne '') { $doctorErr }
  if ($verdictsOk) {
    $lines = @('GATE_VERDICTS_FORMAT=1', 'RED_SUITES=1', 'RED_SUITE=doctor', 'GATE_STATE=doctor-red')
    try {
      Set-Content -LiteralPath $verdictsPath -Value $lines -Encoding ASCII
    } catch {
      "GATE_VERDICTS_WRITE_FAILED: $($_.Exception.Message)"
    }
    "GATE_VERDICTS=$verdictsPath"
  }
  exit 1
}
"GATE_DOCTOR_EXIT=$doctorExit (suite declarations: $suiteProblems problem(s))"
# There is no "the doctor is red for a reason that is not about the suites,
# the gate proceeded" note here any more, and there must not be: the
# doctor's exit code is the threshold, so a doctor that is red for ANY
# reason stops the gate. That note existed because the exit code could not
# be used as the threshold -- the doctor was long-term red on a clean
# clone, because the avatar-md5 invariant hashed a text file whose bytes
# differ between a working tree (CRLF) and a fresh checkout (LF). That
# invariant now normalises the line endings before hashing, so the exit
# code is meaningful and "red for another reason" is a stop, not a
# proceed. The information a reader needs is on the red path instead:
# GATE_DOCTOR_REASON says why, and the doctor's own output says which
# section was red.

# RED is an EXPLICIT marker a suite prints, never an inference. Three attempts at
# inferring red from prose each produced a false result: $LASTEXITCODE is not set
# by a PowerShell script (flagged six green suites), and `\d+ failed` matches the
# "0 failed" in "passed=5 failed=0" (flagged two more). A gate that cries wolf is
# worse than no gate, so this one only ever looks for these strings.
$RED = @('>>> RED', 'NO damage number')
# A CRASH is not the same failure as a DEAD BACKEND, and the two must not share
# a reason: a red for a reason that is not true is not a measurement.
#
# MEASURED 2026-10-23, against real stderr captured on this machine from a
# python process, a node process and a refused urlopen. The previous one-line test
#   'URLError|Connection refused|WinError 10061|Traceback \(most recent call last\)'
# matched the python traceback, and matched NOTHING AT ALL in the node trace.
# Node prints
#   file:///C:/.../x.mjs:2
#     at file:///C:/.../x.mjs:2:3
#   TypeError: Cannot read properties of null (reading 'y')
# none of which contains any of those four words -- so $bad stayed empty, the
# gate printed "ok", and a suite that died measured nothing and was scored as a
# pass. It also matched the python traceback when the backend was perfectly
# alive, and reported "could not reach the backend": red for a reason that was
# measurably false.
#
# Each shape below is ONE string a crash prints, matched separately, so the red
# reason can name which one fired. Every shape says what it MATCHES and what it
# does NOT match, because a shape that fires on a healthy suite is a wrong
# measurement, not a warning -- and the "does NOT match" half is the half that
# has to be true, because a shape that only says what it catches cannot be
# reviewed.
#
#   python traceback        MATCHES "Traceback (most recent call last):", an
#                                  unhandled python exception.
#                           NOT    a node stack, a PowerShell error record, or
#                                  a healthy suite that merely prints the word.
#   node stack frame        MATCHES a line that is exactly "    at <x>:<n>:<n>".
#                           NOT    a frame written as "at fn (...)" -- the
#                                  frame has to END with two colon-separated
#                                  numbers. A crash that has such a frame may
#                                  still carry another frame that does match.
#   node module header      MATCHES a line "<word> : file:///...", which is how
#                                  the FIRST line of a node crash arrives.
#                           NOT    a node that wrote to stderr before it
#                                  crashed: then the header line carries no
#                                  "<word> :" in front of it.
#   node error line         MATCHES a line starting with a JavaScript error
#                                  class name -- TypeError, ReferenceError,
#                                  SyntaxError, RangeError, EvalError, URIError.
#                           NOT    node's bare "Error: ..." (no class name),
#                                  and NOT a suite's own data line such as
#                                  "Error: 0 layers rejected". The class name
#                                  used to be OPTIONAL, so the shape degenerated
#                                  to a bare "Error: " and called a suite that
#                                  had just printed "VERDICT passed 4/4" red,
#                                  for a reason that was not true. A bare
#                                  "Error: " node crash is still caught by
#                                  "node stack frame".
#   powershell error record MATCHES a rendered error record whose
#                                  comma-separated id contains
#                                  "Microsoft.PowerShell.Commands.".
#                           NOT    native stderr, whose id is
#                                  "NativeCommandError" and carries no
#                                  namespace. The rendered line begins with
#                                  four spaces, a "+" and a space, so the old
#                                  "^\s*FullyQualifiedErrorId" anchor could
#                                  never match it; and the namespace is not
#                                  guaranteed to be the FIRST element of the id
#                                  list ("PathNotFound,Microsoft.PowerShell.
#                                  Commands.GetContentCommand").
#
# MEASURED 2026-10-23, the trap that last shape has to avoid: Windows PowerShell
# 5.1 renders the stderr of EVERY native command as a PowerShell error record,
# so a crashing python prints
#     At C:\...\gate.ps1:225 char:9
#         + CategoryInfo          : NotSpecified: (Traceback...) [], RemoteException
#         + FullyQualifiedErrorId : NativeCommandError
# A shape built on "CategoryInfo", or on "At ... char:", therefore fires on every
# native stderr line in the tree -- healthy or not -- and would turn ordinary
# logging red. The discriminator is the error id, and both sides were measured:
#   native stderr -> FullyQualifiedErrorId : NativeCommandError
#   Write-Error   -> FullyQualifiedErrorId : Microsoft.PowerShell.Commands.WriteErrorException
# so only the second is a shape here. That is the whole reason the old test was
# four narrow words rather than "did it crash": under 5.1, "did it crash" is not
# answerable from stderr, because stderr is always a PowerShell error record.
# The three lines that DECIDE red are further down and are not touched by this.
$script:CrashShapes = [ordered]@{
  'python traceback' = 'Traceback \(most recent call last\):'
  'node stack frame' = '(?m)^\s+at\s+\S+:\d+:\d+\s*$'
  'node module header' = '(?m)^\S+\s*:\s*file:///'
  'node error line' = '(?m)^(?:Type|Reference|Syntax|Range|Eval|URI)Error:\s'
  'powershell error record' = '(?m)^\s*\+?\s*FullyQualifiedErrorId\s*:[^\r\n]*Microsoft\.PowerShell\.Commands\.'
}
# The connection words are kept exactly as they were, in their own list, because
# they answer a different question: the backend, not the suite, is what failed.
$script:BackendShapes = @('URLError', 'Connection refused', 'WinError 10061')
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
  # A suite that raises a TERMINATING error must not be able to kill the gate.
  #
  # MEASURED 2026-10-23: with nothing here, an injected suite that throws ended
  # the whole gate. It printed its own "=== name ===" header and then died:
  # stdout was 3 lines, "RED SUITES" was never printed AT ALL, the other probe
  # suites never ran, and the exit code was 1 -- from PowerShell aborting, not
  # from this gate deciding. A reader cannot tell those two apart, and a gate
  # that dies leaves no verdict, which in this output is indistinguishable from
  # all green.
  $where = (Get-Location).Path
  $text = ''
  $died = ''
  try {
    $text = (& $block 2>&1 | Out-String -Width 200)
  } catch {
    $died = $_.Exception.GetType().FullName + ': ' + $_.Exception.Message
    # MEASURED 2026-10-23: two suites here are written
    # "Push-Location $Root; node ...; Pop-Location". An exception between the
    # Push and the Pop leaves the location moved, so every suite after it would
    # run in the wrong directory. Put it back before carrying on.
    try { Set-Location -LiteralPath $where } catch { }
  }
  if ($died -ne '') {
    "   >>> RED (the suite died: $died) -- $name"; $script:fail++; $script:redSuites += $name
    return
  }
  # A crash in the middle of a suite is a failed suite, whatever it printed --
  # but the reason has to say WHICH failure, because a dead backend and a dead
  # suite are different things and only one of them is the suite's own fault.
  $backendHit = @($script:BackendShapes | Where-Object { $text.Contains($_) })
  $crashHit = @()
  foreach ($shape in $script:CrashShapes.Keys) {
    if ($text -match $script:CrashShapes[$shape]) { $crashHit += $shape }
  }
  if ($backendHit.Count -gt 0 -or $crashHit.Count -gt 0) {
    if ($backendHit.Count -gt 0) {
      $why = 'the suite could not reach the backend: ' + ($backendHit -join ', ')
    } else {
      $why = 'the suite died: ' + ($crashHit -join ', ')
    }
    "   >>> RED ($why)"; $script:fail++; $script:redSuites += $name
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

# ADR-0005 criterion 2 again, the other two numbers the same info node carries:
# the attack's DAMAGE (Mob.cpp:160, with the mob's own PADamage/MADamage as the
# fallback when the attack declares none) and the projectile's speed
# (Mob.cpp:161). Same shape as the suite above: the numbers come from the
# ARCHIVE and the reference's formulas are applied to them here, so a port that
# publishes a wrong damage number cannot pass by agreeing with itself. It needs
# Mob.nx open, which the documented loop does.
Run 'attack-damage' { python "$S\attack-damage.py" }

# The MOB's own attack -- the touch attack, not the character's swing. The
# reference has no single number here: Mob.cpp:1853-1856 draws one hit from
# [floor(0.8 * PADamage), PADamage - 1] (Randomizer.h:72-75 makes the upper
# bound exclusive), so the judge reads PADamage from the ARCHIVE and checks
# every hit the port lands against that range, and checks that the hits are
# not all one number when the range holds more than one. A build that freezes
# the number cannot pass it.
Run 'contact-damage' { python "$S\contact-damage.py" }

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