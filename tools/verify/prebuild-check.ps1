# prebuild-check.ps1 -- decide whether a build is allowed to start, and say why.
#
# Two of the five incidents were a build or measurement that could not run and
# produced output indistinguishable from a successful run:
#   * DOTNET_ROOT unset -> the apphost exited inside one second and every
#     measurement column came back 0.
#   * a running backend held bin\Release\MapleBenchX.dll -> the release build
#     failed late, after a long compile.
#
# This tool fails LOUDLY and early on either. It never lets a build start on
# the strength of an environment it could not verify.
#
# Exit: 0 PASS  1 FAIL  2 INCONCLUSIVE  3 TOOL_ERROR

[CmdletBinding()]
param(
    [string] $RepoRoot = '',
    [int]    $MinMajor = 10,
    [string] $Project = '',
    [string] $WatchPath = '',
    [switch] $SkipBinLock,
    [switch] $RequirePathDotnet
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib\Common.ps1')

Initialize-Verification -Tool 'prebuild-check' -Checked @(
    'DOTNET_ROOT present, directory exists, and dotnet --version actually runs',
    'an installed SDK whose major version is at least the required one',
    'which dotnet the PATH would resolve to, because that is not the same one',
    'the project TargetFramework',
    'exclusive-open probe of the build output for a lock held by a running process'
)

$fail = $false
$unknown = $false

if ([string]::IsNullOrWhiteSpace($RepoRoot)) { $RepoRoot = Resolve-RepoRoot }
Set-Field 'REPO_ROOT'  ([string]$RepoRoot)
Set-Field 'MIN_MAJOR'  ([string]$MinMajor)

function Add-Remediation { param([string]$Line) Add-Detail ('REMEDIATION: ' + $Line) }

# --- 1. DOTNET_ROOT --------------------------------------------------------
$dn = $script:VerifyDotNet
Set-Field 'CHECK_DOTNET_ROOT' $dn.Status
if ($dn.Status -ne 'OK') {
    $fail = $true
    Add-Detail ('CHECK DOTNET_ROOT: FAIL -- ' + $dn.Reason)
    Add-Remediation '$env:DOTNET_ROOT = ''C:\MUWORK\app\scoop\apps\dotnet-sdk\current'''
    Add-Remediation '& "$env:DOTNET_ROOT\dotnet.exe" build <project> -c Release'
    Add-Remediation 'Do NOT rely on the dotnet on PATH: it resolves to the 8.0 install and reports NETSDK1045.'
} else {
    Add-Detail ('CHECK DOTNET_ROOT: OK -- ' + $dn.Version)
}

# --- 2. installed SDKs -----------------------------------------------------
$dotnetExe = ''
if ($dn.Status -eq 'OK') {
    $dotnetExe = [System.IO.Path]::Combine($dn.Root, 'dotnet.exe')
    $sdks = Invoke-CheckedProcess -FilePath $dotnetExe -ArgumentList @('--list-sdks') -TimeoutSeconds 60
    $sdkLines = @()
    $bestMajor = -1
    if ($sdks.ExitCode -eq 0) {
        foreach ($l in ([string]$sdks.StdOut -split "`r?`n")) {
            if ($l.Trim() -eq '') { continue }
            $sdkLines += $l.Trim()
            $ver = ($l -split ' ')[0]
            $maj = 0
            if ([int]::TryParse(($ver -split '\.')[0], [ref]$maj)) { if ($maj -gt $bestMajor) { $bestMajor = $maj } }
        }
    }
    Set-Field 'SDK_LIST'    ($sdkLines -join ' ; ')
    Set-Field 'SDK_MAXMAJOR' ([string]$bestMajor)
    if ($sdkLines.Count -eq 0) {
        $fail = $true
        Set-Field 'CHECK_SDK' 'FAIL'
        Add-Detail 'CHECK SDK: FAIL -- dotnet --list-sdks returned nothing'
        Add-Remediation 'install an SDK into DOTNET_ROOT; an empty SDK list cannot build'
    } elseif ($bestMajor -lt $MinMajor) {
        $fail = $true
        Set-Field 'CHECK_SDK' 'FAIL'
        Add-Detail ('CHECK SDK: FAIL -- highest SDK major is ' + $bestMajor + ', need ' + $MinMajor)
    } else {
        Set-Field 'CHECK_SDK' 'OK'
        Add-Detail ('CHECK SDK: OK -- major ' + $bestMajor + ' >= ' + $MinMajor)
    }
} else {
    $unknown = $true
    Set-Field 'CHECK_SDK' 'UNKNOWN'
}

# --- 3. what the PATH would give you ---------------------------------------
$pathDotnet = $null
try { $pathDotnet = (Get-Command dotnet -ErrorAction SilentlyContinue) } catch { $pathDotnet = $null }
if ($null -eq $pathDotnet) {
    Set-Field 'CHECK_PATH_DOTNET' 'ABSENT'
    Add-Detail 'CHECK PATH dotnet: ABSENT -- a bare "dotnet" will not resolve; use $env:DOTNET_ROOT\dotnet.exe'
} else {
    $pv = Invoke-CheckedProcess -FilePath $pathDotnet.Source -ArgumentList @('--version') -TimeoutSeconds 60
    $pvTrim = ([string]$pv.StdOut).Trim()
    $pMaj = 0
    if ($pvTrim -ne '') { [void][int]::TryParse(($pvTrim -split '\.')[0], [ref]$pMaj) }
    $same = $false
    if ((-not [string]::IsNullOrWhiteSpace($dotnetExe)) -and
        (-not [string]::IsNullOrWhiteSpace([string]$pathDotnet.Source))) {
        # GetFullPath on an empty string throws, so both sides must be real.
        $same = ([System.IO.Path]::GetFullPath([string]$pathDotnet.Source) -eq [System.IO.Path]::GetFullPath($dotnetExe))
    }
    Set-Field 'PATH_DOTNET'      $pathDotnet.Source
    Set-Field 'PATH_DOTNET_VER'  $pvTrim
    Set-Field 'PATH_DOTNET_SAME_AS_DOTNET_ROOT' ([string]$same)
    if ($pMaj -lt $MinMajor) {
        Set-Field 'CHECK_PATH_DOTNET' 'TOO_OLD'
        Add-Detail ('CHECK PATH dotnet: TOO_OLD -- ' + $pvTrim + ' at ' + $pathDotnet.Source)
        Add-Remediation ('always invoke the SDK explicitly: & "$env:DOTNET_ROOT\dotnet.exe" ...')
        if ($RequirePathDotnet) {
            $fail = $true
            Add-Detail '  and -RequirePathDotnet was given, so this is a failure'
        }
    } else {
        Set-Field 'CHECK_PATH_DOTNET' 'OK'
        Add-Detail ('CHECK PATH dotnet: OK -- ' + $pvTrim)
    }
}

# --- 4. project ------------------------------------------------------------
if ([string]::IsNullOrWhiteSpace($Project)) {
    $unknown = $true
    Set-Field 'CHECK_PROJECT' 'UNKNOWN'
    Add-Detail 'CHECK PROJECT: not supplied with -Project; TargetFramework not verified'
} else {
    $projPath = $Project
    if (-not [System.IO.Path]::IsPathRooted($projPath) -and -not [string]::IsNullOrWhiteSpace($RepoRoot)) {
        $projPath = Join-Path $RepoRoot $Project
    }
    Set-Field 'PROJECT_PATH' $projPath
    if (-not [System.IO.File]::Exists($projPath)) {
        $fail = $true
        Set-Field 'CHECK_PROJECT' 'FAIL'
        Add-Detail ('CHECK PROJECT: FAIL -- no such file: ' + $projPath)
    } else {
        $tfm = ''
        try {
            [xml]$xml = [System.IO.File]::ReadAllText($projPath, [System.Text.Encoding]::UTF8)
            $tfm = [string]$xml.Project.PropertyGroup.TargetFramework
        } catch {
            $tfm = ''
        }
        Set-Field 'TARGET_FRAMEWORK' $tfm
        $tfmMajor = 0
        if ($tfm -match '^net(\d+)') { [void][int]::TryParse($Matches[1], [ref]$tfmMajor) }
        if ($tfmMajor -eq 0) {
            $unknown = $true
            Set-Field 'CHECK_PROJECT' 'UNKNOWN'
            Add-Detail ('CHECK PROJECT: TargetFramework unreadable or missing: <' + $tfm + '>')
        } elseif ($tfmMajor -lt $MinMajor) {
            $fail = $true
            Set-Field 'CHECK_PROJECT' 'FAIL'
            Add-Detail ('CHECK PROJECT: FAIL -- TargetFramework ' + $tfm + ' is below the required major ' + $MinMajor)
        } else {
            Set-Field 'CHECK_PROJECT' 'OK'
            Add-Detail ('CHECK PROJECT: OK -- TargetFramework ' + $tfm)
        }
    }
}

# --- 5. build output lock --------------------------------------------------
# The running backend holds its own DLL. Opening it with FileShare.None is a
# read-only probe: nothing is written, and it answers the only question that
# matters -- will the release build fail on this file.
if ($SkipBinLock) {
    Set-Field 'CHECK_BIN_LOCK' 'SKIPPED'
    Add-Detail 'CHECK BIN LOCK: skipped by request'
} else {
    $wp = $WatchPath
    if ([string]::IsNullOrWhiteSpace($wp)) { $wp = 'MapleBench\bin\Release' }
    if (-not [System.IO.Path]::IsPathRooted($wp) -and -not [string]::IsNullOrWhiteSpace($RepoRoot)) {
        $wp = Join-Path $RepoRoot $wp
    }
    Set-Field 'WATCH_PATH' $wp

    if (-not [System.IO.Directory]::Exists($wp)) {
        $unknown = $true
        Set-Field 'CHECK_BIN_LOCK' 'UNKNOWN'
        Add-Detail ('CHECK BIN LOCK: UNKNOWN -- directory does not exist yet: ' + $wp)
        Add-Remediation 'an absent output directory cannot be locked; a first build will create it'
    } else {
        # Derive the assembly name from the project file, never hard-code it.
        $asm = ''
        if (-not [string]::IsNullOrWhiteSpace($Project)) {
            $asm = [System.IO.Path]::GetFileNameWithoutExtension($Project)
        }
        if ($asm -eq '') { $asm = 'MapleBenchX' }

        $locked = New-Object System.Collections.ArrayList
        $readOnly = New-Object System.Collections.ArrayList
        $probed = 0
        # The output lands in bin\<config>\<tfm>, so the assembly is searched
        # for rather than assumed to sit directly in the watch path.
        $candidates = New-Object System.Collections.ArrayList
        foreach ($ext in @('.dll', '.exe')) {
            $found = @(Get-ChildItem -LiteralPath $wp -Filter ($asm + $ext) -Recurse -File -ErrorAction SilentlyContinue)
            foreach ($f in $found) { [void]$candidates.Add($f.FullName) }
        }
        foreach ($candidate in $candidates) {
            $probed++
            try {
                $fs = [System.IO.File]::Open($candidate, [System.IO.FileMode]::Open,
                        [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
                $fs.Close()
                Add-Detail ('CHECK BIN LOCK: ' + $candidate + ' is free')
            } catch [System.UnauthorizedAccessException] {
                $readOnly.Add($candidate)
                Add-Detail ('CHECK BIN LOCK: ' + $candidate + ' is read-only on disk (a different failure from a lock)')
            } catch [System.IO.IOException] {
                $locked.Add($candidate)
                Add-Detail ('CHECK BIN LOCK: ' + $candidate + ' IS LOCKED by another process')
            }
        }
        Set-Field 'FILES_FOUND' (($candidates -join ' ; '))
        if ($probed -eq 0) {
            $unknown = $true
            Set-Field 'CHECK_BIN_LOCK' 'UNKNOWN'
            Add-Detail ('CHECK BIN LOCK: UNKNOWN -- no ' + $asm + '.dll/.exe found under ' + $wp)
        } else {
            # Attribution. A backend started as "dotnet MapleBenchX.dll" has
            # the process name "dotnet", so Get-Process -Name MapleBenchX
            # finds nothing while the file is very much locked. Match the
            # command line instead.
            $pids = @()
            $attrib = 'unknown'
            try {
                $procs = @(Get-CimInstance Win32_Process -Filter ("Name='dotnet.exe' OR Name='" + $asm + ".exe'") -ErrorAction Stop)
                foreach ($p in $procs) {
                    $cl = [string]$p.CommandLine
                    if ($cl -eq '') { continue }
                    $clFlat = $cl -replace '\\', '/'
                    $asmFlat = ($asm + '.dll') -replace '\\', '/'
                    if ($clFlat.Contains($asmFlat) -or $clFlat.Contains($asm)) {
                        $pids += ([string]$p.ProcessId)
                        Add-Detail ('CHECK BIN LOCK HOLDER: pid=' + $p.ProcessId + ' cmd=' + $cl)
                    }
                }
                $attrib = 'cim-commandline'
            } catch {
                $attrib = 'unavailable'
                foreach ($p in @(Get-Process -Name $asm -ErrorAction SilentlyContinue)) { $pids += [string]$p.Id }
            }
            Set-Field 'LOCK_ATTRIBUTION' $attrib
            Set-Field 'RUNNING_PROCESS_IDS' (($pids -join ',') + ' (' + $pids.Count + ')')
            Set-Field 'FILES_PROBED' ([string]$probed)
            if ($locked.Count -gt 0) {
                $fail = $true
                Set-Field 'CHECK_BIN_LOCK' 'FAIL'
                Add-Detail ('CHECK BIN LOCK: FAIL -- ' + $locked.Count + ' locked file(s); holder pid(s): ' + ($pids -join ',') + ' (attribution: ' + $attrib + ')')
                Add-Remediation 'stop the running backend, or build to a private output: -p:OutDir=$env:TEMP\mb-build\'
            } elseif ($readOnly.Count -gt 0) {
                $unknown = $true
                Set-Field 'CHECK_BIN_LOCK' 'READONLY'
                Add-Detail ('CHECK BIN LOCK: read-only files present: ' + ($readOnly -join ','))
            } else {
                Set-Field 'CHECK_BIN_LOCK' 'OK'
                Add-Detail ('CHECK BIN LOCK: OK -- ' + $probed + ' file(s) free')
            }
        }
    }
}

# --- command the caller should actually run -------------------------------
Set-Field 'USE_COMMAND' ('& "$env:DOTNET_ROOT\dotnet.exe" build ' + $Project + ' -c Release')
Add-Detail ('BUILD COMMAND: & "$env:DOTNET_ROOT\dotnet.exe" build ' + $Project + ' -c Release')

if ($fail) { Complete-Verification -Result 'FAIL' -Message 'at least one precondition for a build is not met; do not start the build' }
if ($unknown) { Complete-Verification -Result 'INCONCLUSIVE' -Trust 'MEDIUM' -Message 'the blocking checks passed but some could not be decided' }
Complete-Verification -Result 'PASS' -Message 'every build precondition was checked and met'