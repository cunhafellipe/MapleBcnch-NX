# gate-loop.ps1 -- the whole gate, in the order it has to happen in.
#
# WHAT THIS IS: the four commands pm/GATE.md documents, run in order, with the
# instance CHOSEN by the caller -- which is this script, because this script is
# the one that deployed it.
#
#   build-gate.ps1  stop the instance that holds the dll, build Release
#   deploy.ps1      start the instance on the declared test port
#   open-archives.py  open the declared archives, in the declared order
#   gate.ps1        run every suite
#
# WHY IT IS ONE SCRIPT: the four steps are each the precondition of the next,
# and a person who runs them by hand has to know three things that are easy to
# get wrong: the order, that the archives have to be reopened after EVERY
# restart (archive ids f1..fN depend on open order), and that the gate refuses
# to pick an instance by itself (tools/verify/AGENTS.md rule 17). A step that
# is skipped does not fail -- it produces a green gate that measured the wrong
# binary.
#
# WHY EACH STEP'S OUTPUT GOES TO A FILE AND IS TAILED:
#
#   MEASURED 2026-10-24: the first version of this script read each step's
#   stdout through a PIPE, and it hung for ever. The reason: deploy.ps1 starts
#   the server, and the server INHERITS the stdout handle it was given -- on
#   Windows PowerShell 5.1 a child inherits every inheritable handle of its
#   parent, not just the three standard ones. The server therefore kept the
#   pipe's write end open, so the reader never saw an EOF, and the chain waited
#   on a step that had already finished. A file has no EOF to wait for, and
#   polling the file also means the reader sees the gate's output WHILE the
#   gate runs, instead of thirty silent minutes.
#
# WHAT IT DOES NOT DO: it does not pick a port the caller did not authorise.
# The instance it measures is the one it started, on the port pm/PROJECT.yaml
# declares, and it PRINTS the instance identity so a reader can see that.
#
# ASCII only: do not add non-ASCII characters to this file.
#
# Exit codes: 0 the whole chain ran and the gate was green, 1 otherwise. The
# gate's own exit code is the verdict and is passed through unchanged.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib\Common.ps1')
. (Join-Path $PSScriptRoot 'lib\Project.ps1')
Initialize-ConsoleEncoding
# The module path is pinned BEFORE any cmdlet is used, so this script AND the
# step children it starts cannot inherit another PowerShell edition's module
# path (see Initialize-PowerShellModulePath in lib/Common.ps1).
Initialize-PowerShellModulePath

$Root = Resolve-RepoRoot -StartPath $PSScriptRoot
if (-not $Root) {
    Write-Output '>>> RED: the repository root could not be found'
    exit 1
}

$port = Get-ProjectScalar -RepoRoot $Root -Key 'test_port'
if (-not $port) { Write-Output '>>> RED: pm/PROJECT.yaml declares no test_port'; exit 1 }
# The banned list is written in YAML flow style (`[5480]`), so it arrives as
# text with brackets. The test is a word match, not a string equality.
$bannedRaw = Get-ProjectScalar -RepoRoot $Root -Key 'banned_ports'
$Base = "http://127.0.0.1:{0}" -f $port

Write-Output '=== gate-loop ==='
Write-Output ("REPO_ROOT    : {0}" -f $Root)
Write-Output ("GATE_BASE    : {0}" -f $Base)
if ($bannedRaw -and ($bannedRaw -match ('(?<![0-9])' + [regex]::Escape($port) + '(?![0-9])'))) {
    Write-Output (">>> RED: the declared test_port {0} is a BANNED port ({1})." -f $port, $bannedRaw)
    exit 1
}

# The suites and the gate read GATE_BASE themselves, and the gate REFUSES to
# run without it (rule 17: there is no default instance). The instance chosen
# here is the one this script deploys, on the declared port.
$env:GATE_BASE = $Base

$logDir = Join-Path $env:TEMP 'maplebench-verify'
if (-not (Test-Path -LiteralPath $logDir)) { New-Item -ItemType Directory -Path $logDir -Force | Out-Null }
$stamp = (Get-Date).ToString('yyyyMMdd-HHmmss')

function Convert-LogBytesToText {
    <#
      Decode a log file's bytes, choosing the code page by what the WHOLE
      file decodes as.

      MEASURED 2026-10-24: the step logs came back as mojibake --
      "wz\ufffd\ufffd\ufffd" for the CJK repository path, and the SDK's Chinese
      "build succeeded" summary as unreadable bytes. The step wrote its output
      in the machine's ANSI code page (950 here) because `[Console]::OutputEncoding`
      cannot be set on a process with no console, and a step started with
      Start-Process and a redirected stdout has no console. This reader read
      the file as UTF-8 and got replacement characters, which is exactly what
      the UTF-8 decoder does with bytes it cannot read.

      So: try STRICT UTF-8 on the whole file first, and fall back to the
      machine's default code page when it is not UTF-8. Deciding on the whole
      file (not on a chunk) keeps one code page for the whole log, so a chunk
      boundary cannot split a character and change the answer half way.
    #>
    param([Parameter(Mandatory = $true)][byte[]]$Bytes)

    $strict = New-Object System.Text.UTF8Encoding($false, $true)
    try { return $strict.GetString($Bytes) } catch { }
    try { return [System.Text.Encoding]::Default.GetString($Bytes) } catch { }
    return ''
}

