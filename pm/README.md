# MapleBench — project control

The state of this project lives **here**, not in a conversation and not in
`JOURNAL/`. That is deliberate: a session can be compacted, lost, or handed to
another agent, and none of those may change what "done" means.

## Cold start — 5 minutes, in this order

You are an agent with no memory of this project. Do exactly this.

```powershell
python tools/pm.py status          # the whole project, one screenful
python tools/pm.py next            # the next 3 actionable items
python tools/pm.py show W-030      # one item in full
```

Then, before writing any code:

| Read | Why |
|---|---|
| `pm/RULES.md` | 20 numbered rules, each with the incident that made it one |
| `pm/GATE.md` | what "green" means here, and the exact commands |
| `pm/TRAPS.md` | every way this project has lied to itself, with a control for each |
| `pm/PARITY.md` | which reference wins, and the verified table |

Do **not** read `JOURNAL/` to answer "what is the state". It is history.
`pm/archive-index.md` says what is in it and why it is still there.

## The shape

```
pm/
  PROJECT.yaml     config: paths, refs, ports, SDK, measured archive facts
  RULES.md         numbered hard rules, each cited to the incident behind it
  WORKFLOW.md      the loop, step by step, with what each step must produce
  GATE.md          the gate: what it runs, what green means, how it decides
  TRAPS.md         measurement traps — the single highest-value file here
  PARITY.md        the two references, the conflict rule, the verified table
  STATE.md         GENERATED. Never hand-edited.
  work/W-###.md    one work item per file; state lives in its frontmatter
  decisions/       ADRs for choices that outlive the item that made them
tools/pm.py        the only writer of state
```

## The five commands

| Command | Does |
|---|---|
| `python tools/pm.py status` | whole project in a screenful; **regenerates** `STATE.md` |
| `python tools/pm.py next` | the next actionable items, dependencies satisfied |
| `python tools/pm.py show <id>` | one item in full |
| `python tools/pm.py new --title "..."` | create an item from the template |
| `python tools/pm.py set <id> state=... evidence="..."` | change state |
| `python tools/pm.py check` | structural audit — run before you believe the state |
| `python tools/pm.py gate` | build, deploy, run every gate suite |

## The three invariants

**1. State is written in exactly one place.** `tools/pm.py` is the only writer.
Editing a `state:` line by hand is a drift bug, not a shortcut. `check` exists
to catch the ones that slip through.

**2. Nothing closes without evidence.** `set <id> state=done` refuses without
`evidence=`, and `check` refuses an item whose acceptance still contains `TODO`.
This is the mechanical form of the lesson this project paid for repeatedly: a
green gate that proves nothing is worse than a red one, because it stops the
search.

**3. A blocked item names its condition.** `state=blocked` refuses without
`blocked_on=`. "Blocked" with no condition is just a stalled item wearing a
label.

## Before compaction

Nothing special is required — that is the point of this layout. If you want to
be kind to the next session anyway, run:

```powershell
python tools/pm.py status
```

and leave `pm/STATE.md` fresh. It is regenerated, so it cannot go stale relative
to the work items, and it cannot be edited into a lie.

## What is deliberately NOT here

- **A single big design document.** It would be 30 KB of prose that goes stale
  on the first change and that nobody reads twice. The work items are the
  design; each one is scoped to be read in under two minutes.
- **Velocity or burn-down metrics.** They invite optimising the number rather
  than the product. What matters is that an item's acceptance is real.
- **Automatic closing from the gate.** The gate proves the acceptance commands
  pass. Whether the *observable behaviour* is right is a separate judgement,
  and for six items right now it is a judgement only the user can make
  (`W-037`).