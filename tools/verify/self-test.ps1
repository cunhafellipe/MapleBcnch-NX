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

$LASTEXITCODE = 0
$script:Pass = 0
$script:Fail = 0
$script:Skip = 0

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

# Run a tool as a child process so the real exit code is observed.
function Invoke-Tool {
    param([string]$Script, [string[]]$ToolArgs = @())
    $path = Join-Path $PSScriptRoot $Script
    if (-not [System.IO.File]::Exists($path)) { throw ('tool not found: ' + $path) }
    $hostExe = (Get-Process -Id $PID).Path
    $all = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $path) + $ToolArgs
    $global:LASTEXITCODE = 0
    $out = & $hostExe @all 2>&1
    $code = $LASTEXITCODE
    return @{ Exit = $code; Out = (($out | ForEach-Object { [string]$_ }) -join "`n") }
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

# --- summary --------------------------------------------------------------
Write-Output ''
Write-Output ('SELF TEST: pass={0} fail={1} skip={2}' -f $script:Pass, $script:Fail, $script:Skip)
Set-Field 'CASES_PASS' ([string]$script:Pass)
Set-Field 'CASES_FAIL' ([string]$script:Fail)
Set-Field 'CASES_SKIP' ([string]$script:Skip)
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