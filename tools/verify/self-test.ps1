# self-test.ps1 -- known-answer regression suite for the verification tools.
#
# STATE.md requires: "before any measurement or verification tool goes live it
# must first pass a known-answer smoke test that shows it produces a non-zero
# and correct result."
#
# This file is that smoke test for the seven tools in this directory. Every
# case reproduces one of the six recorded tool failures, or the two new ones
# found while building the toolkit, and asserts that the tool now gives the
# RIGHT answer instead of the plausible wrong one.
#
# All fixtures are built at run time under the temp directory. This file
# contains no non-ASCII characters; the CJK characters used in the fixtures are
# produced from code points, which is also what makes the non-ASCII path case
# real.
#
# Exit: 0 when every case passes, 1 when any case fails.

[CmdletBinding()]
param(
    [switch] $KeepFixtures
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib\Common.ps1')

Initialize-Verification -Tool 'self-test' -Checked @(
    'every tool in this directory, against a fixture whose correct answer is known in advance',
    'including reproductions of the five recorded tool failures'
)

$script:Pass = 0
$script:Fail = 0
$script:Skip = 0
# Rule 10 sentinel: how many child exit codes this run actually OBSERVED.
# A harness that reads 0 for everything and one that reads the right code
# produce the same case list, so the count is printed and asserted below.
#
# $LASTEXITCODE is no longer used to read a child's exit code anywhere in
# this file. It is an AUTOMATIC variable, and WHICH copy of it a native
# call updates depends on how this script was entered: MEASURED, the
# -File form updates the function-scope copy (LOCAL=1 where the child
# exited 1) and the -Command form updates only the global copy
# (LOCAL=0, GLOBAL=1). The real code comes from ProcessStartInfo.ExitCode
# - see Invoke-Tool.
$script:ChildExitCaptured = 0
$script:ChildExitNonZero = 0

function Add-Case {
    param([string]$Id, [string]$Expectation, [bool]$Ok, [string]$Evidence)
    if ($Ok) {
        $script:Pass++
        Write-Output ("CASE {0}: PASS  ({1})" -f $Id, $Expectation)
    } else {
        $script:Fail++
        Write-Output ("CASE {0}: FAIL  ({1})" -f $Id, $Expectation)
    }
    if ($Evidence -ne '') { Write-Output ('      ' + $Evidence) }
}

function Add-Skip {
    param([string]$Id, [string]$Why)
    $script:Skip++
    Write-Output ("CASE {0}: SKIP  ({1})" -f $Id, $Why)
}

# A python tool that is not in this tree must not end the run, and must not
# pass either.  The case is RECORDED -- as a SKIP, carrying the reason it was
# skipped -- and the group keeps a case that ASSERTS the dependency is
# present, so a missing tool is a visible fact and never a silent pass.
# "SKIP is not PASS" is stated in the summary for the same reason: a skipped
# case is not a green one.
function Add-CaseOrSkip {
    param([string]$Id, [string]$Expectation, [hashtable]$Result,
          [bool]$Ok, [string]$Evidence)
    if ($Result.Missing) {
        Add-Skip -Id $Id -Why ('the tool is not in this tree: ' + $Result.Out)
        return
    }
    Add-Case -Id $Id -Expectation $Expectation -Ok $Ok -Evidence $Evidence
}

# Run a tool as a child process so the real exit code is observed.
#
# THE EXIT CODE COMES FROM ProcessStartInfo.ExitCode, NEVER FROM
# $LASTEXITCODE.
#
# $LASTEXITCODE is an AUTOMATIC variable, and WHICH copy a native call
# updates depends on how the script was entered. The old code read
# `$LASTEXITCODE` inside Invoke-Tool after `& $hostExe`. MEASURED on
# 2026-10-22, recording both copies immediately after the call:
#   -File form    : LOCAL=1 GLOBAL=1   (the child exited 1)
#   -Command form : LOCAL=0 GLOBAL=1   (the child exited 1)
# So in the -Command form the function-scope copy stayed 0 and the read
# returned that. EVERY case that expects a non-zero exit then read 0 and
# reported FAIL while the tool's own stdout proved it had done the right
# thing: 15 of 27 cases, all in the same direction, so a genuinely broken
# tool would have been judged PASS. It was SILENT - nothing said "I did
# not read an exit code".
#
# Invoke-CheckedProcess (lib/Common.ps1) already builds the child's
# environment explicitly - R2: an apphost does not inherit DOTNET_ROOT,
# and a child that dies in a second measures zero - and it returns the
# real ExitCode. Start-Process is NOT used: Common.ps1 says so, and
# Invoke-CheckedProcess is the helper that replaced it.
#
# The child's StdOut carries the tool's fields; StdErr is empty for these
# tools and is appended anyway so that nothing is dropped.
function Invoke-Tool {
    param([string]$Script, [string[]]$ToolArgs = @())
    $path = Join-Path $PSScriptRoot $Script
    if (-not [System.IO.File]::Exists($path)) { throw ('tool not found: ' + $path) }
    $hostExe = (Get-Process -Id $PID).Path
    $all = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $path) + $ToolArgs

    $r = Invoke-CheckedProcess -FilePath $hostExe -ArgumentList $all -TimeoutSeconds 600
    if ($r.LaunchError -ne '') { throw ('could not launch ' + $Script + ': ' + $r.LaunchError) }
    if ($r.TimedOut) { throw ($Script + ' timed out') }

    $script:ChildExitCaptured++
    $code = [int]$r.ExitCode
    if ($code -ne 0) { $script:ChildExitNonZero++ }

    $out = ([string]$r.StdOut)
    $err = ([string]$r.StdErr)
    if ($err.Trim() -ne '') { $out = $out + "`n" + $err }

    return @{ Exit = $code; Out = $out; Missing = $false }
}

function Get-ToolField {
    param([string]$Out, [string]$Field)
    foreach ($line in ($Out -split "`n")) {
        $t = $line.Trim()
        # skip prose and the [env] summary: only "FIELD=value" lines are fields
        if ($t -eq '' -or $t.StartsWith('[') -or $t.StartsWith('===') -or $t.StartsWith('CHECKED:')) { continue }
        if ($t.StartsWith($Field + '=')) { return $t.Substring($Field.Length + 1) }
    }
    return $null
}

function Get-ToolResult {
    param([string]$Out)
    foreach ($line in ($Out -split "`n")) {
        $t = $line.Trim()
        # the trailing "RESULT: PASS" line, or the "RESULT=PASS" field
        if ($t -match '^RESULT:\s*(PASS|FAIL|INCONCLUSIVE)\b') { return $Matches[1] }
        if ($t -match '^RESULT=(PASS|FAIL|INCONCLUSIVE)$') { return $Matches[1] }
    }
    return 'NONE'
}

# --- fixtures -------------------------------------------------------------
$cjkA = [string][char]0x6E2C + [string][char]0x8A66 + [string][char]0x8A66
$cjkB = [string][char]0x91CD + [string][char]0x65B0 + [string][char]0x6E2C
$root = Join-Path $env:TEMP ('vself-' + [System.Guid]::NewGuid().ToString('N').Substring(0, 8))
$repo = Join-Path $root $cjkA
$utf8Path = Join-Path $root ($cjkB + '.txt')
$concatPath = Join-Path $root 'concat-sample.cs'
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

New-Item -ItemType Directory -Path $repo -Force | Out-Null

