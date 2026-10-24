# Project.ps1 -- the values this repository declares, and the instances that
# hold the output dll.
#
# Shared by build-gate.ps1 and deploy.ps1. ASCII only: do not add non-ASCII
# characters to this file (a .ps1 read in the ANSI codepage mangles a path --
# incident 1 in tools/verify/AGENTS.md).
#
# WHY A SEPARATE FILE: pm/PROJECT.yaml is the single place a value that
# appears in more than one command lives. These helpers are the only code
# allowed to read it, so a value cannot be typed into a second file by
# accident. The repository root is DISCOVERED, never spelled: it contains
# non-ASCII characters and a literal in a .ps1 gets mangled on write (T-15).

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-ProjectScalar {
    <#
      One scalar value out of pm/PROJECT.yaml, or $null when the key is absent.
      A value that appears in more than one command belongs in that file, and
      this is the only code that reads it (pm/PROJECT.yaml, header).
    #>
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$Key
    )
    $path = Join-Path $RepoRoot 'pm\PROJECT.yaml'
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    # Read as UTF-8 explicitly: the default encoding is not safe for a file
    # that contains non-ASCII (R4 of lib/Common.ps1).
    $text = [System.IO.File]::ReadAllText($path, [System.Text.Encoding]::UTF8)
    $m = [regex]::Match($text, '(?m)^' + [regex]::Escape($Key) + ':\s*(.*)$')
    if (-not $m.Success) { return $null }
    $value = $m.Groups[1].Value.Trim()
    if ($value.Length -ge 2) {
        $first = $value.Substring(0, 1)
        $last = $value.Substring($value.Length - 1, 1)
        if (($first -eq "'" -and $last -eq "'") -or ($first -eq '"' -and $last -eq '"')) {
            $value = $value.Substring(1, $value.Length - 2)
        }
    }
    return $value.Trim()
}

function Get-ProjectMd5Invariants {
    <#
      Every `kind: md5` invariant in pm/SPEC.yaml, as objects with Id, File and
      Md5. These are the tripwires: a file this project does not otherwise
      touch, whose digest is declared. The DECLARED digest is read from the
      registry, never hard-coded here, so a legitimate change updates one
      place (pm/SPEC.yaml) and not two (R-17).
    #>
    param([Parameter(Mandatory = $true)][string]$RepoRoot)

    $path = Join-Path $RepoRoot 'pm\SPEC.yaml'
    if (-not (Test-Path -LiteralPath $path)) { return @() }
    $text = [System.IO.File]::ReadAllText($path, [System.Text.Encoding]::UTF8)

    $out = New-Object System.Collections.ArrayList
    # Each invariant is a "- id: <name>" block. The block runs until the next
    # "- id:" at the same indent or the end of the file.
    $blocks = [regex]::Matches($text, '(?ms)^\s*-\s+id:\s*(\S+)\s*$(.*?)(?=^\s*-\s+id:|\z)')
    foreach ($b in $blocks) {
        $body = $b.Groups[2].Value
        $kind = [regex]::Match($body, '(?m)^\s*kind:\s*(\S+)\s*$')
        if (-not $kind.Success -or $kind.Groups[1].Value -ne 'md5') { continue }
        $file = [regex]::Match($body, '(?m)^\s*file:\s*["'']?([^"''\r\n]+?)["'']?\s*$')
        $md5 = [regex]::Match($body, '(?m)^\s*md5:\s*["'']?([0-9a-fA-F]{32})["'']?\s*$')
        if (-not $file.Success -or -not $md5.Success) { continue }
        [void]$out.Add([pscustomobject]@{
            Id  = $b.Groups[1].Value
            File = $file.Groups[1].Value.Trim()
            Md5 = $md5.Groups[1].Value.ToLower()
        })
    }
    # ONE object with the items inside, for the same reason
    # Stop-MapleBenchInstances returns one: the shape is declared, not guessed.
    return [pscustomobject]@{ Items = @($out.ToArray()) }
}

