# 2026-10-14 — 第 7、8 項的資料前提查核：兩件事與回報的描述不符

回報 7「怪物不會攻擊」與 8「召喚怪物應當有圖跟裝備一樣的選單」。
動工前查了存檔，兩件事的前提都不成立，所以先寫在這裡 —— 這兩條會改變「做完」是什麼意思。

---

## 8. 怪沒有 `icon` 節點 —— 「跟裝備一樣的圖示」在資料上不存在

```
f5/2220100.img        -> die1, hit1, info, jump, move, stand
f5/2220100.img/info   -> MADamage, MDDamage, PADamage, PDDamage, acc, bodyAttack,
                         eva, exp, fs, level, maxHP, maxMP, mobType, pushed,
                         speed, summonType, undead
f5/2220100.img/info/icon -> 404
```

裝備有 `info/icon`，怪**沒有**。所以「跟裝備一樣的圖示網格」沒有同一份資料來源。

能做的替代：拿怪自己的 `stand/0` 當縮圖。這是誠實的替代（對標的怪視窗本來就是
畫怪的圖），但**它不是 `icon`**，是「這個怪的站姿第一幀」。
視窗形狀可以跟裝備挑選器一樣，資料來源不能假裝一樣。

對照：對標的 `MobForm.cs:107-140` 本來就是 **DataGridView 清單**，
不是圖示方格牆（`AvatarForm.cs:107` 的 21 格 `ImageListView` 才是裝備那種）。
所以「跟裝備一樣」本身就是超出對標的改動，需要宣告。

## 7. 這隻怪沒有 `attack1` —— 沒有攻擊資料就沒有攻擊

```
f5/2220100.img/info/attack1       -> 404
f5/2220100.img/info/attack1/info  -> 404
f5/2220100.img/info/attack1/range -> 404
attack1/PADamage = (不存在)  attack1/attackAfter = (不存在)  attack1/range = (不存在)
```

C++ 對標確實有整套怪物攻擊（`MobAttack.cpp`、`Mob.cpp:144-177`），但它讀的正是
`info/attack1..N`；**這個存檔的怪沒有那個節點**，只有頂層的 `PADamage`/`MADamage`/
`bodyAttack`/`acc`/`eva`。

所以照對標做出來會是：這個存檔的怪**仍然不攻擊**，
因為對標自己的資料也沒有讓它攻擊。C++ 另外有 `info/bodyAttack` 的接觸傷害
（`Mob.cpp:69` `touchdamage`），但這隻怪的 `info` 裡我也沒量到 `bodyAttack` 節點
被列出（它在 children 列表裡，但值需要另外確認）。

**這不是我可以不查就決定的事** —— 「怪物要會攻擊」在對標裡是一個資料驅動的事實，
不是一個可以補上的功能。兩條路：

| 路線 | 內容 | 代價 |
|---|---|---|
| A. 只做對標有的 | 讀 `info/attack1`，有的怪會攻擊、沒有的不會 | 可能做出來等於沒做（這個存檔多數怪沒有）|
| B. 宣告自設計 | 給所有能靠近的怪一套攻擊（用 `PADamage` + 一個自訂範圍）| 超出對標，必須宣告；範圍與時機沒有對標可抄 |

## 已完成且不受這兩條影響的

見 `2026-10-14-2245`（6.3）與本輪：1 輔助線、2 骨架人、3 玩家被蓋住、
4 傷害數字、5 HP 條、6 死亡動畫、9 技能特效骨架。