$gitExe = (Get-Command git -ErrorAction SilentlyContinue)
$haveGit = ($null -ne $gitExe)
if ($haveGit) {
    $g = { param([string[]]$a) Invoke-CheckedProcess -FilePath $gitExe.Source -ArgumentList $a -WorkingDirectory $repo -TimeoutSeconds 60 }
    & $g @('init', '-q', '-b', 'main') | Out-Null
    & $g @('-c', 'user.email=vself@example.invalid', '-c', 'user.name=vself', '-c', 'core.autocrlf=false', 'commit', '--allow-empty', '-q', '-m', 'init') | Out-Null
    # tracked file, LF endings, committed
    $tracked = Join-Path $repo 'tracked.txt'
    $lines = @()
    for ($i = 1; $i -le 200; $i++) { $lines += ('line ' + $i) }
    [System.IO.File]::WriteAllText($tracked, (($lines -join "`n") + "`n"), $utf8NoBom)
    & $g @('-c', 'core.autocrlf=false', 'add', 'tracked.txt') | Out-Null
    & $g @('-c', 'user.email=vself@example.invalid', '-c', 'user.name=vself', '-c', 'core.autocrlf=false', 'commit', '-q', '-m', 'add tracked') | Out-Null
    # modified in the working tree, not staged
    $lines2 = @($lines)
    $lines2[0] = 'line 1 CHANGED'
    [System.IO.File]::WriteAllText($tracked, (($lines2 -join "`n") + "`n"), $utf8NoBom)
    # a second file, staged only
    $staged = Join-Path $repo 'staged.txt'
    [System.IO.File]::WriteAllText($staged, ("staged`n"), $utf8NoBom)
    & $g @('-c', 'core.autocrlf=false', 'add', 'staged.txt') | Out-Null
    # three untracked files, one of them with a CJK name
    [System.IO.File]::WriteAllText((Join-Path $repo 'untracked1.txt'), ("u1`n"), $utf8NoBom)
    [System.IO.File]::WriteAllText((Join-Path $repo 'untracked2.txt'), ("u2`n"), $utf8NoBom)
    [System.IO.File]::WriteAllText((Join-Path $repo ($cjkB + '-3.txt')), ("u3`n"), $utf8NoBom)
}

# UTF-8 without BOM, CJK content (incident 4)
$utf8Text = 'pre ' + [string][char]0x6E2C + [string][char]0x8A66 + ' ' + [string][char]0x89E3 + [string][char]0x78BC + ' post'
[System.IO.File]::WriteAllText($utf8Path, ($utf8Text + "`n"), $utf8NoBom)

# a message assembled from three concatenated literals over three lines (incident 5)
$concatText =
'        throw new InvalidOperationException(' + "`r`n" +
'            "Payload rejected: code=" +' + "`r`n" +
'            " after 3 retries");' + "`r`n"
[System.IO.File]::WriteAllText($concatPath, $concatText, $utf8NoBom)

Write-Output ("FIXTURE ROOT: {0}" -f $root)
Write-Output ''

