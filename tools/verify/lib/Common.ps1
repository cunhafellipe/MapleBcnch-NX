# Common.ps1 -- shared helpers for the MapleBench-NX verification tools.
#
# RULES THIS FILE ENFORCES (see docs/ENGINEERING/STATE.md, section
# "verification tool failure log", for the five incidents that motivated them):
#
#   R1  Every script is pure ASCII. Paths are parameters, never literals.
#       A write tool that mangles non-ASCII inside a script destroys the
#       script's own inputs and the run still "succeeds" with zeroes.  -- incident 1
#   R2  Every script self-checks DOTNET_ROOT before doing anything else.
#       Without it the apphost exits inside one second and every measurement
#       is 0, which reads as "the data is like that".                    -- incident 2
#   R3  Never use the PowerShell -like/-notlike idiom to classify git status
#       lines. '?' is a single-character wildcard there and matches
#       everything, so 35 untracked files reported as 0.                -- incident 3
#   R4  Never read a file with Get-Content and the default encoding. Under
#       Windows PowerShell 5.1 that decodes UTF-8-without-BOM as the system
#       ANSI code page and destroys non-ASCII text.                     -- incident 4
#   R5  Every search normalises for line-wrapped string concatenation and
#       for indentation before it reports "not present".               -- incident 5
#   R6  Any check that cannot decide emits INCONCLUSIVE. It never guesses.
#   R7  Every run prints what it checked, what it found, and whether the
#       finding can be trusted.
#
# Compatible with Windows PowerShell 5.1 and PowerShell 7+.
# ASCII only: do not add non-ASCII characters to this file.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------
# Result vocabulary and exit codes
# ---------------------------------------------------------------------------
$script:VerifyExitCode = @{
    PASS         = 0
    FAIL         = 1
    INCONCLUSIVE = 2
    TOOL_ERROR   = 3
}

$script:VerifyFields  = [ordered]@{}
$script:VerifyDetail  = New-Object System.Collections.ArrayList
$script:VerifyTool    = 'unknown'
$script:VerifyChecked = 'nothing'
$script:VerifyDotNet  = $null
$script:VerifyStarted = Get-Date

# ---------------------------------------------------------------------------
# Output helpers
# ---------------------------------------------------------------------------

function Set-Field {
    param([Parameter(Mandatory = $true)][string]$Name,
          [Parameter(Mandatory = $true)][AllowEmptyString()][string]$Value)
    $script:VerifyFields[$Name] = $Value
}

function Get-Field {
    param([Parameter(Mandatory = $true)][string]$Name)
    if ($script:VerifyFields.Contains($Name)) { return $script:VerifyFields[$Name] }
    return $null
}

function Add-Detail {
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string]$Text)
    [void]$script:VerifyDetail.Add($Text)
}

