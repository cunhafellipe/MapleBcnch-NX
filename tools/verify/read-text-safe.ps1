# read-text-safe.ps1 -- read a text file without losing a single character.
#
# Incident 4: Get-Content on a UTF-8-without-BOM file decoded the bytes with
# the system ANSI code page. Reproduced on this machine (Windows PowerShell
# 5.1): a 15 character UTF-8 file came back as 16 characters ending in two
# U+003F '?' replacements. The file looked destroyed and the work looked lost.
#
# This tool never falls back to a lossy decode. It reports the encoding it
# decided on, and an undecodable byte is a FAIL with the exact offset, not a
# U+FFFD substitution.
#
# Exit: 0 PASS  1 FAIL  2 INCONCLUSIVE

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $Path,
    [string]  $Grep = '',
    [int]     $MaxLines = 0,
    [int]     $MaxGrepHits = 60,
    [switch]  $ShowFirstBytes,
    [int]     $FirstBytes = 16,
    [switch]  $ReportCodepoints
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib\Common.ps1')

Initialize-Verification -Tool 'read-text-safe' -Checked @(
    'BOM detection, then strict UTF-8 validation of every byte',
    'the decoded character count and the literal U+FFFD count',
    'optional single-line grep over the decoded text'
)

if (-not [System.IO.File]::Exists($Path)) {
    Set-Field 'PATH' $Path
    Complete-Verification -Result 'INCONCLUSIVE' -Message 'file does not exist'
}

$bytes = [System.IO.File]::ReadAllBytes($Path)
$dec = ConvertFrom-SafeBytes -Bytes $bytes

Set-Field 'PATH'            $Path
Set-Field 'SIZE_BYTES'      ([string]$dec.Bytes)
Set-Field 'BOM'             $dec.Bom
Set-Field 'ENCODING'        $dec.Encoding
Set-Field 'ENCODING_GUESSED' ([string]$dec.Guessed)
Set-Field 'CHARS'           ([string]$dec.Chars)
Set-Field 'LF_LINES'        ([string]$dec.Lines)
Set-Field 'LITERAL_FFFD'    ([string]$dec.LiteralFffd)
Set-Field 'DECODE_LOSSLESS' 'true'

if ($ShowFirstBytes) {
    $take = [math]::Min($FirstBytes, $bytes.Length)
    $hex = @()
    for ($i = 0; $i -lt $take; $i++) { $hex += ('{0:X2}' -f $bytes[$i]) }
    Add-Detail ('FIRST_BYTES: ' + ($hex -join ' '))
    Add-Detail ('HEX_ASCII  : ' + (($hex | ForEach-Object { $v = [Convert]::ToInt32($_, 16); if ($v -ge 32 -and $v -lt 127) { [char]$v } else { '.' } }) -join ''))
}

if (-not $dec.Ok) {
    Set-Field 'DECODE_ERROR'  $dec.Error
    Set-Field 'ERROR_OFFSET'  ([string]$dec.ErrorOffset)
    Complete-Verification -Result 'FAIL' -Message ('not valid UTF-8 at byte offset ' + $dec.ErrorOffset + '; the bytes were NOT decoded with a replacement character')
}

if ($dec.Guessed) {
    Add-Detail 'ENCODING WAS GUESSED from NUL density: this file has no BOM and the guess is not certain'
}

# Show what the unsafe reader would have produced, so the difference is visible.
if ($env:OS -eq 'Windows_NT') {
    $legacy = $null
    try { $legacy = [System.Text.Encoding]::Default } catch { $legacy = $null }
    if ($null -ne $legacy -and $dec.Bom -eq 'none') {
        $asDefault = $legacy.GetString($bytes)
        # [regex]::Escape is required: [char]0x3F is '?', which is a regex
        # quantifier and throws "Quantifier {x,y} following nothing".
        $qMark = [regex]::Escape([string][char]0x003F)
        $replc = [regex]::Escape([string][char]0xFFFD)
        $lossy = ([regex]::Matches($asDefault, $qMark)).Count +
                 ([regex]::Matches($asDefault, $replc)).Count
        Set-Field 'CODEPAGE_DECODE_CHARS' ([string]$asDefault.Length)
        Set-Field 'CODEPAGE_DECODE_LOSSY_CHARS' ([string]$lossy)
        Add-Detail ('CODEPAGE COMPARISON: default decoding gives ' + $asDefault.Length + ' chars (strict UTF-8 gives ' + $dec.Chars + '); ' + $lossy + ' question-mark or replacement characters')
    }
}

if ($ReportCodepoints) {
    $take = [math]::Min(24, $dec.Text.Length)
    $codes = @()
    for ($i = 0; $i -lt $take; $i++) { $codes += ([int]$dec.Text[$i]) }
    Set-Field 'CODEPOINTS_FIRST' ($codes -join ',')
    Add-Detail ('CODEPOINTS_FIRST: ' + ($codes -join ','))
}

if ($Grep -ne '') {
    $rx = New-Object System.Text.RegularExpressions.Regex($Grep)
    $hits = 0
    $lineNo = 0
    foreach ($line in ([regex]::Split($dec.Text, "`n"))) {
        $lineNo++
        if ($rx.IsMatch($line)) {
            $hits++
            if ($hits -le $MaxGrepHits) {
                $shown = $line -replace "`r", ''
                if ($shown.Length -gt 400) { $shown = $shown.Substring(0, 400) + '...<truncated>' }
                Add-Detail ('LINE ' + $lineNo + ': ' + $shown)
            }
        }
        if ($MaxLines -gt 0 -and $lineNo -ge $MaxLines) { break }
    }
    Set-Field 'GREP_PATTERN'  $Grep
    Set-Field 'GREP_MATCHES'  ([string]$hits)
    if ($hits -eq 0) {
        Complete-Verification -Result 'FAIL' -Message 'the grep pattern matched nothing; the decode itself succeeded'
    }
} elseif ($MaxLines -gt 0) {
    $lineNo = 0
    foreach ($line in ([regex]::Split($dec.Text, "`n"))) {
        $lineNo++
        if ($lineNo -gt $MaxLines) { break }
        $shown = $line -replace "`r", ''
        if ($shown.Length -gt 400) { $shown = $shown.Substring(0, 400) + '...<truncated>' }
        Add-Detail ('LINE ' + $lineNo + ': ' + $shown)
    }
}

Complete-Verification -Result 'PASS' -Message 'decoded losslessly with a strict decoder'