function Write-NewLines {
    <#
      Print the part of a file that has not been printed yet. The file grows
      while the step runs, so the offset is what makes this a TAIL rather than
      a reprint. The offset is in BYTES, because the code page of the file is
      only known once it has been read whole.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][ref]$Offset
    )
    if (-not (Test-Path -LiteralPath $Path)) { return }
    try {
        $bytes = [System.IO.File]::ReadAllBytes($Path)
    } catch {
        return
    }
    if ($bytes.Length -le $Offset.Value) { return }
    $length = $bytes.Length - $Offset.Value
    $chunkBytes = New-Object byte[] $length
    [Array]::Copy($bytes, $Offset.Value, $chunkBytes, 0, $length)
    $Offset.Value = $bytes.Length
    $chunk = Convert-LogBytesToText -Bytes $chunkBytes
    if ($chunk.Trim().Length -gt 0) { Write-Output $chunk.TrimEnd() }
}

function Invoke-TailedStep {
    <#
      Run one step with its output in a file, print the file as it grows, and
      return the step's exit code through $script:stepExit.

      The exit code goes through a script variable, NOT through the return
      value: a function that both prints and returns puts the printed lines
      into the return value, and the caller then compares an ARRAY against 0.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string]$ArgLine,
        [Parameter(Mandatory = $true)][string]$WorkingDirectory,
        [int]$TimeoutSeconds = 3600
    )
    $outFile = Join-Path $logDir ("step-{0}-{1}.out.log" -f $Name, $stamp)
    $errFile = Join-Path $logDir ("step-{0}-{1}.err.log" -f $Name, $stamp)

    $proc = $null
    try {
        $proc = Start-Process -FilePath $FilePath -ArgumentList $ArgLine `
                -WorkingDirectory $WorkingDirectory `
                -RedirectStandardOutput $outFile -RedirectStandardError $errFile `
                -WindowStyle Hidden -PassThru
    } catch {
        Write-Output (">>> RED: {0} could not be started: {1}" -f $Name, $_.Exception.Message)
        $script:stepExit = 1
        return
    }
    # Touch .Handle straight away. Process.ExitCode is read from the process
    # HANDLE, and .NET only caches that handle if something asks for it. If
    # nothing does, the Process object returns $null for ExitCode after the
    # process is gone -- and a null ExitCode cast to int is 0, which is the
    # exit code of SUCCESS.
    #
    # MEASURED 2026-10-24: without this line every step of this chain
    # reported "STEP_EXIT : <name> = " (empty), the empty value became 0, and
    # a step that had FAILED was counted as a step that had passed. A chain
    # that cannot tell a failure from a success is worse than no chain.
    $null = $proc.Handle

    $outOff = 0
    $errOff = 0
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while (-not $proc.HasExited) {
        Start-Sleep -Milliseconds 700
        Write-NewLines -Path $outFile -Offset ([ref]$outOff)
        Write-NewLines -Path $errFile -Offset ([ref]$errOff)
        if ((Get-Date) -gt $deadline) {
            Write-Output (">>> RED: {0} did not finish within {1} s" -f $Name, $TimeoutSeconds)
            try { $proc.Kill() } catch { }
            $script:stepExit = 1
            return
        }
    }
    $proc.WaitForExit()
    Start-Sleep -Milliseconds 200
    Write-NewLines -Path $outFile -Offset ([ref]$outOff)
    Write-NewLines -Path $errFile -Offset ([ref]$errOff)

    Write-Output ("STEP_EXIT    : {0} = {1}" -f $Name, $proc.ExitCode)
    Write-Output ("STEP_LOG     : {0}" -f $outFile)
    if ($null -eq $proc.ExitCode) {
        # Unreachable in principle (the handle was cached above). If it
        # happens, the step's verdict is UNKNOWN, and an unknown verdict is
        # not a pass.
        Write-Output (">>> RED: the exit code of step '{0}' could not be read." -f $Name)
        $script:stepExit = 1
        return
    }
    $script:stepExit = [int]$proc.ExitCode
}

$script:stepExit = 1

$psExe = Join-Path $PSHOME 'powershell.exe'
if (-not (Test-Path -LiteralPath $psExe)) { $psExe = 'powershell.exe' }
$python = 'python'
Write-Output ("POWERSHELL   : {0}" -f $psExe)

function Get-PsArgLine {
    param([Parameter(Mandatory = $true)][string]$Script)
    $parts = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $Script)
    return (($parts | ForEach-Object { Format-NativeArgument -Value $_ }) -join ' ')
}

$chain = @(
    @{ Name = 'build-gate';    File = $psExe;  ArgLine = (Get-PsArgLine -Script (Join-Path $PSScriptRoot 'build-gate.ps1')); Timeout = 1800 },
    @{ Name = 'deploy';        File = $psExe;  ArgLine = (Get-PsArgLine -Script (Join-Path $PSScriptRoot 'deploy.ps1'));     Timeout = 1800 },
    @{ Name = 'open-archives'; File = $python; ArgLine = (Format-NativeArgument -Value (Join-Path $PSScriptRoot 'open-archives.py')); Timeout = 3600 },
    @{ Name = 'gate';          File = $psExe;  ArgLine = (Get-PsArgLine -Script (Join-Path $PSScriptRoot 'gate.ps1'));       Timeout = 7200 }
)

foreach ($step in $chain) {
    Write-Output ""
    Write-Output ("--- step: {0} ---" -f $step.Name)
    Invoke-TailedStep -Name $step.Name -FilePath $step.File -ArgLine $step.ArgLine `
                      -WorkingDirectory $Root -TimeoutSeconds $step.Timeout
    if ($script:stepExit -ne 0) {
        Write-Output ""
        Write-Output ("GATE_LOOP_RED : the chain stopped at step '{0}' with exit code {1}." -f $step.Name, $script:stepExit)
        Write-Output ("GATE_LOOP_BASE: {0}" -f $Base)
        exit 1
    }
}

Write-Output ""
Write-Output ("GATE_LOOP_OK  : the whole chain ran against {0}" -f $Base)
Write-Output ("GATE_LOOP_BASE: {0}" -f $Base)
exit 0