function Get-ProcessModulePaths {
    <#
      The full paths of the modules loaded into a process, or $null when the
      list could not be read.

      WHY THIS EXISTS: Win32_Process.CommandLine is not always populated. On
      this machine PID 26272 (MapleBenchX.exe) and PID 34224 (dotnet.exe) both
      came back with an EMPTY CommandLine, so a command-line filter cannot
      see them. The loaded modules are the second, independent way to ask
      "does this process hold MapleBenchX.dll". A process whose modules cannot
      be enumerated is reported as $null -- UNKNOWN, never "no".

      MEASURED: on this machine $p.Modules is $null for a process whose module
      list the OS refuses to expose (PID 34224). Piping that $null into
      ForEach-Object runs the block once with $_ = $null, and StrictMode then
      throws "the property FileName cannot be found on this object" -- which
      used to be caught as "cannot enumerate", for the wrong reason. The null
      is checked explicitly instead.
    #>
    param([Parameter(Mandatory = $true)][int]$ProcessId)

    try {
        $p = Get-Process -Id $ProcessId -ErrorAction Stop
    } catch {
        return $null
    }
    $raw = $p.Modules
    if ($null -eq $raw) {
        # The OS would not give the list. UNKNOWN, not "no modules".
        return $null
    }
    $out = New-Object System.Collections.ArrayList
    foreach ($m in @($raw)) {
        if ($null -eq $m) { continue }
        $fn = $null
        try { $fn = $m.FileName } catch { continue }
        if ([string]::IsNullOrWhiteSpace($fn)) { continue }
        [void]$out.Add($fn)
    }
    return ,@($out.ToArray())
}

