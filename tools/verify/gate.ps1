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

# How many suites the gate ACTUALLY entered, counted here rather than
# inferred from the output.
#
# MEASURED 2026-10-23: counting the "=== name ===" headers in the gate's own
# stdout is what made a previous version of this check wrong. That count is
# taken AFTER the doctor's output has been echoed into the same stream on the
# doctor-red path, so it read the doctor's 6 headers as if they were suites.
# The counter is incremented inside Run itself, on every entry, including the
# early return when the backend is unreachable -- a suite the gate did not run
# must still be counted as one the gate did not run.
$script:ranSuites = 0

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
# The DENOMINATOR, read with the SAME discipline as the token above, and for
# the same reason: the number must be the last thing on its own line, so it
# cannot be forged by a suite's DECLARED verdict string printed earlier in the
# doctor's output. The regex is anchored at column 0 and ends at \s*$, so an
# appended number on a busy line does not match.
$declaredLines = @([regex]::Matches($doctorOut, '(?m)^DOCTOR_SUITES_DECLARED=(\d+)\s*$'))
$declaredSuites = $null
if ($declaredLines.Count -eq 1) {
  $declaredSuites = [int]$declaredLines[0].Groups[1].Value
} elseif ($declaredLines.Count -gt 1) {
  $doctorProblem = 'the doctor reported DOCTOR_SUITES_DECLARED ' + $declaredLines.Count + ' times -- the answer is ambiguous'
}
# How many of the declared suites the gate is supposed to run.
#
# The doctor prints one line per declared suite:
#   ok    <id>    registry=True  gate=True
# `registry` is the SPEC's own `in_gate` flag and `gate` is whether the id is
# in gate.ps1's Run list. The count of registry=True rows is therefore the
# number the DECLARATION says the gate owns, and it comes from the SPEC rather
# than from the gate's own Run list -- a check whose expected value comes from
# the thing it judges cannot fail (rule 15).
#
# The row is matched whole-line and the gate= value is captured, so a suite
# whose declared verdict string contains this text cannot forge a row.
$registryRows = @([regex]::Matches($doctorOut, '(?m)^\s+(?:ok|DRIFT)\s+(\S+)\s+registry=(True|False)\s+gate=(True|False)\s*$'))
$gateOwnedSuites = 0
foreach ($row in $registryRows) {
  if ($row.Groups[2].Value -eq 'True') { $gateOwnedSuites++ }
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
} elseif ($null -eq $declaredSuites) {
  # A doctor that could not be MEASURED is not a doctor that found nothing.
  # Same treatment as a missing DOCTOR_SUITES_PROBLEMS token.
  $doctorRed = $true
  $doctorReason = 'the doctor did not report DOCTOR_SUITES_DECLARED'
} elseif ($declaredSuites -le 0) {
  # The A-arm seal.
  #
  # MEASURED 2026-10-23, on this tree, with the doctor UNMODIFIED: with SPEC's
  # suites: block emptied and gate.ps1's 33 Run calls deleted, and no suite
  # files on disk, the doctor exits 0, prints DOCTOR_SUITES_PROBLEMS=0 and
  # DOCTOR_SUITES_DECLARED=0, and prints no ERROR line at all. The gate then
  # ran ZERO suites and reported "RED SUITES: 0", "GATE_STATE=complete" and
  # exit code 0. Two empty lists agreed with each other, so "I measured
  # nothing" was reported as "nothing is wrong" -- the gate's green had two
  # meanings, and nothing said which one this run was.
  #
  # The doctor's own EMPTY REGISTRY check (tools/pm.py:1640) catches this only
  # when there ARE suite files on disk; with none, the check's precondition is
  # false and the doctor stays green. This is the gate-side seal, and it does
  # not depend on that check existing.
  $doctorRed = $true
  $doctorReason = 'the doctor declared ' + $declaredSuites + ' suite(s) -- a registry of nothing is a measurement that was not taken, not a measurement of zero'
} elseif ($gateOwnedSuites -le 0) {
  # The doctor declared suites but printed no row saying the gate owns one.
  # That is a doctor whose output shape changed, not a gate that owns nothing,
  # and the two must not be allowed to look alike.
  $doctorRed = $true
  $doctorReason = 'the doctor declared ' + $declaredSuites + ' suite(s) but printed no registry row saying the gate runs one -- the count of suites to run could not be read'
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
# The gate points at ONE instance and the CALLER chooses it. There is no
# default: the port 5591 instance is forbidden, and a gate that silently falls
# back to a forbidden instance measures a process nobody authorised
# (tools/verify/AGENTS.md rule 17).
#
# The refusal is a RED, and it is a refusal to CHOOSE -- not a failure to
# connect. The gate is not allowed to pick an instance on the caller's behalf.
if (-not $env:GATE_BASE) {
  ">>> RED (GATE_BASE is not set: the gate refuses to choose an instance -- there is no default)"
  "RED_SUITES=1"
  "RED_SUITE=gate-base-unset"
  "GATE_STATE=refused"
  # The verdicts file is TRUNCATED here too: a gate that stops early must not
  # leave the PREVIOUS run's RED_SUITE names for a reader to believe.
  try {
    $vp = Join-Path $env:TEMP 'maplebench-gate-verdicts.txt'
    Set-Content -LiteralPath $vp -Value @('GATE_VERDICTS_FORMAT=1', 'RED_SUITES=1', 'RED_SUITE=gate-base-unset', 'GATE_STATE=refused') -Encoding ASCII
  } catch { }
  exit 1
}
$Base = $env:GATE_BASE
# A base URL without a scheme does not parse as a URI, so the port would come
# out empty and the suites would receive something they cannot use.
if ($Base -notmatch '^https?://') { $Base = 'http://' + $Base }
$Port = ([uri]$Base).Port
# The suites run IN THIS PROCESS -- the gate calls each suite as a file, not as
# a child process -- so the base URL has to be exported for them: the suites
# read $env:GATE_BASE themselves and refuse to run when it is unset.
$env:GATE_BASE = $Base

# --------------------------------------------------------------------------- #
# THE INSTANCE THE GATE IS POINTED AT, AND HOW FRESH IT IS.
#
# rule 17: "A judge pointed at an instance running a STALE binary is not a
# measurement of the tree."  The gate used to print NOTHING about which binary
# the instance was running, so a RED that came from an old instance read
# exactly like a RED that came from the tree, and the reader had no way to
# tell which one they were looking at.
#
# THE FIVE NUMBERS rule 17 asks for, and where each one comes from:
#   the pid                 Get-NetTCPConnection -State Listen -LocalPort <port>
#                           -> OwningProcess.  BY PORT, never by process NAME:
#                           name lookup returns the wrong process (rule 17).
#   the port it owns        the same query
#   its start time          Win32_Process.CreationDate for that pid
#   the binary's build time the LastWriteTime of the DLL that pid has LOADED
#   the commit count        commits in this tree whose COMMITTER date (%cI) is
#                           newer than that build time
#
# Every number is printed WITH ITS SOURCE, because a number without its source
# is a claim.
#
# THIS BLOCK PRINTS.  It does not change the verdict: the decision lines
# further down are not touched, and the exit code is not touched.  Whether a
# stale instance should be RED is the owner's decision -- this makes it
# VISIBLE.
#
# The function RETURNS its lines instead of printing them: a function that
# both prints and returns returns an ARRAY (rule 13), and the caller here
# wants one string.
function Get-GateInstanceReport {
  param([string]$BaseUrl, [int]$PortNumber)
  # A git query that cannot answer must say so.  git's fatal errors go to
  # stderr, and a swallowed stderr turns a git FAILURE into a count of ZERO
  # -- and a comparison that read zero lines is not a comparison (rule 19).
  # MEASURED 2026-10-25: an earlier version of this block built its
  # '--since=<stamp>' argument with a '+' INSIDE an array literal, and
  # PowerShell split it into TWO arguments.  git then failed with
  # "fatal: bad revision '2026-10-24T15:46:31+08:00'", the stderr was
  # swallowed, and the product-commit count read 0 -- a FALSE ZERO that said
  # the instance was fresh when one product commit was missing from it.
  function Get-GateGitLines([string[]]$GitArgs) {
    $all = @(& git @GitArgs 2>&1)
    $fatal = ''
    $lines = @()
    foreach ($item in $all) {
      if ($item -is [System.Management.Automation.ErrorRecord]) {
        $text = $item.Exception.Message
        if ($text -match 'fatal:') { $fatal += (' ' + $text) }
      } else {
        $lines += $item
      }
    }
    return @{ Lines = $lines; Fatal = $fatal.Trim() }
  }

  $out = @()
  $out += ('GATE_INSTANCE={0}' -f $BaseUrl)
  $out += ('GATE_INSTANCE_PORT={0}' -f $PortNumber)

  $procId = 0
  $pidSource = ''
  if ($PortNumber -gt 0) {
    try {
      $conns = @(Get-NetTCPConnection -State Listen -LocalPort $PortNumber -ErrorAction Stop)
      if ($conns.Count -ge 1) {
        $procId = [int]$conns[0].OwningProcess
        $pidSource = 'Get-NetTCPConnection -State Listen -LocalPort {0} -> OwningProcess' -f $PortNumber
      } else {
        $pidSource = 'Get-NetTCPConnection -State Listen -LocalPort {0} -> no listener' -f $PortNumber
      }
    } catch {
      $pidSource = 'Get-NetTCPConnection failed: ' + $_.Exception.Message
    }
  } else {
    $pidSource = 'the URL carries no port'
  }
  if ($procId -gt 0) {
    $out += ('GATE_INSTANCE_PID={0}' -f $procId)
  } else {
    $out += 'GATE_INSTANCE_PID=unknown'
  }
  $out += ('GATE_INSTANCE_PID_SOURCE={0}' -f $pidSource)

  # The start time of THAT process.
  $startText = 'unknown'
  $startSource = 'no pid'
  if ($procId -gt 0) {
    try {
      $wp = Get-CimInstance Win32_Process -Filter ('ProcessId={0}' -f $procId) -ErrorAction Stop
      if ($wp -and $wp.CreationDate) {
        $startText = ([datetime]$wp.CreationDate).ToString('yyyy-MM-dd HH:mm:ss')
        $startSource = 'Win32_Process.CreationDate for ProcessId={0}' -f $procId
      } else {
        $startSource = 'Win32_Process carried no CreationDate for ProcessId={0}' -f $procId
      }
    } catch {
      $startSource = 'Win32_Process failed: ' + $_.Exception.Message
    }
  }
  $out += ('GATE_INSTANCE_START={0}' -f $startText)
  $out += ('GATE_INSTANCE_START_SOURCE={0}' -f $startSource)

  # The binary.  The COMMAND LINE may hold a RELATIVE path ("MapleBenchX.dll"),
  # so the loaded module is the source that resolves to a real file.
  $dllPath = ''
  $dllSource = 'no pid'
  $cmdLine = ''
  if ($procId -gt 0) {
    try {
      $wp2 = Get-CimInstance Win32_Process -Filter ('ProcessId={0}' -f $procId) -ErrorAction Stop
      if ($wp2 -and $wp2.CommandLine) { $cmdLine = $wp2.CommandLine }
    } catch { }
    try {
      $proc = Get-Process -Id $procId -ErrorAction Stop
      foreach ($mod in @($proc.Modules)) {
        if ($mod.ModuleName -like 'MapleBenchX*') {
          $dllPath = $mod.FileName
          $dllSource = 'the loaded module {0} of ProcessId={1}' -f $mod.ModuleName, $procId
          break
        }
      }
      if ($dllPath -eq '') { $dllSource = 'no MapleBenchX module is loaded by ProcessId={0}' -f $procId }
    } catch {
      $dllSource = 'the module list could not be read: ' + $_.Exception.Message
    }
  }
  if ($dllPath -ne '') {
    $out += ('GATE_INSTANCE_BINARY={0}' -f $dllPath)
  } else {
    $out += 'GATE_INSTANCE_BINARY=unknown'
  }
  $out += ('GATE_INSTANCE_BINARY_SOURCE={0}' -f $dllSource)
  if ($cmdLine -ne '') {
    $out += ('GATE_INSTANCE_COMMAND_LINE={0}' -f $cmdLine)
  }

  # The binary's build time: the LastWriteTime of that file.
  $builtText = 'unknown'
  $builtSource = 'the binary path is unknown'
  if ($dllPath -ne '' -and (Test-Path -LiteralPath $dllPath -PathType Leaf)) {
    try {
      $built = (Get-Item -LiteralPath $dllPath -ErrorAction Stop).LastWriteTime
      $builtText = $built.ToString('yyyy-MM-dd HH:mm:ss')
      $builtSource = 'the LastWriteTime of {0}' -f $dllPath
    } catch {
      $builtSource = 'the build time could not be read: ' + $_.Exception.Message
    }
  }
  $out += ('GATE_INSTANCE_BINARY_BUILT={0}' -f $builtText)
  $out += ('GATE_INSTANCE_BINARY_BUILT_SOURCE={0}' -f $builtSource)

  # The tree, and the commit count between the binary and the tree.
  $headSha = 'unknown'
  $headDate = 'unknown'
  $newer = -1
  try {
    $headLine = @(& git -C $Root log -1 --format='%H|%cI' 2>$null)[0]
    if ($headLine -match '^([0-9a-fA-F]{40})\|(.+)$') {
      $headSha = $Matches[1]
      $headDate = $Matches[2]
    }
  } catch { }
  $out += ('GATE_TREE_HEAD={0}' -f $headSha)
  $out += ('GATE_TREE_HEAD_COMMITTED={0}' -f $headDate)
  $out += 'GATE_TREE_HEAD_SOURCE=git -C $Root log -1 --format=%H|%cI'

  $builtLocal = $null
  if ($builtText -ne 'unknown') {
    try { $builtLocal = [datetime]::Parse($builtText) } catch { $builtLocal = $null }
  }
  $gitFatal = ''

  # Every commit newer than the build.
  $newer = -1
  if ($builtLocal -ne $null -and $headDate -ne 'unknown') {
    $q = Get-GateGitLines @('-C', $Root, 'log', '--format=%cI')
    if ($q.Fatal -eq '') {
      $count = 0
      foreach ($stamp in $q.Lines) {
        try { if ([datetime]::Parse($stamp) -gt $builtLocal) { $count++ } } catch { }
      }
      $newer = $count
    } else {
      $gitFatal = $q.Fatal
    }
  }
  if ($newer -ge 0) {
    $out += ('GATE_COMMITS_AFTER_BINARY={0}' -f $newer)
  } else {
    $out += 'GATE_COMMITS_AFTER_BINARY=unknown'
  }
  $out += 'GATE_COMMITS_AFTER_BINARY_SOURCE=commits in this tree whose COMMITTER date (%cI) is newer than the binary''s build time' + $(if ($gitFatal -ne '') { ' [git said: ' + $gitFatal + ']' } else { '' })

  # The PRODUCT commits: the ones that can change the binary AT ALL.  The
  # binary is MapleBenchX.dll, built from MapleBench/ and dependencies/MapleLib/
  # (MapleBenchX.csproj's ProjectReference).  A commit that only touches a
  # document or the verify tooling cannot make that binary stale, and counting
  # it would make every instance look stale a few minutes after a docs commit
  # -- a false alarm that trains the reader to ignore the notice.
  $productPaths = @('MapleBench', 'dependencies/MapleLib')
  $productNewer = -1
  $newestProduct = ''
  if ($builtLocal -ne $null) {
    # The stamp is built in its OWN statement: an expression with a '+'
    # inside an array literal is split into two arguments by PowerShell.
    $sinceArg = '--since=' + $builtLocal.ToString('yyyy-MM-ddTHH:mm:sszzz')
    $q2 = Get-GateGitLines (@('-C', $Root, 'log', '--format=%H|%cI|%s', $sinceArg, '--') + $productPaths)
    if ($q2.Fatal -eq '') {
      $count = 0
      foreach ($line in $q2.Lines) {
        try {
          $fields = $line -split '\|', 3
          if ($fields.Count -ge 3 -and [datetime]::Parse($fields[1]) -gt $builtLocal) {
            $count++
            if ($newestProduct -eq '') {
              $newestProduct = ('{0} {1}' -f $fields[0].Substring(0, 7), $fields[2])
            }
          }
        } catch { }
      }
      $productNewer = $count
    } else {
      $gitFatal = ($gitFatal + ' ' + $q2.Fatal).Trim()
    }
  }
  if ($productNewer -ge 0) {
    $out += ('GATE_PRODUCT_COMMITS_AFTER_BINARY={0}' -f $productNewer)
  } else {
    $out += 'GATE_PRODUCT_COMMITS_AFTER_BINARY=unknown'
  }
  $out += 'GATE_PRODUCT_COMMITS_AFTER_BINARY_SOURCE=commits touching MapleBench/ or dependencies/MapleLib/ (the sources MapleBenchX.dll is built from) whose COMMITTER date (%cI) is newer than the binary''s build time' + $(if ($gitFatal -ne '') { ' [git said: ' + $gitFatal + ']' } else { '' })
  if ($newestProduct -ne '') {
    $out += ('GATE_NEWEST_PRODUCT_COMMIT={0}' -f $newestProduct)
  } else {
    $out += 'GATE_NEWEST_PRODUCT_COMMIT=none'
  }

  # Stale: the binary was NOT built from the product code the tree holds now.
  $stale = 'unknown'
  if ($productNewer -ge 0) {
    if ($productNewer -gt 0) { $stale = 'yes' } else { $stale = 'no' }
  }
  $out += ('GATE_INSTANCE_STALE={0}' -f $stale)
  $out += 'GATE_INSTANCE_STALE_SOURCE=the PRODUCT commit count above: the binary is stale when the tree holds product commits it was not built from'
  return ($out -join "`n")
}

# The notice itself.  It is NOT a '>>> RED' line: the decision lines further
# down judge each SUITE's output, and this line is about the INSTANCE.  A
# '>>> RED' here would be a verdict the exit code does not carry -- the very
# split the gate exists to avoid (rule 14: a verdict read from two places).
# Whether a stale instance should make the gate RED is the owner's decision.
$instanceReport = Get-GateInstanceReport -BaseUrl $Base -PortNumber $Port
Write-Output $instanceReport
if ($instanceReport -match '(?m)^GATE_INSTANCE_STALE=yes$') {
  $staleCount = ([regex]::Match($instanceReport, '(?m)^GATE_PRODUCT_COMMITS_AFTER_BINARY=(\d+)\s*$')).Groups[1].Value
  $staleBuilt = ([regex]::Match($instanceReport, '(?m)^GATE_INSTANCE_BINARY_BUILT=(.+)$')).Groups[1].Value.Trim()
  $staleNewest = ([regex]::Match($instanceReport, '(?m)^GATE_NEWEST_PRODUCT_COMMIT=(.+)$')).Groups[1].Value.Trim()
  Write-Output (">>> STALE (the instance at {0} is running a binary built {1}, and {2} PRODUCT commit(s) landed in this tree after that build -- the newest is {3}; the verdicts below are about THAT INSTANCE, not necessarily about this tree) -- instance {0}" -f $Base, $staleBuilt, $staleCount, $staleNewest)
  Write-Output 'GATE_INSTANCE_STALE_NOTICE=the instance is older than the tree'
} else {
  Write-Output 'GATE_INSTANCE_STALE_NOTICE=none'
}

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
  $script:ranSuites++
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
  $bad = @($RED | Where-Object { $text.Contains($_) })
  # The COUNT, not the literal 'n'.
  #
  # MEASURED 2026-10-24: the gate printed ">>> RED (BAD=n)" for two suites. The
  # 'n' was a placeholder that was never filled in, so the only number a reader
  # ever saw for a failing suite was a letter.
  #
  # THE BAD CHANNEL READS THE VERDICT LINE, NOT THE WHOLE OUTPUT.
  #
  # MEASURED 2026-10-24: sim-health was RED in the gate and GREEN when run on
  # its own, on the same instance, and the gate said "BAD=2" while the suite's
  # own last line said "BAD=0". The suite's OUTPUT EXPLAINING ITSELF mentions
  # the number: "the shipped judge still reported BAD=2 once (not attributed
  # to this block)". The old channel was `$text -match 'BAD=[1-9]'` over the
  # WHOLE output, so a sentence about a past run was read as this run's
  # verdict. A suite that passes was red for having written about a number.
  #
  # The suites' convention is a STANDALONE line: `print(f"\nBAD={bad}")`. So
  # the channel is narrowed to the lines that stand alone before the test, and
  # prose with a number in it is not a verdict. Nothing is weakened: a
  # standalone 'BAD=1' (contact-damage.py:120, the "this judge cannot run"
  # marker) still reddens, and the '>>> RED' channel above still reads the
  # whole output.
  #
  # THE SHAPE OF THIS LINE IS A CONTRACT, NOT A STYLE. pm.py's doctor reads the
  # gate's red channels out of gate.ps1 by finding the pattern test that ADDS
  # to $bad, and it takes the part of that pattern before the character class
  # as the marker a suite may declare ('BAD='). The doctor takes the FIRST
  # match in the file, so this comment must not spell that shape out -- a
  # commented copy of it is picked up in preference to the code.
  #
  # MEASURED 2026-10-24: a first attempt at this change put the zero-check on
  # a line of its own, so the doctor's pattern no longer matched the code, and
  # the doctor reported 20 suites whose declared marker the gate does not read.
  # The gate was fine and the DECLARATION was broken -- the same family of
  # defect this doctor exists to catch, caused by the doctor itself.
  #
  # [1-9]\d* is the non-zero count: 'BAD=0' is a suite that PASSED and must
  # not redden the gate, and 'BAD=12' is one suite with twelve problems, not
  # one suite with one. The character class is kept -- the doctor reads the
  # marker as the text before it, so a parenthesised group would hand it
  # 'BAD=(' instead of 'BAD='.
  $text = ($lines | Where-Object { $_ -match '^\s*BAD=\d+\s*$' }) -join "`n"
  if ($text -match 'BAD=[1-9]\d*') { $bad += 'BAD=' + $Matches[0].Substring('BAD='.Length) }
  if ($bad) { "   >>> RED ($($bad -join ', '))"; $script:fail++; $script:redSuites += $name }
  else { "   ok" }
  $verdict = $lines | Where-Object {
    $_ -match 'VERDICT|ALL LADDERS PASS|passed,|BAD=|PASS:' } | Select-Object -Last 3
  if ($bad) {
    # A RED suite's own evidence is printed too.
    #
    # MEASURED 2026-10-24: the gate printed the red line and NOTHING else, so
    # the only thing a reader could learn about a failing suite was that it
    # failed -- not WHICH assertion failed. The suites say which: they print
    # their own '>>>' marker on the assertion that did not hold. A gate that
    # swallows that turns a ten-minute diagnosis into a re-run with a print
    # statement added by hand.
    $markers = @($lines | Where-Object { $_.Trim().StartsWith('>>>') } | Select-Object -Last 6)
    if ($markers.Count -eq 0 -and -not $verdict) {
      $markers = @($lines | Select-Object -Last 4)
    }
    $markers | ForEach-Object { "      $($_.Trim())" }
    if ($verdict) { $verdict | ForEach-Object { "      $($_.Trim())" } }
  }
  elseif ($verdict) { $verdict | ForEach-Object { "   GREEN? $($_.Trim())" } }
  else { "   (read the tail):"; ($lines | Select-Object -Last 4) | ForEach-Object { "      $_" } }
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

# W-096. The empty-container creator: it makes a .wz and an .nx from nothing,
# opens both through the app's own open path, and measures the three refusals
# (no overwrite, no format/name mismatch, nothing written into the folder the
# open archives live in). The last one is what keeps the data root clean, so
# it is measured against the live folder rather than asserted in a comment.
Run 'empty-archive'   { python "$S\empty-archive.py" }
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
# Did the gate run what the declaration says it runs?
# --------------------------------------------------------------------------- #
#
# All three numbers are printed, always, so a reader never has to infer one
# from another.
#
# MEASURED 2026-10-24 on this tree: declared = 35 and the gate owns 34. The
# difference is save-audit, which SPEC.yaml marks `in_gate: false` because it
# copies an archive and cannot be run against a server that already has one
# open (the comment above says why). So the gate compares the number it RAN
# against the number it OWNS, not against the raw declared total: comparing
# 34 against 35 would redden a healthy tree every run, and a gate that cries
# wolf is worse than no gate.
"GATE_SUITES_DECLARED=$declaredSuites"
"GATE_SUITES_OWNED=$gateOwnedSuites"
"GATE_SUITES_RAN=$script:ranSuites"
if ($gateOwnedSuites -ne $script:ranSuites) {
  # Counted and named, exactly as the doctor path above counts and names
  # itself, so the number in "RED SUITES:" and the RED_SUITE= lines in the
  # verdicts file stay consistent with each other and with the exit code. A
  # reader who sees this name knows the failure is the gate's accounting, not
  # a suite's verdict.
  "   >>> RED (the gate ran $script:ranSuites suite(s) but the doctor says it owns $gateOwnedSuites -- the registry and the gate disagree about what the gate runs)"
  $script:fail++
  $script:redSuites += 'gate-suite-count'
}

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