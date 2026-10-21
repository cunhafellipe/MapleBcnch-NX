---
id: W-093
title: Three gate suites take their answer from a machine-local cache file, so a different machine gives a different answer
state: todo
priority: 90
owner:
opened: 2026-10-21
closed:
depends: []
refs: projectile-verify.py FIGHTER_CACHE; mobfly-verify.py CACHE; ballart-verify.py CACHE; gate.ps1 Run
blocked_on:
evidence: |
  measured 2026-10-21 by reading the three suites where they live
  (%LOCALAPPDATA%\hermes\cache\scratch\), and by listing that directory:
    projectile-verify.py:120  FIGHTER_CACHE = r"...\scratch\fighter.json"
      read  :153-155  guarded by os.path.exists
      write :180      NOT guarded
    mobfly-verify.py:54      CACHE = r"...\scratch\mobfly.json"
      read  :61-64    guarded by os.path.exists
      write :93       NOT guarded
    ballart-verify.py:74     CACHE = r"...\scratch\ballfighter.json"
      read  :80-82    guarded by os.path.exists
      write :85       NOT guarded
  the three cache files are not in the repository:
    fighter.json 109 B, mobfly.json 1086 B, ballfighter.json 20 B
  and no work item in pm/work/ mentions any of them
  (grep for fighter.json|mobfly.json|ballfighter.json over pm/ -> 0 hits),
  so nothing currently tracks this.
  The consequence is not a crash. It is a different answer:
    ballart-verify.py:80-84 reads the cache and fights the mob it names;
    when the cache is absent it falls back to candidates[0].
    Same program, different machine, different mob, different verdict.
workstream: simulate
kind: defect
risk: high
---

## Intent

Three of the gate's suites do not measure the product. They measure **whatever the
last run left in a cache file in the agent's own cache directory**. The cache
makes the suite cheaper, and the cache makes the suite's answer a property of the
machine rather than of the code.

This is the class of defect reviewer-nx called out in task-22: *a judge that can
report the wrong thing is worse than no judge, because it puts the fix queue in
the wrong order.* A broken path crashes and says so. This one passes.

## What was measured

The three suites and the file each one reads and writes. All three paths are
absolute and hard-coded into the source of the suite, so they do not follow the
suite if the suite moves:

| suite | line | cache file | read | write |
| --- | --- | --- | --- | --- |
| `projectile-verify.py` | 120 | `fighter.json` (109 B) | :153-155, guarded | **:180, unguarded** |
| `mobfly-verify.py` | 54 | `mobfly.json` (1086 B) | :61-64, guarded | **:93, unguarded** |
| `ballart-verify.py` | 74 | `ballfighter.json` (20 B) | :80-82, guarded | **:85, unguarded** |

`mobfly-verify.py:66-67` states the principle in its own comment -- *"a cache
that is never checked is a claim, not a measurement"* -- and then re-validates
the cached census against the live server. That is the right idea. The problem
is not the read; it is that the **choice of what to measure** depends on the
cache in the first place.

## The mechanism

`ballart-verify.py:78-84` is the clearest case:

```python
multi = [m for m, _, ok, f in have if ok and f >= 2]
candidates = multi or [m for m, _, ok, _ in have]
if os.path.exists(CACHE):
    with open(CACHE, encoding="utf-8") as fh:
        FIGHTER = json.load(fh).get("mobId")
if FIGHTER not in candidates:
    FIGHTER = candidates[0]
```

The cache names the mob to fight. On a machine where the cache exists the suite
fights that mob. On a machine where it does not, the suite fights
`candidates[0]`. Those are different measurements of different things, and the
gate's red-or-green for this suite is therefore a fact about the machine.

`ballfighter.json` is **20 bytes**. It is one JSON object with one key. Whether
the gate's verdict for this suite is reproducible depends on the presence of a
20-byte file.

## Why this is worse than the hard-coded path

A hard-coded path that does not exist raises `FileNotFoundError` at the
`open(CACHE, "w")` at :85 (and :93, :180). The suite dies, the gate prints a
traceback, and the gate's own rule (`gate.ps1:36`) turns that into RED. The
failure is loud.

This failure is quiet. The suite runs, measures a different mob, and reports a
clean result.

## The other half: the cache is also state that outlives the machine

`scratch` self-prunes (`.last_prune` marker in that directory). A cache file
that prunes away changes what the next run measures. A cache file that survives
keeps pointing at a mob that may no longer be the interesting one.

## What is not done

1. **The cache path is still hard-coded.** Moving these three suites into the
   repository (task-26) without changing these three lines turns a machine-local
   answer into a machine-local *crash*.
2. **The choice of subject is still cache-driven.** The fix is to make the
   subject a deterministic function of the data -- e.g. always the lowest mob id
   among the candidates, or a mob named in the suite's own source -- so the
   suite measures the same thing everywhere. A cache may then record what it
   measured, but must not decide what gets measured.
3. **No work item tracked this before this one.** The grep over `pm/` for the
   three file names returns 0 hits, so the 68 work items did not cover it.

## How to check a fix

- Delete the cache file, run the suite, and confirm it still measures the same
  subject (the log line naming the mob must be identical with and without the
  cache).
- Run the suite with the cache directory read-only, or absent, and confirm it
  does not raise.
- Run the suite twice on a machine with no cache at all and confirm the two
  outputs are identical.
