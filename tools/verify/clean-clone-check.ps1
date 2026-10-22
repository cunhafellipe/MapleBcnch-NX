# clean-clone-check.ps1 -- does a clean clone of HEAD actually build?
#
# WHY THIS EXISTS
#
# Three times now, this repository built perfectly in its working directory and
# would not build from a clean clone, because product sources were never added
# to version control:
#
#   * Services/Simulate/ -- 12 files, 11,559 lines, missing until 16c19e5
#   * tools/verify/ gates and 27 suites -- living in an agent scratch cache
#   * 19 product .cs files -- missing at the time this tool was written
#
# Every one was found by a person, after the fact. A clone carries only what is
# tracked, so it is the only thing that can see this class of defect, and a
# worktree cannot: an untracked .cs file sitting next to its callers makes the
# worktree compile perfectly while the repository is broken.
#
# `git worktree` is deliberately NOT used here. A worktree carries untracked
# files across, which is exactly the population this tool exists to exclude.
#
# WHAT IT CHECKS
#
#   1. a clean clone of HEAD exists and its .cs census is taken
#   2. the .cs census of the working tree, for comparison
#   3. MapleBench/MapleBenchX.csproj builds inside the clone
#   4. the two censuses agree
#
# A clone that builds but a census that disagrees is still a FAILURE: a source
# file that is in the tree and not in the repository is a file that is about to
# disappear, whether or not today's build needs it.
#
# Exit: 0 PASS  1 FAIL  2 INCONCLUSIVE  3 TOOL_ERROR
#
# ASCII ONLY (incident 1). Every path arrives as a parameter; none is embedded.

[CmdletBinding()]
param(
    [string] $RepoRoot = '',
    [string] $CloneParent = '',
    [string] $Project = 'MapleBench/MapleBenchX.csproj',
    [string] $SourceSubdir = 'MapleBench',
    [int]    $BuildTimeoutSeconds = 600,
    [switch] $KeepClone
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib\Common.ps1')

Initialize-Verification -Tool 'clean-clone-check' -Checked @(
    'a clean clone of HEAD exists (git clone --no-hardlinks, never git worktree, because a worktree carries untracked files across)',
    'the .cs source census of the clone and of the working tree, so an untracked product source cannot hide',
    'that the project builds inside the clone, with the SDK invoked by absolute path',
    'that the two censuses agree'
)

function Add-Remediation { param([string]$Line) Add-Detail ('REMEDIATION: ' + $Line) }

# Incident 13: a helper can be shadowed by a built-in alias (Alias > Function >
# Cmdlet) and the script keeps printing numbers. Two habits prevent it: a
# verb-noun name no alias can take, and a sentinel that proves the helper ran.
$script:HelperCalls = 0
$script:Sentinel = 'CLEAN-CLONE-CHECK-HELPERS-RAN'

function Invoke-CloneStep {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [string[]]$ArgumentList = @(),
        [string]$WorkingDirectory = '',
        [int]$TimeoutSeconds = 300
    )
    $script:HelperCalls++
    if ([string]::IsNullOrWhiteSpace($WorkingDirectory)) {
        return (Invoke-CheckedProcess -FilePath $FilePath -ArgumentList $ArgumentList -TimeoutSeconds $TimeoutSeconds)
    }
    return (Invoke-CheckedProcess -FilePath $FilePath -ArgumentList $ArgumentList -WorkingDirectory $WorkingDirectory -TimeoutSeconds $TimeoutSeconds)
}

function Get-SourceCensus {
    # Counts .cs under a subdirectory, EXCLUDING bin/ and obj/. Anything in
    # those is build output and is not source.
    param([Parameter(Mandatory = $true)][string]$Root, [Parameter(Mandatory = $true)][string]$Subdir)
    $script:HelperCalls++
    $target = Join-Path $Root $Subdir
    if (-not [System.IO.Directory]::Exists($target)) {
        # A missing directory is NOT "zero files". Incident 12: a missing thing
        # and a zero look identical downstream. This throws rather than
        # returning 0.
        throw ('no such source directory: ' + $target)
    }
    $all = @(Get-ChildItem -LiteralPath $target -Filter '*.cs' -File -Recurse -ErrorAction Stop)
    $kept = @($all | Where-Object {
        $parts = $_.FullName -split '[\\/]'
        -not ($parts -contains 'bin') -and -not ($parts -contains 'obj')
    })
    return $kept.Count
}

