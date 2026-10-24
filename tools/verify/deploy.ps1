# deploy.ps1 -- put the built product in front of a person, and say which
# build that person is looking at.
#
# Own-design: there is no C++ counterpart for this. What it obeys is in this
# repository:
#
#   R-14  The server holds MapleBenchX.dll; deploy starts it after the build
#         stopped it.
#   R-16  No CJK path in a script: the repository root is DISCOVERED and the
#         child is started with a RELATIVE argument and the root as its
#         working directory, so the non-ASCII path never passes through a
#         command line (the trick gate.ps1 uses).
#   R-17  Three-way md5: source, build output, SERVED. The digest is read
#         from pm/SPEC.yaml, never hard-coded.
#   T-0   A stale server answers exactly like a current one, so THIS
#         repository's instance is stopped first -- another checkout's is
#         left alone and reported as STOP_SKIPPED -- and this instance
#         prints its own identity.
#   T-16  Server sockets exhaust after a few thousand requests. That is the
#         harness, not the product.
#   T-20  deploy does not build: the dll that gets served must be NEWER than
#         the newest product source.
#
# ASCII only: do not add non-ASCII characters to this file.
#
# Exit codes: 0 deployed and answering, 1 refused.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib\Common.ps1')
. (Join-Path $PSScriptRoot 'lib\Project.ps1')
# UTF-8 console, so the paths this script prints (the repository root contains
# non-ASCII characters) come out readable instead of as mojibake.
Initialize-ConsoleEncoding
# The module path is pinned before any cmdlet is used, so a chain started from
# another PowerShell edition cannot take a cmdlet away (see
# Initialize-PowerShellModulePath in lib/Common.ps1).
Initialize-PowerShellModulePath

$Root = Resolve-RepoRoot -StartPath $PSScriptRoot
if (-not $Root) {
    Write-Output '>>> RED: the repository root could not be found'
    exit 1
}

$sdk = Get-ProjectScalar -RepoRoot $Root -Key 'sdk'
$outRel = Get-ProjectScalar -RepoRoot $Root -Key 'output_dir'
$port = Get-ProjectScalar -RepoRoot $Root -Key 'test_port'
if (-not $sdk) { Write-Output '>>> RED: pm/PROJECT.yaml declares no sdk'; exit 1 }
if (-not $port) { Write-Output '>>> RED: pm/PROJECT.yaml declares no test_port'; exit 1 }
if (-not (Test-Path -LiteralPath $sdk)) {
    Write-Output (">>> RED: the declared SDK is not there: {0}" -f $sdk)
    exit 1
}

$benchDir = Join-Path $Root 'MapleBench'
$outDir = Join-Path $benchDir ($outRel -replace '/', '\')
$dll = Join-Path $outDir 'MapleBenchX.dll'
if (-not (Test-Path -LiteralPath $dll)) {
    Write-Output (">>> RED: there is no built MapleBenchX.dll in {0}" -f $outDir)
    Write-Output '         Run tools/verify/build-gate.ps1 first.'
    exit 1
}

Write-Output '=== deploy ==='
Write-Output ("REPO_ROOT    : {0}" -f $Root)
Write-Output ("OUTPUT_DIR   : {0}" -f $outDir)
Write-Output ("PORT         : {0}" -f $port)

# --------------------------------------------------------------------------- #
# 1. the dll must be newer than the newest source (T-20)
# --------------------------------------------------------------------------- #
$dllTime = (Get-Item -LiteralPath $dll).LastWriteTime
$src = Get-NewestProductSourceTime -RepoRoot $Root
Write-Output ("BUILT_DLL    : {0:yyyy-MM-dd HH:mm:ss}" -f $dllTime)
Write-Output ("NEWEST_SOURCE: {0:yyyy-MM-dd HH:mm:ss}" -f $src.Time)
if ($src.Time -gt $dllTime) {
    Write-Output '>>> RED: the dll to serve is OLDER than the newest product source (T-20).'
    exit 1
}

# --------------------------------------------------------------------------- #
# 2. stop THIS repository's MapleBench (T-0). Another checkout's instance is
#    left alone: it does not hold this dll, and stopping a process that
#    belongs to a different checkout is not this script's business. It is
#    reported as STOP_SKIPPED, so it is visible and not silent.
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

# Every remaining MapleBench process and the port it listens on. T-0 is about
# WHICH instance answers, so the ports have to be visible: an instance on
# another port is not harmless, it is the stale one the old deploy.ps1 used to
# stop (and this script could not stop, because it runs elevated).
$listenRows = @()
try {
    $cand = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
               Where-Object { $_.Name -match 'MapleBenchX' -or
                             ([string]$_.CommandLine -and [string]$_.CommandLine -match 'MapleBenchX') })
    foreach ($c in $cand) {
        $ports = @(Get-NetTCPConnection -State Listen -OwningProcess ([int]$c.ProcessId) -ErrorAction SilentlyContinue |
                   ForEach-Object { $_.LocalPort } | Sort-Object -Unique)
        $listenRows += [pscustomobject]@{
            Id = [int]$c.ProcessId; Name = [string]$c.Name
            Ports = (($ports | ForEach-Object { [string]$_ }) -join ',')
        }
    }
} catch {
    $listenRows = @()
}
if ($listenRows.Count -eq 0) {
    Write-Output 'LISTENERS     : no MapleBench process is listening'
} else {
    foreach ($r in $listenRows) {
        Write-Output ("LISTENERS     : pid {0} ({1})  port {2}" -f $r.Id, $r.Name, $(if ($r.Ports) { $r.Ports } else { '(none)' }))
    }
}

