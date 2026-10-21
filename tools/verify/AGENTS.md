# AGENTS.md — `tools/verify`

Scripts that decide whether a change is correct. **These tools have been wrong
six times, and every time they returned a plausible-looking wrong answer rather
than an obvious failure.** That is the failure mode to design against.

---

## Every failure here returned valid-looking data

The numbers in the first column are **project-wide incident numbers**, not a
count of the rows: #12 is the twelfth failure of this shape in the project and
the sixth recorded in this directory.

| # | What happened | Why it was dangerous | Rule it produced |
|---|---|---|---|
| 1 | A non-ASCII (Chinese) path inside a `.ps1` was corrupted on write. The script path broke, the measurement returned all zeros. | **All zeros read as a valid result.** | `.ps1` files are **ASCII only**. Pass paths as parameters; never embed them. |
| 2 | `DOTNET_ROOT` is not inherited by `Start-Process`, so the app host exited in ~1 s and every measurement returned zero. | Again: zeros, not an error. | Set the environment **inside** the script. See `prebuild-check.ps1`. |
| 3 | `Where-Object { $_ -notlike '??*' }` — PowerShell's `-like` treats `?` as a single-character wildcard, so the filter matched **everything**. 35 genuinely modified files were reported as 0. | A filter bug that reported "clean tree". | Use `-notmatch '^\?\?'`, never `-like` with `?`. |
| 4 | `Get-Content` on a UTF-8 file without a BOM mangled the Chinese text, making intact code look corrupted. | Looked like data loss; would have prompted a bogus "fix". | Use `read-text-safe.ps1`. |
| 5 | A regex was written across a message split over three concatenated string literals, so a **fixed** file was reported as **not fixed**. | The gate said "fail" on correct code. | Assert with `content-assert.ps1 -NeedleFile`, which matches the file's real bytes. |
| 12 | A DTO field was renamed (`excludedByOrientation` -> `excludedByNotForwardOriented`) and a measurement script kept the old name. In PowerShell a property that is not there is `$null`; `$null` added to a counter is `0`. The run reported **"0 candidates were excluded"** while the filter had in fact removed **924**. It threw nothing, printed no error, and exited 0. | A **missing** field and a **zero** are indistinguishable in the output. Read on its own, the CSV says the filter does nothing at all. | **Name every field you are about to read, and check that it exists first.** See `require-fields.ps1` and rule 9. |

Two of these were **the same bug twice**: #5 had already produced a dedicated
tool, and the tool was not used.

---

## Rules

1. **A tool that cannot measure must fail loudly.** Return non-zero, or emit a
   token the caller greps for. Never let a measurement collapse to `0`, `""`,
   `null`, or an empty array and let that pass as data.
2. **Always run `self-test.ps1` after changing anything here.** It carries 25
   known-answer cases precisely so the six failures above cannot return.
   **It takes no `-RepoRoot`** — its only parameter is `-KeepFixtures`.

   ```powershell
   powershell -File tools\verify\self-test.ps1
   ```

   Expected on a healthy tree. The counts depend on the environment, so read
   the case list rather than the totals when you are checking a specific
   number. Of the twenty-five cases, **seven need git** (the two
   `git-status-report` groups and the two `eol-check` groups) and **two more
   need a usable `DOTNET_ROOT`**; every case that cannot run reports SKIP
   rather than silently disappearing.

   The environment the 2026-08-20 numbers were taken in had git but no usable
   `DOTNET_ROOT`:

   ```
   CASES_PASS=23
   CASES_FAIL=0
   CASES_SKIP=1
   RESULT=PASS
   TRUST=HIGH
   ```

   That is the old `21/0/1` plus the two incident-12 cases, which need neither
   git nor `DOTNET_ROOT` and so run everywhere. Where `DOTNET_ROOT` is usable
   the two conditional cases run as well and the skip disappears:
   `CASES_PASS=25 CASES_FAIL=0 CASES_SKIP=0`. Where git is missing, seven more
   cases skip: `CASES_PASS=18 CASES_FAIL=0 CASES_SKIP=2`.

   Anything else means this tooling is now unreliable.