# =========================================================================
# INCIDENT 3 + 1 : git status under-reporting, and the -like wildcard trap
# =========================================================================
if ($haveGit) {
    $r = Invoke-Tool -Script 'git-status-report.ps1' -ToolArgs @('-RepoRoot', $repo, '-ExpectUntracked', '3', '-ExpectStaged', '1', '-ExpectWorktree', '1', '-MaxList', '0')
    $untracked = Get-ToolField -Out $r.Out -Field 'UNTRACKED'
    $cross = Get-ToolField -Out $r.Out -Field 'CROSS_CHECK'
    Add-Case -Id 'INC3-untracked-count' -Expectation '3 untracked files reported as 3' `
        -Ok (($r.Exit -eq 0) -and ($untracked -eq '3') -and ($cross -eq 'AGREE')) `
        -Evidence ("exit=" + $r.Exit + " UNTRACKED=" + $untracked + " CROSS_CHECK=" + $cross)

    # The faulty idiom, run here on purpose so the regression is visible.
    $naive = Invoke-CheckedProcess -FilePath $gitExe.Source -ArgumentList @('status', '--porcelain') -WorkingDirectory $repo
    $naiveLines = @(([string]$naive.StdOut -split "`r?`n") | Where-Object { $_.Trim() -ne '' })
    $naiveFiltered = @($naiveLines | Where-Object { $_ -notlike '??*' })
    Add-Case -Id 'INC3-naive-idiom-is-broken' -Expectation 'the -notlike "??" idiom removes every record and under-reports' `
        -Ok ($naiveFiltered.Count -eq 0 -and $naiveLines.Count -gt 0) `
        -Evidence ("naive -notlike filter kept " + $naiveFiltered.Count + " of " + $naiveLines.Count + " records; the tool kept " + $untracked + " untracked")

    # A wrong expectation must FAIL loudly rather than be absorbed.
    $rBad = Invoke-Tool -Script 'git-status-report.ps1' -ToolArgs @('-RepoRoot', $repo, '-ExpectUntracked', '99')
    Add-Case -Id 'INC3-wrong-expectation-fails' -Expectation 'a stated untracked count that is wrong makes the tool exit 1' `
        -Ok ($rBad.Exit -eq 1) -Evidence ("exit=" + $rBad.Exit + ' ' + (Get-ToolResult -Out $rBad.Out))
} else {
    Add-Skip -Id 'INC3-*' -Why 'git is not available'
}

# =========================================================================
# INCIDENT 1 : a non-ASCII path inside a tool
# =========================================================================
if ($haveGit) {
    $rCjk = Invoke-Tool -Script 'git-status-report.ps1' -ToolArgs @('-RepoRoot', $repo, '-ExpectUntracked', '3', '-MaxList', '10')
    $foundCjk = ([string]$rCjk.Out).Contains($cjkB + '-3.txt')
    Add-Case -Id 'INC1-cjk-path-survives' -Expectation 'the CJK file name reaches the tool intact and is listed' `
        -Ok (($rCjk.Exit -eq 0) -and $foundCjk) `
        -Evidence ("exit=" + $rCjk.Exit + ' path_listed=' + $foundCjk)

    $rEolCjk = Invoke-Tool -Script 'eol-check.ps1' -ToolArgs @('-Path', (Join-Path $repo ($cjkB + '-3.txt')), '-RepoRoot', $repo)
    $cjkSummary = Get-ToolField -Out $rEolCjk.Out -Field 'SUMMARY'
    # The fixture holds "u3\n": exactly 1 bare-LF and no CRLF. The file is
    # untracked, so there is no HEAD version and the verdict is INCONCLUSIVE
    # rather than PASS -- but the census itself must be right.
    Add-Case -Id 'INC1-cjk-path-eol-check' -Expectation 'eol-check reads a CJK path and counts its endings correctly' `
        -Ok (([string]$cjkSummary).Contains('crlf=0 bare_lf=1')) `
        -Evidence ('summary=' + $cjkSummary + ' verdict=' + (Get-ToolResult -Out $rEolCjk.Out) + ' exit=' + $rEolCjk.Exit)
}

# =========================================================================
# INCIDENT 4 : UTF-8 without BOM
# =========================================================================
$rRead = Invoke-Tool -Script 'read-text-safe.ps1' -ToolArgs @('-Path', $utf8Path, '-ReportCodepoints')
$chars = Get-ToolField -Out $rRead.Out -Field 'CHARS'
$enc = Get-ToolField -Out $rRead.Out -Field 'ENCODING'
$expectedCodes = ((($utf8Text + "`n").ToCharArray() | ForEach-Object { [int]$_ }) -join ',')
$actualCodes = Get-ToolField -Out $rRead.Out -Field 'CODEPOINTS_FIRST'
# the fixture ends with a newline, so the decoded length is one more
$expectedChars = $utf8Text.Length + 1
Add-Case -Id 'INC4-strict-utf8-roundtrip' -Expectation ('the CJK text decodes to ' + $expectedChars + ' characters') `
    -Ok (($rRead.Exit -eq 0) -and ($enc -eq 'utf-8') -and ($chars -eq [string]$expectedChars) -and ($actualCodes -eq $expectedCodes)) `
    -Evidence ("exit=" + $rRead.Exit + ' ENCODING=' + $enc + ' CHARS=' + $chars + ' codes_match=' + ($actualCodes -eq $expectedCodes))

# What the naive reader would have done on this same file.
$naiveText = $null
try { $naiveText = Get-Content -LiteralPath $utf8Path -Raw } catch { $naiveText = $null }
$naiveOk = ($null -ne $naiveText) -and ($naiveText -ceq $utf8Text)
if ($naiveOk) {
    Add-Skip -Id 'INC4-naive-get-content' -Why ('the host shell decodes UTF-8 without BOM correctly (PowerShell ' + $PSVersionTable.PSVersion + '), so the wrong-answer cannot be reproduced here')
} else {
    Add-Case -Id 'INC4-naive-get-content-differs' -Expectation 'Get-Content with the default encoding loses characters on this file' `
        -Ok $true -Evidence ('Get-Content gave ' + (($naiveText.ToCharArray() | ForEach-Object { [int]$_ }) -join ',') + ' vs the correct ' + $expectedCodes)
}

# A file that is not valid UTF-8 must be a FAIL with an offset, never a
# silent replacement character.
$badPath = Join-Path $root 'invalid.bin'
[System.IO.File]::WriteAllBytes($badPath, [byte[]]@(0x41, 0x42, 0xE6, 0xB8, 0x41, 0x43))
$rBad2 = Invoke-Tool -Script 'read-text-safe.ps1' -ToolArgs @('-Path', $badPath)
$off = Get-ToolField -Out $rBad2.Out -Field 'ERROR_OFFSET'
Add-Case -Id 'INC4-invalid-utf8-is-fail' -Expectation 'invalid UTF-8 is reported as FAIL at byte offset 2, not decoded lossily' `
    -Ok (($rBad2.Exit -eq 1) -and ($off -eq '2')) `
    -Evidence ("exit=" + $rBad2.Exit + ' ERROR_OFFSET=' + $off)

# =========================================================================
# INCIDENT 5 : string concatenation across lines
# =========================================================================
$needle = 'Payload rejected: code= after 3 retries'
$rCat = Invoke-Tool -Script 'content-assert.ps1' -ToolArgs @('-Path', $concatPath, '-Needle', $needle)
$mode = Get-ToolField -Out $rCat.Out -Field ('MODE_' + $concatPath)
Add-Case -Id 'INC5-concat-needle-found' -Expectation 'a needle that exists only after concatenation is found' `
    -Ok (($rCat.Exit -eq 0) -and ($mode -ne 'NOT_FOUND')) `
    -Evidence ("exit=" + $rCat.Exit + ' mode=' + $mode + ' (the naive single-line search cannot see this string)')

$rMiss = Invoke-Tool -Script 'content-assert.ps1' -ToolArgs @('-Path', $concatPath, '-Needle', 'a token that is definitely not present anywhere')
Add-Case -Id 'INC5-absent-needle-fails' -Expectation 'an absent needle fails instead of being assumed present' `
    -Ok ($rMiss.Exit -eq 1) -Evidence ("exit=" + $rMiss.Exit)

$rNeg = Invoke-Tool -Script 'content-assert.ps1' -ToolArgs @('-Path', $concatPath, '-Needle', $needle, '-Negate')
Add-Case -Id 'INC5-negate-rejects-present' -Expectation '-Negate fails when the needle is present' `
    -Ok ($rNeg.Exit -eq 1) -Evidence ("exit=" + $rNeg.Exit)

# =========================================================================
# INCIDENT 2 : a process that cannot run must not produce zeros
# =========================================================================
$savedRoot = [string]$env:DOTNET_ROOT
try {
    $env:DOTNET_ROOT = ''
    $rNoEnv = Invoke-Tool -Script 'prebuild-check.ps1' -ToolArgs @('-Project', 'nothing.csproj')
    $envState = Get-ToolField -Out $rNoEnv.Out -Field 'DOTNET_ENV'
    Add-Case -Id 'INC2-prebuild-refuses-without-dotnet-root' -Expectation 'prebuild-check exits non-zero and names DOTNET_ROOT as the reason' `
        -Ok (($rNoEnv.Exit -ne 0) -and ($envState -eq 'MISSING') -and ((Get-ToolField -Out $rNoEnv.Out -Field 'CHECK_DOTNET_ROOT') -eq 'MISSING')) `
        -Evidence ("exit=" + $rNoEnv.Exit + ' DOTNET_ENV=' + $envState)

    $rNoEnv2 = Invoke-Tool -Script 'assert-measurement.ps1' -ToolArgs @('-Command', $env:ComSpec, '-CommandArgs', '/c', 'echo 1 2 3')
    $msg2 = Get-ToolField -Out $rNoEnv2.Out -Field 'MESSAGE'
    Add-Case -Id 'INC2-measurement-refuses-without-dotnet-root' -Expectation 'a measurement is refused by name rather than run and reported as zeros' `
        -Ok (($rNoEnv2.Exit -eq 1) -and ([string]$msg2).Contains('DOTNET_ROOT') -and ($null -eq (Get-ToolField -Out $rNoEnv2.Out -Field 'VERDICT'))) `
        -Evidence ("exit=" + $rNoEnv2.Exit + ' MESSAGE=' + $msg2 + ' (no VERDICT field: it refused instead of judging data)')
} finally {
    $env:DOTNET_ROOT = $savedRoot
}

# With DOTNET_ROOT present, a child that exits 1 must still FAIL.
if ($script:VerifyDotNet.Status -eq 'OK') {
    $rChildFail = Invoke-Tool -Script 'assert-measurement.ps1' -ToolArgs @('-Command', $env:ComSpec, '-CommandArgs', '/c', 'exit 1')
    Add-Case -Id 'INC2-failed-child-is-fail' -Expectation 'a child that exits non-zero fails even though the environment is good' `
        -Ok ($rChildFail.Exit -eq 1) -Evidence ("exit=" + $rChildFail.Exit + ' ' + (Get-ToolField -Out $rChildFail.Out -Field 'EXEC_EXIT'))

    $rChildEmpty = Invoke-Tool -Script 'assert-measurement.ps1' -ToolArgs @('-Command', $env:ComSpec, '-CommandArgs', '/c', 'exit 0')
    Add-Case -Id 'INC2-silent-child-is-fail' -Expectation 'a child that exits 0 and prints nothing fails instead of measuring zero' `
        -Ok ($rChildEmpty.Exit -eq 1) -Evidence ("exit=" + $rChildEmpty.Exit + ' ' + (Get-ToolField -Out $rChildEmpty.Out -Field 'MESSAGE'))
} else {
    Add-Skip -Id 'INC2-child-cases' -Why 'DOTNET_ROOT is not usable in this environment: ' + $script:VerifyDotNet.Reason
}

# =========================================================================
# Measurement degeneracy
# =========================================================================
$rZeros = Invoke-Tool -Script 'assert-measurement.ps1' -ToolArgs @('-Values', '0,0,0,0')
Add-Case -Id 'MEAS-all-zero-fails' -Expectation 'all-zero samples fail' `
    -Ok ($rZeros.Exit -eq 1) -Evidence ("exit=" + $rZeros.Exit + ' ' + (Get-ToolField -Out $rZeros.Out -Field 'REASON'))

$rConst = Invoke-Tool -Script 'assert-measurement.ps1' -ToolArgs @('-Values', '7,7,7,7')
Add-Case -Id 'MEAS-all-constant-fails' -Expectation 'all-identical samples fail' `
    -Ok ($rConst.Exit -eq 1) -Evidence ("exit=" + $rConst.Exit + ' ' + (Get-ToolField -Out $rConst.Out -Field 'REASON'))

$rOne = Invoke-Tool -Script 'assert-measurement.ps1' -ToolArgs @('-Values', '3')
Add-Case -Id 'MEAS-single-sample-inconclusive' -Expectation 'one sample cannot be called constant, so the answer is INCONCLUSIVE' `
    -Ok ($rOne.Exit -eq 2) -Evidence ("exit=" + $rOne.Exit)

$rGood = Invoke-Tool -Script 'assert-measurement.ps1' -ToolArgs @('-Values', '2348,87,100,3')
Add-Case -Id 'MEAS-healthy-passes' -Expectation 'a healthy set passes' `
    -Ok ($rGood.Exit -eq 0) -Evidence ("exit=" + $rGood.Exit + ' DISTINCT=' + (Get-ToolField -Out $rGood.Out -Field 'DISTINCT'))

# =========================================================================
# Line ending inflation
# =========================================================================
if ($haveGit) {
    # A dedicated committed file: rewriting its endings changes every byte
    # while changing no text, which is the whole-file rewrite this project
    # must never produce with an edit.
    $eolFile = Join-Path $repo 'eolclean.txt'
    $eolLines = @()
    for ($i = 1; $i -le 200; $i++) { $eolLines += ('row ' + $i) }
    [System.IO.File]::WriteAllText($eolFile, (($eolLines -join "`n") + "`n"), $utf8NoBom)
    & $g @('-c', 'core.autocrlf=false', 'add', 'eolclean.txt') | Out-Null
    & $g @('-c', 'user.email=vself@example.invalid', '-c', 'user.name=vself', '-c', 'core.autocrlf=false', 'commit', '-q', '-m', 'add eolclean') | Out-Null
    # now rewrite the SAME text with CRLF endings: identical text, different bytes
    $asCrLf = ((($eolLines -join "`n") + "`n") -replace "`n", "`r`n")
    [System.IO.File]::WriteAllText($eolFile, $asCrLf, $utf8NoBom)

    $rInfl = Invoke-Tool -Script 'eol-check.ps1' -ToolArgs @('-Path', $eolFile, '-RepoRoot', $repo)
    $inflSummary = Get-ToolField -Out $rInfl.Out -Field 'SUMMARY'
    Add-Case -Id 'EOL-crlf-rewrite-is-caught' -Expectation 'identical text rewritten with CRLF endings is detected as inflation and fails' `
        -Ok (($rInfl.Exit -eq 1) -and ([string]$inflSummary).Contains('text_equal=True') -and ([string]$inflSummary).Contains('eol_rewrite=True')) `
        -Evidence ("exit=" + $rInfl.Exit + ' :: ' + $inflSummary)

    # and a file whose TEXT really changed must not be reported as inflation
    $realChange = ((($eolLines -join "`n") + "`n"))
    $realChange = $realChange -replace 'row 200', 'row 200 EDITED'
    [System.IO.File]::WriteAllText($eolFile, $realChange, $utf8NoBom)
    $rReal = Invoke-Tool -Script 'eol-check.ps1' -ToolArgs @('-Path', $eolFile, '-RepoRoot', $repo)
    $realSummary = Get-ToolField -Out $rReal.Out -Field 'SUMMARY'
    Add-Case -Id 'EOL-real-edit-not-inflation' -Expectation 'a one-line content edit is not reported as inflation' `
        -Ok (($rReal.Exit -eq 0) -and ([string]$realSummary).Contains('text_equal=False')) `
        -Evidence ("exit=" + $rReal.Exit + ' :: ' + $realSummary)
} else {
    Add-Skip -Id 'EOL-*' -Why 'git is not available'
}

# mixed endings are a fact about this repository, not a failure
$probe = Join-Path $repo 'mixed.txt'
$mixed = "a`r`nb`r`nc`nd`ne`n"
[System.IO.File]::WriteAllText($probe, $mixed, $utf8NoBom)
$rMixed = Invoke-Tool -Script 'eol-check.ps1' -ToolArgs @('-Path', $probe, '-NoHeadCompare')
Add-Case -Id 'EOL-mixed-counted-not-failed' -Expectation '2 CRLF and 3 bare-LF are counted and reported without failing' `
    -Ok (($rMixed.Exit -eq 0) -and ((Get-ToolResult -Out $rMixed.Out) -eq 'PASS')) `
    -Evidence (Get-ToolField -Out $rMixed.Out -Field 'SUMMARY')

# =========================================================================
# ASCII discipline: a tool that contains non-ASCII cannot be trusted here
# =========================================================================
$nonAsciiFiles = @()
foreach ($f in @(Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.ps1' -File -Recurse)) {
    $bytes = [System.IO.File]::ReadAllBytes($f.FullName)
    for ($i = 0; $i -lt $bytes.Length; $i++) {
        if ($bytes[$i] -gt 0x7F) { $nonAsciiFiles += ($f.Name + ' at byte ' + $i); break }
    }
}
Add-Case -Id 'R1-all-tools-are-ascii' -Expectation 'no tool file contains a non-ASCII byte' `
    -Ok ($nonAsciiFiles.Count -eq 0) -Evidence ("offenders: " + ($nonAsciiFiles -join '; '))

# =========================================================================
# INCIDENT 12 : a renamed DTO field read as a missing property, counting 0
# =========================================================================
# The payload carries the name the DTO had BEFORE the rename, and the script
# reads the name it has AFTER. In PowerShell a property that is not there is
# $null, $null added to a counter is 0, and the run reports a number: "0
# candidates were excluded", while the truth was 924.
#
# The payload travels base64 so the case needs no fixture file and no quoting
# on a command line.
$inc12Json = '{"playerDamage":12,"status":{"hp":50,"maxHp":50},"mobs":[{"hp":30,"maxHp":40}],"excludedByOrientation":0}'
$inc12B64 = [System.Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($inc12Json))

# The direction that matters: the field the script is about to read is NOT in
# the payload, and the tool has to say so by name and exit non-zero.
$rInc12Missing = Invoke-Tool -Script 'require-fields.ps1' -ToolArgs @(
    '-JsonBase64', $inc12B64, '-Fields', 'excludedByNotForwardOriented', '-Context', 'the foothold probe')
$inc12Message = [string](Get-ToolField -Out $rInc12Missing.Out -Field 'MESSAGE')
Add-Case -Id 'INC12-renamed-field-fails' -Expectation 'a field the payload does not carry fails by name, instead of being counted as zero' `
    -Ok (($rInc12Missing.Exit -eq 1) -and $inc12Message.Contains('excludedByNotForwardOriented')) `
    -Evidence ("exit=" + $rInc12Missing.Exit + ' ' + $inc12Message)

# The other direction, and it is not optional: without it the case above is
# also satisfied by a tool that fails on everything. This one says the tool
# still passes on the fields that ARE there.
$rInc12Present = Invoke-Tool -Script 'require-fields.ps1' -ToolArgs @(
    '-JsonBase64', $inc12B64, '-Fields', 'playerDamage,status.hp,mobs[].hp')
Add-Case -Id 'INC12-present-fields-pass' -Expectation 'the fields the payload does carry pass, so the failure above is about the missing field and not about the tool' `
    -Ok ($rInc12Present.Exit -eq 0) `
    -Evidence ("exit=" + $rInc12Present.Exit + ' ' + [string](Get-ToolField -Out $rInc12Present.Out -Field 'MESSAGE'))

# =========================================================================
# Untracked product sources: only a clean clone can see them
# =========================================================================
# Three times this repository built in its working directory and would not
# build from a clean clone, because product sources were never added to git.
# A worktree cannot catch that -- the untracked .cs file sits next to its
# callers and everything compiles. So the fixture is a repository that DOES
# clone and DOES build, and the case asserts both that it passes and that a
# file present only in the working tree makes it fail.
#
# Needs both git and a usable DOTNET_ROOT, because the check builds for real.
if ($haveGit -and $script:VerifyDotNet.Status -eq 'OK') {
    $cloneRepo = Join-Path $root 'clonefixture'
    $cloneLib = Join-Path $cloneRepo 'lib'
    New-Item -ItemType Directory -Path $cloneLib -Force | Out-Null

    $miniProj =
        '<Project Sdk="Microsoft.NET.Sdk">' + "`n" +
        '  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>' + "`n" +
        '</Project>' + "`n"
    [System.IO.File]::WriteAllText((Join-Path $cloneLib 'Thing.csproj'), $miniProj)
    [System.IO.File]::WriteAllText((Join-Path $cloneLib 'Thing.cs'), "public class Thing { public int V => 1; }`n")

    $cg = { param([string[]]$a) Invoke-CheckedProcess -FilePath $gitExe.Source -ArgumentList $a -WorkingDirectory $cloneRepo -TimeoutSeconds 120 }
    & $cg @('init', '-q', '-b', 'main') | Out-Null
    & $cg @('-c', 'user.email=vself@example.invalid', '-c', 'user.name=vself', '-c', 'core.autocrlf=false', 'add', '-A') | Out-Null
    & $cg @('-c', 'user.email=vself@example.invalid', '-c', 'user.name=vself', '-c', 'core.autocrlf=false', 'commit', '-q', '-m', 'init') | Out-Null

    $rCloneOk = Invoke-Tool -Script 'clean-clone-check.ps1' -ToolArgs @(
        '-RepoRoot', $cloneRepo, '-Project', 'lib/Thing.csproj', '-SourceSubdir', 'lib', '-CloneParent', $root)
    Add-Case -Id 'CLONE-fully-tracked-builds' -Expectation 'a clone whose sources are all tracked builds and passes' `
        -Ok (($rCloneOk.Exit -eq 0) -and ((Get-ToolField -Out $rCloneOk.Out -Field 'CS_DELTA') -eq '0') `
              -and ((Get-ToolField -Out $rCloneOk.Out -Field 'BUILD_EXIT') -eq '0') `
              -and ((Get-ToolField -Out $rCloneOk.Out -Field 'CLONE_REMOVED') -eq 'True')) `
        -Evidence ("exit=" + $rCloneOk.Exit + ' CS_DELTA=' + (Get-ToolField -Out $rCloneOk.Out -Field 'CS_DELTA') +
                   ' BUILD_EXIT=' + (Get-ToolField -Out $rCloneOk.Out -Field 'BUILD_EXIT') +
                   ' helpers=' + (Get-ToolField -Out $rCloneOk.Out -Field 'HELPER_CALLS') +
                   ' (helpers > 0 proves the tool ran its own code, incident 13)')

    # A .cs file that exists in the working tree and not in the repository.
    # This is the exact defect class the gate exists for, and it does NOT make
    # the clone's build fail on its own -- only the census catches it.
    [System.IO.File]::WriteAllText((Join-Path $cloneLib 'Untracked.cs'), "public class Untracked { }`n")
    $rCloneBad = Invoke-Tool -Script 'clean-clone-check.ps1' -ToolArgs @(
        '-RepoRoot', $cloneRepo, '-Project', 'lib/Thing.csproj', '-SourceSubdir', 'lib', '-CloneParent', $root)
    $badDelta = Get-ToolField -Out $rCloneBad.Out -Field 'CS_DELTA'
    $badCloneBuild = Get-ToolField -Out $rCloneBad.Out -Field 'BUILD_EXIT'
    Add-Case -Id 'CLONE-untracked-source-is-caught' -Expectation 'a source file in the tree and not in the repository fails, even though the clone still builds' `
        -Ok (($rCloneBad.Exit -eq 1) -and ($badDelta -eq '1') -and ($badCloneBuild -eq '0')) `
        -Evidence ("exit=" + $rCloneBad.Exit + ' CS_DELTA=' + $badDelta + ' BUILD_EXIT=' + $badCloneBuild +
                   ' (BUILD_EXIT=0 with CS_DELTA=1 is the whole point: only the census sees it)')
} else {
    Add-Skip -Id 'CLONE-*' -Why ('needs git and a usable DOTNET_ROOT; git=' + $haveGit +
                                  ' dotnet=' + $script:VerifyDotNet.Status)
}

# =========================================================================
# CITATION CHECK : a checker that cannot go red is not a check
# =========================================================================
# citation-check.py is a Python tool, so it needs its own launcher.  The
# exit code comes from ProcessStartInfo.ExitCode through Invoke-CheckedProcess
# (rule 11) -- never from $LASTEXITCODE.
#
# Three cases.  The last two are a PAIR over ONE fixture that differ only
# in the spec: the same file, the same line, quoted text that IS on the line
# in one spec and is NOT in the other.  A tool that always exits 0 fails
# the red arm; a tool that always exits non-zero fails the green arm and
# the self-test arm.  Either arm on its own would be satisfied by a broken
# tool, which is the whole point of the control group.
function Invoke-PythonTool {
    param([string]$Script, [string[]]$ToolArgs = @())
    $path = Join-Path $PSScriptRoot $Script
    if (-not [System.IO.File]::Exists($path)) {
        # A tool that is not in this tree is a FACT to record, not a reason to
        # kill the run.  MEASURED 2026-10-23 (task-170, finding B1): the
        # census tool was not tracked, so in a fresh clone this line THREW, the
        # run died in the middle of the CENSUS group and printed no RESULT=
        # line at all -- the harness had no verdict to give, which is worse
        # than a red one.  The marker keeps every case in the list, where
        # Add-CaseOrSkip records it as a SKIP with a reason.
        return @{ Exit = 127; Out = ('>>> RED: tool not found: ' + $path); Missing = $true }
    }
    $py = (Get-Command python -ErrorAction SilentlyContinue)
    if ($null -eq $py) { throw 'python is not on PATH' }
    $r = Invoke-CheckedProcess -FilePath $py.Source -ArgumentList (@($path) + $ToolArgs) -TimeoutSeconds 600
    if ($r.LaunchError -ne '') { throw ('could not launch ' + $Script + ': ' + $r.LaunchError) }
    if ($r.TimedOut) { throw ($Script + ' timed out') }

    $script:ChildExitCaptured++
    $code = [int]$r.ExitCode
    if ($code -ne 0) { $script:ChildExitNonZero++ }

    $out = ([string]$r.StdOut)
    $err = ([string]$r.StdErr)
    if ($err.Trim() -ne '') { $out = $out + "`n" + $err }
    return @{ Exit = $code; Out = $out; Missing = $false }
}

$pyExe = (Get-Command python -ErrorAction SilentlyContinue)
$havePython = ($null -ne $pyExe)
if ($haveGit -and $havePython) {
    # A repository with ONE committed, CLEAN file.  The file must be clean:
    # a dirty file is reported unblessed, and an unblessed row is a defect,
    # so a dirty fixture would make the green arm red for the wrong reason.
    $citeRepo = Join-Path $root 'citerepo'
    New-Item -ItemType Directory -Path $citeRepo -Force | Out-Null
    $ccg = { param([string[]]$a) Invoke-CheckedProcess -FilePath $gitExe.Source -ArgumentList $a -WorkingDirectory $citeRepo -TimeoutSeconds 60 }
    & $ccg @('init', '-q', '-b', 'main') | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $citeRepo 'cited.cs'), ("the marker line`nsecond line`n"), $utf8NoBom)
    & $ccg @('-c', 'user.email=vself@example.invalid', '-c', 'user.name=vself', '-c', 'core.autocrlf=false', 'add', '-A') | Out-Null
    & $ccg @('-c', 'user.email=vself@example.invalid', '-c', 'user.name=vself', '-c', 'core.autocrlf=false', 'commit', '-q', '-m', 'add cited') | Out-Null

    $citeGood = Join-Path $root 'cite-good.json'
    $citeBad = Join-Path $root 'cite-bad.json'
    [System.IO.File]::WriteAllText($citeGood, '{"citations":[{"file":"cited.cs","line":1,"text":"the marker line"}]}', $utf8NoBom)
    [System.IO.File]::WriteAllText($citeBad, '{"citations":[{"file":"cited.cs","line":1,"text":"a line that is on no line at all"}]}', $utf8NoBom)

    $rCiteSelf = Invoke-PythonTool -Script 'citation-check.py' -ToolArgs @('--self-test')
    Add-CaseOrSkip -Result $rCiteSelf -Id 'CITE-self-test-is-green' `
        -Expectation 'the citation checker passes its own known-answer suite' `
        -Ok (($rCiteSelf.Exit -eq 0) -and ((Get-ToolField -Out $rCiteSelf.Out -Field 'CASES_FAIL') -eq '0')) `
        -Evidence ("exit=" + $rCiteSelf.Exit + ' CASES_PASS=' + (Get-ToolField -Out $rCiteSelf.Out -Field 'CASES_PASS') +
                   ' CASES_FAIL=' + (Get-ToolField -Out $rCiteSelf.Out -Field 'CASES_FAIL'))

    $rCiteGood = Invoke-PythonTool -Script 'citation-check.py' -ToolArgs @('--spec', $citeGood, '--repo', $citeRepo)
    Add-CaseOrSkip -Result $rCiteGood -Id 'CITE-a-correct-citation-is-green' `
        -Expectation 'a citation whose quoted text really is on the cited line passes' `
        -Ok (($rCiteGood.Exit -eq 0) -and ((Get-ToolField -Out $rCiteGood.Out -Field 'CITATION_OK') -eq '1')) `
        -Evidence ("exit=" + $rCiteGood.Exit + ' CITATION_OK=' + (Get-ToolField -Out $rCiteGood.Out -Field 'CITATION_OK'))

    $rCiteBad = Invoke-PythonTool -Script 'citation-check.py' -ToolArgs @('--spec', $citeBad, '--repo', $citeRepo)
    Add-CaseOrSkip -Result $rCiteBad -Id 'CITE-a-wrong-citation-goes-red' `
        -Expectation 'a citation whose quoted text is on no line at all fails' `
        -Ok (($rCiteBad.Exit -ne 0) -and ((Get-ToolField -Out $rCiteBad.Out -Field 'CITATION_NOT_FOUND') -eq '1')) `
        -Evidence ("exit=" + $rCiteBad.Exit + ' CITATION_NOT_FOUND=' + (Get-ToolField -Out $rCiteBad.Out -Field 'CITATION_NOT_FOUND'))
} else {
    Add-Skip -Id 'CITE-*' -Why ('needs git and python; git=' + $haveGit + ' python=' + $havePython)
}

# --- probe-instance-census ------------------------------------------------
# Four cases.  The last two exist because of a MEASURED asymmetry, not a guess.
#
# Measured by ablation on the census: its own --self-test DOES catch a PICKED
# condition that can never fire (4 mismatches) and an is_defaulted that is
# always False.  It does NOT catch the opposite direction -- widening the
# PICKED kind gate so the tool reports things that are not defects passes with
# ZERO mismatches.  So --self-test alone would wave through an over-reporting
# tool, which is the same species as every "quiet when broken" row in this
# directory's table.  ARM_DEVTOOLS is the arm that closes that direction: a
# hard-coded Chrome DevTools port is a browser the script launched, not an
# adopted instance, and a tool with the kind gate removed calls it PICKED.
#
# THE TOOL'S OWN PRESENCE IS A CASE (task-170, finding B1).  The census tool
# was not tracked, so in a fresh clone it is simply not there, and the
# harness used to THROW on it: the run died in the middle of this group and
# printed no RESULT= line at all, so the harness had no verdict to give --
# which is worse than a red one.  Now the group skips with a reason and this
# case says whether the tool the group measures with is in the tree, so a
# missing dependency is a visible FAIL rather than a silent gap.
$censusTool = Join-Path $PSScriptRoot 'probe-instance-census.py'
Add-Case -Id 'CENSUS-the-census-tool-is-present-in-this-tree' `
    -Expectation 'the census tool this group measures with is in the tree' `
    -Ok ([System.IO.File]::Exists($censusTool)) `
    -Evidence ('path=' + $censusTool + ' present=' + [System.IO.File]::Exists($censusTool) +
               ' (a missing tool is a FAIL here and a SKIP below, never a pass)')
