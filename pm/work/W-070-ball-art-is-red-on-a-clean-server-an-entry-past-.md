---
id: W-070
title: ball-art is red on a clean server: an entry past its delay still does not publish the ball art
state: done
priority: 82
owner: 
opened: 2026-10-16
closed: 2026-10-17
depends: []
refs: MobPatrol.cs EveryEntry; MapSimSessionStore.cs MapSimAttackEntry; walkview.js drawMobAttackBall; ballart-verify.py; pm/work/W-054
blocked_on: 
evidence: |
  the frame reported _entries[0] only. 4220000 has TWO attacks -- attack1 type 2
  (flying, delay 1320 ms, ballAction "attack1/ball", 2 frames) and attack2 type 0
  (static, delay 1200 ms, no ball) -- so while the mob used attack2 the frame
  reported a ball-less entry for the whole fight while the art sat in the archive.
  after: every entry is published and every one is drawn
    python .../ballart-verify.py
      frame 136: the ball art is named (attack1/ball), frame 0/2,
        elapsed=1333ms of delay=1320ms
      ball frame indices seen: [0, 1]
      BAD=0
workstream: simulate
kind: defect
risk: med
---
## Intent

One paragraph. What changes for the user when this is done.

## Reference

`path:line` — the exact construct being ported. If there is none, say so
explicitly and mark the item own-design.

## Approach

Ordered steps. Each names a file and a symbol.

## Acceptance

Commands, and the output each must produce. An item with no acceptance
cannot be closed.

```
python %LOCALAPPDATA%\hermes\cache\scratch\ballart-verify.py
# BAD=0, and it prints:
#   frame 136: the ball art is named (attack1/ball), frame 0/2, elapsed=1333ms of delay=1320ms
#   ball frame indices seen: [0, 1]
```

## Result

Filled in when closed: 2026-10-17 what actually landed, and the evidence.

---

## W-070 已修（2026-10-17）

**症狀**：`ballart-verify.py` 報「球的圖從來沒出現在 frame payload」。

**封存那側一直是好的**：60 隻抽樣的怪裡 5 隻有 type-2 攻擊，5 隻都正確發布了
球的圖（`4220000 attack1` 2 幀、`8150000` 3 幀、`8220004` 4 幀、`8610012 attack2`
5 幀、`9619005` 3 幀）。問題從來不在封存。

**真正的原因**：frame 只回報 `_entries[0]`。`4220000` 有兩個攻擊 ——
`attack1` 是 type 2（飛行、delay 1320ms、`ballAction "attack1/ball"`），
`attack2` 是 type 0（靜態、delay 1200ms、沒有球）。怪用 attack2 時，條目 0 就是
attack2，於是 frame 在整場戰鬥裡都回報一個沒有球的靜態條目，**而那張圖一直躺在封存裡**。

**修法**（`MobPatrol.EveryEntry` / `MapSimMobFrame.attackEntries` / `MapSimAttackEntry`）：
逐條發布所有在飛的條目，客戶端每一條都畫。參標 `Mob.cpp:1104-1125` 本來就是
「每個條目過了延遲就畫它的球」。純量欄位保留給單條目的舊路徑。

**測量**：
```
frame 136: the ball art is named (attack1/ball), frame 0/2, elapsed=1333ms of delay=1320ms
ball frame indices seen: [0, 1]      <- 影格會前進
BAD=0
```

suite 也改了：它原本只讀純量 `attackEntryBallAction`，而且觀察窗 300 幀，而飛行條目
可能在喚醒後第 136~198 幀才出現 —— 窗太窄，兩個問題疊在一起看起來像「從來沒有」。