# Archive index — what `JOURNAL/` is, and what it is not

**`JOURNAL/` is history. It is not read to answer "what is the state".**

The state is `pm/STATE.md`, generated from `pm/work/*.md`. If you are a new
agent and you are reading the journal to find out where the project is, you
have already taken a wrong turn: you are reading 80 files that were each
correct when written and that contradict each other now.

## Why it is still here

The measurements in it are real, taken against archives that are 678 MB and
13.5 GB of RAM, and are expensive to reproduce. Several of them took hours and
established facts that no code comment carries. Deleting them would mean
re-deriving them, and the most likely outcome of re-deriving is a *different*
answer that nobody notices.

`pm/TRAPS.md` and `pm/PARITY.md` are the distilled, current form of that
knowledge. They supersede the journal wherever they speak.

## What is in it

| Range | What |
|---|---|
| `2026-10-10` … `2026-10-12-*` | The first two weeks: physics, foothold trees, the simulator build, and an audit of the audit |
| `2026-10-12-1025-audit-itself-is-wrong.md` | An audit that audited the wrong thing. Read it before trusting any audit-shaped document here. |
| `2026-10-13-*` | Worklog commander, the command-surface work |
| `2026-10-14-*` | Simulator parity: ladder, skill cast, swing, summon, NPC labels. **The most useful run.** |
| `2026-10-15-*` | Mob touch attack, chase, the reference gap survey |
| `2026-10-16-*` | Gap analysis, then segments 3 and 5. `STATE.md` supersedes these. |

`PLAN-*.md` files in the same directory are superseded by
`pm/work/W-041` and the backlog implied by `pm/STATE.md`.

## Superseded root-level documents

| File | Superseded by |
|---|---|
| `HANDOFF-2026-10-14-simulator-parity.md` | `pm/work/W-001` … `W-017` |
| `HANDOVER.md` | `pm/STATE.md` |
| `任務計畫.md` (24 KB) | `pm/work/` + `pm/PARITY.md` |
| `JOURNAL/TODO.md` (18 KB) | `pm/work/` |
| `REVIEW-TASK.md` | `pm/GATE.md` |
| `工作室守則.md` | **not superseded** — the studio rules are the authority |

`README.md` at the repo root now points here.

None of these are deleted. They are history, and deleting them would destroy
measurements that cannot be cheaply recovered. They are simply no longer
authoritative, and this file is the statement of that.

## The rule, stated once

> A new fact goes in the **work item**. A measurement that only matters to the
> conversation goes in the **journal**. State goes nowhere but `pm/work/`.