# --------------------------------------------------------------------------- #
# 3. start the instance
# --------------------------------------------------------------------------- #
$logDir = Join-Path $env:TEMP 'maplebench-deploy'
if (-not (Test-Path -LiteralPath $logDir)) { New-Item -ItemType Directory -Path $logDir -Force | Out-Null }
$stamp = (Get-Date).ToString('yyyyMMdd-HHmmss')
$outLog = Join-Path $logDir ("server-{0}.out.log" -f $stamp)
$errLog = Join-Path $logDir ("server-{0}.err.log" -f $stamp)

$serverArgs = @('MapleBenchX.dll', '--port', $port, '--allow-multiple', '--no-browser')
# The arguments are joined with the same quoting helper lib/Common.ps1 uses, so
# the same string is produced on Windows PowerShell 5.1 and on PowerShell 7+.
$argLine = (($serverArgs | ForEach-Object { Format-NativeArgument -Value $_ }) -join ' ')

# R2: the child needs DOTNET_ROOT. Start-Process passes the parent's
# environment to the child -- measured on this machine: a cmd.exe started
# through it with DOTNET_ROOT set in the parent saw the value. It is set here
# because the value is derived from the declared SDK rather than assumed.
if ([string]::IsNullOrWhiteSpace($env:DOTNET_ROOT)) {
    $env:DOTNET_ROOT = Split-Path -Parent $sdk
}
$env:ASPNETCORE_ENVIRONMENT = 'Production'

# The server's output goes to FILES, not to pipes.
#
# Why: a child that inherits this script's stdout keeps that pipe open for as
# long as it runs, and a caller that reads the child's stdout to the end waits
# for an EOF that only arrives when the SERVER exits. The gate calls this
# script through Invoke-CheckedProcess, which reads stdout to the end -- so
# the gate would wait forever. With files, the log is always there and the
# script returns.
#
# The previous version had the same problem in a second place: the failure
# path printed $outTask.Result, and that task only completes when the server
# closes its stdout -- which a server that is still running never does. The
# failure path hung forever with no output at all.
$proc = $null
try {
    $proc = Start-Process -FilePath $sdk -ArgumentList $argLine `
            -WorkingDirectory $outDir `
            -RedirectStandardOutput $outLog -RedirectStandardError $errLog `
            -WindowStyle Hidden -PassThru
} catch {
    Write-Output (">>> RED: the server could not be started: {0}" -f $_.Exception.Message)
    exit 1
}
$pid_ = $proc.Id
Write-Output ("STARTED       : pid {0}" -f $pid_)
Write-Output ("LOG           : {0}" -f $outLog)
Write-Output ("ERR LOG       : {0}" -f $errLog)

function Get-LogTail {
    param([Parameter(Mandatory = $true)][string]$Path, [int]$Lines = 40)
    if (-not (Test-Path -LiteralPath $Path)) { return '(no such log)' }
    try {
        $text = [System.IO.File]::ReadAllText($Path, [System.Text.Encoding]::UTF8)
    } catch {
        return ('(log unreadable: {0})' -f $_.Exception.Message)
    }
    $all = $text -split "`r?`n"
    if ($all.Count -gt $Lines) { $all = $all[($all.Count - $Lines)..($all.Count - 1)] }
    return ($all -join "`n")
}

# --------------------------------------------------------------------------- #
# 4. wait for it to answer
# --------------------------------------------------------------------------- #
$base = "http://127.0.0.1:{0}" -f $port
# System.Net.Http is not always loaded in Windows PowerShell 5.1.
try { Add-Type -AssemblyName System.Net.Http -ErrorAction SilentlyContinue } catch { }
$client = New-Object System.Net.Http.HttpClient
$client.Timeout = [TimeSpan]::FromSeconds(10)
$ready = $false
for ($i = 0; $i -lt 120; $i++) {
    Start-Sleep -Seconds 2
    try {
        $r = $client.GetAsync("$base/api/about").GetAwaiter().GetResult()
        if ($r.IsSuccessStatusCode) {
            $ready = $true
            Write-Output ("ABOUT         : {0}" -f $r.Content.ReadAsStringAsync().GetAwaiter().GetResult())
            break
        }
    } catch {
        # not up yet
    }
    if ($proc.HasExited) { break }
}
if (-not $ready) {
    try { $proc.Kill() } catch { }
    Write-Output '>>> RED: the server did not answer /api/about within 240 s'
    # The log is read from the FILE. The previous version printed
    # $outTask.Result, which only completes when the server closes its
    # stdout -- so the failure path waited forever and printed nothing.
    Write-Output ("SERVER_STDOUT : {0}" -f (Get-LogTail -Path $outLog))
    Write-Output ("SERVER_STDERR : {0}" -f (Get-LogTail -Path $errLog))
    exit 1
}