if ($havePython) {
    # Get-ToolField anchors on StartsWith, so it can only reach a field that is
    # the FIRST on its line.  The census prints "SELFTEST_PASS=17  SELFTEST_FAIL=0"
    # on ONE line and "RESULT=UNPROVEN  (exit 2)" with a trailing note, so both
    # of those read as absent.  Measured: with Get-ToolField, two cases went red
    # on a tool that had answered correctly -- rule 3's "a case that fails for
    # the WRONG reason is not a test".  This reads the token after '=' anywhere
    # on the line, and says <absent> rather than returning null, so a field that
    # is genuinely missing is visible instead of silently comparing unequal.
    function Get-FieldToken {
        param([string]$Out, [string]$Field)
        $m = [regex]::Match($Out, '(?m)(?:^|\s)' + [regex]::Escape($Field) + '=(\S+)')
        if ($m.Success) { return $m.Groups[1].Value }
        return '<absent>'
    }

    $censusTree = Join-Path $root 'census-picks'
    New-Item -ItemType Directory -Path (Join-Path $censusTree 'tools/probe') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $censusTree 'MapleBench/tools') -Force | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $censusTree 'MapleBench/tools/soak.py'), "BASE=require_gate_base()`n", $utf8NoBom)
    # Five clean scripts, so the population clears the census's own refusal to
    # conclude from fewer than five (that refusal is case CENSUS-refuses-*).
    for ($i = 0; $i -lt 5; $i++) {
        [System.IO.File]::WriteAllText((Join-Path $censusTree ("tools/probe/pad$i.mjs")),
            "const server = process.argv[2];`nawait fetch(server + '/api/files');`n", $utf8NoBom)
    }
    # The one that MUST be reported: a defaulting instance, caller never names it.
    [System.IO.File]::WriteAllText((Join-Path $censusTree 'tools/probe/picks.mjs'),
        "const SERVER = process.env.WZ_SERVER ?? 'http://127.0.0.1:5590/api';`nawait fetch(SERVER);`n", $utf8NoBom)

    $rCensusSelf = Invoke-PythonTool -Script 'probe-instance-census.py' -ToolArgs @('--self-test')
    Add-CaseOrSkip -Result $rCensusSelf -Id 'CENSUS-self-test-is-green' `
        -Expectation 'the census passes its own known-answer suite' `
        -Ok (($rCensusSelf.Exit -eq 0) -and ((Get-FieldToken $rCensusSelf.Out 'SELFTEST_FAIL') -eq '0')) `
        -Evidence ("exit=" + $rCensusSelf.Exit + ' SELFTEST_PASS=' + (Get-FieldToken $rCensusSelf.Out 'SELFTEST_PASS') +
                   ' SELFTEST_FAIL=' + (Get-FieldToken $rCensusSelf.Out 'SELFTEST_FAIL'))

    $rCensusPick = Invoke-PythonTool -Script 'probe-instance-census.py' -ToolArgs @('--root', $censusTree, '--quiet')
    Add-CaseOrSkip -Result $rCensusPick -Id 'CENSUS-a-tree-that-picks-goes-red' `
        -Expectation 'a tree holding one script that defaults its instance is RED, not clean' `
        -Ok ($rCensusPick.Exit -ne 0) `
        -Evidence ("exit=" + $rCensusPick.Exit + ' picked_files_printed=' +
                   ([regex]::Matches($rCensusPick.Out, 'tools/probe/picks\.mjs')).Count)

    # THE BROKEN-DIRECTION ARM.  --self-test cannot supply this one: widening
    # the kind gate passes the census's own 17-fixture suite with 0 mismatches.
    $censusDev = Join-Path $root 'census-devtools'
    New-Item -ItemType Directory -Path (Join-Path $censusDev 'tools/probe') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $censusDev 'MapleBench/tools') -Force | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $censusDev 'MapleBench/tools/soak.py'), "BASE=require_gate_base()`n", $utf8NoBom)
    for ($i = 0; $i -lt 5; $i++) {
        [System.IO.File]::WriteAllText((Join-Path $censusDev ("tools/probe/pad$i.mjs")),
            "const server = process.argv[2];`nawait fetch(server + '/api/files');`n", $utf8NoBom)
    }
    # A hard-coded 9222 is the Chrome DevTools port.  This script launched that
    # browser; it is not adopting anybody's instance, so it must stay clean.
    # The port is bound to a numeric constant rather than written straight into
    # the URL, because a literal URL is EXPLICIT under the census's own rule and
    # would never be defaulted -- and then this case could not see the broken
    # direction at all.  Measured: with the literal-in-URL form, widening the
    # kind gate (A3) still left this case GREEN, so the case proved nothing.
    $devFixed = (@'
const PORT = 9222;
const list = await (await fetch(`http://127.0.0.1:${PORT}/json/list`)).json();
console.log(list);
'@) -replace "`r`n", "`n"
    [System.IO.File]::WriteAllText((Join-Path $censusDev 'tools/probe/devtools-fixed.mjs'), $devFixed, $utf8NoBom)

    $rCensusDev = Invoke-PythonTool -Script 'probe-instance-census.py' -ToolArgs @('--root', $censusDev, '--quiet')
    Add-CaseOrSkip -Result $rCensusDev -Id 'CENSUS-a-hardcoded-devtools-port-is-not-an-instance' `
        -Expectation 'a fixed Chrome DevTools port is a browser the script launched, so it must not be reported PICKED' `
        -Ok (($rCensusDev.Exit -eq 0) -and ((Get-FieldToken $rCensusDev.Out 'RESULT') -eq 'CLEAN')) `
        -Evidence ("exit=" + $rCensusDev.Exit + ' RESULT=' + (Get-FieldToken $rCensusDev.Out 'RESULT'))

    # F4: the audit's verdict used to be printed and thrown away, so the tool
    # said "treat any clean result as unproven" and then said RESULT=CLEAN.
    $censusTiny = Join-Path $root 'census-tiny'
    New-Item -ItemType Directory -Path (Join-Path $censusTiny 'tools/probe') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $censusTiny 'MapleBench/tools') -Force | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $censusTiny 'MapleBench/tools/soak.py'), "BASE=require_gate_base()`n", $utf8NoBom)
    [System.IO.File]::WriteAllText((Join-Path $censusTiny 'tools/probe/only.mjs'),
        "const server = process.argv[2];`nawait fetch(server);`n", $utf8NoBom)

    $rCensusTiny = Invoke-PythonTool -Script 'probe-instance-census.py' -ToolArgs @('--root', $censusTiny, '--quiet')
    Add-CaseOrSkip -Result $rCensusTiny -Id 'CENSUS-refuses-to-conclude-from-a-tiny-population' `
        -Expectation 'a population too small to conclude from reports UNPROVEN, never CLEAN' `
        -Ok (($rCensusTiny.Exit -ne 0) -and ((Get-FieldToken $rCensusTiny.Out 'RESULT') -eq 'UNPROVEN')) `
        -Evidence ("exit=" + $rCensusTiny.Exit + ' RESULT=' + (Get-FieldToken $rCensusTiny.Out 'RESULT'))
} else {
    Add-Skip -Id 'CENSUS-*' -Why ('needs python; python=' + $havePython)
}

# =========================================================================
# REGISTRY UNIQUENESS : a doctor that cannot see a duplicate is not a doctor
# =========================================================================
# pm.py's doctor is what the gate reads, so a defect the doctor cannot see
# is a defect the gate cannot see.  MEASURED 2026-10-23 (task-140, arm
# `dup-ladder`): a registry that declared the same row twice printed
# "doctor: 0 problem(s)" and "DOCTOR_SUITES_PROBLEMS=0" and the gate stayed
# green -- the duplicate was invisible to every check the doctor ran.
#
# THE FIXTURE.  Two registries built from the SAME source -- the repository's
# own pm/SPEC.yaml -- differing in ONE thing: the second carries a verbatim
# copy of the first row of the `suites:` block, so one id and one file are
# each declared twice.  A row copied VERBATIM keeps its own verdict and its
# own red_marker, so the copy cannot make the DECLARATION family red: the
# only family the defect arm can turn on is the one under test.
#
# The row block is found BY LINE INDEX, because a regex that spans a block
# of YAML has to carry its own proof that it captured the whole block, and a
# capture group that starts at a format string captures nothing.
#
# The fixture is written under the temp directory and the doctor is pointed
# at it with --spec: the working tree is not touched.
#
# The child's stdout carries CJK, so PYTHONIOENCODING is set in the PARENT
# (the child inherits the environment) and restored afterwards.
#
# GATE_BASE is set to a DEAD, well-formed instance (task-170, finding F2).
# The doctor refuses to run without one and exits 1, so with GATE_BASE
# cleared every arm of this group would exit 1 and the exit code could not
# MOVE between the arms -- the relative assertion below would have nothing
# to compare.  A dead port is the right value: the doctor takes its normal
# path, the connection is refused, the instance block only PRINTS (it adds
# nothing to any counter), and the fixture still does not depend on a live
# backend.
if ($havePython) {
    $repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
    $specSrc = Join-Path $repoRoot 'pm/SPEC.yaml'
    $prevIoEnc = $env:PYTHONIOENCODING
    $prevGateBase = $env:GATE_BASE
    $env:PYTHONIOENCODING = 'utf-8'
    $env:GATE_BASE = 'http://127.0.0.1:59997'
    $dupId = ''
    $dupFile = ''
    $ctlSpec = ''
    $dupSpec = ''
    $built = $false
    try {
        if (Test-Path -LiteralPath $specSrc) {
            $specRaw = [System.IO.File]::ReadAllText($specSrc, [System.Text.Encoding]::UTF8)
            $eol = if ($specRaw.Contains("`r`n")) { "`r`n" } else { "`n" }
            $specLines = @($specRaw -split "`r?`n")
            $suitesAt = -1
            for ($i = 0; $i -lt $specLines.Count; $i++) {
                if ($specLines[$i] -match '^suites:\s*$') { $suitesAt = $i; break }
            }
            $rowAt = -1
            if ($suitesAt -ge 0) {
                for ($i = $suitesAt + 1; $i -lt $specLines.Count; $i++) {
                    if ($specLines[$i] -match '^\s+- id:\s') { $rowAt = $i; break }
                }
            }
            if ($rowAt -ge 0) {
                $rowEnd = $rowAt + 1
                while ($rowEnd -lt $specLines.Count) {
                    $ln = $specLines[$rowEnd]
                    if (($ln -match '^\s+- id:\s') -or ($ln -match '^[A-Za-z_][A-Za-z0-9_]*:')) { break }
                    $rowEnd++
                }
                $rowBlock = @($specLines[$rowAt..($rowEnd - 1)])
                $dupId = ($rowBlock[0] -replace '^\s*- id:\s*', '').Trim().Trim('"')
                $fileLine = @($rowBlock | Where-Object { $_ -match '^\s+file:' } | Select-Object -First 1)
                if ($fileLine.Count -ge 1) { $dupFile = ($fileLine[0] -replace '^\s*file:\s*', '').Trim().Trim('"') }
                $ctlSpec = Join-Path $root 'registry-copy.yaml'
                $dupSpec = Join-Path $root 'registry-dup.yaml'
                [System.IO.File]::WriteAllBytes($ctlSpec, [System.IO.File]::ReadAllBytes($specSrc))
                $tail = @()
                if ($rowEnd -lt $specLines.Count) { $tail = @($specLines[$rowEnd..($specLines.Count - 1)]) }
                $newLines = @($specLines[0..($rowEnd - 1)]) + $rowBlock + $tail
                [System.IO.File]::WriteAllText($dupSpec, ($newLines -join $eol), $utf8NoBom)
                $built = (($dupId -ne '') -and ($dupFile -ne ''))
            }
        }
        if ($built) {
            $rCtl = Invoke-PythonTool -Script '../pm.py' -ToolArgs @('doctor', '--spec', $ctlSpec)
            $rDup = Invoke-PythonTool -Script '../pm.py' -ToolArgs @('doctor', '--spec', $dupSpec)
            $rRepo = Invoke-PythonTool -Script '../pm.py' -ToolArgs @('doctor')
            $ctlTok = Get-FieldToken $rCtl.Out 'DOCTOR_SUITES_PROBLEMS'
            $dupTok = Get-FieldToken $rDup.Out 'DOCTOR_SUITES_PROBLEMS'
            $repoTok = Get-FieldToken $rRepo.Out 'DOCTOR_SUITES_PROBLEMS'
            $ctlExit = $rCtl.Exit
            $dupExit = $rDup.Exit
            # The tokens are read as NUMBERS, and a token that is not a number
            # is a token that cannot be compared -- TryParse says so instead
            # of the cast raising and killing the whole run.
            $ctlNum = -1
            $dupNum = -1
            $ctlNumOk = [int]::TryParse($ctlTok, [ref]$ctlNum)
            $dupNumOk = [int]::TryParse($dupTok, [ref]$dupNum)
            $dupTokenGreater = ($ctlNumOk -and $dupNumOk -and ($dupNum -gt $ctlNum))

            # THE CONTROL.  A doctor that always reports a duplicate fails
            # here.
            #
            # WHAT THE NEXT CASE DOES NOT DO (task-170, finding F1).  The
            # comment here used to say "a copy mechanism that changed nothing
            # fails the next case".  That was FALSE.  MEASURED 2026-10-23:
            # with the doctor IGNORING --spec -- it reads the tree's own
            # registry whatever the copy says -- the copy arm and the
            # repository arm are then the SAME registry, so the relative case
            # below still reads equal and stays GREEN.  What catches a copy
            # that changed nothing is the DUPLICATE arm (the four cases after
            # it): the duplicated registry is a different FILE, a doctor that
            # ignores --spec finds no duplicate in the tree's registry, and
            # those cases go red.  Two cases cannot both be the control for
            # each other.
            Add-CaseOrSkip -Result $rCtl -Id 'DOC-REG-a-copy-of-the-registry-reports-no-duplicate' `
                -Expectation 'a registry that declares every id and every file once reports no duplicate' `
                -Ok ($rCtl.Out -notmatch 'DUPLICATE SUITE') `
                -Evidence ('exit=' + $rCtl.Exit + ' duplicate_lines=' +
                           ([regex]::Matches($rCtl.Out, 'DUPLICATE SUITE')).Count + ' token=' + $ctlTok)

            # The RELATIVE control: the copy must measure exactly what the
            # registry itself measures.  A relative assertion, so it holds
            # whether or not the repository's own registry is green today.
            Add-CaseOrSkip -Result $rCtl -Id 'DOC-REG-a-copy-of-the-registry-measures-the-same-as-the-registry' `
                -Expectation 'pointing the doctor at a byte-identical copy gives the same problem count as the registry itself' `
                -Ok (($ctlTok -ne '<absent>') -and ($repoTok -ne '<absent>') -and ($ctlTok -eq $repoTok)) `
                -Evidence ('copy_token=' + $ctlTok + ' repo_token=' + $repoTok +
                           ' copy_exit=' + $ctlExit + ' repo_exit=' + $rRepo.Exit)

            Add-CaseOrSkip -Result $rDup -Id 'DOC-REG-a-duplicated-suite-id-is-named' `
                -Expectation 'the doctor names the id that two rows declare' `
                -Ok ($rDup.Out -match ('ERROR\s+' + [regex]::Escape($dupId) + '\s+DUPLICATE SUITE ID')) `
                -Evidence ('id=' + $dupId + ' exit=' + $dupExit)

            Add-CaseOrSkip -Result $rDup -Id 'DOC-REG-a-duplicated-suite-file-is-named' `
                -Expectation 'the doctor names the file that two ids declare' `
                -Ok ($rDup.Out -match ('DUPLICATE SUITE FILE\s+' + [regex]::Escape($dupFile))) `
                -Evidence ('file=' + $dupFile + ' exit=' + $dupExit)

            # THE RELATIVE CONTROL, MEASURED AGAINST THE CONTROL ARM.
            # These two cases used to assert ABSOLUTES ("exit != 0" and
            # "token >= 1"), and an absolute is satisfied by ANY unrelated
            # problem: with the tree already red, a doctor that sees no
            # duplicate at all still passes both.  MEASURED 2026-10-23 at
            # this tree: the doctor reports one problem of its own (the
            # data-root-untouched invariant), so the copy arm is red BEFORE
            # the duplicate is added and the EXIT CODE cannot move.  The
            # graded token can move: it counts problems, so the duplicated
            # registry reports STRICTLY MORE of them than the identical copy
            # does.  Both relations are RELATIVE to the control arm, and
            # neither is satisfied by a doctor that sees no duplicate at all.
            Add-CaseOrSkip -Result $rDup -Id 'DOC-REG-a-duplicated-registry-makes-the-doctor-red' `
                -Expectation 'the duplicated registry makes the doctor WORSE than the byte-identical copy: a different exit code, or a strictly larger problem token' `
                -Ok (($dupExit -ne $ctlExit) -or $dupTokenGreater) `
                -Evidence ('copy_exit=' + $ctlExit + ' dup_exit=' + $dupExit +
                           ' copy_token=' + $ctlTok + ' dup_token=' + $dupTok +
                           ' (relative to the control arm; the absolute exit!=0 was satisfied by any unrelated problem)')

            Add-CaseOrSkip -Result $rDup -Id 'DOC-REG-the-problem-token-counts-the-duplicate' `
                -Expectation 'the problem token is STRICTLY greater on the duplicated registry than on the byte-identical copy' `
                -Ok $dupTokenGreater `
                -Evidence ('copy_token=' + $ctlTok + ' dup_token=' + $dupTok +
                           ' dup_exit=' + $dupExit + ' (relative to the control arm)')
        } else {
            Add-Skip -Id 'DOC-REG-*' -Why 'pm/SPEC.yaml has no `suites:` block with a row to duplicate'
        }
    } finally {
        if ($null -eq $prevIoEnc) { Remove-Item Env:PYTHONIOENCODING -ErrorAction SilentlyContinue }
        else { $env:PYTHONIOENCODING = $prevIoEnc }
        if ($null -eq $prevGateBase) { Remove-Item Env:GATE_BASE -ErrorAction SilentlyContinue }
        else { $env:GATE_BASE = $prevGateBase }
    }
} else {
    Add-Skip -Id 'DOC-REG-*' -Why ('needs python; python=' + $havePython)
}

