# tools/verify -- reusable verification tools

These are NOT product code. They are the tools used to decide whether product
code is correct. On 2026-10-19 five verification attempts inside this project
produced a plausible wrong answer instead of an error. This directory exists so
that the next one is a script that says so.

## The five failures these tools exist to prevent

| # | What was run | How it failed | What it pretended to be |
|---|---|---|---|
| 1 | a measurement script with a Chinese path | the write tool corrupted the path inside the script | a column of zeros, i.e. "the data is like that" |
| 2 | a measurement script with no `DOTNET_ROOT` | the process exited inside one second | the same column of zeros |
| 3 | `Where-Object { $_ -notlike '??*' }` | `?` is a single-character wildcard in `-like` and matches everything | 35 untracked files reported as 0 |
| 4 | `Get-Content` on UTF-8 without BOM | Windows PowerShell 5.1 decoded with the ANSI code page and lost characters | a destroyed file, i.e. "your work is gone" |
| 5 | a regex over a message | the message is concatenated from literals over three lines | code that was already fixed reported as unfixed |

## Rules every tool here obeys

* **Zero non-ASCII bytes.** Every path is a parameter. A write tool that mangles
  the text inside a script destroys that script's own inputs (failure 1), and
  `self-test.ps1` asserts this byte by byte.
* **`DOTNET_ROOT` is checked first.** `Get-DotNetEnvStatus` in `lib/Common.ps1`
  runs `dotnet --version` before anything else. `Start-Process` is never used;
  every child gets `DOTNET_ROOT` written into its environment explicitly.
* **No `-like` on a status line.** `git-status-report.ps1` compares the two
  status characters with string equality.
* **No lossy decoding.** `ConvertFrom-SafeBytes` validates UTF-8 strictly and
  reports the byte offset of the first invalid sequence. It never substitutes.
* **Search tolerates concatenation.** `content-assert.ps1` treats any run of
  whitespace and any `" + "` glue between tokens as transparent.
* **Empty is not a result.** `assert-measurement.ps1` exits non-zero for an
  all-zero, all-constant or single-sample set, and for a child process that
  exited non-zero or printed nothing.
* **Undecidable means INCONCLUSIVE.** Never a guess.

## Output contract

Every tool prints:

```
=== TOOL: <name> ===
CHECKED: <what was checked>
[env] DOTNET_ENV=<OK|MISSING|UNUSABLE> DOTNET_ROOT=<path> VERSION=<x>
FIELD=value            <- machine readable
--- detail ---
... human readable ...
RESULT: PASS|FAIL|INCONCLUSIVE
TRUST: HIGH|MEDIUM|NONE
EXIT: 0|1|2
```

Exit codes: `0` PASS, `1` FAIL, `2` INCONCLUSIVE, `3` TOOL_ERROR.
`TRUST=HIGH` only when the answer is a confirmed fact.

## The tools

| Script | Purpose |
|---|---|
| `git-status-report.ps1` | tracked-modified / staged / untracked counts, cross-checked by three independent git commands |
| `eol-check.ps1` | byte-level CRLF / bare-LF / bare-CR census of a file, compared to the HEAD blob read as raw bytes |
| `content-assert.ps1` | token present or absent, tolerant of line-wrapped string concatenation |
| `read-text-safe.ps1` | encoding-safe read of UTF-8 with or without BOM |
| `prebuild-check.ps1` | SDK, `DOTNET_ROOT`, PATH dotnet, TargetFramework and build-output lock, before a build starts |
| `assert-measurement.ps1` | refuses to accept a degenerate measurement |
| `self-test.ps1` | known-answer regression suite for all of the above |
| `lib/Common.ps1` | shared output contract, process runner, decoder, normaliser |

## Usage

```powershell
# regression suite first, always
powershell -NoProfile -ExecutionPolicy Bypass -File tools\verify\self-test.ps1

$env:DOTNET_ROOT = 'C:\MUWORK\app\scoop\apps\dotnet-sdk\current'

# working tree, with the count asserted instead of eyeballed
powershell -File tools\verify\git-status-report.ps1 -ExpectUntracked 3 -RequireClean

# after an edit: did the file get rewritten wholesale?
powershell -File tools\verify\eol-check.ps1 -Path MapleBench\Services\WzSaveService.cs

# is the fix actually in the code? (needle via FILE when it contains CJK)
powershell -File tools\verify\content-assert.ps1 -Path src\Foo.cs -NeedleFile needle.txt

# before every build
powershell -File tools\verify\prebuild-check.ps1 -Project MapleBench\MapleBenchX.csproj

# after a measurement run
powershell -File tools\verify\assert-measurement.ps1 -Values '2348,87,100,3'
```

## Measured facts on this machine (2026-10-20)

* The default shell here is **Windows PowerShell 5.1**, not PowerShell 7.
  `Get-Content` therefore decodes UTF-8-without-BOM as CP950: a 15 character
  file came back as 16 characters ending in two `?` characters. Under 5.1 the
  naive reader is demonstrably wrong and `self-test.ps1` asserts that it is.
* The command channel itself corrupts non-ASCII: the same CJK needle delivered
  with `-Needle` arrived mangled, while the identical characters delivered with
  `-NeedleFile` were found on the correct line. **Never type non-ASCII into
  these tools' command lines.**
* `docs/ENGINEERING/STATE.md` contains three literal U+FFFD characters on the
  line describing failure 4. `read-text-safe.ps1` reports `LITERAL_FFFD=3`
  for that file without being told where to look.
* `PATH` resolves `dotnet` to `C:\Program Files\dotnet` (8.0.425) while
  `DOTNET_ROOT` holds 10.0.401. `prebuild-check.ps1` reports both.
* `MapleBench\bin\Release\net10.0-windows\MapleBenchX.dll` is locked by
  `dotnet.exe` pid 29852 (`... MapleBenchX.dll --port 5600`). A release build
  to that output directory will fail. Note the holder is named `dotnet`, not
  `MapleBenchX`, so `Get-Process -Name MapleBenchX` does not find it.