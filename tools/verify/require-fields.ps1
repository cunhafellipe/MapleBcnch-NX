# require-fields.ps1 -- assert that a JSON payload still has the fields a
# measurement script is about to read.
#
# WHY THIS EXISTS (incident 12, tools/verify/AGENTS.md):
#
#   A field was renamed in the C# DTO. The measurement script kept the old
#   name. In PowerShell, reading a property that is not there yields $null,
#   and $null added to a counter is 0. The script then reported
#   "0 candidates were excluded" -- a number, a valid-looking answer, and the
#   exact opposite of the truth (924 exclusions).
#
#   It did not throw, did not print an error, and exited 0. A gate that
#   cannot measure must fail loudly: this tool exists so that "the field I am
#   about to read is not there" is a non-zero exit with the field named.
#
# It is a TOOL and not only a function, because a measurement script can call
# it as a child process and read its exit code, which is the only thing a
# gate can trust.
#
# USAGE
#   require-fields.ps1 -JsonBase64 <base64 of the JSON> -Fields 'a.b','c[].d'
#   require-fields.ps1 -JsonFile <path>          -Fields 'a.b'
#   require-fields.ps1 -Url <http://...>          -Fields 'a.b' [-Context ...]
#
#   -Json       the JSON text itself. Use -JsonBase64 when the caller cannot
#               pass quotes through a command line safely.
#   -JsonBase64 the JSON text, base64 encoded.
#   -JsonFile   a file holding the JSON.
#   -Url         fetch the JSON over HTTP instead.
#   -Fields      the required field paths. A single argument may hold several,
#               separated by commas: `powershell -File` does not split a
#               comma list for you (AGENTS.md rule 5), so this tool splits it.
#   -Context     a label used in the error message. Say what was being
#               measured; "the response" is not a label.
#   -AllowNull   a field that is PRESENT but null passes. Absent never passes.
#
# FIELD PATH GRAMMAR
#   a              top-level field
#   a.b            nested object
#   a[0]           array element by index
#   a[].b          field b of EVERY element of the array a, and the array
#                  must not be empty. An empty array is a measurement that
#                  measured nothing, and it is reported as a failure for the
#                  same reason a zero is.
#
# EXIT
#   0  every required field is present (and non-null unless -AllowNull)
#   1  at least one required field is absent, null, or an empty array
#   2  the tool could not decide (bad JSON, unreachable URL, no fields given)
#
# ASCII only: do not add non-ASCII characters to this file.

