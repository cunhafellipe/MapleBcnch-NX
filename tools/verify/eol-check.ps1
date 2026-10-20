# eol-check.ps1 -- byte level newline census for one file, compared to HEAD.
#
# The repository is documented as having MIXED line endings: .gitattributes says
# "* text=auto eol=lf" but WzSaveService.cs was measured at 2,348 CRLF and 87
# bare-LF endings. Any tool that rewrites the whole file therefore turns one
# edited line into a several-hundred-line diff, and a normal text read cannot
# see it because Get-Content already normalised the endings before the count.
#
# Everything here is counted on the raw byte array. Nothing decodes, nothing
# normalises, nothing rewrites.
#
# Exit: 0 PASS  1 FAIL (file was inflated by a whole-file rewrite)  2 INCONCLUSIVE

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string[]]$Path,
    [string] $RepoRoot = '',
    [switch] $NoHeadCompare,
    [switch] $RequireHeadCompare,
    [double] $MaxChangedLineRatio = 0.25
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib\Common.ps1')

Initialize-Verification -Tool 'eol-check' -Checked @(
    'CRLF / bare-LF / bare-CR counts taken from the raw byte array of the working file',
    'the same census on the HEAD blob, read as raw bytes through git cat-file',
    'git diff --numstat to detect a whole-file rewrite (line-ending inflation)'
)

function Get-EolStats {
    param([byte[]]$Bytes)
    $crlf = 0; $lfTotal = 0; $crTotal = 0
    $firstCrlf = -1; $firstLf = -1
    $n = $Bytes.Length
    for ($i = 0; $i -lt $n; $i++) {
        $b = $Bytes[$i]
        if ($b -eq 0x0A) {
            $lfTotal++
            if ($i -gt 0 -and $Bytes[$i - 1] -eq 0x0D) {
                $crlf++
                if ($firstCrlf -lt 0) { $firstCrlf = $i - 1 }
            } elseif ($firstLf -lt 0) {
                $firstLf = $i
            }
        } elseif ($b -eq 0x0D) {
            $crTotal++
        }
    }
    $bom = 'none'
    if ($n -ge 3 -and $Bytes[0] -eq 0xEF -and $Bytes[1] -eq 0xBB -and $Bytes[2] -eq 0xBF) { $bom = 'UTF8' }
    elseif ($n -ge 2 -and $Bytes[0] -eq 0xFF -and $Bytes[1] -eq 0xFE) { $bom = 'UTF16LE' }
    elseif ($n -ge 2 -and $Bytes[0] -eq 0xFE -and $Bytes[1] -eq 0xFF) { $bom = 'UTF16BE' }

    # NUL density in the first 4 KiB: a text file has none.
    $limit = [math]::Min(4096, $n)
    $nul = 0
    for ($i = 0; $i -lt $limit; $i++) { if ($Bytes[$i] -eq 0) { $nul++ } }
    $utf16Suspect = ($limit -gt 0 -and $nul -gt ($limit / 4))

    return @{
        Bytes          = $n
        CRLF           = $crlf
        LF_Total       = $lfTotal
        CR_Total       = $crTotal
        BareLF         = $lfTotal - $crlf
        BareCR         = $crTotal - $crlf
        Lines          = $lfTotal + ($crTotal - $crlf)
        BOM            = $bom
        Utf16Suspect   = $utf16Suspect
        FirstCRLFOff   = $firstCrlf
        FirstBareLFOff = $firstLf
    }
}

# Normalises CRLF and bare CR to LF so two versions can be compared as TEXT.
function ConvertTo-LfText {
    param([byte[]]$Bytes)
    if ($null -eq $Bytes -or $Bytes.Length -eq 0) { return '' }
    $enc = New-Object System.Text.UTF8Encoding($false, $false)
    $s = $enc.GetString($Bytes)
    $s = $s -replace "`r`n", "`n"
    $s = $s -replace "`r", "`n"
    return $s
}

