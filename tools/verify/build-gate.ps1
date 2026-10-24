# build-gate.ps1 -- build the product the way the gate needs it.
#
# Own-design: there is no C++ counterpart for this. What it obeys is in this
# repository:
#
#   R-14  Stop the server before building: it holds MapleBenchX.dll.
#   R-16  No CJK path in a script. The repository root is DISCOVERED, and the
#         project path is passed to the child as a RELATIVE argument with the
#         root as the working directory, so the non-ASCII path never passes
#         through a command line (the trick gate.ps1 uses).
#   R-17  The tripwire digest is read from pm/SPEC.yaml, never hard-coded.
#   T-0   A stale server on another port answers exactly like a current one.
#   T-13  The running server locks the output dll; MSB3027 reads like a
#         compile error and is not one.
#   T-14  A stale artifact looks like a build difference: the previous output
#         is kept and DIFFED, and an extra file is a question, not a failure.
#   T-20  The built dll must be NEWER than the newest product source.
#
# ASCII only: do not add non-ASCII characters to this file.
#
# Exit codes: 0 built, 1 refused (something was wrong before or during the
# build -- the tool that cannot measure fails loudly, rule 1 of
# tools/verify/AGENTS.md).

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib\Common.ps1')
. (Join-Path $PSScriptRoot 'lib\Project.ps1')
# The console has to be UTF-8 before the SDK child is started. Without it the
# child writes the build summary in the machine's OEM code page (950 on this
# machine) and the summary comes back as mojibake -- the localized
# "0 warnings / 0 errors" line becomes unreadable bytes. It also decides the
# encoding the child inherits.
Initialize-ConsoleEncoding
# The module path is pinned BEFORE any cmdlet is used, so a chain started from
# another PowerShell edition cannot take `Get-FileHash` away (see
# Initialize-PowerShellModulePath in lib/Common.ps1).
Initialize-PowerShellModulePath

$Root = Resolve-RepoRoot -StartPath $PSScriptRoot
if (-not $Root) {
    Write-Output '>>> RED: the repository root could not be found'
    exit 1
}

$sdk = Get-ProjectScalar -RepoRoot $Root -Key 'sdk'
$outRel = Get-ProjectScalar -RepoRoot $Root -Key 'output_dir'
if (-not $sdk) { Write-Output '>>> RED: pm/PROJECT.yaml declares no sdk'; exit 1 }
if (-not (Test-Path -LiteralPath $sdk)) {
    Write-Output (">>> RED: the declared SDK is not there: {0}" -f $sdk)
    exit 1
}
if (-not $outRel) { Write-Output '>>> RED: pm/PROJECT.yaml declares no output_dir'; exit 1 }