function Get-InvariantLegs {
    <#
      The three places one md5 invariant has to agree (R-17): the source file
      in the working tree, the file inside the build output, and the file the
      running server SERVES.

      The invariant file is declared repo-relative ("MapleBench/wwwroot/js/
      avatar.js"). The build output mirrors the PROJECT directory inside the
      output directory, and the server serves the same relative path. So the
      project segment is removed once and the remainder is used for both
      other legs.

      Getting this wrong does not fail loudly -- it makes a leg MISSING and
      the tripwire silently checks fewer legs than it claims. That happened:
      the build leg was built as <outDir>/MapleBench/wwwroot/... and reported
      MISSING, so only one of the two legs was actually compared.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$OutputDir,
        [Parameter(Mandatory = $true)][string]$OutputProjectName,
        [Parameter(Mandatory = $true)][string]$InvariantFile
    )

    $decl = $InvariantFile -replace '/', '\'
    $source = Join-Path $RepoRoot $decl

    $sub = $decl
    $prefix = $OutputProjectName + '\'
    if ($decl.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        $sub = $decl.Substring($prefix.Length)
    }
    $build = Join-Path $OutputDir $sub

    # The SERVED path is not the same as the build path. ASP.NET Core serves
    # the static web root AS the content root, so wwwroot/js/avatar.js is
    # served at /js/avatar.js. Asking for /wwwroot/js/avatar.js returns 404 --
    # measured on the running instance -- and the tripwire then fails on a
    # file that IS being served.
    $webRoot = 'wwwroot'
    $servedSub = $sub
    $firstSeg = ($servedSub -split '\\')[0]
    if ($firstSeg -ieq $webRoot) {
        $servedSub = $servedSub.Substring($webRoot.Length).TrimStart('\', '/')
    }
    # The URL uses forward slashes. A backslash in a URL path is not a path
    # separator to the server and the fetch fails.
    $served = '/' + ($servedSub -replace '\\', '/')

    return [pscustomobject]@{
        Source = $source
        Build  = $build
        Served = $served
        Sub    = $sub
    }
}

function Get-MapleBenchInstanceCandidates {
    <#
      Every process on this machine that could be a MapleBench instance, with
      the EVIDENCE that says so. Three independent ways to be one:

        1. the command line mentions MapleBenchX.dll or MapleBenchX.exe
        2. the process image is MapleBenchX.exe (the apphost). PROJECT.yaml
           says NEVER run MapleBenchX.exe directly, so an apphost instance is
           an instance out of policy and is stopped even when its command
           line is unreadable
        3. the process has a module under this repository's output directory
           -- the direct "it holds the dll" answer, used when the command
           line is empty

      Returns ONE object: { Candidates = @(...); Undetermined = @(...) }.
      Undetermined holds processes that LOOK like they could be one (dotnet.exe
      or MapleBenchX.exe) but whose command line is unreadable AND whose
      modules could not be enumerated. Rule 1 of tools/verify/AGENTS.md: a
      check that cannot decide has to say so instead of quietly passing.
    #>
    param([Parameter(Mandatory = $true)][string]$RepoRoot)

    $candidates = New-Object System.Collections.ArrayList
    $undetermined = New-Object System.Collections.ArrayList
    $outDirTail = [System.IO.Path]::Combine('bin', 'Release')

    $all = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue)
    foreach ($p in $all) {
        $name = [string]$p.Name
        $cmd = [string]$p.CommandLine
        $pid_ = [int]$p.ProcessId

        $byName = ($name -ieq 'MapleBenchX.exe')
        $byCmd = ($cmd -and ($cmd -match 'MapleBenchX\.dll' -or $cmd -match 'MapleBenchX\.exe'))

        # The loaded modules are the STRONGEST evidence of which checkout a
        # process belongs to, because they are the FILES it is running.
        #
        # MEASURED 2026-10-24: deploy.ps1 starts the server with a RELATIVE
        # argument (R-16: the non-ASCII path must not pass through a command
        # line), so the command line of our own instance is
        #   dotnet.exe MapleBenchX.dll --port 5591 --allow-multiple --no-browser
        # and contains NO repository path at all. A test that asked only the
        # command line decided "not this repository", SKIPPED the instance,
        # and the build then failed on a locked dll. The module path of that
        # same process is the full path under this checkout -- which is the
        # answer that was wanted all along.
        $mods = $null
        $repoModules = @()
        if ($byName -or $byCmd) {
            $mods = Get-ProcessModulePaths -ProcessId $pid_
            if ($null -ne $mods) {
                $repoModules = @($mods | Where-Object {
                    $_ -and $_.IndexOf($RepoRoot, [System.StringComparison]::OrdinalIgnoreCase) -ge 0
                })
            }
        }

        if (-not $byName -and -not $byCmd) {
            # Not an instance unless the loaded modules say so. Only the two
            # hosts that could plausibly run this dll are inspected.
            if ($name -ieq 'dotnet.exe' -and [string]::IsNullOrWhiteSpace($cmd)) {
                $mods = Get-ProcessModulePaths -ProcessId $pid_
                if ($null -eq $mods) {
                    [void]$undetermined.Add([pscustomobject]@{ Id = $pid_; Name = $name })
                    continue
                }
                $hit = @($mods | Where-Object {
                    $_ -and $_ -match 'MapleBenchX\.dll$' -and
                    $_.IndexOf($RepoRoot, [System.StringComparison]::OrdinalIgnoreCase) -ge 0
                })
                if ($hit.Count -eq 0) { continue }
                [void]$candidates.Add([pscustomobject]@{
                    Id = $pid_; Name = $name; Reason = 'loaded MapleBenchX.dll'; Detail = ($hit -join '; ')
                    IsThisRepo = $true
                })
                continue
            }
            continue
        }

        $reason = ''
        if ($byCmd) { $reason = 'command line names MapleBenchX' } else { $reason = 'process image is MapleBenchX.exe' }
        $detail = $cmd
        $isThisRepo = $false
        if ($cmd) {
            $isThisRepo = ($cmd.IndexOf($RepoRoot, [System.StringComparison]::OrdinalIgnoreCase) -ge 0)
            if ($isThisRepo) { $reason = $reason + ' + command line names this repository' }
        }
        if ($repoModules.Count -gt 0) {
            $isThisRepo = $true
            $reason = $reason + ' + module under this repository'
            $detail = ($repoModules -join '; ')
        } elseif (-not $cmd) {
            # No command line: decide by the loaded modules.
            if ($null -ne $mods) {
                $reason = $reason + '; holds no module of this repository (command line unreadable)'
            } else {
                $reason = $reason + '; modules not enumerable and command line unreadable'
            }
        }
        [void]$candidates.Add([pscustomobject]@{
            Id = $pid_; Name = $name; Reason = $reason; Detail = $detail
            IsThisRepo = $isThisRepo
        })
    }

    return [pscustomobject]@{
        Candidates   = @($candidates.ToArray())
        Undetermined = @($undetermined.ToArray())
    }
}