if ([string]::IsNullOrWhiteSpace($RepoRoot)) { $RepoRoot = Resolve-RepoRoot }
Set-Field 'REPO_ROOT' ([string]$RepoRoot)

$gitCmd = $null
if (-not $NoHeadCompare) {
    $gitCmd = Get-Command git -ErrorAction SilentlyContinue
    if ($null -eq $gitCmd -and [string]::IsNullOrWhiteSpace($RepoRoot)) {
        Complete-Verification -Result 'INCONCLUSIVE' -Message 'HEAD comparison needs git and a repository; pass -NoHeadCompare to skip it'
    }
}

$summary = @()
$failed = $false
$inconclusive = $false

foreach ($p in $Path) {
    if (-not [System.IO.File]::Exists($p)) {
        $inconclusive = $true
        Add-Detail ("MISSING: {0}" -f $p)
        continue
    }
    $bytes = [System.IO.File]::ReadAllBytes($p)
    $w = Get-EolStats -Bytes $bytes
    $name = [System.IO.Path]::GetFileName($p)

    $line = ('{0}: bytes={1} crlf={2} bare_lf={3} bare_cr={4} lines={5} bom={6} mixed={7} utf16_suspect={8}' -f `
        $name, $w.Bytes, $w.CRLF, $w.BareLF, $w.BareCR, $w.Lines, $w.BOM,
        ($w.CRLF -gt 0 -and $w.BareLF -gt 0), $w.Utf16Suspect)
    Add-Detail $line

    if ($w.Utf16Suspect) { $inconclusive = $true; Add-Detail '  WARNING: NUL density suggests this is not a plain text file' }
    if ($w.Bytes -eq 0) { $inconclusive = $true; Add-Detail '  WARNING: file is empty' }

    if ($NoHeadCompare) {
        $summary += ('{0}: crlf={1} bare_lf={2} mixed={3}' -f $name, $w.CRLF, $w.BareLF, ($w.CRLF -gt 0 -and $w.BareLF -gt 0))
        continue
    }

    $rel = ConvertTo-RepoRelative -RepoRoot $RepoRoot -Path $p
    if ($null -eq $rel) {
        $inconclusive = $true
        Add-Detail ('  INCONCLUSIVE: file is outside the repository, HEAD has no version to compare')
        $summary += ('{0}: crlf={1} bare_lf={2} head=UNAVAILABLE' -f $name, $w.CRLF, $w.BareLF)
        continue
    }

    $blob = Invoke-CheckedProcess -FilePath $gitCmd.Source `
               -ArgumentList @('-C', $RepoRoot, 'cat-file', '-p', ('HEAD:' + $rel)) -Binary
    if ($blob.ExitCode -ne 0 -or $null -eq $blob.StdOutBytes) {
        # New or untracked file: there is no HEAD blob. Report the working
        # tree facts and say the comparison is unavailable.
        $inconclusive = $true
        Add-Detail ('  HEAD blob unavailable (new or untracked file): ' + $name)
        $summary += ('{0}: crlf={1} bare_lf={2} head=UNAVAILABLE' -f $name, $w.CRLF, $w.BareLF)
        continue
    }
    $h = Get-EolStats -Bytes $blob.StdOutBytes

    # Whole-file rewrite detection: git's own line accounting.
    $numstat = Invoke-CheckedProcess -FilePath $gitCmd.Source -ArgumentList @('-C', $RepoRoot, 'diff', '--numstat', '--', $rel)
    $added = -1; $deleted = -1; $numstatState = 'ok'
    if ($numstat.ExitCode -ne 0) {
        $numstatState = 'failed'
        $inconclusive = $true
    } else {
        $nsLine = (([string]$numstat.StdOut) -split "`n" | Where-Object { $_.Trim() -ne '' } | Select-Object -First 1)
        if (-not $nsLine) {
            # Empty numstat means the file is byte identical to HEAD.
            $added = 0; $deleted = 0; $numstatState = 'unchanged'
        } else {
            $cols = $nsLine -split "`t"
            if ($cols.Count -ge 2 -and $cols[0] -ne '-' -and $cols[1] -ne '-') {
                [void][int]::TryParse($cols[0], [ref]$added)
                [void][int]::TryParse($cols[1], [ref]$deleted)
            } else {
                # Binary file: git refuses to count lines.
                $numstatState = 'binary'
            }
        }
    }
    Set-Field ('NUMSTAT_STATE_' + $name) $numstatState

    $headLines = [math]::Max(1, $h.Lines)
    $changed = $added + $deleted
    $ratio = if ($added -ge 0 -and $deleted -ge 0) { [math]::Round($changed / $headLines, 4) } else { -1 }

    # git's own line accounting CANNOT see a line-ending rewrite: with
    # "* text=auto eol=lf" the index stores LF, so an LF file rewritten as
    # CRLF diffs as one changed line (measured: changed_ratio=0.01 for a
    # 200 line file). The decisive test is therefore done here, on the bytes:
    # normalise both versions to LF and compare. Identical text but different
    # bytes means the whole file was rewritten.
    $wNorm = ConvertTo-LfText -Bytes $bytes
    $hNorm = ConvertTo-LfText -Bytes $blob.StdOutBytes
    $textEqual = ($wNorm -ceq $hNorm)
    $bytesEqual = ($w.Bytes -eq $h.Bytes)
    if ($textEqual) {
        $eolRewrite = -not $bytesEqual
    } else {
        $eolRewrite = $false
    }

    $inflated = $eolRewrite -or ($ratio -ge 0 -and $changed -gt 0 -and $ratio -gt $MaxChangedLineRatio)
    $normalised = ($h.BareLF -gt 0 -and $w.BareLF -eq 0 -and $w.CRLF -gt 0)

    Add-Detail ('  HEAD : crlf={0} bare_lf={1} bare_cr={2} lines={3} bytes={4}' -f $h.CRLF, $h.BareLF, $h.BareCR, $h.Lines, $h.Bytes)
    Add-Detail ('  DELTA: crlf={0} bare_lf={1} lines={2} numstat_added={3} numstat_deleted={4} changed_ratio={5} numstat={6}' -f `
        ($w.CRLF - $h.CRLF), ($w.BareLF - $h.BareLF), ($w.Lines - $h.Lines), $added, $deleted, $ratio, $numstatState)
    Add-Detail ('  TEXT: same_ignoring_line_endings={0} same_bytes={1}' -f $textEqual, $bytesEqual)
    if ($normalised) { Add-Detail '  ENDINGS_NORMALISED: HEAD had bare-LF endings and the working copy has none; a whole-file rewrite normalised them' }
    if ($eolRewrite) {
        Add-Detail '  INFLATED: the text is unchanged once line endings are normalised, but the bytes are different: the whole file was rewritten with different endings'
        $failed = $true
    } elseif ($inflated) {
        Add-Detail ('  INFLATED: {0} of {1} lines changed ({2}%) for an edit that should be small; use a byte level splice' -f $changed, $headLines, [int]($ratio * 100))
        $failed = $true
    }
    $summary += ('{0}: work crlf={1} bare_lf={2} | head crlf={3} bare_lf={4} | text_equal={5} eol_rewrite={6} changed_ratio={7}' -f `
        $name, $w.CRLF, $w.BareLF, $h.CRLF, $h.BareLF, $textEqual, $eolRewrite, $ratio)
}

Set-Field 'SUMMARY' ($summary -join ' | ')

if ($failed) { Complete-Verification -Result 'FAIL' -Message 'a file was rewritten wholesale; the diff is line-ending inflation' }
if ($inconclusive) { Complete-Verification -Result 'INCONCLUSIVE' -Trust 'MEDIUM' -Message 'some checks could not decide; the reported counts are real, the comparison is incomplete' }
Complete-Verification -Result 'PASS' -Message 'newline census taken from raw bytes and compared to HEAD'