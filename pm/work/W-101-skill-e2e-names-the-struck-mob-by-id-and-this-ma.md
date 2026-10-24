---
id: W-101
title: skill-e2e names the struck mob by id, and this map carries ELEVEN mobs with the same id
state: done
priority: 76
owner: 
opened: 2026-10-24
closed: 2026-10-24
depends: []
refs: [tools/verify/suites/skill-e2e.py:136; tools/verify/suites/skill-e2e.py:193; tools/verify/suites/skill-test.ps1:122-124; MapleBench/Services/Simulate/MapSimSessionStore.cs:142-153]
blocked_on: 
evidence: the map carries 11 mobs with id 2220100 (measured in a live frame: x 2091/2221/2767/2553/2553/2681/2847/3043/2006/2210/2451/2435), so the id names an ORDINAL, not an animal. The victim is now the instance nearest the hit position (MapSimDamageNumber.X) in both frames: 3/3 standalone runs 1000 -> 800, exit 0; skill-e2e GREEN in the gate at 14:45 and 14:54.
---
## Intent

The gate said `skill-e2e` was red with "the struck mob's HP dropped
(2220100: 1000 -> 1000)" — a mob that took 100 damage twice and did not lose
a single point of HP. The mob was fine: the suite was comparing two different
animals, because it named the victim by an id that this map does not make
unique. A gate that reports a defect that is not there costs the same as a
gate that misses one that is: both make the verdict untrustworthy.

## Reference

- `tools/verify/suites/skill-e2e.py:136` (before) — `hp_before_by_id = {m["mobId"]: m["hp"] for m in last["mobs"]}`. A dict: eleven mobs with one id collapse into ONE entry, the last one's HP.
- `tools/verify/suites/skill-e2e.py:193` (before) — `victim = next((m for m in f["mobs"] if m.get("mobId") == victim_id), None)`. The FIRST mob with that id.
- `tools/verify/suites/skill-test.ps1:122-124` — the same map, already measured and already written down: "This map carries ELEVEN instances of 2220100 (measured from the live frame: x = 2091, 2221, 2767, 2553, 2553, 2681, 2847, 3043, 2006, 2210, 2451, 2435). A position-pinned lookup therefore matches an ORDINAL, not an animal."
- `MapleBench/Services/Simulate/MapSimSessionStore.cs:142-153` — `MapSimDamageNumber` carries `X` and `Y`: the position of the hit. The damage event names the animal; the id does not.

## Approach

1. Measure the claim instead of trusting it. Created a session on the same map
   (`f4/Map/Map1/100000004.img`) and counted the frame's mobs: **24 mobs, of
   which 11 carry the id 2220100** (x = 2091, 2221, 2767, 2553, 2553, 2681,
   2847, 3043, 2006, 2210, 2451, 2435) and 13 carry 2110200. All at hp 1000.
   So `hp_before_by_id` held the HP of the mob at x=2435 and the "after" lookup
   held the mob at x=2091 — and the one that was actually struck was whichever
   the skill's box covered.
2. Locate the victim by the HIT POSITION, in both frames: the instance of that
   id nearest `damage[0]["x"]`, in the frame before the cast and in the cast
   frame. The instances stand ~130px apart, so "nearest" names the same animal
   in both frames even though they move a little between them.
3. Keep the assertion exactly as strong. It still requires the HP to have
   DROPPED on that animal; it now requires it of the animal that was hit.

## Acceptance

```
# 1. the suite names the animal it struck
python tools\verify\suites\skill-e2e.py
# EXPECT: "the cast reported damage on mob 2220100 at x=2093"
#         "mob 2220100: 1000 -> 800"      <- the same animal, both frames
#         "VERDICT: skill-e2e 14 assertions, 0 failed", exit 0

# 2. the gate
powershell -NoProfile -ExecutionPolicy Bypass -File tools\verify\gate-loop.ps1
# EXPECT: no RED_SUITE=skill-e2e
```

## Result

**Fixed in the suite; the tree was never broken.**

The suite now finds the struck mob by the position of the hit
(`nearest_instance(...)` in `tools/verify/suites/skill-e2e.py`) instead of by
an id that this map does not make unique, and the comment that recorded the
wrong premise — "Every mob's HP before the cast, keyed by id -- so the victim
can be looked up afterwards instead of assumed to be the nearest one" — was
corrected, because it is what led the next reader to the same mistake.

Measured:

- `python tools/verify/suites/skill-e2e.py`, three consecutive runs: each
  printed `the cast reported damage on mob 2220100 at x=2093`,
  `mob 2220100: 1000 -> 800`, `VERDICT: skill-e2e 14 assertions, 0 failed`,
  exit 0. (Before the fix the same suite passed standalone too — by luck,
  because the first 2220100 in the list happened to be the one that was hit.
  It failed in the gate, where the frame's mob order differed. That is what
  "flaky" was: an assertion that depended on the ORDER of a list.)
- The gate, 2026-10-24: `=== skill-e2e ===  ok`.

The assertion was not weakened. It still fails if the struck animal does not
lose HP: with `hp_after == hp_before` the check is false, exactly as before.
What changed is WHICH animal it compares.
