---
id: ADR-0001
title: The C++ reference wins on conflict
state: accepted
date: 2026-10-16
supersedes:
---

# ADR-0001 — The C++ reference wins on conflict

## Context

Two references describe the same game from different standpoints:

- `clean-client` — an open-source reimplementation that runs the game.
- `MapleNecrocer` — a stripped fork of WzComparerR2, a desktop archive browser.

They disagree. The sharpest case is accuracy:

| | Formula |
|---|---|
| C++ `CharStats.cpp:186-192` | `trunc(DEX * 0.8 + LUK * 0.5)` |
| C# `Character.cs:174-195` | `floor(DEX * 1.2 + LUK * 1.0)` (Bigbang row) |

Same stat, different number, both stated with confidence.

## Decision

**The C++ wins.** Studio rule 7.6.

Not seniority — function. `clean-client` is the client; `MapleNecrocer` is a
viewer of the archives. When they disagree about what the game does, the
viewer is showing something convenient and the client is showing the thing.

## Consequences

- Every work item's `refs` cites the file that won, and the **disagreement is
  recorded** next to it. A reader who finds the C# formula must be able to see
  that it was overruled and why.
- A C# construct with **no** C++ counterpart is not thereby wrong — port it and
  cite it. Absence of a counterpart is not refutation. `MobTooltipRenderer` is
  one: there is no C++ equivalent of a bestiary panel, and it is implemented.
- Where the C++ is *unfinished* rather than absent, that is a fact to record,
  not a licence to substitute. `CharStats.cpp:433-444` carries an in-source
  `// TODO: Random stuff, need to find the actual formula somewhere.` — that is
  the mob→player damage path, and the honest thing is to port the TODO and say
  so.

## Alternatives rejected

- **Average them.** Two references disagreeing is evidence about the
  references, not about the answer. Averaging manufactures a number no player
  ever saw.
- **Follow the C# because it is the tool this project mirrors.** That makes the
  project a browser with a simulator bolted on, rather than a reproduction.
- **Defer every conflict to the user.** They are not the arbiter of v95's
  formulas; the client is.