function Remove-CloneTree {
    # Deletes a directory this tool created, and reports whether it is gone.
    #
    # Retries, and clears the read-only attribute first. A 534 MB clone of this
    # repository came back as an EMPTY directory after a single
    # Directory.Delete(recursive) -- the files went, the directory did not, and
    # the exception that followed was treated as "cleanup failed". Retrying and
    # then trying the non-recursive overload is what turns that into "gone".
    param([Parameter(Mandatory = $true)][string]$Path)
    $script:HelperCalls++
    if (-not [System.IO.Directory]::Exists($Path)) { return $true }

    for ($attempt = 1; $attempt -le 3; $attempt++) {
        try {
            foreach ($f in @(Get-ChildItem -LiteralPath $Path -Recurse -File -Force -ErrorAction SilentlyContinue)) {
                if ($f.IsReadOnly) { $f.IsReadOnly = $false }
            }
            [System.IO.Directory]::Delete($Path, $true)
        } catch {
            if ($attempt -eq 3) { Add-Detail ('CLONE CLEANUP: ' + $_.Exception.GetType().Name + ' -- ' + $_.Exception.Message) }
        }
        if (-not [System.IO.Directory]::Exists($Path)) { return $true }
        # Files gone but the directory survived: the non-recursive overload
        # removes an empty one, which is exactly the state left behind above.
        try { [System.IO.Directory]::Delete($Path, $false) } catch { }
        if (-not [System.IO.Directory]::Exists($Path)) { return $true }
        Start-Sleep -Milliseconds 250
    }
    return (-not [System.IO.Directory]::Exists($Path))
}

# --- 0. preconditions -------------------------------------------------------
if ([string]::IsNullOrWhiteSpace($RepoRoot)) { $RepoRoot = Resolve-RepoRoot }
Set-Field 'REPO_ROOT' $RepoRoot
Set-Field 'PROJECT' $Project
Set-Field 'SOURCE_SUBDIR' $SourceSubdir

if (-not [System.IO.Directory]::Exists($RepoRoot)) {
    Set-Field 'REPO_ROOT_EXISTS' 'NO'
    Complete-Verification -Result 'FAIL' -Message ('repository root does not exist: ' + $RepoRoot)
}
Set-Field 'REPO_ROOT_EXISTS' 'YES'

$gitExe = $null
try { $gitExe = Get-Command git -ErrorAction Stop } catch { $gitExe = $null }
if ($null -eq $gitExe) {
    Set-Field 'GIT_STATUS' 'MISSING'
    Complete-Verification -Result 'FAIL' -Message 'git is not available, so no clean clone can be made and this tool cannot measure anything'
}
Set-Field 'GIT_STATUS' 'OK'
Set-Field 'GIT_PATH' ([string]$gitExe.Source)

$dn = $script:VerifyDotNet
Set-Field 'DOTNET_STATUS' $dn.Status
if ($dn.Status -ne 'OK') {
    Complete-Verification -Result 'FAIL' -Message ('the SDK cannot be invoked by absolute path: ' + $dn.Reason)
}
$dotnetExe = [System.IO.Path]::Combine($dn.Root, 'dotnet.exe')
Set-Field 'DOTNET_EXE' $dotnetExe
if (-not [System.IO.File]::Exists($dotnetExe)) {
    Set-Field 'DOTNET_EXE_EXISTS' 'NO'
    Complete-Verification -Result 'FAIL' -Message ('DOTNET_ROOT does not contain dotnet.exe: ' + $dotnetExe)
}
Set-Field 'DOTNET_EXE_EXISTS' 'YES'

# --- 1. the clone ------------------------------------------------------------
# A GUID, never a fixed name: two runs on one machine must not share a
# directory, and a stale directory from a killed run must never be mistaken for
# a fresh clone.
if ([string]::IsNullOrWhiteSpace($CloneParent)) { $CloneParent = $env:TEMP }
$clonePath = Join-Path $CloneParent ('clean-clone-' + [System.Guid]::NewGuid().ToString('N').Substring(0, 10))
Set-Field 'CLONE_PATH' $clonePath

$fail = $false

$clone = Invoke-CloneStep -FilePath $gitExe.Source -ArgumentList @(
    'clone', '--no-hardlinks', '--quiet', $RepoRoot, $clonePath) -TimeoutSeconds 600

if ($clone.TimedOut) {
    Set-Field 'CLONE_EXIT' 'TIMEOUT'
    Complete-Verification -Result 'FAIL' -Message 'git clone timed out; the tree may be too large for a full clone'
}
Set-Field 'CLONE_EXIT' ([string]$clone.ExitCode)
if ($clone.LaunchError -ne '') {
    Set-Field 'CLONE_LAUNCH_ERROR' $clone.LaunchError
    Complete-Verification -Result 'TOOL_ERROR' -Message ('git could not be launched: ' + $clone.LaunchError)
}
if ($clone.ExitCode -ne 0) {
    Add-Detail ('CLONE: FAIL -- git clone exited ' + $clone.ExitCode)
    Add-Detail ('CLONE STDERR: ' + [string]$clone.StdErr)
    Complete-Verification -Result 'FAIL' -Message 'git clone failed; nothing was measured'
}
if (-not [System.IO.Directory]::Exists($clonePath)) {
    Set-Field 'CLONE_EXISTS' 'NO'
    Complete-Verification -Result 'FAIL' -Message ('git clone reported success but the directory is not there: ' + $clonePath)
}
Set-Field 'CLONE_EXISTS' 'YES'
Add-Detail ('CLONE: OK -- ' + $clonePath)