function Stop-MapleBenchInstances {
    <#
      Stop every MapleBench instance on this machine, and say which ones went.

      T-0: a stale server on another port answers exactly like a current one,
      and three instances at ~8 GB each starve the machine until Map.nx
      (678 MB, ~13.5 GB to convert) refuses to open anywhere. The old
      deploy.ps1 stopped every other MapleBench before starting; that
      behaviour was lost with the script, so it lives here now.

      T-13/R-14: the running server holds MapleBenchX.dll, and a build against
      a locked dll retries ten times and then fails with MSB3027, which reads
      like a compile error and is not one. The build has to be able to stop it.

      Returns ONE object: { Stopped = @(...); Failed = @(...); Undetermined = @(...) }.
      The shape is declared here rather than left to the caller to guess.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [switch]$OnlyThisRepository
    )

    $stopped = New-Object System.Collections.ArrayList
    $failed = New-Object System.Collections.ArrayList
    $skipped = New-Object System.Collections.ArrayList

    $found = Get-MapleBenchInstanceCandidates -RepoRoot $RepoRoot
    foreach ($c in $found.Candidates) {
        $isThisRepo = $true
        if ($c.PSObject.Properties.Name -contains 'IsThisRepo') { $isThisRepo = [bool]$c.IsThisRepo }
        if ($OnlyThisRepository -and -not $isThisRepo) {
            # Another checkout's instance. The build does not need it stopped
            # (it does not hold THIS dll), but the deploy does, because it
            # must be the only instance answering on the gate's port.
            [void]$skipped.Add([pscustomobject]@{ Id = $c.Id; Name = $c.Name; Reason = $c.Reason })
            continue
        }
        $age = 'unknown'
        try {
            $age = '{0:N1} h' -f ((Get-Date) - (Get-Process -Id $c.Id).StartTime).TotalHours
        } catch { }
        try {
            Stop-Process -Id $c.Id -Force -ErrorAction Stop
            [void]$stopped.Add([pscustomobject]@{
                Id = $c.Id; Name = $c.Name; Age = $age; Reason = $c.Reason
            })
        } catch {
            [void]$failed.Add([pscustomobject]@{
                Id = $c.Id; Name = $c.Name; Age = $age
                Reason = $c.Reason + ' -- ' + $_.Exception.Message
            })
        }
    }

    # ONE object with arrays inside, never a bare list. A function that
    # emits objects and strings on the same pipeline hands the caller a
    # collection it has to guess the shape of -- and the guess failed here
    # once already: a STOP_FAILED string reached a caller that expected a
    # record with an Id. The shape is declared here instead.
    return [pscustomobject]@{
        Stopped     = @($stopped.ToArray())
        Failed      = @($failed.ToArray())
        Skipped     = @($skipped.ToArray())
        Undetermined = @($found.Undetermined)
    }
}

function Test-FileLocked {
    <#
      True when some process holds the file open. The build needs to know
      this for certain: a dll that is still locked fails the build with
      MSB3027, and MSB3027 reads like a compile error (T-13). Probing the
      lock directly is the difference between "the build failed" and "the
      build failed because an instance is still running".
    #>
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) { return $false }
    try {
        $fs = [System.IO.File]::Open($Path,
            [System.IO.FileMode]::Open,
            [System.IO.FileAccess]::ReadWrite,
            [System.IO.FileShare]::None)
        $fs.Close()
        return $false
    } catch [System.IO.IOException] {
        return $true
    } catch [System.UnauthorizedAccessException] {
        # Opened by someone else with a deny-write share, or we lack the
        # rights. Either way the file cannot be written over.
        return $true
    }
}

