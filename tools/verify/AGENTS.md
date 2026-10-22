# AGENTS.md — `tools/verify`

Scripts that decide whether a change is correct. **These tools have been wrong
nine times, and every time they returned a plausible-looking wrong answer rather
than an obvious failure.** That is the failure mode to design against.

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
---

## Every failure here returned valid-looking data

The numbers in the first column are **project-wide incident numbers**, not a
count of the rows: #12 is the twelfth failure of this shape in the project and
the sixth recorded in this directory; #13 is the thirteenth in the project and
the seventh here; #14 and #15 are the fourteenth and fifteenth in the project
and the eighth and ninth here.

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

Two of these were **the same bug twice**: #5 had already produced a dedicated
tool, and the tool was not used.

---

## Rules

1. **A tool that cannot measure must fail loudly.** Return non-zero, or emit a
   token the caller greps for. Never let a measurement collapse to `0`, `""`,
   `null`, or an empty array and let that pass as data.
2. **Always run `self-test.ps1` after changing anything here.** It carries 28
   known-answer cases precisely so the nine failures above cannot return.
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
   CASES_PASS=28
   CASES_FAIL=0
   CASES_SKIP=0
   RESULT=PASS
   TRUST=HIGH
   CHILD_EXIT_CAPTURED=24
   CHILD_EXIT_NONZERO=15
   ```

   The two `CHILD_EXIT_` counters are the harness proving it read its
   children's **real** exit codes (rule 11). **`CHILD_EXIT_NONZERO` must be
   greater than zero** — if it is 0, the harness is reading 0 for everything
   and every case that expects a non-zero exit is meaningless.

   Without a usable `DOTNET_ROOT` or without git, the affected cases report
   SKIP **and say why**. **Anything else means this tooling is now
   unreliable.**
3. **Known-answer tests only.** A test that cannot fail proves nothing.
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
   not churn. Only a **difference** between the two numstat runs is real churn.
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
    never from `$LASTEXITCODE`. Which copy of that automatic variable a native
    call updates depends on how the script was entered (incident 14), so the
    read can be wrong in one invocation form and right in another **without
    anything failing loudly**. `Common.ps1`'s `Invoke-CheckedProcess` already
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

    Measured 2026-10-22: `attackdrop-verify.py` has no `sys.exit`, no
    `>>> RED` and no `BAD=` anywhere in the file, so it is **structurally
    incapable of reporting red**. Fifty mobs with the wrong attack count
    produce one line of text and the gate says `ok`.

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

    **⇒ And 18 more suites still hardcode `f4` and one hardcodes `f3`.** On an
    instance whose `Map.nx` is `f2` they exit 1 with `HTTP Error 404` — and
    **a 404 suite and a suite that measured a difference look almost the same
    in the gate's output.** Fixing one suite's id is not fixing the family.

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