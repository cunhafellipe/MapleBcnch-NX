# AGENTS.md — `tools/verify`

Scripts that decide whether a change is correct. **These tools have been wrong
nine times, and every time they returned a plausible-looking wrong answer rather
than an obvious failure.** That is the failure mode to design against.

---

## Every failure here returned valid-looking data

The first column is an **id used by this table**. It is not an ordinal, and it
is not the numbering of any other list in this repository: **no other list
assigns these numbers to incidents**, and the sequence is internal to this
table. This file's own `## Rules` list further down carries a separate
**1-27** numbering, and the two are unrelated: **row #14 here is incident 14,
not rule 14**, and the "See rule N" in the table's last column points at that
Rules list, never at a row of this table. Case names in `self-test.ps1` point
at some of them: **`INC1`–`INC5` and `INC12`** name the corresponding rows
here, and **rows 13 to 23 currently have no case name pointing at them.**

A scan of the repository's script and text files -- **the file names are taken
from the commit**, so the population is reproducible -- (`.ps1`, `.mjs`, `.py`,
`.md`, `.js`, `.json`; `bin/`, `obj/`, `node_modules/` and `.git/` excluded;
the listing is read with `core.quotepath=false`, without which every CJK-named
file is dropped for carrying a trailing quote)
found `INC`-style ids in `self-test.ps1` and in this paragraph only; a few
tools cite the same rows in prose (`incident 14` in `gate.ps1`, `incident 12`
in `require-fields.ps1`). **Re-run it before trusting this sentence** -- the
file list moves with the commit, and the content is read from the working
tree's copy of those files. Measured at `HEAD` = `879a45b`:

```powershell
# 1. the population: file names from the commit, not from the working tree.
#    core.quotepath=false is LOAD-BEARING: without it git prints a non-ASCII
#    name as "docs/\350\252\257.md\"" -- the trailing quote means the extension
#    regex does not match, and the population silently loses every CJK-named
#    file. With it: 522. Without it: 518. Same tree, same rule, 4 files apart.
$files = git -c core.quotepath=false ls-tree -r HEAD --name-only |
  Where-Object { $_ -match '\.(ps1|mjs|py|md|js|json)$' -and $_ -notmatch '^(bin|obj|node_modules|\.git)/' }
$files.Count          # -> 522      (the commit tree holds 949 files in all)

# 2. which of them carry an INC-style id
$files | ForEach-Object { if (Select-String -Path $_ -Pattern '\bINC[0-9]+\b' -Quiet) { $_ } }
                        # -> tools/verify/AGENTS.md
                        # -> tools/verify/self-test.ps1
                        #    2 of 522 files
Select-String -Path tools/verify/self-test.ps1 -Pattern '\bINC[0-9]+\b' -AllMatches |
  ForEach-Object { $_.Matches } | ForEach-Object { $_.Value } | Sort-Object -Unique
                        # -> INC1 INC2 INC3 INC4 INC5 INC12   (6 distinct, 20 occurrences)

# 3. this table's own row numbers -- also answers the 17 / 23 / 6-11 quoted
#    in the reading note below
Select-String -Path tools/verify/AGENTS.md -Pattern '^\| (\d+) \|' |
  ForEach-Object { $_.Matches[0].Groups[1].Value }
                        # -> 1, 2, 3, 4, 5, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23
                        #    17 rows, highest 23, numbers not used: 6, 7, 8, 9, 10, 11

# 4. the other numbering in this file
Select-String -Path tools/verify/AGENTS.md -Pattern '^(\d+)\. \*\*' |
  ForEach-Object { $_.Matches[0].Groups[1].Value }
                        # -> 1 .. 27   (27 rules)
Select-String -Path tools/verify/AGENTS.md -Pattern 'See rule (\d+)' |
  Where-Object { $_.Line -match '^\|' } | ForEach-Object { $_.Matches[0].Groups[1].Value }
                        # -> 10 11 12 13 22 26 18   (the table's last column)
```

The one numbering that lives elsewhere is `pm/TRAPS.md`'s `T-n`, and **it is
not this column's numbering**: row #1's subject is T-15 there. **This file
states no counterpart for the other rows** -- a correspondence was not
established for them, and this paragraph does not say they have none. If
`pm/TRAPS.md` gains a trap on the same subject as a row here, that row's `T-n`
is the counterpart and this sentence is out of date. **One visible pair is not
a mapping rule; do not derive the rest from it.**

Counting rows in this table, #12 is the sixth row here, #13 the seventh, #14 and
#15 the eighth and ninth, #16 the tenth, and **#17-#23 are the eleventh through
seventeenth rows here**, added on 2026-10-23.

**⇒ How to read this column.** Three things the numbers alone do not tell you:

- **The number is an id, not an ordinal.** Do not renumber, and do not read a
  gap as a row that was deleted. This table carries **17 rows** and its highest
  number is **23**, so **6-11 are not used by this table, and this file does not
  say why.** Those two counts are a **reading note, not a census**: true when
  written, and stale the moment a row is added. Recount rather than trust them;
  rule 16 raises exactly this against the census it documents.
- **A number here does not mean the shape is still open.** Several rows record
  a defect that has since been repaired (#5 produced a dedicated tool; #16 is
  cited by rule 13). A row records that the failure happened and what rule it
  produced, not that the defect is still present.
- **A row that restates an earlier row is a second measurement, not a
  duplicate.** #22 is another occurrence of #3's mechanism: the first
  under-reported 35 modified files as 0, this one over-reported 42 untracked
  files. Same rule, two runs, and the opposite sign is what makes the second
  one worth a row of its own.

