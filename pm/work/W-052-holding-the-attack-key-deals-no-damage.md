---
id: W-052
title: Holding the attack key deals no damage
state: done
priority: 1
owner: 
opened: 2026-10-16
closed: 2026-10-16
depends: []
refs: [Player.cpp:1250-1253, Combat.cpp:1794]
blocked_on: 
evidence: holdkey-verify.py BAD=0: 600 held frames, mob 9500302 50->4 HP, 16 damage numbers
---
## Intent

Holding the attack button fights. Today it does nothing: the character goes
through the swing animation and never deals a single point of damage, for as long
as the key is down. Tapping works, which is why this went unnoticed — every
automated probe taps — but a player holds the button.

## Reference

`Player.cpp:1250-1253` —

```cpp
bool Player::can_attack() const
{
    return (...) && !attacking && !held_skill && !is_climbing() && !is_sitting()
        && look.get_equips().has_weapon();
}
```

The reference cannot BEGIN a second swing while one is playing, so the hit of the
first is never re-armed by the key staying down. `Combat.cpp:1794` asks for
`get_attackdelay(0)` and searches the map on a LATER tick, not on the keypress.

## Approach

`Services/Simulate/MapSimSessionStore.cs`, `Swing`:

1. Call `ResolveSwingHit(session, body)` FIRST, every frame, before any guard —
   the pending hit resolves on its own schedule whether or not the key is down.
2. Then refuse a new swing while one is in flight:
   `if (body.AttackPoseTicks > 0) return;` — the port's `attacking` flag.
3. Delete the trailing `ResolveSwingHit(session, body)` at the end of `Swing`,
   which was the re-arm: it decremented the countdown that the top of the same
   call had just written.

No brace surgery in `Advance`; the `else { ResolveSwingHit(...) }` at the call
site is already the key-up path and stays.

## Acceptance

```
python %SCRATCH%\holdkey-verify.py
# in reach: player x=45, mob x=71, 26px apart, mob HP 50/50
#   mob HP 50 -> 4
#   damage numbers produced: 16
#     first: frame 17, value 1, mob 9500302
#     last : frame 577, value 5, mob 9500302
#   the mob lost 46 HP to a key that was never released
#   swings that landed: 16 over 600 frames of a held key
# BAD=0

powershell -File %SCRATCH%\gate.ps1
# =========== RED SUITES: 0 ===========
```

## Result

Fixed and gated. Before the change the same 600-frame held-key run produced
`damage numbers produced: 0`; the diagnostic `swingDelayTicks` sat at 21 for the
whole run, because every frame wrote 21 back and then decremented it to 20.

The suite also carries the cadence check that falls out of the same rule: 16
swings landed over 600 frames, not 600, so the pose and the hit delay still space
them out. A "one hit per frame" reading would mean the delay is being ignored
rather than merely re-armed, and the suite fails on it.

`attack-test.ps1` went red on the day of the change and the reason was its own
setup, not this fix: it walks the character 158 frames along a ledge and into
mob `2220100` at x=2091, and a 50 HP character is dead by x=2097 — at which point
`Swing` correctly refuses and the suite printed "attackStance was EMPTY on every
swing -- the link is broken", a false accusation. The suite now runs
invulnerable, which does not affect what it measures: the player's damage number
comes from `mobPatrol.TakeHit`, never from `PlayerStatus.Hurt`.