# Console output must be UTF-8 or non-ASCII text is mangled on the way out.
# Wrapped: a redirected/no-console host throws here and that is not a reason
# to abandon the run.
function Initialize-ConsoleEncoding {
    try {
        [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
    } catch {
        # Nothing to do; the fields below still carry the raw text.
    }
}

function Initialize-Verification {
    param(
        [Parameter(Mandatory = $true)][string]$Tool,
        [Parameter(Mandatory = $true)][string[]]$Checked,
        [switch]$Json
    )
    Initialize-ConsoleEncoding
    $script:VerifyTool    = $Tool
    $script:VerifyChecked = ($Checked -join '; ')
    $script:VerifyFields  = [ordered]@{}
    $script:VerifyDetail  = New-Object System.Collections.ArrayList
    Set-Field 'TOOL'       $Tool
    Set-Field 'TOOL_SCHEMA' '1'
    Set-Field 'CHECKED'    $script:VerifyChecked
    Set-Field 'HOST_SHELL' ('PowerShell ' + $PSVersionTable.PSVersion.ToString())
    Set-Field 'OS'         $env:OS

    # R2: self-check DOTNET_ROOT before anything else runs.
    $script:VerifyDotNet = Get-DotNetEnvStatus
    Set-Field 'DOTNET_ENV'    $script:VerifyDotNet.Status
    Set-Field 'DOTNET_ROOT'   ([string]$script:VerifyDotNet.Root)
    Set-Field 'DOTNET_VERSION' ([string]$script:VerifyDotNet.Version)
    Set-Field 'DOTNET_DETAIL' ([string]$script:VerifyDotNet.Reason)

    Write-Output ("=== TOOL: {0} ===" -f $Tool)
    Write-Output ("CHECKED: {0}" -f $script:VerifyChecked)
    # The [env] prefix keeps this human summary from being mistaken for a
    # machine field: "DOTNET_ENV=MISSING DOTNET_ROOT= VERSION=" would parse as
    # a DOTNET_ENV whose value is the whole rest of the line.
    Write-Output ("[env] DOTNET_ENV={0} DOTNET_ROOT={1} VERSION={2}" -f `
        $script:VerifyDotNet.Status, [string]$script:VerifyDotNet.Root, [string]$script:VerifyDotNet.Version)
}

function Complete-Verification {
    param(
        [Parameter(Mandatory = $true)][ValidateSet('PASS', 'FAIL', 'INCONCLUSIVE')][string]$Result,
        [ValidateSet('HIGH', 'MEDIUM', 'NONE')][string]$Trust,
        [string]$Message = ''
    )
    if ($PSBoundParameters.ContainsKey('Trust')) {
        $t = $Trust
    } elseif ($Result -eq 'PASS') {
        $t = 'HIGH'
    } elseif ($Result -eq 'INCONCLUSIVE') {
        $t = 'NONE'
    } else {
        $t = 'NONE'
    }
    if ($t -eq 'HIGH' -and $Result -ne 'PASS') { $t = 'NONE' }

    Set-Field 'RESULT' $Result
    Set-Field 'TRUST'  $t
    Set-Field 'MESSAGE' $Message

    foreach ($k in $script:VerifyFields.Keys) {
        $v = [string]$script:VerifyFields[$k]
        if ($v -match "`r" -or $v -match "`n") { $v = $v -replace "`r", '\r' -replace "`n", '\n' }
        Write-Output ("{0}={1}" -f $k, $v)
    }
    if ($script:VerifyDetail.Count -gt 0) {
        Write-Output '--- detail ---'
        foreach ($d in $script:VerifyDetail) { Write-Output $d }
    }
    $secs = [math]::Round(((Get-Date) - $script:VerifyStarted).TotalSeconds, 2)
    Write-Output ("RESULT: {0}" -f $Result)
    Write-Output ("TRUST: {0}" -f $t)
    Write-Output ("EXIT: {0}" -f $script:VerifyExitCode[$Result])

    if ($PSBoundParameters.ContainsKey('Json')) { }
    exit $script:VerifyExitCode[$Result]
}

# ---------------------------------------------------------------------------
# Path helpers
# ---------------------------------------------------------------------------

# R1: no path literal anywhere in these tools. Resolve from the script location.
function Resolve-RepoRoot {
    param([string]$StartPath = $PSScriptRoot)
    if ([string]::IsNullOrWhiteSpace($StartPath)) { $StartPath = (Get-Location).Path }
    $dir = (Resolve-Path -LiteralPath $StartPath -ErrorAction SilentlyContinue)
    if ($null -eq $dir) { return $null }
    $cur = $dir.Path
    for ($i = 0; $i -lt 64; $i++) {
        if (Test-Path -LiteralPath (Join-Path $cur '.git')) { return $cur }
        $parent = Split-Path -Parent $cur
        if ([string]::IsNullOrEmpty($parent) -or $parent -eq $cur) { break }
        $cur = $parent
    }
    return $null
}

function ConvertTo-RepoRelative {
    param([Parameter(Mandatory = $true)][string]$RepoRoot,
          [Parameter(Mandatory = $true)][string]$Path)
    $full = [System.IO.Path]::GetFullPath($Path)
    $root = [System.IO.Path]::GetFullPath($RepoRoot)
    if ($full.Length -le $root.Length) { return $null }
    if ($full.Substring(0, $root.Length) -ne $root.TrimEnd('\', '/')) { return $null }
    $rel = $full.Substring($root.Length).TrimStart('\', '/')
    return $rel -replace '\\', '/'
}

# ---------------------------------------------------------------------------
# Process helpers
#
# Start-Process is never used. STATE.md records that DOTNET_ROOT is not
# inherited through Start-Process on this machine, and the apphost then exits
# within one second. Here the child environment is always built explicitly.
# ---------------------------------------------------------------------------

function Format-NativeArgument {
    param([Parameter(Mandatory = $true)][string]$Value)
    # Standard Windows command-line quoting (MSVC rules).
    if ($Value -notmatch '[\s"]') { return $Value }
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.Append('"')
    $backslashes = 0
    foreach ($ch in $Value.ToCharArray()) {
        if ($ch -eq '\') {
            $backslashes++
            continue
        }
        if ($ch -eq '"') {
            [void]$sb.Append(('\' * ($backslashes * 2 + 1)))
            [void]$sb.Append('"')
            $backslashes = 0
            continue
        }
        if ($backslashes -gt 0) {
            [void]$sb.Append(('\' * $backslashes))
            $backslashes = 0
        }
        [void]$sb.Append($ch)
    }
    if ($backslashes -gt 0) { [void]$sb.Append(('\' * ($backslashes * 2))) }
    [void]$sb.Append('"')
    return $sb.ToString()
}

function Invoke-CheckedProcess {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [string[]]$ArgumentList = @(),
        [int]$TimeoutSeconds = 120,
        [string]$WorkingDirectory,
        [switch]$Binary,
        [switch]$Quiet
    )

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $FilePath
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    if (-not $Binary) {
        # Must be set BEFORE Start(): the reader is created there.
        # Decode as UTF-8. The ambient code page is NOT a safe default for a
        # tool that has to read non-ASCII path and text output.  -- R4
        $psi.StandardOutputEncoding = New-Object System.Text.UTF8Encoding($false)
        $psi.StandardErrorEncoding = New-Object System.Text.UTF8Encoding($false)
    }

    $hasArgList = $psi.PSObject.Properties.Name -contains 'ArgumentList'
    if ($hasArgList) {
        foreach ($a in $ArgumentList) { [void]$psi.ArgumentList.Add($a) }
    } else {
        $parts = @()
        foreach ($a in $ArgumentList) { $parts += (Format-NativeArgument -Value $a) }
        $psi.Arguments = ($parts -join ' ')
    }

    # R2: hand DOTNET_ROOT to the child explicitly instead of hoping it inherits.
    if (-not [string]::IsNullOrWhiteSpace($env:DOTNET_ROOT)) {
        $psi.EnvironmentVariables['DOTNET_ROOT'] = $env:DOTNET_ROOT
    }
    if ($WorkingDirectory) { $psi.WorkingDirectory = $WorkingDirectory }

    $result = @{
        FilePath      = $FilePath
        Arguments     = ($ArgumentList -join ' ')
        ExitCode      = $null
        TimedOut      = $false
        StdOut        = ''
        StdErr        = ''
        StdOutBytes   = $null
        LaunchError   = ''
        ChildEnvDotNetRoot = [string]$psi.EnvironmentVariables['DOTNET_ROOT']
    }

    $proc = New-Object System.Diagnostics.Process
    $proc.StartInfo = $psi
    try {
        [void]$proc.Start()
    } catch {
        $result.LaunchError = $_.Exception.Message
        return $result
    }

    $errTask = $proc.StandardError.ReadToEndAsync()
    if ($Binary) {
        $ms = New-Object System.IO.MemoryStream
        try {
            $proc.StandardOutput.BaseStream.CopyTo($ms)
            $result.StdOutBytes = $ms.ToArray()
        } catch {
            $result.LaunchError = 'stdout copy failed: ' + $_.Exception.Message
        }
        $ms.Dispose()
    } else {
        $outTask = $proc.StandardOutput.ReadToEndAsync()
        $ok = $proc.WaitForExit($TimeoutSeconds * 1000)
        if (-not $ok) {
            try { $proc.Kill() } catch { }
            $result.TimedOut = $true
        }
        if (-not $result.TimedOut) {
            $result.StdOut = $outTask.Result
            $result.StdErr = $errTask.Result
            $result.ExitCode = $proc.ExitCode
        }
        $proc.Dispose()
        return $result
    }

    $ok = $proc.WaitForExit($TimeoutSeconds * 1000)
    if (-not $ok) {
        try { $proc.Kill() } catch { }
        $result.TimedOut = $true
    }
    if (-not $result.TimedOut) { $result.ExitCode = $proc.ExitCode }
    $result.StdErr = $errTask.Result
    $proc.Dispose()
    return $result
}

# R2: DOTNET_ROOT has to exist AND have to run, before any measurement.
function Get-DotNetEnvStatus {
    $status = @{
        Status  = 'OK'
        Root    = ''
        Version = ''
        Reason  = 'DOTNET_ROOT set, directory present, dotnet --version ran'
    }
    $root = [string]$env:DOTNET_ROOT
    $status.Root = $root
    if ([string]::IsNullOrWhiteSpace($root)) {
        $status.Status = 'MISSING'
        $status.Reason = 'DOTNET_ROOT is not set in this process environment'
        return $status
    }
    if (-not [System.IO.Directory]::Exists($root)) {
        $status.Status = 'UNUSABLE'
        $status.Reason = 'DOTNET_ROOT directory does not exist'
        return $status
    }
    $exe = [System.IO.Path]::Combine($root, 'dotnet.exe')
    if (-not [System.IO.File]::Exists($exe)) {
        $status.Status = 'UNUSABLE'
        $status.Reason = 'dotnet.exe is not present under DOTNET_ROOT'
        return $status
    }
    $r = Invoke-CheckedProcess -FilePath $exe -ArgumentList @('--version') -TimeoutSeconds 60 -Quiet
    if ($r.LaunchError -ne '' -or $r.TimedOut) {
        $status.Status = 'UNUSABLE'
        $status.Reason = 'dotnet --version did not run: ' + $r.LaunchError
        return $status
    }
    $v = ([string]$r.StdOut).Trim()
    if ($r.ExitCode -ne 0 -or $v -eq '') {
        # Exit code 0 with no output is exactly the silent-zero failure shape.
        $status.Status = 'UNUSABLE'
        $status.Reason = ('dotnet --version exit={0} stdout=<{1}>' -f $r.ExitCode, $v)
        return $status
    }
    $status.Version = $v
    return $status
}

# ---------------------------------------------------------------------------
# Text decoding (R4)
# ---------------------------------------------------------------------------

function Get-InvalidUtf8Offset {
    param([byte[]]$Bytes)
    $i = 0
    $n = $Bytes.Length
    while ($i -lt $n) {
        $b = $Bytes[$i]
        if ($b -lt 0x80) { $i++; continue }
        if ($b -ge 0xC2 -and $b -le 0xDF) {
            if ($i + 1 -ge $n) { return $i }
            if ($Bytes[$i + 1] -lt 0x80 -or $Bytes[$i + 1] -gt 0xBF) { return $i }
            $i += 2; continue
        }
        if ($b -ge 0xE0 -and $b -le 0xEF) {
            if ($i + 2 -ge $n) { return $i }
            $lo = 0x80; $hi = 0xBF
            if ($b -eq 0xE0) { $lo = 0xA0 }
            if ($b -eq 0xED) { $hi = 0x9F }
            if ($Bytes[$i + 1] -lt $lo -or $Bytes[$i + 1] -gt $hi) { return $i }
            if ($Bytes[$i + 2] -lt 0x80 -or $Bytes[$i + 2] -gt 0xBF) { return $i }
            $i += 3; continue
        }
        if ($b -ge 0xF0 -and $b -le 0xF4) {
            if ($i + 3 -ge $n) { return $i }
            $lo = 0x80; $hi = 0xBF
            if ($b -eq 0xF0) { $lo = 0x90 }
            if ($b -eq 0xF4) { $hi = 0x8F }
            if ($Bytes[$i + 1] -lt $lo -or $Bytes[$i + 1] -gt $hi) { return $i }
            for ($k = 2; $k -le 3; $k++) {
                if ($Bytes[$i + $k] -lt 0x80 -or $Bytes[$i + $k] -gt 0xBF) { return $i }
            }
            $i += 4; continue
        }
        return $i
    }
    return -1
}

# Decodes a byte array to text without ever silently substituting characters.
# Returns a hashtable; Text is only meaningful when Ok is true.
function ConvertFrom-SafeBytes {
    param([Parameter(Mandatory = $true)][byte[]]$Bytes)

    $out = @{
        Ok        = $false
        Text      = ''
        Encoding  = 'unknown'
        Bom       = 'none'
        BomOffset = 0
        Chars     = 0
        Lines     = 0
        Bytes     = $Bytes.Length
        Error     = ''
        ErrorOffset = -1
        LiteralFffd = 0
        Guessed   = $false
    }

    if ($null -eq $Bytes -or $Bytes.Length -eq 0) {
        $out.Ok = $true
        $out.Encoding = 'empty'
        return $out
    }

    $offset = 0
    if ($Bytes.Length -ge 3 -and $Bytes[0] -eq 0xEF -and $Bytes[1] -eq 0xBB -and $Bytes[2] -eq 0xBF) {
        $out.Bom = 'UTF8'; $offset = 3
    } elseif ($Bytes.Length -ge 2 -and $Bytes[0] -eq 0xFF -and $Bytes[1] -eq 0xFE) {
        if ($Bytes.Length -ge 4 -and $Bytes[2] -eq 0x00 -and $Bytes[3] -eq 0x00) {
            $out.Bom = 'UTF32LE'; $offset = 4
        } else { $out.Bom = 'UTF16LE'; $offset = 2 }
    } elseif ($Bytes.Length -ge 2 -and $Bytes[0] -eq 0xFE -and $Bytes[1] -eq 0xFF) {
        $out.Bom = 'UTF16BE'; $offset = 2
    }
    $out.BomOffset = $offset

    # Slice off the BOM if there is one. When there is no BOM the body IS the
    # input: a zero-filled array here would decode to NUL characters and the
    # tool would report a confident, completely wrong answer.
    if ($offset -gt 0) {
        $body = New-Object byte[] ($Bytes.Length - $offset)
        [System.Array]::Copy($Bytes, $offset, $body, 0, $body.Length)
    } else {
        $body = $Bytes
    }

    switch ($out.Bom) {
        'UTF8' {
            $out.Encoding = 'utf-8'
            $enc = New-Object System.Text.UTF8Encoding($false, $true)
            try { $out.Text = $enc.GetString($body) } catch {
                $out.Error = $_.Exception.Message
                $out.ErrorOffset = (Get-InvalidUtf8Offset -Bytes $body)
                return $out
            }
        }
        'UTF16LE' { $out.Encoding = 'utf-16le'; $out.Text = [System.Text.Encoding]::Unicode.GetString($body) }
        'UTF16BE' { $out.Encoding = 'utf-16be'; $out.Text = [System.Text.Encoding]::BigEndianUnicode.GetString($body) }
        'UTF32LE' { $out.Encoding = 'utf-32le'; $out.Text = [System.Text.Encoding]::UTF32.GetString($body) }
        default {
            # No BOM. Decide between UTF-8 and BOM-less UTF-16 by NUL density,
            # then decode UTF-8 strictly. A guess is reported as a guess.
            $limit = [math]::Min(4096, $body.Length)
            $nul = 0
            for ($i = 0; $i -lt $limit; $i++) { if ($body[$i] -eq 0) { $nul++ } }
            if ($limit -gt 0 -and $nul -gt ($limit / 4)) {
                $out.Encoding = 'utf-16le-guessed'
                $out.Guessed = $true
                $out.Text = [System.Text.Encoding]::Unicode.GetString($body)
                $out.Ok = $true
            } else {
                $out.Encoding = 'utf-8'
                $enc = New-Object System.Text.UTF8Encoding($false, $true)
                try { $out.Text = $enc.GetString($body) } catch {
                    $out.Error = $_.Exception.Message
                    $out.ErrorOffset = (Get-InvalidUtf8Offset -Bytes $body)
                    return $out
                }
            }
        }
    }

    $out.Chars = $out.Text.Length
    if ($out.Chars -gt 0) {
        $out.Lines = ([regex]::Matches($out.Text, "`n")).Count
    }
    $out.LiteralFffd = ([regex]::Matches($out.Text, [regex]::Escape([string][char]0xFFFD))).Count
    $out.Ok = $true
    return $out
}

function Read-SafeTextFile {
    param([Parameter(Mandatory = $true)][string]$Path)
    if (-not [System.IO.File]::Exists($Path)) {
        return @{ Ok = $false; Error = 'file does not exist'; Text = ''; Encoding = ''; Bom = 'none'; Chars = 0; Lines = 0; Bytes = 0 }
    }
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    $res = ConvertFrom-SafeBytes -Bytes $bytes
    return $res
}

# ---------------------------------------------------------------------------
# Normalisation for content search (R5)
# ---------------------------------------------------------------------------

# Builds a regex from a literal needle that tolerates:
#   - any run of whitespace where the needle has a single space (indentation,
#     a literal wrapped across lines, a CRLF vs LF difference)
#   - the string-concatenation glue that appears between the parts:
#     '" +' end of line, '"' opening the next line, any amount of '+'
# Token boundaries inside the needle are therefore never assumed to be on one
# line and are never assumed to be adjacent in the source.
function ConvertTo-TolerantSearchPattern {
    param(
        [Parameter(Mandatory = $true)][string]$Needle,
        [switch]$CaseSensitive
    )
    # @() so that a single-word needle is still an array under StrictMode.
    $parts = @($needle -split '\s+' | Where-Object { $_ -ne '' })
    if ($parts.Count -eq 0) { throw 'needle is empty after whitespace normalisation' }
    $sep = '(?:\s+|["'']?\s*\+[\s"'']*["'']?\s*)'
    $quoted = ($parts | ForEach-Object { [regex]::Escape($_) }) -join $sep
    $opts = [System.Text.RegularExpressions.RegexOptions]::IgnoreCase
    if ($CaseSensitive) { $opts = [System.Text.RegularExpressions.RegexOptions]::None }
    return New-Object System.Text.RegularExpressions.Regex($quoted, $opts)
}

function Get-LineOfOffset {
    param([Parameter(Mandatory = $true)][string]$Text, [int]$Offset)
    if ($Offset -le 0) { return 1 }
    $n = 0
    for ($i = 0; $i -lt $Offset -and $i -lt $Text.Length; $i++) {
        if ($Text[$i] -eq [char]10) { $n++ }
    }
    return $n + 1
}

function ConvertTo-SingleLine {
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string]$Text)
    return ($Text -replace '\s+', ' ').Trim()
}