$benchDir = Join-Path $Root 'MapleBench'
$outDir = Join-Path $benchDir ($outRel -replace '/', '\')

Write-Output '=== build-gate ==='
Write-Output ("REPO_ROOT    : {0}" -f $Root)
Write-Output ("SDK          : {0}" -f $sdk)
Write-Output ("OUTPUT_DIR   : {0}" -f $outDir)

# --------------------------------------------------------------------------- #
# 1. the instance that holds the dll must be gone before the build (R-14, T-13)
# --------------------------------------------------------------------------- #
$stopResult = Stop-MapleBenchInstances -RepoRoot $Root -OnlyThisRepository
$stopped = @($stopResult.Stopped)
$stopFailed = @($stopResult.Failed)
$stopSkipped = @($stopResult.Skipped)
$stopUndet = @($stopResult.Undetermined)
if ($stopped.Count -eq 0) {
    Write-Output 'STOPPED      : none (no instance of this repository was running)'
} else {
    foreach ($s in $stopped) {
        Write-Output ("STOPPED      : pid {0} ({1})  age {2}  [{3}]" -f $s.Id, $s.Name, $s.Age, $s.Reason)
    }
}
foreach ($f in $stopFailed) {
    Write-Output ("STOP_FAILED  : pid {0} ({1})  {2}" -f $f.Id, $f.Name, $f.Reason)
}
foreach ($s in $stopSkipped) {
    Write-Output ("STOP_SKIPPED : pid {0} ({1})  not this repository -- {2}" -f $s.Id, $s.Name, $s.Reason)
}
foreach ($u in $stopUndet) {
    Write-Output ("STOP_UNKNOWN : pid {0} ({1})  command line unreadable and modules not enumerable" -f $u.Id, $u.Name)
}

# The lock is probed, not assumed. A dll that is still locked fails the build
# with MSB3027, which reads like a compile error and is not one (T-13).
# Probing it here turns "MSB3027" into a sentence that names the cause.
#
# MEASURED 2026-10-24: the probe has to WAIT for the stopped processes to be
# gone. Stop-Process -Force asks the process to die; the file handles it held
# are released a moment later, and a probe 500 ms after the kill still saw
# the lock and reported RED for a dll that was free two seconds later. So:
# wait for the pids to disappear, and then probe with retries.
$stoppedPids = @($stopped | ForEach-Object { $_.Id })
if ($stoppedPids.Count -gt 0) {
    $gone = $false
    for ($i = 0; $i -lt 40; $i++) {
        $alive = @($stoppedPids | Where-Object { Get-Process -Id $_ -ErrorAction SilentlyContinue })
        if ($alive.Count -eq 0) { $gone = $true; break }
        Start-Sleep -Milliseconds 250
    }
    if ($gone) {
        Write-Output 'STOPPED      : every stopped process is gone'
    } else {
        $alive = @($stoppedPids | Where-Object { Get-Process -Id $_ -ErrorAction SilentlyContinue })
        Write-Output ("STOP_STILL_UP : pid {0} did not exit within 10 s" -f ($alive -join ', '))
    }
}
$dllPath = Join-Path $outDir 'MapleBenchX.dll'
if (Test-Path -LiteralPath $dllPath) {
    $locked = $true
    for ($i = 0; $i -lt 20; $i++) {
        if (-not (Test-FileLocked -Path $dllPath)) { $locked = $false; break }
        Start-Sleep -Milliseconds 500
    }
    if ($locked) {
        Write-Output (">>> RED: {0} is still locked after stopping the instances." -f $dllPath)
        Write-Output '         A process still holds it. The build would fail with MSB3027,'
        Write-Output '         which reads like a compile error and is not one (T-13).'
        exit 1
    }
    Write-Output 'DLL_LOCK     : free'
}

# --------------------------------------------------------------------------- #
# 2. keep the previous output so the build can be DIFFED against it (T-14)
# --------------------------------------------------------------------------- #
$prev = Join-Path $env:TEMP 'maplebench-build-prev'
if (Test-Path -LiteralPath $prev) { Remove-Item -LiteralPath $prev -Recurse -Force }
$hadPrevious = $false
if (Test-Path -LiteralPath $outDir) {
    Copy-Item -LiteralPath $outDir -Destination $prev -Recurse -Force
    $hadPrevious = $true
}
Write-Output ("PREVIOUS_OUT : {0}" -f $(if ($hadPrevious) { $prev } else { '(none -- first build in this tree)' }))

# --------------------------------------------------------------------------- #
# 3. build. TreatWarningsAsErrors makes the exit code carry the warning count,
#    so the check does not have to read a localized summary line (the SDK on
#    this machine prints its summary in Chinese).
# --------------------------------------------------------------------------- #
$res = Invoke-CheckedProcess -FilePath $sdk -ArgumentList @(
        'build', 'MapleBench\MapleBenchX.csproj', '-c', 'Release',
        '-p:TreatWarningsAsErrors=true', '--nologo') `
    -WorkingDirectory $Root -TimeoutSeconds 900

if ($res.LaunchError) {
    Write-Output (">>> RED: the build could not be started: {0}" -f $res.LaunchError)
    exit 1
}
if ($res.TimedOut) {
    Write-Output '>>> RED: the build did not finish within 900 s'
    exit 1
}
Write-Output $res.StdOut
if ($res.StdErr) { Write-Output $res.StdErr }
if ($res.ExitCode -ne 0) {
    Write-Output (">>> RED: the build failed with exit code {0}" -f $res.ExitCode)
    exit 1
}

# --------------------------------------------------------------------------- #
# 4. the built dll must be newer than the newest product source (T-20)
# --------------------------------------------------------------------------- #
$dll = Join-Path $outDir 'MapleBenchX.dll'
if (-not (Test-Path -LiteralPath $dll)) {
    Write-Output '>>> RED: the build produced no MapleBenchX.dll'
    exit 1
}
$dllTime = (Get-Item -LiteralPath $dll).LastWriteTime
$src = Get-NewestProductSourceTime -RepoRoot $Root
Write-Output ("NEWEST_SOURCE: {0:yyyy-MM-dd HH:mm:ss}  {1}" -f $src.Time, $src.Path)
Write-Output ("BUILT_DLL    : {0:yyyy-MM-dd HH:mm:ss}  {1}" -f $dllTime, $dll)
if ($src.Time -gt $dllTime) {
    Write-Output '>>> RED: the built dll is OLDER than the newest product source.'
    Write-Output '         A fix would look like it did nothing (T-20).'
    exit 1
}

# --------------------------------------------------------------------------- #
# 5. what this build changed against the previous output (T-14)
# --------------------------------------------------------------------------- #
if ($hadPrevious) {
    # The RAW md5, not the normalised one: these are build artifacts (dll, pdb,
    # json), and CRLF-folding a binary would make two different files hash the
    # same. The tripwire legs below use the normalised rule because they are
    # text; the two rules are for two different things and both are stated.
    $prevMap = @{}
    foreach ($f in Get-ChildItem -LiteralPath $prev -Recurse -File) {
        $prevMap[$f.FullName.Substring($prev.Length)] = Get-RawMd5OfPath -Path $f.FullName
    }
    $newMap = @{}
    foreach ($f in Get-ChildItem -LiteralPath $outDir -Recurse -File) {
        $newMap[$f.FullName.Substring($outDir.Length)] = Get-RawMd5OfPath -Path $f.FullName
    }
    $added = @($newMap.Keys | Where-Object { -not $prevMap.ContainsKey($_) } | Sort-Object)
    $removed = @($prevMap.Keys | Where-Object { -not $newMap.ContainsKey($_) } | Sort-Object)
    $changed = @($newMap.Keys | Where-Object { $prevMap.ContainsKey($_) -and $prevMap[$_] -ne $newMap[$_] } | Sort-Object)
    Write-Output ("BUILD_DIFF   : {0} added, {1} changed, {2} removed" -f $added.Count, $changed.Count, $removed.Count)
    foreach ($k in $changed) { Write-Output ("  changed   {0}" -f $k) }
    foreach ($k in $added)   { Write-Output ("  added     {0}" -f $k) }
    foreach ($k in $removed) { Write-Output ("  removed   {0}  (a question, not a failure -- T-14)" -f $k) }
}

# --------------------------------------------------------------------------- #
# 6. the tripwires: source and build output. The served side is deploy.ps1's.
# --------------------------------------------------------------------------- #
$invariants = @((Get-ProjectMd5Invariants -RepoRoot $Root).Items)
if ($invariants.Count -eq 0) {
    Write-Output 'TRIPWIRES    : none declared'
} else {
    $projectName = Split-Path -Leaf $benchDir
    foreach ($inv in $invariants) {
        $legs = Get-InvariantLegs -RepoRoot $Root -OutputDir $outDir `
                -OutputProjectName $projectName -InvariantFile $inv.File
        $srcMd5 = Get-NormalisedMd5OfPath -Path $legs.Source
        $outMd5 = Get-NormalisedMd5OfPath -Path $legs.Build
        Write-Output ("TRIPWIRE     : {0}  declared {1}" -f $inv.Id, $inv.Md5)
        Write-Output ("  source      {0}" -f $(if ($srcMd5) { $srcMd5 } else { 'MISSING' }))
        Write-Output ("  build       {0}" -f $(if ($outMd5) { $outMd5 } else { 'MISSING' }))
        # A leg that is MISSING is not a pass. The tripwire claims to compare
        # the source and the build; if one of them is not there, it compared
        # nothing and said so by printing a digest it never computed.
        if (-not $srcMd5) {
            Write-Output (">>> RED: the source leg of {0} is missing: {1}" -f $inv.Id, $legs.Source)
            exit 1
        }
        if (-not $outMd5) {
            Write-Output (">>> RED: the build leg of {0} is missing: {1}" -f $inv.Id, $legs.Build)
            exit 1
        }
        if ($srcMd5 -ne $inv.Md5) {
            Write-Output (">>> RED: {0} does not match its declared digest in the source." -f $inv.Id)
            exit 1
        }
        if ($outMd5 -ne $inv.Md5) {
            Write-Output (">>> RED: {0} does not match its declared digest in the build output." -f $inv.Id)
            exit 1
        }
    }
}

Write-Output 'BUILD_OK'
exit 0
