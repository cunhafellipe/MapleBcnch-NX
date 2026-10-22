# 2026-10-15 — `firstAttack`：拿取已實作，但**是惰性的**，畫面上看不出來

這一輪刻意**沒把東西交出去**，因為發現自己做出來的是一個空殼。記錄下來是為了
下一輪不要重做，也為了不要有人誤以為這是完成的。

---

## 兩個對標在這裡衝突，守則 §7.6 判 C++ 贏

```csharp
MapleNecrocer/Client/Mob.cs:434-460   怪只有被��才會反應
                                   ——「客戶端沒有仇恨半徑」
clean-client/Mob.cpp:79              first_attack = info["firstAttack"].get_bool();
clean-client/Mob.cpp:671             chasing = ... && (first_attack || aggro);
clean-client/MobAttack.cpp:81        if (first_attack && (|dx|/10 + |dy|/3 <= 40
                                                        || !available.empty())) aggro = true;
```

**存檔站在 C++ 那邊**：`info/firstAttack` ��� **2,053 隻裡的 522 隻**有，
而且是裸的 `1`、沒有任何子節點（400 個名字的取樣讀到 43 個 `'1'`，沒有別的值）。
照 C# 會讓這個旗標在四分之一的怪物圖鑑裡是死資料。

`!available.empty()` 那一半**不移植** —— 它問的是怪有沒有可用的攻擊範圍，
而 `info/attack1` 在這個存檔是 **0/2053**，`available` 恆空，
移植過來就是永遠不會執行的程式碼。

## 實作了什麼

`CombatService.MobFirstAttack(id)` 讀旗標；`MobPatrol` 帶著它；
`AttackPlayer` 的閘門從「只有被打過的 Aggro」放寬成
`Aggro || (firstAttack && |dx|/10 + |dy|/3 <= 40)`，權重照 C++（x 除 10、y 除 3，
所以範圍是水平比垂直寬四倍，因為不會有人跨樓層走過去）。

## 為什麼它是惰性的 —— 缺的是「追逐」那一半

**拿到 aggro ≠ 打到人。**

| 閘門 | 值 | 結果 |
|---|---|---|
| 獲取範圍 `|dx|/10+|dy|/3 <= 40` | dx 可到 **400** | 通過 |
| 命中範圍 `AttackReach`（既有，**自設計**）| **40** | 62px 外的怪打不到 |

而且它不會自己拉近距離：`MobPatrol.Step` 只在 **`HitsTaken > 0`**（被攻擊過）
才轉向並走過去（`:549`）—— 那正是 `firstAttack` 存在的目的所要避免的情況。

C++ 裡讓獲取有意義的是 `Mob.cpp:671` 的 `chasing`：
```
const bool chasing = target_alive && !target_hidden && (first_attack || aggro);
```
**它驅動怪走向目標。那一半還沒移植。**

### 實測（`f4/Map/Map1/100000004.img`）

```
校準：2220000 firstAttack = '1'      必須
校準：2220100 firstAttack = <absent>  必須
1. 沒有 firstAttack 的怪，未揮刀      -> 傷害 0    ✅
2. 召喚 2220000（firstAttack=1, bodyAttack=1, PADamage=90）
   召喚點 dx=62 dy=0 -> 6.20 <= 40  在獲取範圍內
   400 幀內傷害                        -> 0        ❌ 因為它不會走過來
```

## 這一輪的量測又錯了兩次

1. **測試挑的樣本沒有旗標**：我拿 `9409000` 當「有 firstAttack 的怪」，
   實際上它**沒有**。測試看起來在跑、其實什麼都沒證明。
   修法：測試**先斷言旗標存在**，不符就 `ABORT`。
2. **對「沒有旗標」的怪查旗標會 404**，而我把 404 當成致命錯誤，
   腳本在校準階段就中止。404 正是預期答案。

## 結論與下一步

**這段程式碼是對的、有校準過、但沒有可觀察效果。** 我不把它算完成。

下一件要做的是 `Mob.cpp:671` 的追逐：讓 `Aggro`（不論來自挨打還是 firstAttack）
驅動怪走向玩家，而不只是在 `HitsTaken > 0` 時轉向。做完之後，
第 2 項測試才會變綠，而且那才是「怪物會主動攻擊」真正看得見的樣子。

## 回歸

建置部署完成，三處 md5 一致。爬梯 5/5、被攻擊的怪會反擊（+19 幀 50 傷害）、
NPC 名牌 2/6 有名、0 隻怪誤帶 —— 全過。
`firstAttack` 的新路徑沒有讓任何東西變差，也沒有讓任何東西變好，這是事實。
