# A known-answer test for the banned-port guard in lib/Project.ps1.
#
# WHAT IT PROVES: `Stop-MapleBenchInstances` refuses to stop an instance that
# is LISTENING on a port declared in pm/PROJECT.yaml's `banned_ports` list,
# and -- this is the half that matters -- the guard CAN turn: with a port
# that is not banned the same candidate IS stopped.
#
# WHY IT CANNOT KILL ANYTHING: the candidate list and Stop-Process are both
# replaced with stubs AFTER the library is dot-sourced, so the call inside
# the library resolves to the stub.  The candidate is this test process, which
# holds a real TCP listener on a port of its own.  The library's own
# discovery never runs.
#
# WHY THE PATH IS NOT EMBEDDED: the repository path contains non-ASCII
# characters, and Windows PowerShell 5.1 reads a .ps1 without a BOM as ANSI,
# so an embedded path arrives mangled (incident 1 in tools/verify/AGENTS.md).
# The root is derived from $PSScriptRoot instead.
$ErrorActionPreference = 'Continue'
$Root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$lib  = Join-Path $Root 'tools\verify\lib\Project.ps1'
$tmp  = Join-Path $env:TEMP ('ban-test-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path (Join-Path $tmp 'pm') -Force | Out-Null

$listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
$listener.Start()
$port = ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port
Write-Host ("port chosen = " + $port)
Write-Host ("this process id = " + $PID)

. $lib

$script:StubCandidates = $null
function Get-MapleBenchInstanceCandidates {
    param([string]$RepoRoot)
    return [pscustomobject]@{
        Candidates   = @($script:StubCandidates)
        Undetermined = @()
    }
}
$script:StopCalls = @()
function Stop-Process {
    [CmdletBinding()]
    param([Parameter(ValueFromPipeline = $true)]$Id, [switch]$Force)
    $script:StopCalls += $Id
    # Emits NOTHING: the real cmdlet emits nothing, and an emitted $null
    # would turn the library's return value into an ARRAY.
}

$script:StubCandidates = @([pscustomobject]@{
    Id = $PID; Name = 'stub'; Reason = 'stub candidate (this test process)'
    Detail = ('listening on ' + $port); IsThisRepo = $true
})

function Write-Ban([int[]]$banned) {
    $yaml = "data_root: 'C:\MUWORK\GAME\MPSHS'`ntest_port: 5591`nbanned_ports: [" + ($banned -join ',') + "]`n"
    [System.IO.File]::WriteAllText((Join-Path $tmp 'pm\PROJECT.yaml'), $yaml, (New-Object System.Text.UTF8Encoding($false)))
}

function Invoke-Arm([int[]]$banned, [string]$label) {
    Write-Ban $banned
    $script:StopCalls = @()
    $r = $null
    try {
        $r = Stop-MapleBenchInstances -RepoRoot $tmp
    } catch {
        Write-Host ("ARM " + $label + " : THREW: " + $_.Exception.GetType().FullName + ' :: ' + $_.Exception.Message)
        return [pscustomobject]@{ Stopped = -1; Skipped = -1; Failed = -1; Reason = 'THREW: ' + $_.Exception.Message }
    }
    $stopped = @($r.Stopped).Count
    $skipped = @($r.Skipped).Count
    $failed  = @($r.Failed).Count
    $reason  = ''
    if ($skipped -gt 0) { $reason = @($r.Skipped)[0].Reason }
    Write-Host ("ARM " + $label + " : banned_ports=[" + ($banned -join ',') + "]  Stopped=" + $stopped + "  Skipped=" + $skipped + "  Failed=" + $failed + "  StopProcessCalls=" + $script:StopCalls.Count)
    Write-Host ("          skipped reason: " + $reason)
    return [pscustomobject]@{ Stopped = $stopped; Skipped = $skipped; Failed = $failed; Reason = $reason }
}

$a = Invoke-Arm -banned @($port) -label 'A (the port IS banned)'
$b = Invoke-Arm -banned @(1)    -label 'B (the port is NOT banned)'

$listener.Stop()

Write-Host '=== VERDICT ==='
$ok = $true
if ($a.Stopped -ne 0) { Write-Host ("FAIL: ARM A stopped " + $a.Stopped + " process(es)"); $ok = $false }
if ($a.Skipped -ne 1) { Write-Host ("FAIL: ARM A skipped " + $a.Skipped + ", expected 1"); $ok = $false }
if ($a.Reason -notmatch 'BANNED PORT') { Write-Host ("FAIL: ARM A's reason does not name the ban: " + $a.Reason); $ok = $false }
if ($b.Stopped -ne 1) { Write-Host ("FAIL: ARM B (control) stopped " + $b.Stopped + ", expected 1"); $ok = $false }
if ($b.Skipped -ne 0) { Write-Host ("FAIL: ARM B (control) skipped " + $b.Skipped + ", expected 0"); $ok = $false }
if ($ok) { Write-Host 'RESULT=PASS  (the guard refuses the banned port, and the control shows it can turn)' }
else { Write-Host 'RESULT=FAIL' }
Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
if (-not $ok) { exit 1 }
exit 0
