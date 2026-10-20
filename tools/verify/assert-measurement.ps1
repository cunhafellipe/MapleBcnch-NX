# assert-measurement.ps1 -- refuse to call an empty measurement a result.
#
# The rule this enforces, from STATE.md:
#   "Any measurement or verification tool must first pass a known-answer
#    smoke test. When a tool outputs nothing, zeros, or all-identical values,
#    suspect the tool first and the data second."
#
# A measurement is only accepted when it has samples, they are not all zero,
# they are not all identical, and the process that produced them exited 0 with
# non-empty output. Otherwise the answer is FAIL or INCONCLUSIVE, never a
# table of zeros that reads like data.
#
# Numbers are parsed with the invariant culture. A locale that reads "1,5"
# as 1.5 turns a real measurement into a wrong one without any error.
#
# Exit: 0 PASS  1 FAIL  2 INCONCLUSIVE  3 TOOL_ERROR

[CmdletBinding()]
param(
    [string] $Path = '',
    [string] $Values = '',
    [string] $Command = '',
    [string[]] $CommandArgs = @(),
    [string] $Key = '',
    [int]    $MinSamples = 2,
    [int]    $MinNonZero = 1,
    [switch] $AllowConstant,
    [switch] $AllowAllZero,
    [int]    $TailLines = 12
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib\Common.ps1')

Initialize-Verification -Tool 'assert-measurement' -Checked @(
    'that samples exist at all',
    'that they are not all zero',
    'that they are not all identical',
    'that the producing process exited 0 and produced non-empty output'
)

Set-Field 'MIN_SAMPLES'  ([string]$MinSamples)
Set-Field 'MIN_NONZERO'  ([string]$MinNonZero)
Set-Field 'ALLOW_CONSTANT' ([string]$AllowConstant)
Set-Field 'ALLOW_ALL_ZERO' ([string]$AllowAllZero)

$inv = [System.Globalization.CultureInfo]::InvariantCulture
$samples = New-Object System.Collections.ArrayList
$sourceType = ''
$origin = ''

function Add-NumberToken {
    param([string]$Token, [string]$Where)
    $t = $Token.Trim()
    if ($t -eq '') { return }
    $v = 0.0
    if ([double]::TryParse($t, [System.Globalization.NumberStyles]::Float, $inv, [ref]$v)) {
        [void]$samples.Add($v)
    }
}

function Extract-NumbersFromText {
    param([string]$Text, [string]$Where)
    $addedBefore = $samples.Count
    $trimmed = $Text.Trim()
    if ($trimmed.StartsWith('[') -and $trimmed.EndsWith(']')) {
        try {
            $arr = ConvertFrom-Json -InputObject $trimmed -ErrorAction Stop
            foreach ($x in @($arr)) {
                if ($x -is [int] -or $x -is [long] -or $x -is [double] -or $x -is [decimal]) {
                    [void]$samples.Add([double]$x)
                } else { Add-NumberToken -Token ([string]$x) -Where $Where }
            }
            return
        } catch {
            Add-Detail 'JSON ARRAY PARSE FAILED, falling back to token scan: ' + $_.Exception.Message
        }
    }
    foreach ($line in ([regex]::Split($Text, "`r?`n"))) {
        if ($line.Trim() -eq '') { continue }
        if ($Key -ne '') {
            $m = [regex]::Match($line, '^\s*([^:=\t,;]+)\s*[:=,\t]\s*(.*)$')
            if (-not $m.Success) { continue }
            if ($m.Groups[1].Value.Trim() -ne $Key) { continue }
            $rest = $m.Groups[2].Value
        } else {
            $rest = $line
        }
        foreach ($tok in ($rest -split '[,;\t ]+')) { Add-NumberToken -Token $tok -Where $Where }
    }
    return
}

# --- source selection -----------------------------------------------------
$exec = $null
if ($Command -ne '') {
    $sourceType = 'command'
    $origin = ($Command + ' ' + ($CommandArgs -join ' ')).Trim()

    # Incident 2: a child that exits in one second and prints nothing is not
    # a measurement of zero, it is a tool that did not run.
    if ($script:VerifyDotNet.Status -ne 'OK') {
        Set-Field 'CHECK_CHILD_ENV' $script:VerifyDotNet.Status
        Complete-Verification -Result 'FAIL' -Message ('refusing to run the measurement: ' + $script:VerifyDotNet.Reason)
    }
    Set-Field 'CHECK_CHILD_ENV' 'OK'
    $exec = Invoke-CheckedProcess -FilePath $Command -ArgumentList $CommandArgs -TimeoutSeconds 300
    Set-Field 'CHILD_ENV_DOTNET_ROOT' $exec.ChildEnvDotNetRoot
    Set-Field 'EXEC_EXIT'       ([string]$exec.ExitCode)
    Set-Field 'EXEC_STDOUT_CHARS' ([string]([string]$exec.StdOut).Length)
    Set-Field 'EXEC_TIMED_OUT'  ([string]$exec.TimedOut)

    if ($exec.LaunchError -ne '') {
        Complete-Verification -Result 'FAIL' -Message ('the measurement process did not start: ' + $exec.LaunchError)
    }
    if ($exec.TimedOut) {
        Complete-Verification -Result 'FAIL' -Message 'the measurement process timed out; there is no data to assert on'
    }
    if ($exec.ExitCode -ne 0) {
        $tail = @()
        foreach ($l in (([string]$exec.StdErr + "`n" + [string]$exec.StdOut) -split "`r?`n")) {
            if ($l.Trim() -eq '') { continue }
            $tail += $l
        }
        if ($tail.Count -gt $TailLines) { $tail = $tail[($tail.Count - $TailLines)..($tail.Count - 1)] }
        foreach ($l in $tail) { Add-Detail ('CHILD OUTPUT: ' + $l) }
        Complete-Verification -Result 'FAIL' -Message ('the measurement process exited ' + $exec.ExitCode + '; a failed run must never be reported as zeros')
    }
    if (([string]$exec.StdOut).Trim() -eq '') {
        Complete-Verification -Result 'FAIL' -Message 'the measurement process exited 0 but printed nothing; this is incident 2 wearing a different hat'
    }
    Extract-NumbersFromText -Text ([string]$exec.StdOut) -Where 'stdout'
} elseif ($Values -ne '') {
    $sourceType = 'inline'
    $origin = '-Values'
    Extract-NumbersFromText -Text $Values -Where 'inline'
} elseif ($Path -ne '') {
    $sourceType = 'file'
    $origin = $Path
    if (-not [System.IO.File]::Exists($Path)) {
        Complete-Verification -Result 'INCONCLUSIVE' -Message 'measurement file does not exist'
    }
    $dec = Read-SafeTextFile -Path $Path
    if (-not $dec.Ok) {
        Set-Field 'DECODE_ERROR' $dec.Error
        Complete-Verification -Result 'INCONCLUSIVE' -Message 'the measurement file could not be decoded; a mis-decoded file measures nothing'
    }
    Extract-NumbersFromText -Text ([string]$dec.Text) -Where ('file:' + $Path)
} else {
    Complete-Verification -Result 'INCONCLUSIVE' -Message 'give one of -Command, -Path or -Values'
}

Set-Field 'SOURCE'      $origin
Set-Field 'SOURCE_TYPE' $sourceType
Set-Field 'SAMPLES'     ([string]$samples.Count)
if ($samples.Count -le 40) {
    Set-Field 'SAMPLE_VALUES' (($samples | ForEach-Object { [string]$_ }) -join ',')
}

$t = Test-Measurement -Values ([double[]]$samples.ToArray()) -MinSamples $MinSamples -MinNonZero $MinNonZero
Set-Field 'VERDICT'  $t.Verdict
Set-Field 'REASON'   $t.Reason
Set-Field 'ZEROS'    ([string]$t.Zeros)
Set-Field 'NONZEROS' ([string]$t.NonZeros)
Set-Field 'DISTINCT' ([string]$t.Distinct)
if ($null -ne $t.Min) { Set-Field 'MIN' ([string]$t.Min); Set-Field 'MAX' ([string]$t.Max); Set-Field 'MEAN' ([string]$t.Mean) }

if ($AllowAllZero -and $t.Zeros -eq $t.Samples) {
    Add-Detail 'ALL-ZERO was explicitly allowed with -AllowAllZero; the caller has taken responsibility for this value'
    Complete-Verification -Result 'PASS' -Trust 'MEDIUM' -Message 'all samples are zero and the caller allowed it explicitly'
}

if ($t.Verdict -eq 'INCONCLUSIVE') {
    Complete-Verification -Result 'INCONCLUSIVE' -Message ('cannot judge this measurement: ' + $t.Reason)
}
if ($t.Verdict -eq 'FAIL') {
    Complete-Verification -Result 'FAIL' -Message ('the measurement is degenerate: ' + $t.Reason)
}
Complete-Verification -Result 'PASS' -Message ('{0} distinct non-degenerate samples accepted' -f $t.Distinct)