# --------------------------------------------------------------------------- #
# 4b. T-0: the instance answering on the port has to be the one this script
#     started. A stale server on the same port answers exactly like a current
#     one, and every measurement below would then be taken against the wrong
#     binary. The check is on the LISTENER's pid, not on the answer.
# --------------------------------------------------------------------------- #
$listenerPid = 0
try {
    $conn = Get-NetTCPConnection -State Listen -LocalPort ([int]$port) -ErrorAction SilentlyContinue |
             Select-Object -First 1
    if ($conn) { $listenerPid = [int]$conn.OwningProcess }
} catch {
    $listenerPid = 0
}
Write-Output ("LISTENER      : pid {0} (started by this script: pid {1})" -f $listenerPid, $pid_)
if ($listenerPid -ne $pid_) {
    Write-Output (">>> RED: the instance answering on port {0} is pid {1}, not the pid {2} this script started." -f $port, $listenerPid, $pid_)
    Write-Output '         A stale server answers exactly like a current one (T-0).'
    Write-Output '         Every measurement below would be taken against the wrong binary.'
    exit 1
}

# --------------------------------------------------------------------------- #
# 5. the three-way md5: source, build output, served (R-17)
#    The path rule and the hashing rule are shared with build-gate.ps1 through
#    lib/Project.ps1. When each script had its own copy, the served leg was
#    built with BACKSLASHES -- which is not a path separator in a URL -- and
#    the build leg was built with a duplicated project segment and reported
#    MISSING. Both failures are silent: the digest that is printed is not the
#    digest of the thing that was fetched.
# --------------------------------------------------------------------------- #
$invariants = @((Get-ProjectMd5Invariants -RepoRoot $Root).Items)
if ($invariants.Count -eq 0) {
    Write-Output 'TRIPWIRES    : none declared'
} else {
    $projectName = Split-Path -Leaf $benchDir
    foreach ($inv in $invariants) {
        $legs = Get-InvariantLegs -RepoRoot $Root -OutputDir $outDir `
                -OutputProjectName $projectName -InvariantFile $inv.File

        $srcBytes = $null
        $outBytes = $null
        if (Test-Path -LiteralPath $legs.Source) { $srcBytes = [System.IO.File]::ReadAllBytes($legs.Source) }
        if (Test-Path -LiteralPath $legs.Build) { $outBytes = [System.IO.File]::ReadAllBytes($legs.Build) }
        $servedBytes = $null
        try {
            $servedBytes = $client.GetByteArrayAsync("$base$($legs.Served)").GetAwaiter().GetResult()
        } catch {
            Write-Output (">>> RED: {0} could not be fetched from the running server: {1}" -f $inv.Id, $_.Exception.Message)
            Write-Output ("         requested {0}{1}" -f $base, $legs.Served)
            exit 1
        }

        $srcMd5 = if ($srcBytes) { Get-NormalisedMd5OfBytes $srcBytes } else { 'MISSING' }
        $outMd5 = if ($outBytes) { Get-NormalisedMd5OfBytes $outBytes } else { 'MISSING' }
        $srvMd5 = Get-NormalisedMd5OfBytes $servedBytes

        Write-Output ("TRIPWIRE     : {0}  declared {1}" -f $inv.Id, $inv.Md5)
        Write-Output ("  source      {0}" -f $srcMd5)
        Write-Output ("  build       {0}" -f $outMd5)
        Write-Output ("  served      {0}" -f $srvMd5)
        Write-Output ("  served path {0}{1}" -f $base, $legs.Served)
        if ($srcMd5 -ne $inv.Md5 -or $outMd5 -ne $inv.Md5 -or $srvMd5 -ne $inv.Md5) {
            Write-Output (">>> RED: {0} does not match its declared digest in all three places." -f $inv.Id)
            exit 1
        }
    }
}

# --------------------------------------------------------------------------- #
# 6. the identity of the instance that is now answering. T-0: a stale server
#    answers exactly like a current one, so this is printed every time.
# --------------------------------------------------------------------------- #
Write-Output ("INSTANCE     : pid {0}  port {1}" -f $pid_, $port)
try {
    $p = Get-Process -Id $pid_
    Write-Output ("  started     : {0:yyyy-MM-dd HH:mm:ss}  (age {1:N1} h)" -f $p.StartTime, ((Get-Date) - $p.StartTime).TotalHours)
} catch {
    Write-Output '  started     : unknown'
}
Write-Output ("  dll         : {0}" -f $dll)
Write-Output ("  dll md5     : {0}" -f (Get-FileMd5 -Path $dll))
Write-Output ("  newest src  : {0:yyyy-MM-dd HH:mm:ss}" -f $src.Time)
Write-Output ("  gate base   : {0}" -f $base)
Write-Output 'DEPLOY_OK'
exit 0