3. **Known-answer tests only.** A test that cannot fail proves nothing.
4. `eol-check.ps1` compares `git diff --numstat` against
   `--ignore-all-space`. They must be equal; a mismatch is line-ending churn
   smuggled in by a whole-file rewrite, not a real change.

5. **`-Path` is a `string[]`, but `powershell -File` does not split a comma
   list for you.** `-Path 'a.md,b.md'` arrives as **one string** named
   `"a.md,b.md"` and every file reports `MISSING`. Loop and invoke per file.

6. **`RESULT=FAIL` on `eol-check.ps1` does not always mean EOL damage.** The
   default `MaxChangedLineRatio = 0.25` makes any legitimately large edit
   exceed the threshold and report `RESULT=FAIL` / `TRUST=NONE` — even when
   `eol_rewrite=False`. Observed 2026-08-20 on a documentation rewrite:
   107 added / 6 deleted → `changed_ratio=0.9187` → `FAIL`, while raw and
   `--ignore-all-space` numstat were **identical (107/6)** and the file held
   **zero CR bytes**. Confirm independently before "fixing" anything:

   ```powershell
   git diff --numstat -- <file>              # raw
   git diff --ignore-all-space --numstat -- <file>   # must equal the above
   ```

   Identical counts and `eol_rewrite=False` ⇒ the FAIL is the ratio heuristic,
   not churn. Only a **difference** between the two numstat runs is real churn.

7. **Windows paths break hand-built JSON.** A path like
   `C:\mbtest2\x.nx` contains `\m` and `\x`, which are **invalid JSON escapes**.
   Posting `{"path":"C:\mbtest2\x.nx"}` returns `HTTP 400 "This request could
   not be read."` — which reads exactly like "the server rejected the bad
   file", when in fact the server never saw the file. Always build the body
   with `ConvertTo-Json`:

   ```powershell
   $body = [Text.Encoding]::UTF8.GetBytes((@{ path = $f } | ConvertTo-Json -Compress))
   ```

   A 400 with a JSON-shape error is a **harness** failure until proven
   otherwise. Never record it as the product's response.

8. **The crafted fixtures live in two directories.** They are not all in one
   place, and a subset in the wrong one reads as "no such file" and then as
   "that behaviour is unreachable":

   | Directory | Files |
   |---|---|
   | `C:\mbtest2\` | `canvas-32768.nx`, `canvas-23171.nx`, `canvas-40000.nx` (211 B), plus `Map.nx` |
   | `C:\mbtest2-gen\` | `canvas-bmp-*.nx` (136 B), `nested-*.nx` (175 B), `string-runs-past-eof.nx` (136 B) |

   `string-runs-past-eof.nx` is the only fixture that reaches the **second**
   string bounds check. A review that looked only in `C:\mbtest2` concluded
   that check was unreachable by any existing artifact — it was reachable and
   the artifact was there. **List the directory before concluding an input
   does not exist.**

9. **Name every field you are about to read, and check it exists first.**
   A missing field and a field whose value is 0 look identical in the
   output, and only one of them is a measurement. In PowerShell a property
   that is not there is `$null`; in JavaScript it is `undefined`; in Python
   `d['x']` raises but `d.get('x')` returns `None` and keeps going. None of
   them stops a script, and none of them prints anything.

   Use `require-fields.ps1`. It takes the payload and the list of field paths
   the script is about to read, and fails with the field **named** and a
   non-zero exit when one of them is not there:

   ```powershell
   # -JsonBase64 carries the JSON without quoting it onto a command line.
   $b64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($json))
   & "$PSScriptRoot\require-fields.ps1" -JsonBase64 $b64 `
       -Fields 'playerDamage','status.hp','mobs[].hp' -Context 'the session frame'
   if ($LASTEXITCODE -ne 0) { throw 'the frame did not carry the fields this script reads' }
   ```

   A field that is present but **null**, and an array path whose array is
   **empty**, are failures too: both are a measurement that measured nothing.
   Use `-AllowNull` only when a null is a value the script really accepts.

---

## Before you trust a measurement

- Did the command actually run, or did it die and leave the output at its
  initial value?
- Is the number physically plausible for this archive?
- Did the tool exit non-zero?

A measurement that did not happen and a measurement that happened are
indistinguishable from the output alone. That is the whole reason `self-test.ps1`
exists — do not skip it.