# ---------------------------------------------------------------------------
# Measurement degeneracy (R6 / "all zero is not a result")
# ---------------------------------------------------------------------------

function Test-Measurement {
    param(
        [Parameter(Mandatory = $true)][double[]]$Values,
        [int]$MinSamples = 2,
        [int]$MinNonZero = 1,
        [switch]$AllowConstant
    )
    $r = @{
        Verdict   = 'OK'
        Reason    = ''
        Samples   = $Values.Count
        Min       = $null
        Max       = $null
        Mean      = $null
        Distinct  = 0
        Zeros     = 0
        NonZeros  = 0
    }
    if ($Values.Count -eq 0) {
        $r.Verdict = 'INCONCLUSIVE'
        $r.Reason = 'no numeric samples were found'
        return $r
    }
    $zeros = 0
    foreach ($v in $Values) { if ($v -eq 0) { $zeros++ } }
    $distinct = @($Values | Sort-Object -Unique).Count
    $r.Zeros = $zeros
    $r.NonZeros = $Values.Count - $zeros
    $r.Distinct = $distinct
    $r.Min = ($Values | Measure-Object -Minimum).Minimum
    $r.Max = ($Values | Measure-Object -Maximum).Maximum
    $r.Mean = [math]::Round((($Values | Measure-Object -Average).Average), 6)

    if ($Values.Count -lt $MinSamples) {
        $r.Verdict = 'INCONCLUSIVE'
        $r.Reason = ('only {0} sample(s); at least {1} required to call a set constant' -f $Values.Count, $MinSamples)
        return $r
    }
    if ($zeros -eq $Values.Count) {
        $r.Verdict = 'FAIL'
        $r.Reason = 'every sample is 0: the run produced no measurement, it did not measure zero'
        return $r
    }
    if ($r.NonZeros -lt $MinNonZero) {
        $r.Verdict = 'FAIL'
        $r.Reason = ('only {0} non-zero sample(s); at least {1} required' -f $r.NonZeros, $MinNonZero)
        return $r
    }
    if ($distinct -eq 1 -and -not $AllowConstant) {
        $r.Verdict = 'FAIL'
        $r.Reason = 'every sample is identical: a constant column is a stuck probe, not a measurement'
        return $r
    }
    return $r
}