# The clone must be a repository in its own right, and it must be at the commit
# the caller's HEAD names. A clone that silently landed somewhere else would
# make every number below describe the wrong tree.
$rev = Invoke-CloneStep -FilePath $gitExe.Source -ArgumentList @('rev-parse', 'HEAD') -WorkingDirectory $clonePath -TimeoutSeconds 120
$cloneHead = ([string]$rev.StdOut).Trim()
Set-Field 'CLONE_HEAD' $cloneHead

$srcRev = Invoke-CloneStep -FilePath $gitExe.Source -ArgumentList @('rev-parse', 'HEAD') -WorkingDirectory $RepoRoot -TimeoutSeconds 120
$srcHead = ([string]$srcRev.StdOut).Trim()
Set-Field 'SOURCE_HEAD' $srcHead

if ($cloneHead -eq '' -or $srcHead -eq '') {
    Complete-Verification -Result 'TOOL_ERROR' -Message 'git rev-parse produced no commit id; the clone or the source tree is not a repository'
}
$headMatch = ($cloneHead -eq $srcHead)
Set-Field 'HEADS_MATCH' ([string]$headMatch)
if (-not $headMatch) {
    Add-Detail ('CLONE HEAD: ' + $cloneHead)
    Add-Detail ('SOURCE HEAD: ' + $srcHead)
    $fail = $true
}

# --- 2. source census -------------------------------------------------------
$csClone = -1
$csTree = -1
try {
    $csClone = Get-SourceCensus -Root $clonePath -Subdir $SourceSubdir
    Set-Field 'CS_CLONE' ([string]$csClone)
    Add-Detail ('CS_CLONE: ' + $csClone)
} catch {
    Set-Field 'CS_CLONE' 'ERROR'
    Add-Detail ('CS_CLONE: ERROR -- ' + $_.Exception.Message)
    $fail = $true
}

try {
    $csTree = Get-SourceCensus -Root $RepoRoot -Subdir $SourceSubdir
    Set-Field 'CS_WORKTREE' ([string]$csTree)
    Add-Detail ('CS_WORKTREE: ' + $csTree)
} catch {
    Set-Field 'CS_WORKTREE' 'ERROR'
    Add-Detail ('CS_WORKTREE: ERROR -- ' + $_.Exception.Message)
    $fail = $true
}

if ($csClone -ge 0 -and $csTree -ge 0) {
    $delta = $csTree - $csClone
    Set-Field 'CS_DELTA' ([string]$delta)
    if ($delta -ne 0) {
        Add-Detail ('CS_CENSUS: FAIL -- the working tree has ' + $csTree + ' source files and the clone has ' +
                    $csClone + ', a difference of ' + $delta)
        Add-Detail '  A source file that exists in the tree and not in the repository is a file that disappears on anyone else''s checkout.'
        Add-Remediation 'git add the missing product sources, then commit. Do not add build output.'
        $fail = $true
    } else {
        Add-Detail ('CS_CENSUS: OK -- ' + $csClone + ' source files in both')
    }
} else {
    Set-Field 'CS_DELTA' 'UNKNOWN'
    $fail = $true
}

# --- 3. build inside the clone ----------------------------------------------
# Two traps, both measured on this machine:
#   * `dotnet` on PATH is 8.0.425 and reports NETSDK1045. DOTNET_ROOT alone is
#     not enough. The SDK is therefore invoked by ABSOLUTE path.
#   * UseSharedCompilation left VBCSCompiler holding MapleLib/obj, and a second
#     build in the same session failed on a locked obj directory.
$buildArgs = @(
    'build', $Project,
    '-c', 'Release',
    '-v', 'minimal',
    '--nologo',
    '-p:UseSharedCompilation=false'
)
Set-Field 'BUILD_COMMAND' ($dotnetExe + ' ' + ($buildArgs -join ' '))

$build = Invoke-CloneStep -FilePath $dotnetExe -ArgumentList $buildArgs -WorkingDirectory $clonePath -TimeoutSeconds $BuildTimeoutSeconds

