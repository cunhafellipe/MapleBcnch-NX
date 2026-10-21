---
id: ADR-0003
title: The gate reads explicit markers only
state: accepted
date: 2026-10-16
supersedes:
---

# ADR-0003 — The gate reads explicit markers only

## Context

The project's gate decides red-vs-green from suite output. Three designs were
tried in one session, and **all three produced false results**:

| Attempt | What went wrong |
|---|---|
| `$LASTEXITCODE != 0` | **Not set by a PowerShell script.** Six green suites flagged red, and the gate was reported as failing while everything passed. |
| `(?im)\d+ failed` | Matches the `0 failed` inside `passed=5 failed=0`. Two more green suites flagged red. |
| `(?im)^\s*FAIL` | Matched nothing useful; suites print `PASS:` and `VERDICT:`, not `FAIL`. |

None of these were wrong by accident. Each was a reasonable inference that
happened not to match how these particular scripts print.

## Decision

**Red is a string a suite prints. Nothing else is red.**

Suites that can judge themselves print one of:

- `>>> RED`
- `NO damage number`
- `BAD=n` with n ≥ 1

The gate searches for exactly those and **shows everything else without
interpreting it**. A suite is added only with its own verdict line.

## Consequences

- A new suite must print a verdict. Adding a pattern to `gate.ps1` is
  forbidden by this decision.
- "No verdict line" is reported as *unknown*, not as green. The gate shows the
  tail so a human reads it.
- The gate cannot catch a **lying suite** — only an explicit marker. That is
  accepted and compensated separately: every harness prints a known-answer
  control first (R-9), and a suite that lies is a bug in the suite, fixed in
  the same change as the code it was hiding.

## The incident that made this a decision

`attack-test.ps1` read `@($r.damage)`. A frame carries no `damage` key at all
until a swing connects, and `@($null).Count == 1` in PowerShell — so the suite
reported

```
damage entries over 8 swings   : 8
VERDICT: attackStance IS on the wire and always inside the declared group.
```

for **eight swings that hit nothing**, on a product whose attack box was the
37×15 bare-hands one lying behind the character. The gate had been green on a
build that could not deal damage, for an unknown number of runs.

The suite was fixed in the same change (`@($r.damage | Where-Object { $_ })`),
which is required by R-8. But the deeper fix is here: a gate that cannot
distinguish "the suite passed" from "the suite printed a number that looks like
a pass" is not a gate.

## Alternatives rejected

- **Keep the regexes and tune them.** Each tuning pass fixed one false red and
  left the next latent. The third attempt was still wrong.
- **Count failures in the output.** Same problem: a suite may not print
  failures at all, or may print them as part of a longer sentence.
- **Require every suite to exit non-zero on failure.** Better, but PowerShell
  scripts need an explicit `exit 1` that none of the existing ones have, and it
  makes the suites harder to read. An explicit marker in the text is cheaper
  and survives `| Select-Object -Skip` in a console.