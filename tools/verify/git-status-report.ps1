# git-status-report.ps1 -- trustworthy git working-tree report.
#
# Replaces the inline commands that reported 35 untracked files as 0.
# That came from:  git status --porcelain | Where-Object { $_ -notlike '??*' }
# '?' and '*' are WILDCARDS in PowerShell's -like operator, so '??*' matches
# every two-or-more-character string and the filter removed everything.
# This tool compares the two status characters for string equality and never
# uses -like on a status line.
#
# Rules: ASCII only, every path is a parameter, DOTNET_ROOT is self-checked
# first, and a parse failure is INCONCLUSIVE rather than a smaller number.
#
# Exit: 0 PASS  1 FAIL  2 INCONCLUSIVE  3 TOOL_ERROR

[CmdletBinding()]
param(
    [string] $RepoRoot = '',
    [int]    $ExpectUntracked = -1,
    [int]    $ExpectStaged = -1,
    [int]    $ExpectWorktree = -1,
    [int]    $MaxList = 40,
    [switch] $RequireClean
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib\Common.ps1')

Initialize-Verification -Tool 'git-status-report' -Checked @(
    'porcelain v1 -z record count, classified by exact XY string equality',
    'independent cross-check against git ls-files --others and git diff'
)

# --- locate repo ---------------------------------------------------------
if ([string]::IsNullOrWhiteSpace($RepoRoot)) { $RepoRoot = Resolve-RepoRoot }
if ([string]::IsNullOrWhiteSpace($RepoRoot) -or -not (Test-Path -LiteralPath $RepoRoot)) {
    Set-Field 'REPO_ROOT' ([string]$RepoRoot)
    Complete-Verification -Result 'INCONCLUSIVE' -Message 'no git work tree could be located; pass -RepoRoot'
}

$gitCmd = Get-Command git -ErrorAction SilentlyContinue
if ($null -eq $gitCmd) {
    Set-Field 'REPO_ROOT' $RepoRoot
    Complete-Verification -Result 'INCONCLUSIVE' -Message 'git is not on PATH'
}
Set-Field 'GIT'   $gitCmd.Source
Set-Field 'REPO_ROOT' $RepoRoot

$top = Invoke-CheckedProcess -FilePath $gitCmd.Source -ArgumentList @('-C', $RepoRoot, 'rev-parse', '--show-toplevel')
if ($top.ExitCode -ne 0 -or $top.TimedOut) {
    Complete-Verification -Result 'INCONCLUSIVE' -Message ('git rev-parse failed: ' + ($top.StdErr -replace '\s+', ' '))
}
Set-Field 'WORK_TREE' ([string]$top.StdOut).Trim()

$head = Invoke-CheckedProcess -FilePath $gitCmd.Source -ArgumentList @('-C', $RepoRoot, 'rev-parse', '--short', 'HEAD')
Set-Field 'HEAD' (([string]$head.StdOut).Trim())
Set-Field 'HEAD_PRESENT' ([string](($head.ExitCode -eq 0) -and (([string]$head.StdOut).Trim() -ne '')))

# --- raw status ----------------------------------------------------------
$st = Invoke-CheckedProcess -FilePath $gitCmd.Source `
        -ArgumentList @('-C', $RepoRoot, '-c', 'core.quotepath=false', 'status', '--porcelain=v1', '-z', '--untracked-files=all')
if ($st.TimedOut) { Complete-Verification -Result 'INCONCLUSIVE' -Message 'git status timed out' }
if ($st.ExitCode -ne 0) {
    Complete-Verification -Result 'INCONCLUSIVE' -Message ('git status exit=' + $st.ExitCode + ' ' + ($st.StdErr -replace '\s+', ' '))
}
Set-Field 'STDOUT_BYTES' ([string]([System.Text.Encoding]::UTF8.GetByteCount([string]$st.StdOut)))

# --- parse ---------------------------------------------------------------
# Porcelain v1 -z: records are NUL separated, never quoted.
# Record  = <XY><space><path>.  For R/C the original path follows as its own
# NUL terminated field.  Anything that does not fit that shape is a parse
# failure: reporting fewer files than git reported is worse than reporting
# nothing at all.
$untracked = New-Object System.Collections.ArrayList
$staged    = New-Object System.Collections.ArrayList
$worktree  = New-Object System.Collections.ArrayList
$malformed = 0

$fields = ([string]$st.StdOut).Split([char]0)
$i = 0
$recordCount = 0
while ($i -lt $fields.Length) {
    $f = $fields[$i]
    if ($f -eq '') { $i++; continue }
    $recordCount++
    if ($f.Length -lt 4) {
        $malformed++
        Add-Detail ('MALFORMED RECORD: ' + $f)
        $i++
        continue
    }
    $xy = $f.Substring(0, 2)
    $x = $xy[0]
    $y = $xy[1]
    $path = $f.Substring(3)

    if ($x -eq 'R' -or $x -eq 'C' -or $y -eq 'R' -or $y -eq 'C') {
        if ($i + 1 -ge $fields.Length -or $fields[$i + 1] -eq '') {
            $malformed++
            Add-Detail ('RENAME RECORD WITHOUT ORIGINAL PATH: ' + $f)
        } else {
            Add-Detail ('RENAME ' + $xy + ' ' + $path + ' (from ' + $fields[$i + 1] + ')')
            $i++
        }
    }

    # exact string equality -- never -like, never a wildcard
    if ($xy -eq '??') {
        [void]$untracked.Add($path)
    } else {
        if ($x -ne ' ' -and $x -ne '?' -and $x -ne '!') { [void]$staged.Add($path + ' [' + $xy + ']') }
        if ($y -ne ' ' -and $y -ne '?' -and $y -ne '!') { [void]$worktree.Add($path + ' [' + $xy + ']') }
    }
    $i++
}

Set-Field 'CLASSIFIER'    'exact-string-equality on the two status characters (no -like wildcard)'
Set-Field 'RECORDS'       ([string]$recordCount)
Set-Field 'MALFORMED'     ([string]$malformed)
Set-Field 'UNTRACKED'     ([string]$untracked.Count)
Set-Field 'STAGED'        ([string]$staged.Count)
Set-Field 'WORKTREE'      ([string]$worktree.Count)

# git collapses an untracked directory into one entry by default and into one
# entry per file with --untracked-files=all. Both numbers are reported because
# a "35 files" figure and a "3336 files" figure can both be correct.
$stCollapsed = Invoke-CheckedProcess -FilePath $gitCmd.Source `
                -ArgumentList @('-C', $RepoRoot, 'status', '--porcelain=v1', '-z')
$collapsed = 0
if ($stCollapsed.ExitCode -eq 0) {
    foreach ($x in ([string]$stCollapsed.StdOut).Split([char]0)) { if ($x -ne '') { $collapsed++ } }
}
Set-Field 'RECORDS_COLLAPSED'      ([string]$collapsed)
Set-Field 'UNTRACKED_COLLAPSED'    ([string]$collapsed)
Set-Field 'UNTRACKED_FILES_EXACT'  ([string]$untracked.Count)
Set-Field 'WORKTREE_DIRTY' ([string](($untracked.Count + $staged.Count + $worktree.Count) -gt 0))

# --- independent cross-checks -------------------------------------------
# A count that is not confirmed by a second, differently shaped command is
# not a result.  This is what incident 3 lacked.
$others = Invoke-CheckedProcess -FilePath $gitCmd.Source `
            -ArgumentList @('-C', $RepoRoot, 'ls-files', '--others', '--exclude-standard', '-z')
$otherCount = 0
if ($others.ExitCode -eq 0) {
    $of = ([string]$others.StdOut).Split([char]0)
    foreach ($x in $of) { if ($x -ne '') { $otherCount++ } }
}
$dfCached = Invoke-CheckedProcess -FilePath $gitCmd.Source -ArgumentList @('-C', $RepoRoot, 'diff', '--cached', '--name-only', '-z')
$cachedCount = 0
if ($dfCached.ExitCode -eq 0) { foreach ($x in ([string]$dfCached.StdOut).Split([char]0)) { if ($x -ne '') { $cachedCount++ } } }
$dfWork = Invoke-CheckedProcess -FilePath $gitCmd.Source -ArgumentList @('-C', $RepoRoot, 'diff', '--name-only', '-z')
$workCount = 0
if ($dfWork.ExitCode -eq 0) { foreach ($x in ([string]$dfWork.StdOut).Split([char]0)) { if ($x -ne '') { $workCount++ } } }

Set-Field 'XCHECK_UNTRACKED_LS_FILES' ([string]$otherCount)
Set-Field 'XCHECK_STAGED_DIFF_CACHED'  ([string]$cachedCount)
Set-Field 'XCHECK_WORKTREE_DIFF'       ([string]$workCount)

$agree = ($malformed -eq 0) -and
         ($untracked.Count -eq $otherCount) -and
         ($staged.Count -eq $cachedCount) -and
         ($worktree.Count -eq $workCount)
Set-Field 'CROSS_CHECK' $(if ($agree) { 'AGREE' } else { 'DISAGREE' })

# --- lists ---------------------------------------------------------------
if ($untracked.Count -gt 0 -and $MaxList -gt 0) {
    Add-Detail ("UNTRACKED ({0}):" -f $untracked.Count)
    $n = 0
    foreach ($p in $untracked) { if ($n -ge $MaxList) { Add-Detail ('  ... ' + ($untracked.Count - $n) + ' more'); break }
        Add-Detail ('  ' + $p); $n++ }
}
if ($staged.Count -gt 0) {
    Add-Detail ("STAGED ({0}):" -f $staged.Count)
    $n = 0
    foreach ($p in $staged) { if ($n -ge $MaxList) { Add-Detail ('  ... ' + ($staged.Count - $n) + ' more'); break }
        Add-Detail ('  ' + $p); $n++ }
}
if ($worktree.Count -gt 0) {
    Add-Detail ("WORKTREE ({0}):" -f $worktree.Count)
    $n = 0
    foreach ($p in $worktree) { if ($n -ge $MaxList) { Add-Detail ('  ... ' + ($worktree.Count - $n) + ' more'); break }
        Add-Detail ('  ' + $p); $n++ }
}

# --- verdict -------------------------------------------------------------
if (-not $agree) {
    Complete-Verification -Result 'INCONCLUSIVE' -Message 'status counts and the independent cross-checks disagree; no count is reported as fact'
}
$fail = $false
if ($ExpectUntracked -ge 0 -and $untracked.Count -ne $ExpectUntracked) { $fail = $true; Add-Detail ("EXPECTATION FAILED: untracked expected " + $ExpectUntracked + " got " + $untracked.Count) }
if ($ExpectStaged -ge 0 -and $staged.Count -ne $ExpectStaged) { $fail = $true; Add-Detail ("EXPECTATION FAILED: staged expected " + $ExpectStaged + " got " + $staged.Count) }
if ($ExpectWorktree -ge 0 -and $worktree.Count -ne $ExpectWorktree) { $fail = $true; Add-Detail ("EXPECTATION FAILED: worktree expected " + $ExpectWorktree + " got " + $worktree.Count) }
if ($RequireClean -and ($untracked.Count + $staged.Count + $worktree.Count) -gt 0) {
    $fail = $true; Add-Detail 'REQUIRE CLEAN: working tree is not clean'
}
if ($fail) { Complete-Verification -Result 'FAIL' -Message 'a stated expectation was not met' }
Complete-Verification -Result 'PASS' -Message 'working tree classified and confirmed by three independent counts'