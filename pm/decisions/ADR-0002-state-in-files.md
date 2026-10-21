---
id: ADR-0002
title: Project state lives in files, written by one tool
state: accepted
date: 2026-10-16
supersedes:
---

# ADR-0002 — Project state lives in files, written by one tool

## Context

At the time of writing this project carried:

- **80** files in `JOURNAL/`, plus 10 `PLAN-*` in the same directory
- **5** root-level plans, including `任務計畫.md` at 24 KB and `TODO.md` at 18 KB
- **4** handoff documents that contradict each other

Roughly 350 KB of prose. A session starting cold cannot tell which is
authoritative; a session that gets compacted cannot tell where it was; and both
failure modes look like the same thing — a confident agent proceeding on a
stale premise.

The compounding factor is that a *plan document* goes stale the moment code
changes and nothing marks it stale. Four of the five root plans were already
wrong in that quiet way.

## Decision

**State is a data structure in files, and `tools/pm.py` is its only writer.**

- One work item per file, `pm/work/W-###.md`, with state in frontmatter.
- `pm/STATE.md` is **generated** and never hand-edited, so it cannot drift from
  the items it summarises.
- `pm/RULES.md`, `GATE.md`, `TRAPS.md`, `PARITY.md` are hand-written, because
  they are judgement, not state, and they change rarely.
- `JOURNAL/` becomes history. It is kept — the measurements are expensive and
  some are irreplaceable — and `pm/archive-index.md` says so explicitly.

## Consequences

- **An item cannot close without evidence.** `pm.py set … state=done` refuses
  without `evidence=`, and `check` refuses while the acceptance block still
  says `TODO`. This is the mechanical form of the lesson this project paid for
  repeatedly: a green gate that proves nothing is worse than a red one,
  because it stops the search.
- **A blocked item must name its condition.** `state=blocked` refuses without
  `blocked_on=`. "Blocked" with no condition is a stalled item wearing a label.
- **Cold start is three commands.** `status`, `next`, `show <id>`. If a future
  agent cannot get oriented in five minutes from the filesystem alone, this
  structure has failed and needs to be made smaller, not bigger.
- **An item is capped at 200 lines.** Past that it is a journal entry wearing a
  work item's filename, and it will not be read.

## Alternatives rejected

- **One big `PLAN.md`.** It is the thing that already failed, five times over.
- **Git branches per task.** Useful for review, useless for a cold agent: the
  branch name carries no acceptance criteria and no evidence.
- **A wiki or issue tracker.** This project has no network and no user account
  in its loop. State has to be readable from a filesystem with `python`.
- **Let agents hand-edit the frontmatter.** It works until the first time two
  agents disagree about what `state: gating` means. `check` exists to catch it;
  a tool that refuses is cheaper than an audit that catches it.