# Rule 10 sentinel: prove the harness observed the children's REAL exit
# codes, not a constant zero.
#
# Without this, "the exit code was read and it was 0" and "the exit code
# was never read" are the same thing on the page: every case that expects
# a non-zero exit fails in both. Counting the children whose exit code was
# NON-ZERO is what separates them - if the exit-code pipeline were still
# broken, this count would be 0 and the case below would say so instead of
# letting fifteen red cases stand in for the proof.
Add-Case -Id 'HARNESS-child-exit-codes-were-observed' `
    -Expectation 'the harness read the real exit codes of its children, non-zero ones included' `
    -Ok (($script:ChildExitCaptured -gt 0) -and ($script:ChildExitNonZero -gt 0)) `
    -Evidence ('children=' + $script:ChildExitCaptured + ' non-zero=' + $script:ChildExitNonZero +
               ' (a non-zero count is what proves the codes were read, not defaulted)')

# --- summary --------------------------------------------------------------
Write-Output ''
Write-Output ('SELF TEST: pass={0} fail={1} skip={2}' -f $script:Pass, $script:Fail, $script:Skip)
# SKIP IS NOT PASS.  A skipped case is a case that did not RUN, so the count
# above is not a green count -- it says how many cases were ANSWERED.  Every
# skip carries its reason (in the case list above), and this line says so in
# the summary, so "not measured" cannot be read as "measured and fine"
# (task-170, finding B1).
if ($script:Skip -gt 0) {
    Write-Output ('NOTE: SKIP IS NOT PASS -- ' + $script:Skip + ' case(s) were SKIPPED, so they are NOT counted as passing; the reason for each is in the case list above.')
}
Set-Field 'CASES_PASS' ([string]$script:Pass)
Set-Field 'CASES_FAIL' ([string]$script:Fail)
Set-Field 'CASES_SKIP' ([string]$script:Skip)
Set-Field 'CHILD_EXIT_CAPTURED' ([string]$script:ChildExitCaptured)
Set-Field 'CHILD_EXIT_NONZERO' ([string]$script:ChildExitNonZero)
Set-Field 'SUMMARY' ('pass=' + $script:Pass + ' fail=' + $script:Fail + ' skip=' + $script:Skip)

if (-not $KeepFixtures) {
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
} else {
    Write-Output ('FIXTURES KEPT AT: ' + $root)
}

if ($script:Fail -gt 0) {
    Complete-Verification -Result 'FAIL' -Message ([string]$script:Fail + ' case(s) failed; a tool in this directory does not do what it claims')
}
Complete-Verification -Result 'PASS' -Message 'every known-answer case behaved as specified'