| # | What happened | Why it was dangerous | Rule it produced |
|---|---|---|---|
| 1 | A non-ASCII (Chinese) path inside a `.ps1` was corrupted on write. The script path broke, the measurement returned all zeros. | **All zeros read as a valid result.** | `.ps1` files are **ASCII only**. Pass paths as parameters; never embed them. |
| 2 | `DOTNET_ROOT` is not inherited by `Start-Process`, so the app host exited in ~1 s and every measurement returned zero. | Again: zeros, not an error. | Set the environment **inside** the script. See `prebuild-check.ps1`. |
| 3 | `Where-Object { $_ -notlike '??*' }` — PowerShell's `-like` treats `?` as a single-character wildcard, so the filter matched **everything**. 35 genuinely modified files were reported as 0. | A filter bug that reported "clean tree". | Use `-notmatch '^\?\?'`, never `-like` with `?`. |
| 4 | `Get-Content` on a UTF-8 file without a BOM mangled the Chinese text, making intact code look corrupted. | Looked like data loss; would have prompted a bogus "fix". | Use `read-text-safe.ps1`. |
| 5 | A regex was written across a message split over three concatenated string literals, so a **fixed** file was reported as **not fixed**. | The gate said "fail" on correct code. | Assert with `content-assert.ps1 -NeedleFile`, which matches the file's real bytes. |
| 12 | A DTO field was renamed (`excludedByOrientation` -> `excludedByNotForwardOriented`) and a measurement script kept the old name. In PowerShell a property that is not there is `$null`; `$null` added to a counter is `0`. The run reported **"0 candidates were excluded"** while the filter had in fact removed **924**. It threw nothing, printed no error, and exited 0. | A **missing** field and a **zero** are indistinguishable in the output. Read on its own, the CSV says the filter does nothing at all. | **Name every field you are about to read, and check that it exists first.** See `require-fields.ps1` and rule 9. |
| 13 | A measurement script defined `function Measure { ... }` and called `Measure`. PowerShell resolves **Alias > Function > Cmdlet**, and `measure` is a built-in alias for `Measure-Object`, so the alias ran and **the function never did**. | `Measure-Object` also produces numbers, so an argument-shaped call would have printed a plausible set of numbers and passed. The call that did happen failed with *"a positional parameter cannot be found"* — an error that points at the **arguments** rather than at the shadowing, so the obvious "fix" is to change the call, not the name. | **Give every helper a name that cannot collide with an alias, and prove the helper ran.** See rule 10. |
| 14 | `self-test.ps1` read a child's exit code from `$LASTEXITCODE`. Which copy of that automatic variable a native call updates depends on how the script was entered: measured, the `-File` form updates the function-scope copy (LOCAL=1 GLOBAL=1) and the `-Command` form updates only the global one (LOCAL=0 GLOBAL=1). The function-scope read returned 0, so **15 of the 27 cases that ran — every one of them a case that expects a NON-ZERO exit — reported FAIL** while each tool's own stdout proved it had done the right thing. (The tree now carries 28 cases, after the `HARNESS-child-exit-codes-were-observed` case was added.) | Silent, and one-directional: **a genuinely broken tool would have been judged PASS.** | **Read a child's exit code from `ProcessStartInfo.ExitCode`, never from `$LASTEXITCODE`.** And assert that the harness observed a non-zero child exit code, so "read it and got 0" cannot look like "never read it". See rule 11. |
| 15 | A citation extractor matched `Name.ext` without requiring the extension to END at a non-identifier character, so the C++ member access `phobj.hspeed = phobj.vx = 0;` was read as a citation of `phobj.h`, and `status.hp: 0` as a citation of `status.h`. Both were reported as `FILE_MISSING`. | The tool reported **2 reference files that do not exist and never needed to exist**. The output was a plausible, well-formed gap list, so a reviewer would have gone looking for two holes in the authoritative reference — and the real answer, "0 missing", was nowhere in the report. A false positive here manufactures work. | **A pattern that matches text must end at a BOUNDARY, not inside a word.** Require the match to be followed by a non-identifier character (`(?![A-Za-z0-9_])`). And when a category comes out empty, write `0` and say what was checked — never leave a reader with a list of things that are not there. See rule 12. |
| 16 | `gate.ps1`'s `Alive` printed a "backend unreachable" message **and** returned `$false` from its `catch`. A PowerShell function returns everything it wrote, so it returned a **two-element array** (the message, then `False`); `-not` on a non-empty array is `$false`, so `if (-not (Alive $name))` **never fired**. | The message was on screen; the counter never knew it. With the backend down, every suite printed `>>> RED (backend unreachable)` and the gate still ended `RED SUITES: 0` and exit 0. A gate that cannot see a red suite is worse than no gate, because it reports green. | **A function that RETURNS A VALUE must not also WRITE to the success stream.** See rule 13. |
| 17 | A measurement was taken from `git cat-file blob HEAD:<path>`, or from an older commit's tree, and the conclusion was then written as a statement about the code as it is now. This happened five times in one round. | The number is real and the sentence attached to it is false. A finding pinned to a tree nobody else holds reads as a defect in the tree the reader is looking at, and re-measuring it gives a different answer with no visible cause. | **Name the tree every report cites, and pin with `git write-tree`.** A pin built by a hand-rolled recipe is not a pin: a `TARGET_SHA256` assembled from a custom concatenation recipe could not be reproduced by the next agent at all, and an unreproducible pin is indistinguishable from no pin. See rules 23 and 24. |
| 18 | A denominator was taken from the registry's own declaration. `DOCTOR_SUITES_DECLARED=34`, while the gate ran **33** — `save-audit` declares itself `in_gate: false`, so it is listed and never executed. | Every "0 of N" and every percentage is then about a population containing a member that was never measured. The number looks like a census of the run; it is a census of a declaration. | **Take the denominator from the EXECUTOR's own list.** The satisfiable form is `ran == registry=True rows`, never `ran == declared`. Report `scanned` next to `truncated`. See rules 18 and 20. |
| 19 | A negative control used port 9 as a dead backend. Port 9 is on Chromium's blocked-port list, so `browser-verify.mjs` ended with `net::ERR_UNSAFE_PORT` and **never issued an HTTP request at all**. | The arm produces no backend-layer evidence: the failure happened one layer below the thing the control was built to observe. A reader looking for a connection-refused shape finds none, and may conclude either that the suite never measured anything or that it passed. | **A control arm must clear the CLIENT's own restrictions before it can be evidence.** State which layer the failure occurred at — browser, transport, or HTTP — and pick a dead port the client will actually attempt. |
| 20 | A positive control captured the doctor's own error text `server NOT reachable at http://127.0.0.1:59997: URLError` up to `...59997:` — the colon belongs to the sentence, not to the URL — and compared that with the gate's `...59997`. On an arm where both sides were necessarily equal and correctly formatted, it reported a divergence. | The captured value was non-empty and plausible, so the control looked live while its boundary was wrong. The "disagreement" it reported did not exist. A capture that stops mid-URL is the same defect as one that captures nothing, wearing a value. | **A control must check the value is RIGHT, not merely that it captured something.** Assert the capture against a known answer — the exact string, without the trailing punctuation — and keep an arm that can only pass when the reader is correct. See rule 22. |
| 21 | Reading `gate.ps1`, the export of the normalised base URL appears to happen after the value was already used, and reads as a defect. It is not: `pm.py` normalises with `startswith` and `gate.ps1` with `-notmatch`, which currently agree on `http://` and `https://`. Measured on task-133's no-scheme arm: the address the doctor printed was identical to the address the gate used (`identical=True`). | Recorded here so the next reader does not re-open it. A duplicate normalisation is indistinguishable from a real ordering bug by reading alone, and "fixing" it would change behaviour that two independent readers already agree on. | **When a parent normalises and a child re-normalises the same value, prove the two agree on the same input, then record that measurement beside the code.** A later export is not a defect. See rule 26. |
| 22 | `Where-Object { $_ -like '??*' }` over `git status --porcelain` reported **42 untracked files**. `-like` treats `?` as a single-character wildcard, so the filter matched every line, including each staged `M `. | Same mechanism as incident 3, and the same danger with the sign flipped: a filter that matches everything yields a confident, wrong file list, and "42 files" is more believable than "0 files" because it is not the answer a reader expects. | **Use `-match '^\?\?'`, never `-like` with `?`.** See incident 3. |
| 23 | A report stated that the doctor is blind to the instance, because `DOCTOR_SUITES_PROBLEMS=0` and `doctor: 0 problem(s)` were printed while the instance was unreachable. It is not blind: `DOCTOR_SUITES_PROBLEMS` is the TOTAL of every problem family the doctor found (declaration + registry-uniqueness + other), and an instance that is SET but unreachable only prints, so it adds 0 to that total. What is NOT true any more is "the instance block only prints": with `GATE_BASE` UNSET there is no instance to reach, and that is a REFUSAL - the doctor adds 1 to the total and exits non-zero (task-170; measured). | An unreachable instance does not move those counters, and that is correct behaviour. But a reader who compares a counter against an unreachable instance sees green where they expected red, and files a defect that does not exist; and a reader who still believes "the instance block only prints" expects a green exit from a doctor that measured nothing at all. | **Read a counter against the family it actually counts before calling it wrong.** Report what each counter is fed by, and say which cases make it move. See rule 18. |

**⇒ Correction to #14's mechanism (measured 2026-10-22, ablation on a 30-line
repro).** The observed `LOCAL`/`GLOBAL` numbers were right; the explanation
was wrong. The `0` was **not** left behind by the native call, and it was
**not** a function-scope copy — it was the script's **own** top-level
`$LASTEXITCODE = 0`, sitting in **script scope**. In the `-File` form,
script scope IS the outermost scope, so the native call overwrites that
same copy and the read is right. In the `-Command` form, script scope is a
**child** of the `-Command` scope, the native call writes the real code one
level **out**, and the script's own `0` **shadows it at read time**. Scope
dump: `-Command` => SCOPE 1 = 0 (script), SCOPE 2 = 1 (-Command), GLOBAL = 1;
`-File` => SCOPE 1 = 1, GLOBAL = 1. Removing that one assignment makes the
`-Command` form correct on its own; removing `$global:LASTEXITCODE = 0` or
`Set-StrictMode` changes nothing. **⇒ The culprit is the assignment, not
the invocation form.** The fix is in `d8b7405` (the harness reads
`ProcessStartInfo.ExitCode`); only this explanation was stale.

Two of these were **the same bug twice**: #5 had already produced a dedicated
tool, and the tool was not used.

---

## Rules

1. **A tool that cannot measure must fail loudly.** Return non-zero, or emit a
   token the caller greps for. Never let a measurement collapse to `0`, `""`,
   `null`, or an empty array and let that pass as data.
2. **Always run `self-test.ps1` after changing anything here.** It carries a growing set of known-answer cases (31 on 2026-10-22)
   precisely so the nine failures above cannot return.
   **It takes no `-RepoRoot`** — its only parameter is `-KeepFixtures`.

   ```powershell
   powershell -File tools/verify/self-test.ps1
   ```

   Expected on a healthy tree. The counts depend on the environment, so read
   the case list rather than the totals when you are checking a specific
   number. **Some need git** (the two `git-status-report` groups and the two
   `eol-check` groups) and **more need a usable `DOTNET_ROOT`**; every case
   that cannot run reports SKIP rather than silently disappearing.

   Measured 2026-10-22 on a healthy tree, with git and a usable `DOTNET_ROOT`:

   ```
   CASES_PASS=31
   CASES_FAIL=0
   CASES_SKIP=0
   RESULT=PASS
   TRUST=HIGH
   CHILD_EXIT_CAPTURED=27
   CHILD_EXIT_NONZERO=16
   ```

   **⇒ The citation cases are a CONTROL GROUP, not three extra checks.**
Same fixture, same spec format; only the cited text differs, and the
wrong-citation case must exit NON-ZERO. A tool that returned 0 for
every run would still pass the two green cases, and only the third
catches it. **⇒ When you add cases, ask what a broken tool would
still pass; that is the case worth adding.**

The two `CHILD_EXIT_` counters are the harness proving it read its
   children's **real** exit codes (rule 11). **`CHILD_EXIT_NONZERO` must be
   greater than zero** — if it is 0, the harness is reading 0 for everything
   and every case that expects a non-zero exit is meaningless.

   Without a usable `DOTNET_ROOT` or without git, the affected cases report
   SKIP **and say why**. **Anything else means this tooling is now
   unreliable.**