[CmdletBinding()]
param(
    [string] $Json,
    [string] $JsonBase64,
    [string] $JsonFile,
    [string] $Url,
    [string[]] $Fields = @(),
    [string] $Context = 'the response',
    [switch] $AllowNull
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib\Common.ps1')

Initialize-Verification -Tool 'require-fields' -Checked @(
    'that every named field is present in a JSON payload before it is read',
    'a missing field, a null field and an empty array are all failures, not zeros'
)

# ---------------------------------------------------------------------------
# Read the payload
# ---------------------------------------------------------------------------
$payloadText = ''
$source = ''

if ($JsonBase64 -ne '') {
    $source = 'base64'
    try {
        $payloadText = [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String($JsonBase64))
    } catch {
        Complete-Verification -Result 'INCONCLUSIVE' -Message ('the base64 payload could not be decoded: ' + $_.Exception.Message)
    }
} elseif ($JsonFile -ne '') {
    $source = 'file'
    $read = Read-SafeTextFile -Path $JsonFile
    if (-not $read.Ok) {
        Complete-Verification -Result 'INCONCLUSIVE' -Message ('the file could not be read: ' + $read.Error)
    }
    $payloadText = $read.Text
} elseif ($Url -ne '') {
    $source = 'url'
    try {
        $response = Invoke-WebRequest -Uri $Url -TimeoutSec 120 -UseBasicParsing
    } catch {
        Complete-Verification -Result 'INCONCLUSIVE' -Message ('the URL could not be fetched: ' + $_.Exception.Message)
    }
    $payloadText = [string]$response.Content
} elseif ($Json -ne '') {
    $source = 'inline'
    $payloadText = $Json
} else {
    Complete-Verification -Result 'INCONCLUSIVE' -Message 'no payload given: pass -Json, -JsonBase64, -JsonFile or -Url'
}

if ([string]::IsNullOrWhiteSpace($payloadText)) {
    Complete-Verification -Result 'INCONCLUSIVE' -Message ('the payload from ' + $source + ' was empty')
}

try {
    $payload = $payloadText | ConvertFrom-Json
} catch {
    Complete-Verification -Result 'INCONCLUSIVE' -Message ('the payload is not valid JSON: ' + $_.Exception.Message)
}

# ---------------------------------------------------------------------------
# The field list. One argument may carry several, comma separated.
# ---------------------------------------------------------------------------
$required = New-Object System.Collections.ArrayList
foreach ($f in $Fields) {
    if ($null -eq $f) { continue }
    foreach ($part in ([string]$f -split ',')) {
        $t = $part.Trim()
        if ($t -ne '') { [void]$required.Add($t) }
    }
}
if ($required.Count -eq 0) {
    Complete-Verification -Result 'INCONCLUSIVE' -Message 'no fields were named: a check that requires nothing checks nothing'
}
Set-Field 'FIELDS_REQUESTED' ([string]$required.Count)
Set-Field 'SOURCE' $source

# ---------------------------------------------------------------------------
# Walk a path
# ---------------------------------------------------------------------------
# Returns a hashtable: Found (bool), Reason (string), Values (the values found)
function Test-FieldPath {
    param(
        [Parameter(Mandatory = $true)]$Root,
        [Parameter(Mandatory = $true)][string]$Path
    )

    $current = @($Root)
    $walked = ''

    foreach ($segment in ($Path -split '\.')) {
        if ($segment -eq '') { continue }

        $name = $segment
        $allElements = $false
        $index = $null

        if ($segment -match '^(.*?)\[(\d*)\]$') {
            $name = $Matches[1]
            $indexText = $Matches[2]
            if ($indexText -eq '') { $allElements = $true } else { $index = [int]$indexText }
        }

        $next = New-Object System.Collections.ArrayList
        foreach ($item in $current) {
            if ($null -eq $item) { continue }
            $value = $item
            if ($name -ne '') {
                # A named segment reads a PROPERTY of the item. The item may be
                # a plain object (a JSON object) or a dictionary; both expose
                # their members through PSObject.Properties.
                $prop = $item.PSObject.Properties[$name]
                if ($null -eq $prop) { continue }
                $value = $prop.Value
                if ($null -eq $value) {
                    return @{ Found = $false; Reason = ('"' + $walked + $name + '" is null, so "' + $Path + '" was never measured'); Values = @() }
                }
            }
            if ($allElements -or $null -ne $index) {
                if ($value -is [string] -or -not ($value -is [System.Collections.IEnumerable])) {
                    continue
                }
                $list = @($value)
                if ($allElements) {
                    if ($list.Count -eq 0) {
                        return @{ Found = $false; Reason = ('the array at "' + $walked + '" is empty, so "' + $Path + '" was never measured'); Values = @() }
                    }
                    foreach ($e in $list) { [void]$next.Add($e) }
                } else {
                    if ($index -ge $list.Count) {
                        return @{ Found = $false; Reason = ('index ' + $index + ' is past the end of the array at "' + $walked + '"'); Values = @() }
                    }
                    [void]$next.Add($list[$index])
                }
            } else {
                [void]$next.Add($value)
            }
        }

        if ($next.Count -eq 0) {
            $where = if ($walked -eq '') { 'the payload' } else { '"' + $walked + '"' }
            return @{ Found = $false; Reason = ('no "' + $name + '" under ' + $where); Values = @() }
        }
        $current = @($next)
        $walked = if ($walked -eq '') { $name } else { $walked + '.' + $name }
    }

    # Every value found must be non-null unless null is allowed. A null where
    # the script expected a number is the same silent-zero shape as an absent
    # field, so it is a failure by default.
    if (-not $AllowNull) {
        foreach ($v in $current) {
            if ($null -eq $v) {
                return @{ Found = $false; Reason = ('"' + $Path + '" is present but null'); Values = @() }
            }
        }
    }
    return @{ Found = $true; Reason = ''; Values = $current }
}

# ---------------------------------------------------------------------------
# Check every required field, and report ALL the failures, not just the first.
# ---------------------------------------------------------------------------
$failures = New-Object System.Collections.ArrayList
$ok = 0
foreach ($path in $required) {
    $r = Test-FieldPath -Root $payload -Path $path
    if ($r.Found) {
        $ok++
        Add-Detail ('ok  ' + $path)
    } else {
        [void]$failures.Add($path)
        Add-Detail ('BAD ' + $path + ' -- ' + $r.Reason)
        Set-Field ('MISSING_' + ($failures.Count)) $path
    }
}

Set-Field 'FIELDS_OK' ([string]$ok)
Set-Field 'FIELDS_MISSING' ([string]$failures.Count)
Set-Field 'CONTEXT' $Context

if ($failures.Count -eq 0) {
    Complete-Verification -Result 'PASS' -Message ('all ' + $ok + ' required field(s) are present in ' + $Context)
}

$names = ($failures -join ', ')
Complete-Verification -Result 'FAIL' -Message (
    ('required field(s) absent from ' + $Context + ': ' + $names + ' -- reading them would have produced zeros, not measurements'))
