---
id: ADR-0006
title: The simulator deals a fixed 100 and every mob spawns at 1000 HP
status: DECIDED
date: 2026-10-17
supersedes: []
superseded_by: []
relates_to: [ADR-0005]
---

# ADR-0006 — the simulator deals a fixed 100 and every mob spawns at 1000 HP

## Status

DECIDED, 2026-10-17, by the user, in the simulator section.

## Context

Damage went through `DamageRoll.Roll` — the level-difference scaling, the mob's
`info/PDDamage` defence term and two floors — and every mob read its own
`info/maxHP` out of Mob.nx. Both are the reference's behaviour and both are
wrong for a test rig:

- the number on screen depended on the character's level, the mob's level and a
  defence term that measured median 170 / p90 950 across 409 mobs, so no test
  could predict it without re-running the formula;
- `maxHP` in the open archives ranged from 8 (a slime) to tens of thousands, so
  the same scenario showed a mob dying in one swing next to one that survived
  twenty.

Before this the skill path also disagreed with itself: a swing used the sheet's
weapon range and a skill used `DamageRoll` with the skill's own node, so the
same mob took wildly different numbers from a punch and from a spell.

## Decision

`MapleBench.Services.Simulate.SimTuning` owns two constants:

| constant | value | used by |
|---|---|---|
| `PlayerDamage` | 100 | `MapSimSessionStore.ApplySkillHits` and the swing |
| `MobHp` | 1000 | `MapSimSessionStore` mob spawn and summon, `MobPatrol.DefaultMaxHp`, `CombatService.MobMaxHp`'s fallback |

A hit removes 100. Ten hits kill anything. One hit is always visible on the
health bar.

What still decides **whether** a hit lands is untouched and is the part that is
actually about the skill: the collision box test in `ApplySkillHits`, the
`MultiStrike` and `HasCollisionBox` guards, and the "no damage node, no damage"
rule (W-061). Only the number is fixed.

`DamageRoll.Roll` is not deleted. It is the reference's formula and it stays
implemented and reachable; the simulator simply does not report it. The archive
`info/maxHP` is likewise still read by `CombatService.MobMaxHp` for the editor
and the bestiary — the simulator overrides it at spawn.

## Why

ADR-0005 says the simulator's job is four things: position accurate, mechanism
accurate, immediate effect, and normally visible. A damage number that depends on
three hidden inputs serves none of them: the tester cannot check it, and the
harness has to re-implement the formula to assert it.

Flat numbers make the one assertion that matters writable in a line — the mob
went from 1000 to 900 — which is what `MapleBench/tools/browser-verify.mjs`
asserts today.

## Consequences

- A mob's HP bar now means what it says. Ten hits, same as every other mob.
- A fight takes ten swings instead of one or twenty, so a session can be read in
  seconds.
- The simulator's damage no longer tracks the reference's. Where a reviewer wants
  the reference's number, `DamageRoll.Roll` is where it is, and the difference
  is now a stated decision rather than an accident.

---

## 2026-10-17 補：角色自己的 HP（`SimTuning.PlayerHp = 1000`）

同一個問題的另一邊。當時只固定了怪物那側，結果量到：

```
opened   x=1710  gap=15  tookHit=150  playerHp=0  dead=true
```

地圖上的怪是 20 級、碰到傷害 **150**；角色 50 HP，**一下就死**，
而 `CastSkill` 第一行就是 `if (session.Status.Dead) return;`。
所以「地圖上有怪 → 放不出技能」不是技能的問題，是角色活不到施放。

`SimTuning.PlayerHp = 1000` 取代 `PlayerStatus.BaseMaxHp = 50`
（`Character.cs` 的參考值）。理由與 `MobHp` 完全相同：
兩邊都是 1000，一擊 100，戰鬥讀得懂；150 的傷害現在是血條上一格，不是一具屍體。

這取代了參考專案的數字。`revive-verify` 之類的 suite 讀的是伺服器回來的
`maxHp`，不是寫死的 50，所以不需要跟著改。

## 2026-10-17 補：朝向跟按鍵走（W-082b）

對標：`PlayerStates.cpp:52-63` 在按鍵處理裡呼叫 `player.set_direction(...)`，
`Player.cpp:1214-1218` 的 `set_direction` 在 `attacking` 時拒絕。

原本的實作是從**身體實際位移**推朝向，而且在 `Describe()` 裡事後才更新 ——
判定框在 `Advance()` 裡用的是上一幀的朝向。所以角色站著不動（被擋住、或施放中
`moveLeft` 被歸零）時永遠不會轉身，技能就朝著上一次走動的方向飛出去。

改成：在 `Advance` 開頭、戰鬥結算之前，由輸入決定朝向。
施放／揮擊中不改方向（對標的規則）。

## 2026-10-17 補：屍體不會走路（W-082c）

`Swing` 和 `CastSkill` 都有 `Status.Dead` 守衛，**走路沒有**。
量到 `playerDead: true, hp: 0` 的角色仍然走了 40 幀（1710 → 1614）。