3. **Known-answer tests only.** A test that cannot fail proves nothing.

   **A test that fails for the WRONG reason is not a test** — it is worse
   than no test, because it passes later for a reason nobody checked.

   Measured 2026-10-22: a case meant to isolate "the census was truncated"
   was built from three *real* citations, and it went red — but for
   `NO_TEXT`, because a sweep never carries citation text (rule 14's own
   mechanism). The truncation it was supposed to prove was never exercised.
   Re-built from three citations naming files that do not exist, the only
   thing left that can turn it red is the truncation itself, and the tool
   printed `exit=0 CITATIONS_TRUNCATED=2 RESULT=PASS` — the defect the case
   was written to catch.

   **⇒ When a case fails, read WHICH counter moved.** "Red" is not evidence;
   the counter that went up is. A case that is red for an incidental reason
   will stop being red the moment that incidental reason is fixed, and the
   case it was written for will never have been tested.

   **⇒ One observable quantity per case** (rule 14 again, applied to the
   test rather than the tool). Assert the exit code alone when the exit code
   is the claim; assert one named counter when the claim is about a count.
   **⇒ And a fix that renames or re-shapes the output must update the
   assertion in the same commit — never widen the assertion to turn it
   green.**
4. **`eol-check.ps1` compares the working tree against `HEAD`, not against the
   index.** A bare `git diff` compares the working tree with the index, so on
   a file whose changes are already staged it is empty — and that empty output
   was read as "byte identical to HEAD": added 0, deleted 0, ratio 0, **PASS**.
   Measured 2026-10-22 on a staged 46/18 change: 0/0, ratio 0, PASS.
   It now runs `git diff HEAD --numstat` and reports three distinct reasons to
   fail, each with its own message and a `fail_reason=` token:
   `eol-rewrite` (same text, different bytes), `endings-normalised` (HEAD
   carried bare-LF endings and the working copy has none left and is CRLF —
   decided on the bytes, so it does not depend on the diff algorithm), and
   `ratio` (git says many lines changed — **the heuristic, not proof of
   line-ending damage**; see rule 6).

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
   git diff HEAD --numstat -- <file>                    # raw, against HEAD
   git diff HEAD --ignore-all-space --numstat -- <file> # must equal the above
   ```

   Identical counts and `eol_rewrite=False` ⇒ the FAIL is the ratio heuristic,
   not churn. **But an EQUAL pair of numstats does NOT prove there is no
   line-ending change** — MEASURED 2026-10-25 on a controlled fixture (a
   scratch git repo outside this one: the HEAD blob has 400 bare-LF endings,
   the working copy has the same text with every 7th line's ending turned
   CRLF — 58 CRLF among 342 bare-LF — and one line edited; `.gitattributes`
   says `* text=auto eol=lf`):
   ```
   git diff HEAD --numstat              -> 1  1
   git diff HEAD --ignore-all-space     -> 1  1     <- EQUAL
   HEAD blob    : 3092 bytes  CR= 0  LF=400
   WORKING copy : 3157 bytes  CR=58  LF=400          <- the endings DID change
   ```
   The reason the pair is equal is the ATTRIBUTE: `text=auto eol=lf` makes git
   CLEAN the working copy's CRLF back to LF before diffing, so the CR
   difference never reaches the diff. **The detector that shipped before
   2026-10-25 passed that file with `RESULT: PASS` and exit 0.**
   **⇒ Only the BYTES can decide the endings question; the numstats cannot.**
   `eol-check.ps1` now decides it on the bytes — a KIND test (the working copy
   has an ending kind that HEAD has none of) — and reports
   `fail_reason=endings-normalised` with both sides' counts.
   **Note the `HEAD`:** a bare `git diff` compares the working tree with the
   index, so on a file whose changes are already staged it comes back empty
   (rule 4).

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

10. **A helper you define can be shadowed by an alias, and the script still
    runs.** PowerShell resolves **Alias > Function > Cmdlet**, so a function
    named `Measure` is never reached while `measure` — the built-in alias for
    `Measure-Object` — is. The script keeps going and keeps printing numbers,
    because `Measure-Object` also prints numbers.

    Measured 2026-10-22: a probe script's per-arm function was named
    `Measure`. The alias ran instead, and the call failed with *"a positional
    parameter cannot be found that accepts argument '2220100'"* — an error
    that points at the **arguments**, not at the shadowing, so the obvious
    "fix" is to change the call rather than the name. An argument shape that
    happened to bind would have printed a plausible set of numbers and the
    script would have passed.

    Two habits that prevent it:

    - Give helpers a **verb-noun** name that no alias can take
      (`Invoke-CastArm`, not `Measure`). Before you commit to one, ask
      `Get-Alias <name>`; if it answers, the name is taken.
    - **Prove your own code ran.** A function that sets a sentinel
      (`$script:Called++`) and a script that asserts the sentinel afterwards
      turns "my function was never called" from a mystery into one line.

11. **A child process's exit code comes from `ProcessStartInfo.ExitCode`,**
    never from `$LASTEXITCODE`. The reason is not "the automatic variable
    behaves differently per invocation form" (incident 14's first
    explanation, now corrected above): it is that a script which assigns
    `$LASTEXITCODE = 0` **in script scope** shadows the real code in the
    `-Command` form, where script scope is a child scope, and not in the
    `-File` form, where it is the outermost scope. **=> Do not assign
    `$LASTEXITCODE` in a script that later reads it** - that is the whole
    defect, and it is silent: the read can be wrong in one invocation form
    and right in another **without anything failing loudly**.
    `Common.ps1`'s `Invoke-CheckedProcess` already
    does this correctly and hands `DOTNET_ROOT` to the child explicitly
    (incident 2), so use it rather than `& $exe`.

12. **A match on text must end at a BOUNDARY, not inside a word.**
    `phobj.hspeed` is not a citation of `phobj.h` (incident 15). The same
    mistake has a second shape: a guard that is "stricter" can **silently drop
    real matches** — requiring the basename to start with an upper-case
    letter threw away 35 genuine lower-case citations, and the total still
    looked reasonable. **Measure every category against a known answer before
    believing a tightened pattern.**

13. **A function that RETURNS A VALUE must not also WRITE to the success
    stream.** PowerShell returns everything a function wrote, so a function
    that prints *and* returns returns an **array**, not the value. `-not` on a
    non-empty array is `$false`, so a guard written that way can never fire.

    Measured 2026-10-22 on `gate.ps1`'s `Alive`:

    ```
    backend UP   -> objects=1  type=Boolean  value=True
    backend DOWN -> objects=2  type=Object[]
                     elements = [   >>> RED (backend unreachable: ...)] [False]
    if (-not (AliveDown ...)) -> False
    the guard never fires     -> YES
    ```

    Emit the message from the caller, or stash it in a script variable.
    **In this tree the family has exactly one site** — 17 `.ps1` files, 56
    function definitions, 47 distinct names, 15 boolean call sites; every
    other candidate was measured at one object of the right type, and there
    are no alias collisions.

    **And the scanner that proved it was wrong five times before it was
    right.** It counted `if` conditions as writes; counted `$x++`
    (`TokenKind` is `PostfixPlusPlus`, not `PlusPlus`); counted
    `[void]$x.Append()` (`TypeName` is bare `void`, not `System.Void`); and
    **never descended into `try`/`catch` bodies at all** — so its first
    full-tree run printed "no instance of the family" while the one real
    instance sat inside a `catch` block. Only a positive control caught it:
    pointing the same harness at the known-bad `Alive` and seeing
    `objects=2` and the guard dead.

    **An empty result from a static scan is not a finding.** "No signal" and
    "clean" must never share a report — the same species as incident 12 and
    as a gate reading a MISSING suite file as GREEN.

14. **A suite's GREEN shape is decoration; only its RED markers decide the
    verdict.** `gate.ps1` decides what to PRINT from one list
    (`VERDICT|ALL LADDERS PASS|passed,|BAD=|PASS:`) and decides what to JUDGE
    from a different one (`$RED` plus `BAD=[1-9]`). A suite with no green
    shape only makes the gate print `(read the tail)` — a **readability**
    defect. A suite with no red marker leaves `$bad` empty, so the gate
    prints `ok` and the suite **passes** — a **verdict** defect.

    The two differ by an order of magnitude, so a review that lists them
    side by side understates the second.

    Measured 2026-10-22, and it was true when measured: at `cbf7a60` -- the
    commit that added this rule, 15:20:25 -- `attackdrop-verify.py` had **0**
    `>>> RED`, **0** `sys.exit` and **0** `BAD=` in the file, so it was
    **structurally incapable of reporting red**. Fifty mobs with the wrong
    attack count produced one line of text and the gate said `ok`.

    **=> This example was FIXED 46 minutes later, so it is history, not a
    live counterexample.** `7b0b2f5` ("let three suites print the markers the
    gate judges", 2026-10-22 16:06:56) gave the suite its markers: its parent
    `fb44a31` carries **0 / 0 / 0**, the commit carries **3 / 2 / 3**. The
    file's newest commit `089b23d` (2026-10-23) is **not** the fix -- it leaves
    the counts exactly where `0c82997` already had them. Measured today on the
    same file, counting code only (a comment is not code):

    ```powershell
    $p    = 'tools/verify/suites/attackdrop-verify.py'
    $raw  = Get-Content $p
    $code = $raw | Where-Object { $_ -notmatch '^\s*#' }
    "file-wide : >>> RED={0}  sys.exit={1}  BAD=={2}" -f (($raw | Select-String -AllMatches '>>> RED').Matches.Count), (($raw | Select-String -AllMatches 'sys\.exit').Matches.Count), (($raw | Select-String -AllMatches 'BAD=').Matches.Count)
    "code only : >>> RED={0}  sys.exit={1}  BAD=={2}" -f (($code | Select-String -AllMatches '>>> RED').Matches.Count), (($code | Select-String -AllMatches 'sys\.exit').Matches.Count), (($code | Select-String -AllMatches 'BAD=').Matches.Count)
    for ($i=0; $i -lt $raw.Count; $i++) { if ($raw[$i] -notmatch '^\s*#' -and $raw[$i] -match '>>> RED|sys\.exit|BAD=') { "L{0}: {1}" -f ($i+1), $raw[$i].Trim() } }
    ```

    ```
    file-wide : >>> RED=5  sys.exit=2  BAD==3
    code only : >>> RED=4  sys.exit=1  BAD==1
    L18: print(">>> RED: archivelib.py is missing next to this suite")
    L21: print(f">>> RED: archivelib.py next to this suite does not parse: {exc}")
    L109: print(">>> RED: too few successful reads for this run to mean anything")
    L164: print(f">>> RED: {mismatch} mob(s) where our attack count differs from what the reference keeps")
    L170: print(f"\nBAD={bad}")
    L171: sys.exit(1 if bad else 0)
    ```

    **=> And no registered suite is a live counterexample today.** The
    denominator is the **executor's own list**, read out of `gate.ps1` -- its
    `Run '<id>'` entries, taken with the same regex `pm.py` uses -- and not
    out of a filtered copy of the declaration: **34** suites are declared in
    `pm/SPEC.yaml`, **33** of those ids are in the gate's Run list, and the
    gate runs those **33**. The one that is declared and not run is
    `save-audit`, which declares `in_gate: false`. The scan therefore covers
    the **33** the gate runs, not the 34 that are declared: **0 missing on
    disk**, so the scan really did read all 33, and **0** of them lack a red
    marker in code.

    **⇒ And the names are not the semantics, so here is what each field
    means.** `scanned` is the number of files **actually read** -- a read
    that fails is not a scan, and a directory is not a file. `truncated` is
    `scanned != <the number the executor says it runs>`: the census did not
    cover the population it set out to cover. `missing_on_disk` is a
    *different* question -- a declared file that is not there at all.
    Deriving `truncated` from `missing_on_disk` answers "is a declared file
    absent" and calls that "the scan was cut short"; a file that is present
    and cannot be read is missing from neither, and a population that was
    cut short before the scan started is missing from neither either. Two
    controls below turn each field on purpose.

    ```powershell
    # The census and its three controls, in ONE instrument. An arm that ran
    # different code would prove nothing about the census.
    #
    # The `suites:` block is cut out EXPLICITLY, for two reasons. (1) Two
    # `file:` keys live OUTSIDE it, under `invariants:` -- they are
    # invariants, not suites, and the same regex over the whole file gives
    # 36 instead of 34. (2) Those two carry LITERAL DOUBLE QUOTES, and a
    # quoted path handed to Test-Path raises "Illegal characters in path",
    # which would make missing_on_disk a false positive.
    # SPEC.yaml is read with an explicit UTF-8 decode: `Get-Content` without
    # -Encoding reads it as ANSI, mangles the CJK header, and returns 467
    # lines instead of 587.
    #
    # The DENOMINATOR is the executor's own list, not a filtered
    # declaration: gate.ps1's `Run '<id>'` entries, with the same regex
    # pm.py uses (tools/pm.py:1590). A row that declares `in_gate: false` is
    # a claim ABOUT the executor; rule 18 asks for the executor's list.
    $gateText = [IO.File]::ReadAllText('tools/verify/gate.ps1', [Text.Encoding]::UTF8)
    $gateIds  = @([regex]::Matches($gateText, "Run\s+'([^']+)'") |
                   ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
    $txt      = [IO.File]::ReadAllText('pm/SPEC.yaml', [Text.Encoding]::UTF8)
    $block    = ([regex]::Match($txt, '(?s)suites:\s*\r?\n(.*?)\r?\ninvariants:')).Groups[1].Value
    $entries  = @([regex]::Matches($block, '(?ms)^[ \t]+- id:[ \t]*(?<id>[^\r\n]+?)[ \t]*\r?\n.*?(?=^[ \t]+- id:|\z)'))
    $suites   = @($entries | ForEach-Object {
      [pscustomobject]@{
        id     = $_.Groups['id'].Value.Trim().Trim('"')
        file   = ([regex]::Match($_.Value, '(?m)^[ \t]+file:[ \t]*"?([^"\r\n]+?)"?[ \t]*$')).Groups[1].Value
        inGate = -not ($_.Value -match '(?m)^[ \t]+in_gate:[ \t]*false[ \t]*$')
      }
    })

    # The instrument. `scanned` counts the files it READ, so a read that
    # fails is not a scan; `truncated` compares that count with the
    # population the executor says it runs, so a population that was cut
    # short cannot report a complete scan.
    #
    # The second clause of `truncated` is not decoration: the expected count
    # comes from the executor's list, so an EMPTY executor list would make
    # the expected count 0, the scanned count 0, and the comparison would
    # call a census that read NOTHING complete. A census of nothing is not a
    # census (rule 18), and `gate.ps1` itself refuses to run with an empty
    # Run list.
    function Measure-SuiteMarkers($paths, $expected, $declaredSuites) {
      $scanned = 0; $missing = @(); $broken = @()
      foreach ($f in $paths) {
        if (-not (Test-Path -LiteralPath $f -PathType Leaf)) {
          if (-not (Test-Path -LiteralPath $f)) { $missing += $f }
          continue
        }
        $err = $null
        $c = @(Get-Content -LiteralPath $f -Encoding UTF8 -ErrorAction SilentlyContinue -ErrorVariable err |
               Where-Object { $_ -notmatch '^\s*#' })
        if ($err) { continue }
        $scanned++
        if ((($c | Select-String -AllMatches '>>> RED|BAD\s*=\s*[1-9]').Matches.Count) -eq 0) { $broken += $f }
      }
      [pscustomobject]@{ scanned = $scanned; missing = $missing; broken = $broken
                        truncated = (($scanned -ne $expected) -or
                                     (($expected -eq 0) -and ($declaredSuites -gt 0))) }
    }
    $fmt = "suites declared={0}  ran={1}  not_run={2}  scanned={3}  missing_on_disk={4}  truncated={5}  with NO red marker in code: {6}"

    # ARM 1 -- the census: the suites the gate actually runs.
    $ranSuites = @($suites | Where-Object { $gateIds -ccontains $_.id })
    $notRun    = @($suites | Where-Object { $gateIds -cnotcontains $_.id })
    $r = Measure-SuiteMarkers @($ranSuites | ForEach-Object { $_.file }) $gateIds.Count $suites.Count
    $fmt -f $suites.Count, $ranSuites.Count, $notRun.Count, $r.scanned, $r.missing.Count, $r.truncated, $r.broken.Count

    # ARM 2 -- control: a DIRECTORY in the population. `scanned` must be 1,
    # not 2: Get-Content on a directory raises a NON-TERMINATING error, and
    # a counter that increments before the read is done counts the attempt.
    $ctlA = @('tools/verify/suites/ladder-all.ps1') + @('tools/verify/suites')
    $rA = Measure-SuiteMarkers $ctlA $ctlA.Count $suites.Count
    $fmt -f $ctlA.Count, $ctlA.Count, 0, $rA.scanned, $rA.missing.Count, $rA.truncated, $rA.broken.Count

    # ARM 3 -- control: the population cut to 21 of the 33. `truncated` must
    # be True: 12 suites were never scanned, and a census that says
    # otherwise is a truncated census wearing a complete-scan label.
    $ctlB = @($ranSuites | Select-Object -First 21 | ForEach-Object { $_.file })
    $rB = Measure-SuiteMarkers $ctlB $gateIds.Count $suites.Count
    $fmt -f $ctlB.Count, $ctlB.Count, 0, $rB.scanned, $rB.missing.Count, $rB.truncated, $rB.broken.Count

    # ARM 4 -- control: a path that is not on disk. `missing_on_disk` must
    # be 1, and `truncated` must be True.
    $ctlC = @('tools/verify/suites/ladder-all.ps1') + @('does/not/exist/nowhere.py')
    $rC = Measure-SuiteMarkers $ctlC $ctlC.Count $suites.Count
    $fmt -f $ctlC.Count, $ctlC.Count, 0, $rC.scanned, $rC.missing.Count, $rC.truncated, $rC.broken.Count

    # ARM 5 -- control: the executor's list EMPTY. `truncated` must be True:
    # the census read nothing of a registry that declares 34, and an
    # expected count of 0 is not a licence to report a complete scan.
    $emptyIds    = @()
    $emptySuites = @($suites | Where-Object { $emptyIds -ccontains $_.id })
    $notRunEmpty = @($suites | Where-Object { $emptyIds -cnotcontains $_.id })
    $rE = Measure-SuiteMarkers @($emptySuites | ForEach-Object { $_.file }) $emptyIds.Count $suites.Count
    $fmt -f $suites.Count, $emptySuites.Count, $notRunEmpty.Count, $rE.scanned, $rE.missing.Count, $rE.truncated, $rE.broken.Count
    ```

    ```
    suites declared=34  ran=33  not_run=1  scanned=33  missing_on_disk=0  truncated=False  with NO red marker in code: 0
    suites declared=2  ran=2  not_run=0  scanned=1  missing_on_disk=0  truncated=True  with NO red marker in code: 0
    suites declared=21  ran=21  not_run=0  scanned=21  missing_on_disk=0  truncated=True  with NO red marker in code: 0
    suites declared=2  ran=2  not_run=0  scanned=1  missing_on_disk=1  truncated=True  with NO red marker in code: 0
    suites declared=34  ran=0  not_run=34  scanned=0  missing_on_disk=0  truncated=True  with NO red marker in code: 0
    ```

    **⇒ The denominator is `ran=33`, never `declared=34`.** It is the
    executor's own list -- read from `gate.ps1`, the list the gate itself
    runs -- and not a filtered copy of the declaration (rule 18, and row 18
    of the table above). A third path agrees: `python tools/pm.py doctor`
    prints one `registry=` row per suite, and 33 of the 34 carry
    `registry=True gate=True`; the one that does not is `save-audit`. The
    census's own `ran` set is that same set of 33 ids, by construction:
    `gate.ps1` has 33 `Run` entries, every one of them names a declared
    suite, and the one declared suite that is not in the list is
    `save-audit`.

    **⇒ And the controls are what make the fields mean what this
    paragraph says they mean.** ARM 2 turns `scanned` (1, not 2), ARM 3
    turns `truncated` (True, not False), ARM 4 turns `missing_on_disk` (1,
    not 0), ARM 5 turns `truncated` for the degenerate case (the executor's
    list empty: `scanned=0` and still `truncated=True`). A field that cannot
    turn is not a measurement (rule 22) -- and a census whose two headline
    fields cannot turn is the same species as incident 12: a missing field
    and a zero look identical.

    **⇒ ARM 5 exists because the obvious form of `truncated` was WRONG.**
    With `truncated = (scanned -ne expected)` and `expected` read from the
    executor's list, an EMPTY executor list gives `expected=0` and
    `scanned=0`, the comparison is satisfied, and the census prints
    `declared=34 ran=0 scanned=0 truncated=False` -- a census that read
    nothing, reporting itself complete. **⇒ The expected count moved with
    the failure, so the comparison could not see it.** That is why the
    clause "`expected == 0` while suites are declared is not a complete
    census" is part of the instrument, and why ARM 5 is here: the hole was
    found by running the block against a scratch copy of `gate.ps1` with
    every `Run` entry removed, and the fix is proven by ARM 5 turning.

    **⇒ And the census covers the executor's list; it does not police the
    registry against it.** `pm.py doctor` is what reports a row whose
    `in_gate` flag and the gate's Run list disagree (`DRIFT`); this census
    only asks whether every suite the gate RUNS has a red marker. The two
    questions have different denominators on purpose.

    **=> So the live counterexample, when you want one, is whatever THAT
    command names -- not `attackdrop-verify.py`.** And a `0` there is a
    finding about the tree, not an empty scan: re-run it whenever you touch a
    suite, and if a suite really has lost its markers, write its name here.

    **A suite that cannot print the marker the gate greps for does not exist
    as far as the gate is concerned** — and a suite that aborts without a
    marker is worse than one that never ran, because the abort reads as a
    pass.

    And note the case sensitivity: `String.Contains` is case-sensitive, so
    `NO DAMAGE over 400 frames` does not match the marker `NO damage
    number`. A fix already exists in the tree — `mob-attack-verify.ps1`
    carries the corrected form — so **copy the shape that is already
    there; do not invent a new marker.**

    **A suite's verdict is read from TWO places: the gate greps the output,
    and every other caller reads the EXIT CODE.** Measured 2026-10-22
    against `gate.ps1:92-100` itself:

    ```
    output with BAD=18, no >>> RED   -> gate: >>> RED (BAD=n)
    output with BAD=8  and >>> RED   -> gate: >>> RED (>>> RED, BAD=n)
    output with nothing red, BAD=0   -> gate: ok
    ```

    So `BAD=[1-9]` **does** catch `BAD=18` — the gate sees it. The gap is
    elsewhere: a suite that prints `BAD=18` and **exits 0** is green to the
    gate but green to every caller that only checks the exit code.
    **⇒ A failing suite must print the red marker AND exit non-zero.**

    **⇒ And a judge must have exactly ONE admissible rule.** `ballart-verify.py`
    used to accept two: the ball's own delays, *and* "100 ms per frame".
    The second is not the reference's rule — `Animation.cpp:64-67` says each
    frame uses its own delay and only a frame with **no** delay node falls
    back to 100. With two rules a frozen build passed: at `since=130` with
    period 120 the reference rule says frame 0, the 100 ms rule says frame 1,
    and the frozen port printed frame 1 — **green**. ⇒ A positive control
    (does the judge fail on a deliberately broken build?) is what exposed it;
    a judge with two rules cannot fail that control.

15. **A judge must not take its expected values from the thing it judges.**
    `ballart-verify.py` used to read the expected per-frame delays from the
    port's own published `ballFrameDelays` — and the "the published frame
    count is the archive's" check compared the port's number with the port's
    number, which was empty. **⇒ The port could read the archive wrong and
    still "agree with itself"; the judge was unable to disagree.**

    The rule's input now comes from the **archive** (`/api/inspect` reads
    `<mob>.img/<action>/info/ball`, applying the reference's own frame-set
    rule: numeric name + bitmap type, per `Animation.cpp:177-193`, delays per
    `:64-67`). ⇒ The judge now has an expectation that is **independent** of
    the port, which is the only way it can fail.

    **⇒ The same shape as the citation rule: a check whose expected value
    comes from the subject under test is not a check.**

16. **Do not hardcode an archive id.** `ballart-verify.py` had
    `MAP = "f4/Map/Map0/000000000.img"`. Ids are handed out in the order
    archives were opened, so `f4` is only `f4` in the session that happened
    to open them in that order — with 20 archives open, `Map.nx` happened to
    be `f4`; on another instance it is something else or nothing. **⇒ Resolve
    archives by NAME, and when the archive is not open, say so and fail
    explicitly** (`>>> RED`), never continue against a wrong or missing
    target.

    **⇒ The family was fixed on 2026-10-22 by `0645f62`** ("resolve archives
    by name instead of a hard-coded id", 23 files). Measured before and after
    with `git grep -l -E 'f[0-9]+/' <tree> -- tools/verify/suites/`:

    ```
    0645f62^  ->  21 suites
    HEAD      ->   0 suites
    ```

    **⇒ 21, not 19.** The earlier "18 hardcode `f4` and one hardcodes `f3`"
    undercounted by one on the `f3` side (`npcchat-verify.py` carries `f3`
    as well as `f6`) **and** missed two suites outside the `f4`/`f3` frame
    entirely — `attackdrop-verify.py` (`f5`) and `equipstats-verify.py`
    (`f1`). **⇒ A census that only looks for the ids it already knows about
    is not a census** (rule 18: report what was scanned, not what you
    expected to find).

    **⇒ Instrument: `tools/verify/archive-id-census.py`.** It reads the
    suite list with `pm.py`'s own parser (one list, not two conventions),
    reports `scanned` and `truncated`, splits **CODE / PROSE / COMMENT**,
    and prints the resolved path and a digest of the tree it actually read.

    **⇒ And the two arms must not be able to tie** (rule 20). Measured
    2026-10-22: the first version of that tool loaded `pm.py` with
    `import pm`, and Python cached the module — so the second arm read the
    FIRST arm's `pm/SPEC.yaml` and resolved suite paths against the first
    arm's root. Both arms reported `CODE=26 / suites=2` exactly, and a
    single-directory run disagreed with the two-arm run. The fix is
    `importlib.util.spec_from_file_location` against the tree being
    censused, one module name per root, plus an assertion that every
    resolved suite path lies inside that root.

    **⇒ Do not read "what is left" out of this rule — read it out of the
    census.** A rule that lists current contents is stale the moment the
    next commit lands, and a stale list is worse than no list: it sends
    someone to fix what is already fixed. Run the instrument and compare:

    ```
    python tools/verify/archive-id-census.py --root . --label current
    ```

    **⇒ The expected answer on 2026-10-22, after `2216675`:**
    `suites_with_CODE = 0` among the declared suites, and the only CODE
    hits left are 3 **deliberate fakes** (`tools/probe/avatar-check.mjs`,
    `tools/probe/close-stub.js`, `tools/probe/mcp-assets.mjs`) — those are
    not defects. **⇒ If your number differs, the census is right and this
    paragraph is out of date; fix the paragraph, not the census.**

17. **A judge pointed at an instance running a STALE binary is not a
    measurement of the tree.** Measured 2026-10-22: the gate instance
    (pid 14384) started 2026-10-21 20:24:37 and ran a `MapleBenchX.dll`
    built 2026-10-20 19:35:06. **28 product commits landed after that
    build**, and four of them were the fixes for the very suites the gate was
    reporting red. Pointing `GATE_BASE` at an instance started after every
    commit, with the same suites and the same archives and the sample sizes
    unchanged, gave:

    ```
    position-accuracy   6/6     attack-frame-timing 9/9     attack-damage 11/11
    ```

    No inference was needed — the failure text printed the pre-fix behaviour
    itself: `standingY -> 84 at y=-25` (the deepest foothold) where the
    reference said the one underneath at `y=-535`, and the fix was exactly
    "the nearest, not the deepest".

    **⇒ Before trusting any red, check what the instance is running:**
    the process start time against the build's mtime against the commits you
    believe are in it. A red suite whose fix is already committed is not a
    defect; it is a process that was never restarted.

    **⇒ And the same family, second shape: a gate run that OVERLAPS a commit
    is not a measurement either.** `4fc935f` landed at 15:50:04 while a gate
    ran 15:41 to 15:52, so `ball-art` reported `BAD=45` under the old judge
    and `BAD=40` under the new one — **each perfectly stable**, and neither
    a flake. "The number changed between runs" is a hypothesis; "the tree
    changed between runs" is a measurement you must exclude first.
    **=> And identify the server's process by the PORT it serves, not by its
    NAME.** Rule 17 was written from a measurement, and re-checking it the same
    day found the check itself was aimed at the wrong process:
    `Get-Process MapleBenchX` returned a pid that was serving nothing (an
    apphost under a temp build directory, not any listening port), while the
    process actually answering on 5591 was a `dotnet` host started the previous
    evening running a DLL built ~25 hours earlier with 26 product commits
    missing. The reported "how stale is the gate instance" was wrong in both
    directions: wrong process, and understated by ~24 hours.

    `Get-Process <name>` matches a **name**, and several copies of the same
    server run at once on this machine - `dotnet MapleBenchX.dll`, an apphost
    `MapleBenchX.exe`, and private per-reviewer builds. Name lookup returns one
    of them, and not necessarily the one serving the port you are about to
    measure against. Resolve the port first:

    ```powershell
    Get-NetTCPConnection -State Listen -LocalPort <port> |
      Select-Object LocalPort, OwningProcess
    Get-CimInstance Win32_Process -Filter "ProcessId=<pid>" |
      Select-Object ProcessId, CreationDate, CommandLine
    ```

    **=> Print the pid, the port it owns, its start time, the binary's build
    time, and the commit count between them. A staleness claim without those
    five numbers is a guess about some process.**

18. **A census must report `scanned` AND `truncated`. A truncated census is
    not a census.** Measured 2026-10-22, in a single turn, the same analyst
    made this mistake twice:

    - A string pool of 5,942 entries was decoded 400 at a time and the
      partial was read as the whole table.
    - An archive sweep with `limit=500` was read as "0 of 500 are mutually
      exclusive"; the full sweep (`limit=5000`, `scanned=126396`,
      `truncated=False`) showed **69 level nodes carry BOTH `lt`/`rb` and
      `range`** — the opposite conclusion.

    **⇒ Two ways this fails, and both look like a finding:**
    - **under-reporting** — a partial sweep reads as "none of them";
    - **trunc vs round** — `trunc` and `Math.Round(ToEven)` differed on
      **0 of 69** of those nodes, so a rounding rule cannot be distinguished
      from a data rule on a truncated sample.

    **⇒ Report the scan size and the truncation flag with every count, and
    if a count is 0, say what was scanned.** "0 hits" over an unstated
    denominator is the same shape as incident 12: a missing field and a zero
    look identical.

19. **A comparison that read ZERO lines is not a comparison.** Measured
    2026-10-22: a check proving "the three lines that decide red are
    byte-identical across two commits" ran `git show :/tools/verify/gate.ps1`,
    which failed with *ambiguous argument*. The script did not stop — it
    compared an empty string against the real file and **reported
    `differences = 3`**. The correct answer was **0**, and the wrong one had
    already been reported as a measurement.

    **⇒ The direction of the error is what makes it dangerous:** the empty
    side *added* differences, so a broken comparison **manufactures a defect**
    in a file someone is about to change. A comparison that read 0 lines of
    what it expected to read is the same species as incident 12 (a missing
    field and a zero look identical) and as rule 14's "an abort reads as a
    pass" — the tool returned a well-formed number instead of nothing.

    **⇒ Two habits, both cheap:**
    - **Print the line count of BOTH sides next to the result.**
      `before=343 after=376 differences=0` is a measurement; a bare
      `differences=0` is a wish.
    - **Assert the non-empty side before diffing.** If a side reads 0 lines
      and the file is known to be non-empty, fail loudly — that is an input
      failure, not a finding.

    **⇒ And the same author hit the same shape twice more in the same
    session, in a different place:** a counter for "how many suites did the
    gate run" counted the *doctor's own* output (29 instead of 0, then 7
    instead of 0), and a whole-line regex matched nothing because the line
    was indented. **⇒ Both were caught only because the receipt was written
    to DERIVE its conclusion from the run instead of printing a constant.**
    **⇒ That design is worth more than the individual counters.**

20. **A script told to measure two different things and reporting the same
    thing for both has measured one.** Measured 2026-10-22: a census that
    counts hard-coded archive ids was run twice to compare "before" and
    "after" a change that removed every one of them. Both runs printed the
    same file count, the same hit count, **and the same line numbers**. The
    reason was that its input directory was a **hard-coded literal** and it
    ignored its own command-line argument entirely, so the second run
    re-read the first run's tree. The reported result was `CODE 31 in 21
    files` for both, printed side by side, where `0` was the answer.

    **⇒ It was not the census that caught this. A different experiment did:**
    an instance with **no archives open** printed the expected failure for
    all 21 suites, which contradicted the census's "31 hits remain". Only
    that contradiction sent anyone back to the script. **⇒ When two arms of
    a comparison measure different trees and report an identical count, the
    instrument is the suspect — not the conclusion.**

    **⇒ Three habits:**
    - **Print the input you actually measured** — the resolved path, and
      something that differs between the two arms (a file count, a hash, a
      commit id). A census that cannot show its own inputs cannot be
      compared with anything.
    - **Make the two arms structurally different.** Pass the path as a
      parameter and **assert the two resolved paths are not equal** before
      comparing. An assertion is cheaper than a wrong census.
    - **Treat a perfect tie as a failure of the experiment, not a result.**
      Real worlds rarely tie exactly; a tie almost always means one side
      was not measured. `files scanned: 34 -> CODE 31` and
      `files scanned: 37 -> CODE 0` is a result; `34 -> 31` and `34 -> 31`
      is a warning.

    **⇒ Same family as rule 18 (a truncated census is not a census) and
    rule 19 (a comparison that read zero lines is not a comparison): the
    tool returned a well-formed number for a run that did not happen.**

21. **A comparison whose operands are not the same kind of thing must
    REFUSE, not return False.** Measured 2026-10-23, three separate
    instances in one session, and the third one is the reason this rule
    exists:

    - **A sha256 was compared against a git blob id** (40 hex). The output
      was `clean=False` on all four rows. It was a **constant**, not a
      finding — "a difference between two different algorithms" is not a
      difference. To compare a working tree against `HEAD`, both sides
      must be git blob ids: `git hash-object <file>` and
      `git rev-parse HEAD:<file>`.
    - **A 16-character prefix was hand-typed into a script and compared
      against a 64-character sha256.** The comparison was structurally
      incapable of returning True. It reported a byte-identical commit as
      "the earlier verdict cannot be carried here", with a confident
      paragraph explaining why.
    - **`-ceq` was applied to two `[byte[]]`.** PowerShell returned
      `System.Object[]`, not a Boolean — a comparison that *prints*
      something verdict-shaped and is not a verdict.

    **⇒ Four habits:**
    - **State the algorithm and the length on BOTH sides of every digest
      comparison.** Print them, not assume them.
    - **Refuse to compare operands of different kinds or lengths.** Raise,
      do not answer `False` — a `False` is indistinguishable from a result.
    - **Pins live in a FILE. A hand-transcribed constant is not a
      measurement.**
    - **Assert that a printed comparison result is a Boolean before you
      read it.**

    **⇒ The uncomfortable half.** Rules 12, 13, 18, 19 and 20 are all about
    instruments that are *quiet* when broken. This one is not. **A broken
    instrument can be loud, and when it is loud it is trusted precisely
    because it is confident.**

22. **Every acceptance reader carries its own positive control, and a
    reader that matched ZERO records on a GREEN arm is broken, not clean.**
    Measured 2026-10-23: a reader matched its id regex against the
    **stripped** line but tested the leading-whitespace guard against the
    **raw** line. Every invariant row is indented, so **all four arms —
    including the arm that passes — returned zero verdict lines**. A
    hardcoded self-check then reported VOID on a run that had worked,
    because it demanded "exactly one reference line" and the
    no-declaration arms produce none.

    **⇒ Three habits:**
    - **Anchor a judgement on ONE form of the line.** Never mix the raw
      line and the stripped line inside a single predicate; split it and
      verify each half separately.
    - **A control arm that matched 0 records voids the round.** In this
      toolchain `0` means "no problem", so a zero must never be read as a
      clean result.
    - **The reader's own self-check must contain a case that only passes
      when the reader works.** A reader that cannot detect its own
      blindness contributes no evidence.

23. **A baseline expressed as a moving reference is not a baseline, and
    an artifact must be FROZEN before anyone certifies against it.**
    Measured 2026-10-23: `C:\mbtest2\t96-cases.py` changed sha four times
    (`9D09793E → 5467a19f → 98bc0043 → b2e2c796`) **while a checker was
    verifying it**, and reported MISMATCH in one run and PASS seconds
    later.

    **⇒ No statement of the form "before/after at `9D09793E`" is
    reproducible by anyone** — that file state no longer exists on disk.

    **⇒ Three habits:**
    - **Name refs by SHA.** `HEAD`, `HEAD~1` and "current" are not
      baselines; they move while you read them.
    - **Freeze the artifact before certifying** — print its digest, its
      size and its line count, and say `FROZEN`. If it moves again, every
      table citing it is void.
    - **Print the `HEAD` a number was measured at, beside the number.**
      A count quoted without its commit is a claim, not a measurement —
      during one session a status list went from four modified files to
      three because a commit landed mid-conversation.

24. **A review target is an immutable id, and no commit may change the
    bytes under a live reviewer.** Measured 2026-10-23, twice in one
    night: `gate.ps1` went `9D2AD8EB… → FBD73D72…` in history, and then
    the working tree grew an uncommitted `+99/-0` while a checker was
    reviewing blob `291bff58…`.

    **⇒ A reviewer must therefore do two things:**
    - **Print the reviewed blob's digest and the working tree's digest
      separately**, and mark the reviewed one as "true when measured".
    - **Byte-compare the region actually under review** before claiming
      their verdicts survive. In that instance the `CrashShapes` block
      hashed identically on both sides — 393 chars taken from the start of
      the `$script:CrashShapes` line through the closing `}`, sha256
      `83252AFC…` on both — so the reviewer's two verdicts stood, proved
      rather than assumed. (The range matters: a digest taken from a
      different start of the same block is a different number. See
      rule 25.)

    **⇒ And the committer's side:** if someone is reviewing a file you are
    about to commit, tell them first. Also remember that a path-prefix
    stage (`git add -- <dir>/`) sweeps in other people's uncommitted
    hunks: **"I may only touch X" is not "commit only my hunk in X".**

25. **A byte-level verification must look at the file's FIRST bytes and
    grep for U+FFFD. Aggregate counts and a strict decode are not a
    corruption check.** Measured 2026-10-23, on the lead's own append to a
    ledger shared by seven agents. Its verification printed, in full:

    ```
    AFTER  bytes=203581 CR=0 NUL=0 U+FFFD=0 headings=111
    STRICT_UTF8_ROUNDTRIP=True
    ```

    Every field green, and the file began `AILURES` — four bytes had been
    lost from the front, so the `F` of `FAILURES` and the UTF-8 BOM were
    gone. Six `U+FFFD` had also appeared elsewhere in the same file, as two
    CJK characters each replaced by three replacement characters, which is
    the signature of a **lossy decode that was re-encoded**.

    **⇒ Six habits:**
    - **Print the first 8 bytes and assert them.** A byte census counts the
      body; only a header assertion can see that the header is gone.
    - **Search for `U+FFFD` as its own check.** A *passing* strict decode
      proves the bytes are valid UTF-8 — and a `U+FFFD` written as literal
      `ef bf bd` sails straight through it.
    - **On a shared append-only file, compare the whole prefix, not the
      tail.** "My text is at the end" cannot see a damaged header; the
      person who checks the prefix is not the person who appends.
    - **Every hash must print its EXTRACTION RULE and its RANGE LENGTH.**
      Measured 2026-10-23: two people measured the same block and got
      `385 chars / 1CF9336E…` and `393 chars / 83252AFC…`. Both were
      internally valid; they measured different ranges — mine started at
      the token `CrashShapes` and skipped the 8-byte prefix `$script:`.
      `385 + 8 = 393`, and with the other person's rule the digests match
      exactly. **⇒ A digest with no stated range is not a measurement
      anyone else can check.** Print the rule, the length and the
      algorithm together, every time.
    - **Run the encoding check AFTER every write, not once.** A reviewer
      measured `non_ascii=0` on his own report and then broke it in the
      same turn by typing `…` and `⇒` into it. **⇒ The check belongs at
      the point of delivery.** List every non-ASCII character, compare it
      against a whitelist, abort on an unknown one, substitute, then
      re-verify **by bytes** — and confirm the count of `?` did not
      change, because an encoder can silently turn a character into `?`.
    - **A `FROZEN` marker must be a FILE with a timestamp, not a message.**
      A pin written into a chat message expires when the message scrolls
      away; a pin in a file with `frozen_at=` does not.

26. **A printed field that participates in a verdict must be recomputed
    from a path INDEPENDENT of the verdict's own path. A field that cannot
    be, should not be printed.** Measured 2026-10-23, by the agent whose
    own checker caught it:

    ```
    rows reproduced: 16 of 16
    ```

    Both numbers were arithmetically perfect and the evidence value was
    **zero**: the numerator and the denominator were computed from the
    same in-memory variables as the verdict — `len(HIS) - len(moved)` —
    with `moved` empty and **not one row compared**. The two fields were
    mutually consistent and mutually worthless.

    **⇒ Three habits, and the third is the one that bites:**
    - **"Recomputable" is not enough. The recomputation must come from a
      different source than the verdict.** Otherwise a self-consistent
      arithmetic mistake looks like a measurement.
    - **Scope the rule to the fields that matter.** A PID, a timestamp, a
      duration and a sampled value have no second path and must not be
      forced into one; a rule that demands the impossible gets ignored.
      **⇒ The line is drawn at "does this field decide red or green", not
      at "is this field a measurement".**
    - **Do not print a field you cannot recompute.** A printed field
      acquires argumentative weight whether or not it is true. That is why
      a wrong `False` was persuasive: it sat underneath two plausible
      digests. **⇒ "Not printed" is always safer than "printed wrong".**

    **⇒ What a non-recomputable field looks like:** `Get-Content |
    Measure-Object -Line` returned 619 for a file with a different line
    count. It did not error, it did not warn — it simply handed over a
    wrong number, and the only reason it was caught is that the same
    number was also available from a second path. **⇒ A quiet number is
    the dangerous kind.**

    **⇒ And the operation itself:** a read-modify-write of a shared file is
    not atomic. Seven agents append to that ledger concurrently, so an
    append must open for **append only** — never read the whole file and
    write it back, which is what loses whatever another writer added in
    between.

27. **Two counts that are EQUAL are not evidence that the two populations
    are the SAME. A population must be compared as a SET, never as a
    count.** Measured 2026-10-23, on `pm/SPEC.yaml`'s own suite registry:

    ```
    declared=34  ondisk=34  EQUAL_COUNTS=True
      => tools/verify/suites/archivelib.ps1
      => tools/verify/suites/archivelib.py
      <= MapleBench/tools/sim-skills-check.mjs
      <= MapleBench/tools/browser-verify.mjs
    SET_DIFFERENCES=4
    SHARED=32  (declared_only=2  ondisk_only=2)
    ```

    Both populations are **34**. They share **32** members and differ by
    **4**: the declared set holds two judges that live OUTSIDE
    `tools/verify/suites/` (`MapleBench/tools/sim-skills-check.mjs`,
    `MapleBench/tools/browser-verify.mjs`), and the on-disk set holds two
    files that are **not judges at all** — `archivelib.py` / `archivelib.ps1`
    are function libraries the harness imports.

    **⇒ Why this is harder to catch than a wrong number.** Every
    single-number check passes: 34 == 34, both sides read as "34 suites",
    and a report can print both and look confirmed. The defect is invisible
    to every comparison that compares counts, and it is the same shape that
    killed the census in this file's own preamble — "With it: 522. Without
    it: 518. Same tree, same rule, 4 files apart", where the difference
    was the trailing quote on CJK-named paths, **not the number**. Rule 20
    is the mirror image: there, a tie means one arm was not measured; here,
    a tie means the two arms measured DIFFERENT things and the tie is
    hiding it.

    **⇒ Four habits:**
    - **Compare sets, not counts.** Print the set difference. `A \ B` and
      `B \ A` both empty is the only statement that means "the same
      population". `Compare-Object -CaseSensitive` — the default string
      comparison in PowerShell is case-INSENSITIVE, which is its own way
      of merging two populations that are not the same.
    - **A count equality is a QUESTION, not a result.** When two counts
      tie, the next command is the set difference — never "they agree".
    - **Name the population's RULE before counting it.** "the declared
      judges" and "the script files in that directory" are two different
      rules, and they will keep being different numbers.
    - **A census must exclude the file the census's own tooling just
      wrote.** Measured 2026-10-23: the edit that produced the numbers
      above wrote them into `pm/SPEC.yaml`, so a repo-wide grep for suite
      counts now hits `pm/SPEC.yaml` itself — a hit that proves only that
      someone typed the number. **⇒ Either exclude the file you just
      wrote, or list it as an EXPECTED hit, and say which.** This file's
      own preamble already carries the pattern: it reports "`INC`-style ids
      in `self-test.ps1` and in this paragraph only" — and that paragraph
      is the file the census was writing into.

    **⇒ The known answer to check a tool against.** On the tree at commit
    `879a45bb` the two populations are 34 and 34, the set difference is
    exactly those 4 paths, and the intersection is 32. A tool that claims
    to compare populations and reports "the same population" here compared
    counts. The command, verbatim, in the repository root. **Both sides are
    read from the SAME ref** — a declared set taken from the working tree
    and a file list taken from a commit are two different trees, and pairing
    them is this rule's own failure (rule 23: a baseline expressed as a
    moving reference is not a baseline):

    ```powershell
    $ref = '879a45bb'
    $t = (git show "${ref}:pm/SPEC.yaml") -join "`n"
    $block = ([regex]::Match($t, '(?s)suites:\s*\r?\n(.*?)\r?\ninvariants:')).Groups[1].Value
    $declared = @([regex]::Matches($block, '(?m)^\s+file:\s*(\S+)\s*$') | ForEach-Object { $_.Groups[1].Value.Trim('"') })
    $ondisk   = @(git -c core.quotepath=false ls-tree -r --name-only $ref tools/verify/suites/ | Where-Object { $_ -match '\.(py|ps1|mjs)$' })
    $diff = @(Compare-Object $declared $ondisk -CaseSensitive)
    "declared={0}  ondisk={1}  EQUAL_COUNTS={2}" -f $declared.Count, $ondisk.Count, ($declared.Count -eq $ondisk.Count)
    $diff | ForEach-Object { "  {0} {1}" -f $_.SideIndicator, $_.InputObject }
    "SET_DIFFERENCES={0}" -f $diff.Count
    $shared = @($declared | Where-Object { $ondisk -contains $_ }).Count
    "SHARED={0}  (declared_only={1}  ondisk_only={2})" -f $shared, ($declared.Count - $shared), ($ondisk.Count - $shared)
    ```

    **⇒ To re-measure a NEWER tree, change `$ref` — and then the answer is a
    fact about that ref, not about this paragraph.** The known answer above
    belongs to `879a45bb` alone; on a tree where a suite was added or
    removed, the counts move and the paragraph is stale, exactly as this
    file's preamble says about its own census.

    **⇒ `-c core.quotepath=false` is LOAD-BEARING, and this file's own
    preamble is what says so** ("without it git prints a non-ASCII name as
    `docs/\350\252\257.md"` -- the trailing quote means the extension regex
    does not match"). A rule about HOW A POPULATION IS COUNTED cannot use a
    listing form this file has already declared to drop population members
    silently. **⇒ Measured today the difference is ZERO files, and that is
    a known answer, not a reason to leave the flag off:** under
    `tools/verify/suites/` there are **0 non-ASCII paths**, so the
    unguarded and the guarded listing both give **37 files / 34 scripts**
    and `differ=False`. The day a CJK-named file lands in that directory
    the unguarded form loses it, and the count moves by one for a reason
    that has nothing to do with the suites. **⇒ "Today correct" is not
    "always correct" — and an untested defence is not a defence, which is
    why the measurement is printed here instead of the flag being added
    silently.**

    **⇒ And the population RULE is stated, because a rule with an unnamed
    population is the failure this rule is about.** The declared side is
    **the `suites:` block only** — 34 `file:` keys, surrounding quotes
    stripped. Measured on `pm/SPEC.yaml`: the whole file carries **36**
    `file:` keys, of which **2 live under `invariants:`** (`avatar.js`,
    `gate.ps1`) and are NOT suites — **⇒ a scan that does not cut the
    `suites:` block out counts 36, not 34.** And those two `invariants:`
    paths are written **QUOTED**, so a regex that requires the value to
    start with `tools/` or `MapleBench/` — without allowing for the
    quote — sees 34 and **drops the two quoted paths with no error at
    all.** Both traps were live in this file's own rule 14 census before
    it was rewritten on 2026-10-23; it now cuts the block out explicitly,
    strips the quotes, and prints
    `declared=34 ran=33 not_run=1 scanned=33` — **the same 34 this rule
    uses.** **⇒ So the two population RULES now agree; what still differs
    is the TREE each is read from: rule 14's census reads the WORKING
    TREE's `pm/SPEC.yaml`, while this command reads a PINNED ref.** That
    is rule 23 again, one level down — same population rule, two different
    trees, and the pinned one is the one anybody can re-measure.

    **⇒ And the other half of this shape is rule 18's `ran == declared`,
    which this rule does NOT fix:** 34 is the number of DECLARED judges;
    the gate RUNS **33** (`save-audit` declares `in_gate: false`). Rule
    14's census now prints both counts side by side
    (`declared=34 ran=33`) — **⇒ do not read 34 here as "the gate runs
    34".**

