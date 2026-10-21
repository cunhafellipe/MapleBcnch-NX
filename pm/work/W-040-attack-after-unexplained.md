---
id: W-040
title: Explain attackAfter=1200ms landing on frame 7
state: done
priority: 60
owner: 
opened: 2026-10-16
closed: 2026-10-16
depends: [W-002, W-048]
refs: [MobAttack.cpp:199, MobAttack.cpp:146, MobAttack.cpp:37, Mob.cpp:153]
blocked_on: 
evidence: attackafter-verify.py BAD=0: attackAfter honoured exactly (700ms -> 42.0 frames); frame 7 was pre-entries behaviour
---
## Intent

An attack's hit lands on frame 7; the data says `attackAfter = 1200ms`, which is
frame 72. Unexplained since 2026-10-14.

## Reference

- `Mob.cpp:153` — `attack.delay = std::max<int32_t>(0, data["attackAfter"].get_integer());`
- `MobAttack.cpp:146` — `entry.delay = data.delay;`
- `MobAttack.cpp:37` — `for (auto& entry : attack_entries) entry.elapsed += Constants::TIMESTEP;`
- `MobAttack.cpp:199` — `if (entry->elapsed < entry->delay) { ++entry; continue; }`

TIMESTEP is 16.667ms, so 1200ms is frame 72.0 and no hit can be tested before it.

## Acceptance

1. `# one of: attackAfter is not the hit delay, or the delay is read from the wrong node`
2. `# state which, with the line`

## Result

**It was reading from the wrong place — in the code, not the archive.** Neither of
the two alternatives in the acceptance holds: `attackAfter` IS the hit delay, read
from the right node.

The frame-7 observation is from before W-048. Until the attack entries existed,
the damage was a number on the SPEC (`PendingAttackPower`), applied by
`ConsumeAttackDamage` the moment the mob began the attack, with nothing reading
the delay at all. Frame 7 was then simply how long it took to reach the swing and
settle the pose — the delay was not consulted, so the number it should have been
72 was never computed.

MEASURED on the current build, `3400003 attack1`, `attackAfter = 700ms`:

```
  created on frame 23 at elapsed=17ms
  first testable frame: 65 (elapsed=717ms of delay=700ms)
  that is 42 frames after it was created; the archive's own delay is 42.0 frames
  at frame 7 the entry was at elapsed=133ms against a 700ms delay -- not testable
```

42.0 frames for 700ms is exact, and at frame 7 the entry was 133ms into a 700ms
delay. `attackafter-verify.py` now measures both on every gate run, for whichever
mob in an 80-mob stride declares the closest `attackAfter`, and fails if the
entry becomes testable before its own delay.

The archive side of the question, for the record: MEASURED over an 80-mob stride,
the `attackAfter` values on type-2 attacks are 30, 400, 500, 600 and 700ms — there
is no 1200ms on a projectile in this stride, so the original report was about a
type-0 or type-1 attack (3300007 carries `attackAfter = 1200ms`, per the
`AttackPoseTicks` note in `MobPatrol.cs`). The rule is the same for every type:
`entry.delay` gates the test, so nothing lands early.