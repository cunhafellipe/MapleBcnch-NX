# WORKFLOW — one item, start to close

Five steps. Each produces an artefact that a cold reader can check without
trusting the agent that wrote it.

```
   1 CLAIM ──▶ 2 MEASURE ──▶ 3 BUILD ──▶ 4 GATE ──▶ 5 CLOSE ──▶ open editor
                  │                                           │
                  └──────── evidence, or a blocked_on ────────┘
```

---

## 0. Claim

```powershell
python tools/pm.py next
python tools/pm.py set W-030 state=building
```

Take the highest-priority ready item. If you intend to take something else,
write down why in the item — an unexplained deviation is how a project ends up
with two agents building the same thing.

If the item is not the right unit of work, split it rather than widening it
(R-7). If it is blocked, stop; do not start adjacent work and call it progress.

---

## 1. Measure — before any code

Two measurements, and they are different:

**The reference.** Read the construct *and its caller*. Cite `file:line` for
both. A formula cited without its caller has been wrong in this project at
least once, with a citation on every line of the architecture (R-1).

**The archive.** Through `/api/inspect` only, never `/api/children` (no
`value`), never PowerShell (flattens). Write the numbers into the item's
**Result** section as they are measured — not as a conclusion.

Three questions, in this order, because each one has cost a wrong answer:

1. **Does the node exist under the name I expect?** `info/wdef` does not exist
   on this archive; `info/PDDamage` does, on 342 of 343 mobs (T-8).
2. **What TYPE is it?** `Long`, `String`, `Double` all occur under the same
   key in the same node — `maxHP` is a String on some mobs, `fs` is a Double on
   all of them.
3. **What is its exact spelling?** `incSpeed` here, `incSPEED` in the C++; and
   `incLUk` on one glove (T-10).

Write the measurement into the item **before** you change anything. If the
change turns out to be wrong, the measurement is still worth something; a
conclusion written after the fact is worth nothing.

---

## 2. Build

- Reuse what exists. Read the file before editing it.
- Own-design is declared **in the code, at the site**, with the reason (R-6).
  Not in a journal entry. A future agent reads the code, not the journal.
- Never `Set-Content` / `Get-Content` a source file (R-15). Use `edit`.
- Never type a CJK path into a script (R-16).
- Everything time-based is **frame-counted** (fixed step 1000/60 ms). A
  wall-clock duration makes a replay differ by client frame rate.
- When a change reveals a second defect: open a second item (R-7).

Mid-build you may `python tools/pm.py check` to see the item's structure is
still sound. You may **not** set `state=done`.

---

## 3. Gate

```powershell
python tools/pm.py gate
```

Green means: build succeeds with 0 errors and 0 warnings, src/bin/served md5
agree, and every suite prints its own explicit pass marker. See `GATE.md` for
what each suite covers and why the gate only reads explicit markers.

**A suite that cannot be trusted is worse than no suite.** If a gate script
lies — and one did, for an unknown number of runs, reporting eight hits where
there were none (T-1) — fix the script in the same change, and say so in the
item's Result.

---

## 4. Close

```powershell
python tools/pm.py set W-005 state=done evidence="<what was observed, with numbers>"
```

The tool refuses without `evidence`, without a `closed` date, and refuses while
the acceptance block still says `TODO`. Fill in **Result** first: what actually
landed, and what did not.

Then:

```powershell
python tools/pm.py status      # regenerates STATE.md
python tools/pm.py check       # structural audit of every item
```

Then **open the editor once**, to the test instance, so the user sees the change
(R-19). Three conditions: deployed, gate green, md5 identical. Not before, not
on partial work, not twice for one change.

---

## When you get blocked

```powershell
python tools/pm.py set W-007 state=blocked blocked_on="a map id that reproduces it"
```

Then open or point at the item that *would* unblock it (`W-038`). Two blocked
items that unblock each other are one item written twice; `check` flags a
blocked item whose blocker is also blocked, which is usually the signal to
merge them.

Never close an item as "won't fix" to make the list shorter. That is what
`state=dropped` is for, and it wants a reason in **Result**.

---

## The loop, restated

`measure → change → gate → record → open the editor`

The order matters. Recording before gating records a hope. Opening the editor
before gating shows a user a build that may not work. Measuring after changing
records the change's own output and calls it evidence — which is how six
self-deceptions in one session happened.