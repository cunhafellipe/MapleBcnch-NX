# content-assert.ps1 -- assert that a token is present (or absent) in a file
# without caring where the compiler put the line breaks.
#
# Incident 5: a regex was run against a message that the source assembles
# from three concatenated string literals. The needle exists in the COMPILED
# string and not in any single line of the file, so the search reported
# "not fixed" about code that was already fixed.
#
# Two passes are run and the one that matched is reported:
#   PASS 1 EXACT      - the literal needle appears as written
#   PASS 2 TOLERANT   - any run of whitespace stands for the needle's single
#                       space (indentation, a literal wrapped over lines) and
#                       string concatenation glue (" +  between two literals)
#                       is transparent
#
# If neither matches the tool says NOT_FOUND and exits 1. It never reports
# "probably fine".
#
# Exit: 0 PASS  1 FAIL  2 INCONCLUSIVE

[CmdletBinding()]
param(
    [string] $Path = '',
    [string] $Needle = '',
    [string] $NeedleFile = '',
    [switch] $Negate,
    [int]    $MinCount = 1,
    [int]    $Context = 0,
    [switch] $CaseSensitive
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib\Common.ps1')

Initialize-Verification -Tool 'content-assert' -Checked @(
    'exact substring pass on the strictly decoded file text',
    'whitespace and string-concatenation tolerant pass, reported with the matching line number',
    'the file decode itself, which must succeed before absence can be claimed'
)

# A needle that contains non-ASCII text must not be typed into a command
# line. The command channel on this machine corrupts it, and the corruption
# looks exactly like "the text is not in the file". Measured: passing
# -Needle with CJK characters through the shell arrived mangled and produced
# "the needle could not be turned into a search pattern".
if ($NeedleFile -ne '') {
    $nf = Read-SafeTextFile -Path $NeedleFile
    if (-not $nf.Ok) {
        Set-Field 'NEEDLE_FILE_ERROR' $nf.Error
        Complete-Verification -Result 'INCONCLUSIVE' -Message 'the needle file could not be decoded'
    }
    # first line only, without its terminator
    $Needle = (([regex]::Split([string]$nf.Text, "`r?`n")) | Select-Object -First 1)
    Set-Field 'NEEDLE_SOURCE' ('file:' + $NeedleFile)
}
if ([string]::IsNullOrEmpty($Path) -or [string]::IsNullOrEmpty($Needle)) {
    Complete-Verification -Result 'INCONCLUSIVE' -Message 'both -Path and a needle (-Needle or -NeedleFile) are required'
}

Set-Field 'NEEDLE'   $Needle
Set-Field 'NEGATE'   ([string]$Negate)
Set-Field 'MIN_COUNT' ([string]$MinCount)
Set-Field 'METHOD'   'exact substring, then a regex that tolerates whitespace runs and " + " concatenation glue between tokens'

$rxTolerant = $null
$rxExact = $null
try {
    $rxTolerant = ConvertTo-TolerantSearchPattern -Needle $Needle -CaseSensitive:$CaseSensitive
    $rxExact = New-Object System.Text.RegularExpressions.Regex(
        [regex]::Escape($Needle),
        $(if ($CaseSensitive) { [System.Text.RegularExpressions.RegexOptions]::None }
          else { [System.Text.RegularExpressions.RegexOptions]::IgnoreCase }))
} catch {
    Set-Field 'NEEDLE_ERROR' $_.Exception.Message
    Complete-Verification -Result 'INCONCLUSIVE' -Message 'the needle could not be turned into a search pattern'
}

$anyDecoded = $false
$allFound = $true
$anyMissing = $false
$anyFound = $false
$reports = @()

foreach ($p in $Path) {
    $dec = Read-SafeTextFile -Path $p
    if (-not $dec.Ok) {
        Set-Field ('DECODE_' + $p) ('FAILED: ' + $dec.Error)
        $allFound = $false
        $anyMissing = $true
        Add-Detail ('CANNOT DECODE: ' + $p + ' -> ' + $dec.Error)
        if (-not [string]::IsNullOrEmpty($dec.ErrorOffset) -and $dec.ErrorOffset -ge 0) {
            Add-Detail ('  first invalid byte offset: ' + $dec.ErrorOffset)
        }
        continue
    }
    $anyDecoded = $true
    $text = [string]$dec.Text

    $mode = ''
    $line = 0
    $count = 0
    $sample = ''

    $mExact = $rxExact.Match($text)
    if ($mExact.Success) {
        $mode = 'EXACT_SUBSTRING'
        $line = Get-LineOfOffset -Text $text -Offset $mExact.Index
        $count = $rxExact.Matches($text).Count
        $sample = $mExact.Value
    } else {
        $mTol = $rxTolerant.Match($text)
        if ($mTol.Success) {
            $mode = 'TOLERANT_CONCAT'
            $line = Get-LineOfOffset -Text $text -Offset $mTol.Index
            $count = $rxTolerant.Matches($text).Count
            $sample = $mTol.Value
        }
    }

    if ($mode) { $anyFound = $true }
    $found = ($mode -ne '') -and ($count -ge $MinCount)
    if (-not $mode) { $allFound = $false; $anyMissing = $true }
    elseif ($count -lt $MinCount) { $allFound = $false; $anyMissing = $true }

    $sampleFlat = ($sample -replace "`r", '' -replace "`n", '\n')
    if ($sampleFlat.Length -gt 240) { $sampleFlat = $sampleFlat.Substring(0, 240) + '...<truncated>' }

    $reports += ('{0}: mode={1} matches={2} line={3}' -f $p, $(if ($mode) { $mode } else { 'NOT_FOUND' }), $count, $line)
    Set-Field ('MODE_'   + $p) $(if ($mode) { $mode } else { 'NOT_FOUND' })
    Set-Field ('MATCHES_' + $p) ([string]$count)
    Set-Field ('LINE_'   + $p) ([string]$line)

    if ($mode) {
        $flat = ($text -replace "`r", '') -split "`n"
        $from = [math]::Max(0, $line - 1 - $Context)
        $to = [math]::Min($flat.Length - 1, $line - 1 + $Context)
        for ($i = $from; $i -le $to; $i++) {
            $shown = $flat[$i]
            if ($shown.Length -gt 300) { $shown = $shown.Substring(0, 300) + '...<truncated>' }
            Add-Detail ('  ' + ($i + 1).ToString().PadLeft(5) + ' | ' + $shown)
        }
    }
}

Set-Field 'REPORT' ($reports -join ' | ')

if (-not $anyDecoded) {
    Complete-Verification -Result 'INCONCLUSIVE' -Message 'no file could be decoded; absence of the needle cannot be claimed'
}

if ($Negate) {
    # Absence is only claimed for files that decoded cleanly AND were searched.
    if ($anyMissing) {
        Complete-Verification -Result 'INCONCLUSIVE' -Message '-Negate cannot claim absence: a file could not be searched'
    }
    if ($anyFound) {
        Complete-Verification -Result 'FAIL' -Message 'the needle was expected to be absent and it is present'
    }
    Complete-Verification -Result 'PASS' -Message 'the needle is absent from every file, and every file decoded cleanly'
}

if ($anyMissing) {
    Complete-Verification -Result 'FAIL' -Message 'the needle was not found; both the exact and the concatenation-tolerant pass failed'
}
Complete-Verification -Result 'PASS' -Message ('the needle is present in every file (mode: ' + $reports[0] + ')')