---

## Before you trust a measurement

- Did the command actually run, or did it die and leave the output at its
  initial value?
- Is the number physically plausible for this archive?
- Did the tool exit non-zero?
- **Did the verdict use the observation the tool printed?** `eol-check.ps1`
  printed `ENDINGS_NORMALISED` and then said PASS: the observation was right
  and the verdict never read it. **A detail line that contradicts the verdict
  is the signature of this failure.**
- **Was the baseline the one you meant?** A bare `git diff` compares the
  working tree with the index, not with `HEAD` (rule 4).
- **Is this the same tree, the same way, every time?** A judge whose answer
  changes under load is not a judge — and a broken test that goes green when
  the machine is quiet has its failure absorbed instead of reported.

A measurement that did not happen and a measurement that happened are
indistinguishable from the output alone. That is the whole reason `self-test.ps1`
exists — do not skip it.

---

## Two `eol-check.ps1` results that look like defects, and are not

Measured 20261024 on this tree (HEAD 879a45b plus the working tree). Every
number below carries the command that produced it.

**(a) A file that is NOT in HEAD returns `RESULT=INCONCLUSIVE`,
`TRUST=MEDIUM`, `EXIT: 2`, and for a NEW file that is EXPECTED that is not a
defect.** The comparison it could not decide is the HEAD-blob one; its
ABSOLUTE counts are still the answer.

