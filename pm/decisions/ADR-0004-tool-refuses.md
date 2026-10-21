# ADR-0004 — The pm tool refuses, rather than warning

---
id: ADR-0004
title: The pm tool refuses, rather than warning
state: accepted
date: 2026-10-16
supersedes:
---

# ADR-0004 — The pm tool refuses, rather than warning

## Context

The state file is the only thing standing between a compacted session and a
confident agent proceeding on a stale premise. Two failure modes matter:

1. An item closes with no proof it was ever built. That is a **false green**,
   and this project has shipped one: `attack-test.ps1` reported eight hits
   where there were none, for an unknown number of runs (ADR-0003).
2. An item sits in `blocked` with nothing saying what would unblock it. That is
   a stalled item wearing a label, and it is invisible in a list of 29.

Both are cheap to prevent mechanically and expensive to detect by reading.

## Decision

**`tools/pm.py` refuses the transition and explains why, rather than warning
and proceeding.**

Verified refusals:

| Attempt | Result |
|---|---|
| `set W-030 state=done` with no `evidence=` | refused, exit 1 |
| `set W-040 state=blocked` with no `blocked_on=` | refused, exit 1 |
| `set X depends=[]` | accepted; parses to an empty list |
| `check` on an item depending on unfinished work | reported, with both ids |

`check` is the backstop for state that was edited by hand or by an older
version of the tool. It is not the primary mechanism — the refusal is.

## Consequences

- **An agent cannot accidentally close an item it did not build.** The refusal
  names the reason, so the fix is to supply the evidence or to admit the item
  is not done.
- **"Blocked" always names a condition.** `check` also flags a blocked item
  whose blocker is itself blocked, which is usually the signal that two items
  are one item written twice.
- **A dependency on unfinished work is legal but noisy.** `set` prints a note
  and `check` reports it until the dependency closes. Backlogs need this; it
  just must not be silent.
- **The tool is the only writer.** Hand-editing frontmatter works until two
  agents disagree about what `state: gating` means. `check` catches that;
  refusing is cheaper.

## Alternatives rejected

- **Warn and proceed.** Every warning in a long-running loop becomes noise, and
  the moment it is ignored is the moment it would have saved a run.
- **Keep the warnings in the journal.** A warning in a journal is invisible at
  exactly the moment it matters: the next session's cold start.
- **Require a human to close items.** That makes the user's attention the
  bottleneck on 29 items and 16 of which are already done. The tool refuses
  *without evidence*; a human with evidence is still free.