if ($build.LaunchError -ne '') {
    Set-Field 'BUILD_LAUNCH_ERROR' $build.LaunchError
    Add-Detail ('BUILD: TOOL_ERROR -- could not launch: ' + $build.LaunchError)
    Complete-Verification -Result 'TOOL_ERROR' -Message ('the SDK could not be launched: ' + $build.LaunchError)
}

$buildOut = [string]$build.StdOut
$buildErr = [string]$build.StdErr

# The compiler's own error lines, verbatim. Incident 5: never re-derive an
# error from a regex the compiler did not write -- print what it said.
$errLines = @(($buildOut + "`n" + $buildErr) -split "`r?`n" | Where-Object { $_ -match ':\s*error\s+(CS|MSB|NETSDK)\d+' })
Set-Field 'BUILD_ERROR_COUNT' ([string]$errLines.Count)
foreach ($l in ($errLines | Select-Object -First 40)) { Add-Detail ('BUILD ERROR: ' + $l.Trim()) }
if ($errLines.Count -gt 40) { Add-Detail ('BUILD ERROR: ... and ' + ($errLines.Count - 40) + ' more') }

if ($build.TimedOut) {
    Set-Field 'BUILD_EXIT' 'TIMEOUT'
    Add-Detail ('BUILD: TIMEOUT after ' + $BuildTimeoutSeconds + 's')
    Complete-Verification -Result 'FAIL' -Message ('the clone build timed out after ' + $BuildTimeoutSeconds + ' seconds')
}
Set-Field 'BUILD_EXIT' ([string]$build.ExitCode)

if ($build.ExitCode -ne 0) {
    Add-Detail ('BUILD: FAIL -- dotnet build exited ' + $build.ExitCode + ' inside the clean clone')
    Add-Detail ('BUILD STDOUT: ' + (($buildOut -split "`r?`n" | Select-Object -Last 40) -join ' | '))
    Add-Remediation 'the sources these errors name are in the working tree but not in the repository: git add them and commit.'
    Add-Remediation 'if the errors are unrelated to missing files, this clone is failing for its own reason and the message above says which.'
    $fail = $true
} else {
    Add-Detail ('BUILD: OK -- dotnet build exited 0 inside the clean clone')
}

# --- 4. helpers actually ran ------------------------------------------------
# Incident 13: prove it, do not assume it. A tool that silently did nothing and
# exited 0 is the exact shape this directory keeps failing in.
Set-Field 'HELPER_CALLS' ([string]$script:HelperCalls)
Set-Field 'HELPER_SENTINEL' $script:Sentinel
$helpersOk = ($script:HelperCalls -ge 2) -and ($script:Sentinel -ceq 'CLEAN-CLONE-CHECK-HELPERS-RAN')
if (-not $helpersOk) {
    Add-Detail ('HELPERS: FAIL -- sentinel=' + $script:Sentinel + ' calls=' + $script:HelperCalls)
    $fail = $true
} else {
    Add-Detail ('HELPERS: OK -- ' + $script:HelperCalls + ' helper invocations recorded')
}

# --- 5. clean up ------------------------------------------------------------
# The clone is a temp directory this tool created; removing it disturbs
# nothing. Nothing inside the source repository is ever deleted.
if ($KeepClone) {
    Set-Field 'CLONE_REMOVED' 'NO'
    Set-Field 'CLONE_CLEANUP' 'SKIPPED'
    Add-Detail ('CLONE KEPT AT: ' + $clonePath)
} else {
    # The clone is a temp directory this tool created; removing it disturbs
    # nothing, and nothing inside the source repository is ever deleted.
    #
    # A leftover is reported loudly and does NOT change the verdict. The
    # verdict answers one question -- does a clean clone carry every source and
    # build -- and a directory the OS would not release is not an answer to
    # that. Reporting FAIL here would be this tool inventing a defect that does
    # not exist, which is the failure mode this directory keeps producing.
    $removed = Remove-CloneTree -Path $clonePath
    Set-Field 'CLONE_REMOVED' ([string]$removed)
    if ($removed) {
        Set-Field 'CLONE_CLEANUP' 'OK'
        Add-Detail 'CLONE CLEANUP: OK -- the temp clone was removed'
    } else {
        Set-Field 'CLONE_CLEANUP' 'LEFTOVER'
        Add-Detail ('CLONE CLEANUP: the temp clone is still at ' + $clonePath)
        Add-Remediation ('remove it by hand: Remove-Item -LiteralPath "' + $clonePath + '" -Recurse -Force')
        Add-Detail '  This does not affect the verdict above.'
    }
}

# --- verdict ----------------------------------------------------------------
if ($fail) {
    Complete-Verification -Result 'FAIL' -Message 'a clean clone of HEAD does not reproduce the working tree, or does not build'
}
Complete-Verification -Result 'PASS' -Message 'a clean clone of HEAD builds and carries every product source the working tree has'