function Get-NewestProductSourceTime {
    <#
      The newest write time among the product's own source files. T-20: a
      deployed dll that is OLDER than the source it was built from makes a fix
      look like it did nothing, so the build reports this number and the
      caller compares it with the dll.

      THE ROOTS ARE THE SOURCES OF THE ARTIFACT THAT IS COMPARED WITH IT, and
      that is not every source in the tree.

      MEASURED 2026-10-24: the roots included 'MapleMcp', and the gate refused
      to build with "the built dll is OLDER than the newest product source" --
      because someone had edited MapleMcp/Program.cs, a DOC-COMMENT change in
      the MCP server's own instructions, and MapleBenchX.dll cannot contain
      that: MapleBenchX.csproj has exactly ONE ProjectReference, to
      MapleLib. The dll was not stale; the COMPARISON was against a source the
      artifact does not contain, so a change to a different assembly reddened
      the build of this one.

      The artifact this measures is MapleBenchX.dll. Its sources are
      MapleBench and dependencies\MapleLib. MapleMcp is a separate assembly
      (maple-mcp.dll) that the chain neither builds nor deploys, and the gate
      does not measure it.

      ONLY .cs FILES ARE SOURCES, and that has to be said in the filter itself.

      MEASURED 2026-10-24: the file list was built with
      `Get-ChildItem -LiteralPath <dir> -Recurse -File -Include '*.cs'`, and
      `-Include` is INERT when the path is given with `-LiteralPath` -- it is
      applied to the leaf only when the path carries a wildcard. So the filter
      did nothing and the "newest product source" was the newest file of ANY
      kind under those roots: the gate refused to build with "the built dll is
      OLDER than the newest product source" because someone had touched
      MapleBench/tools/soak.py -- a python helper that is not compiled into
      anything. The dll was not stale; the comparison was against a file that
      cannot be in it.
    #>
    param([Parameter(Mandatory = $true)][string]$RepoRoot)

    $roots = @('MapleBench', 'dependencies\MapleLib')
    $newest = [datetime]::MinValue
    $newestPath = ''
    foreach ($r in $roots) {
        $dir = Join-Path $RepoRoot $r
        if (-not (Test-Path -LiteralPath $dir)) { continue }
        # The extension is tested HERE, not with -Include: -Include is ignored
        # when the path is a literal (measured 2026-10-24, see above).
        $files = @(Get-ChildItem -LiteralPath $dir -Recurse -File -ErrorAction SilentlyContinue |
                   Where-Object {
                       $_.Extension -eq '.cs' -and
                       $_.FullName -notmatch '\\(bin|obj)\\' })
        foreach ($f in $files) {
            if ($f.LastWriteTime -gt $newest) {
                $newest = $f.LastWriteTime
                $newestPath = $f.FullName
            }
        }
    }
    return [pscustomobject]@{ Time = $newest; Path = $newestPath }
}

function Get-RawMd5OfBytes {
    <#
      The md5 of a byte array, exactly as it is on disk.

      No module is involved: MD5 comes from System.Security.Cryptography, not
      from a cmdlet. That is not a style choice.

      MEASURED 2026-10-24: `Get-FileHash` lives in Microsoft.PowerShell.Utility,
      and a child PowerShell can end up with a PSModulePath that points at
      PowerShell 7's module directories. PowerShell 7's Utility (7.0.0.0) is
      then the first match, it does not load into Windows PowerShell 5.1, and
      `Get-FileHash` is simply NOT THERE -- "The term 'Get-FileHash' is not
      recognized as the name of a cmdlet". The build gate failed on a line
      that had nothing to do with the build. Every other cmdlet in that module
      was still there, so nothing about the failure said "wrong PowerShell".
    #>
    param([Parameter(Mandatory = $true)][byte[]]$Bytes)

    $md5 = [System.Security.Cryptography.MD5]::Create()
    try {
        return (([BitConverter]::ToString($md5.ComputeHash($Bytes))) -replace '-', '').ToLower()
    } finally {
        $md5.Dispose()
    }
}

function Get-RawMd5OfPath {
    <#
      The raw md5 of a file, or $null when the file is not there.
    #>
    param([Parameter(Mandatory = $true)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    return Get-RawMd5OfBytes -Bytes ([System.IO.File]::ReadAllBytes($Path))
}

function Get-FileMd5 {
    param([Parameter(Mandatory = $true)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    return Get-RawMd5OfPath -Path $Path
}

function Get-NormalisedMd5OfBytes {
    <#
      The md5 of a byte array after CRLF has been folded to LF.

      R-17: the registry's md5 invariants are normalised this way (pm.py does
      the same), so a line-ending difference between a working tree and a
      fresh checkout does not read as a content change. All legs of a
      tripwire have to use the SAME rule, or the comparison is between two
      different rules.
    #>
    param([Parameter(Mandatory = $true)][byte[]]$Bytes)

    $text = [System.Text.Encoding]::UTF8.GetString($Bytes)
    $text = $text.Replace("`r`n", "`n")
    $norm = [System.Text.Encoding]::UTF8.GetBytes($text)
    $md5 = [System.Security.Cryptography.MD5]::Create()
    try {
        return (([BitConverter]::ToString($md5.ComputeHash($norm))) -replace '-', '').ToLower()
    } finally {
        $md5.Dispose()
    }
}

function Get-NormalisedMd5OfPath {
    <#
      The normalised md5 of a file, or $null when the file is not there.
    #>
    param([Parameter(Mandatory = $true)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    return Get-NormalisedMd5OfBytes -Bytes ([System.IO.File]::ReadAllBytes($Path))
}