```
powershell -File tools/verify/eol-check.ps1 -RepoRoot . -Path tools/verify/probe-instance-census.py
SUMMARY=probe-instance-census.py: crlf=0 bare_lf=1176 head=UNAVAILABLE
RESULT=INCONCLUSIVE
TRUST=MEDIUM
probe-instance-census.py: bytes=51451 crlf=0 bare_lf=1176 bare_cr=0 lines=1176 bom=none mixed=False utf16_suspect=False
  HEAD blob unavailable (new or untracked file): probe-instance-census.py
RESULT: INCONCLUSIVE
TRUST: MEDIUM
EXIT: 2
```

**(b) The repository has NO trailing-newline convention, and nobody should
"fix" one.** Of the 26 `tools/verify/suites/*.py` files, 5 end with a newline
and 21 do not.

```
python -B -c "import glob; fs=sorted(glob.glob('tools/verify/suites/*.py')); n=sum(1 for f in fs if open(f,'rb').read().endswith(b'\n')); print('suites_py_files=%d ends_with_newline=%d ends_without=%d'%(len(fs),n,len(fs)-n))"
suites_py_files=26 ends_with_newline=5 ends_without=21
```

`tools/verify/probe-instance-census.py` ends with `)` and no newline;
`tools/verify/crashshape-select.mjs` ends with `;` and no newline.

```
python -B -c "import os; [print('ENDS_NL=%s tail=%r'%(os.path.basename(f), open(f,'rb').read()[-3:])) for f in ['tools/verify/probe-instance-census.py','tools/verify/crashshape-select.mjs']]"
ENDS_NL=probe-instance-census.py tail=b'n()'
ENDS_NL=crashshape-select.mjs tail=b'0);'
```

**(c) `lines=` counts NEWLINE CHARACTERS, not lines.** For the census file
`eol-check.ps1` prints `lines=1176` while `Get-Content .Count` and python
`splitlines()` both say 1177 LINES. Those are different quantities, not a
discrepancy: the file's last line has no newline, so it is a line but not a
newline character. The repo's own tool uses the newline-character
convention, so a number quoted from it must be labelled as such.

```
python -B -c "p=open(r'tools/verify/probe-instance-census.py','rb').read(); print('bytes=%d lf_chars=%d splitlines=%d endswith_nl=%s'%(len(p), p.count(b'\n'), len(p.splitlines()), p.endswith(b'\n')))"
bytes=51451 lf_chars=1176 splitlines=1177 endswith_nl=False
```

```
(Get-Content tools/verify/probe-instance-census.py -Encoding UTF8).Count
1177
```
