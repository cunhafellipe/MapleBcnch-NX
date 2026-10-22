# 2026-10-14 10:20 — 對標考核：MapleNecrocer 缺什麼，以及「六迴路全通」是錯的

接續 [1005](2026-10-14-1005-combat-and-portal-fixed.md)（同一輪，不覆蓋）。

---

## 1. 使用者的指正

> 「對標的模擬器項目 你是否有當作驗證考核? 它有甚麼 你都要有」

**答案是沒有。** 先前每一輪的驗收格式都是「我做了什麼、我測了幾筆」，
從來沒有回答過「對標專案有的、我沒有的有哪些」。

對標專案是 **`C:\MUWORK\GAME\_necrocer_src\MapleNecrocer`**
（C# / WinForms，`.sln`，120 個項目）。它是 v83 地圖與角色模擬器，
`MobPatrol.cs` 的註解本身就引用它的 `Client/Mob.cs`。

本輪新建 `docs/對標考核-MapleNecrocer.md`（對標表本體，不覆蓋）。

---

## 2. 對標表挖出的第一件事：我 10:05 的結論是錯的

我 10:05 寫「模擬器六迴路全通」。逐項對照 Necrocer 後：

**沒有玩家 HP 系統。** `MaxHp`/`Hp` 只存在於 `MapSimMob`（怪），
`MapSimSession` 沒有任何玩家狀態欄位。`grep` 結果：

```
Services/Simulate/CombatService.cs:47:  public const int DefaultLevel = 0;
Services/Simulate/MapSimSessionStore.cs:1138: int mobLevel = _combat.MobLevel(mobId);
```

`SimulatedPlayerLevel = 1` 是一個常數，沒有東西消費它。

**怪物不會攻擊玩家。** `grep -rn "AttackPlayer\|HurtPlayer\|PlayerHurt"` 全空。

所以「hp 8→0、擊殺、45 幀死亡動畫」**全部是怪物側**。
我驗收的是戰鬥鏈的**單向半邊**：可以打怪，怪會死，可以傳送；
沒有 HP 條、沒有怪還手、沒有 XP、沒有技能。

**「六迴路全通」這個說法本身不成立。** 它把「玩家→怪」讀成了「戰鬥」。

---

## 3. 對標能力面（靜態掃描，不是猜的）

```
Necrocer/Client/*.cs        30 檔 ~10,700 行
Necrocer/CharaSim/*.cs       5 檔 ~1,790 行（角色屬性/裝備/技能計算）
Necrocer/CharaSimControl/*  30 檔 ~14,000 行（UI + 8 種 tooltip 渲染器）
```

### P0 缺口（阻擋「模擬器」成立）

| # | 缺口 | 對標依據 |
|---|---|---|
| 1 | 玩家 HP/MP/EXP/等級 | `CharacterStatus.cs` 348 行 |
| 2 | 怪物攻擊玩家 | 伺服器端邏輯，客戶端沒有 → **無對標可抄** |
| 3 | 傷害數字 vs 血條 | 有數字，無 HP 條 |
| 4 | XP 與升級 | `Character.cs UpdateProps` |
| 5 | 技能施放鏈 | `Skill.cs` 581 行（我只有公式求值） |

### P1 表現層（全部缺）

`MapleEffect` 620 · `Sound`+`Music` 156 · `MapParticle` 116 ·
`AfterImage` 144 · `NameTag`+`MedalTag` 399 · `ItemDrop` 182 · `FadeScreen`

### P2 物件互動（全部缺）

`Npc` AI 307 · `Pet` 557 · `Familiar` 444 · `TamingMob` 379 ·
`Android` 364 · `Morph` 181 · `MapleChair` 323 · `MapleTV` 88 · `Reactor` 89

### P3 資訊深度

我 1 個 `tooltip.js` vs 對標 8 個渲染器 5,700 行
（`GearTooltipRender2` 1707 / `ItemTooltipRender2` 1218 / `GearTooltipRender` 722 …）

### 真實完成度

以 Necrocer 的 Client+CharaSim 為分母：**約 25%**。

---

## 4. 本輪補上的（缺口 #1、#2）

### `Services/Simulate/PlayerStatus.cs`（新檔，157 行）

照抄 `CharacterStatus.cs` 的欄位與公式，每一條都註明來源行號：

| 欄位 | 來源 |
|---|---|
| `MaxHp = 50` | `Character.cs`: `status.MaxHP.BaseVal = 50;` |
| `Attack` | `CalcAttack(GearType.barehand)`: `(str*4 + dex) * 1.43 * pad * 0.01` |
| HP/MP/EXP/AP/POP、Job/Level、四屬性、PDD/MDD/PEva/MEva | `CharacterStatus.cs` 逐一 |
| HP 讀寫皆 clamp 到 [0, MaxHp] | `CharacterStatus.cs` 的 setter 行為 |

**自設計處已在原始碼註解聲明**：`ExpToNextLevel` 是平坦曲線，
武器類型分支（dagger/cane/wand）未移植，所以裝了武器仍走 barehand 曲線。

### `MobPatrol.AttackPlayer()` + `Aggro`

**沒有對標可抄——這點必須明說。**
Necrocer 是單機編輯器，客戶端不解決怪物傷害（它只畫伺服器送來的數字），
`Mob.cs` 的 `MobCollision` 是**視覺碰撞體**不是攻擊判定。
我查過 `Mob.cs` 全部 `Attack` 關鍵字，只有一處 `if (Skill.Attacking)`。

**從對標來的是觸發條件**：`Mob.cs:434-460` —— 怪物只在**被打之後**才注意玩家，
客戶端沒有仇恨半徑。所以走過身旁的怪不會攻擊。
**射程 40px、冷卻 60 幀、傷害 = MaxHp/8 全是自設計**，原始碼註解裡逐項標明。

---

## 5. 過程中的三個真 bug（都不是靠猜找出來的）

### (1) `Engaged` 是一幀事件，不是狀態

我第一版用 `if (!Alive || !Engaged) return 0;` 當開關。

`MobPatrol.cs:459-479`：`HitsTaken > 0` 時設 `Engaged = true`，
其餘所有路徑 `Engaged = false`。**所以怪只在被擊中那一幀為 true。**
用它當持續開關，怪永遠打不出來。

新增 `Aggro`（黏性狀態）與 `Engaged`（一幀事件）分開。

### (2) `Aggro = true` 放在 early return 之後 → 永遠執行不到

`MobPatrol.cs:462-476` 的結構是：

```csharp
Engaged = true;
if (Facing != 0) { Direction = Facing; return; }   // ← 幾乎每次都走這裡
Engaged = false;
return;
```

我把 `Aggro = true` 放在這個 `return` **之後**。
怪被打時 `Facing` 幾乎必然非 0（它會轉向攻擊者），所以那行**一次都沒跑過**。

修正：移到 `Engaged = true` 旁邊。

### (3) DLL 鎖：MSB3021 之後還要以 `p.kill()` 收尾

`p.terminate()` 後 4 秒不足以讓 Windows 釋放 DLL handle，
下一次建置就 `MSB3021 / MSB3027`。
要 `p.kill()` + 睡 6 秒才穩定。這一輪踩了兩次。

---

## 6. 診斷端點：本輪第三次證明它的價值

前兩次（`/debug/portal`、`/debug/mob-swing`）都在症狀無法自證時直接抓到根因。

這次 `/debug/mob-swing` 回報 `dx=71, aggro=False, alive=True`，
一眼看出兩個問題：射程外的怪不會打、被擊中的怪 aggro 沒設上。

**但它也暴露了自己的缺陷**：`Any()` 回傳字典裡**第一個** session，
我開了多個 session 時它回報的是別人的戰鬥（`mobs: []`、怪在 `dx=-1454`）。
診斷端點給出**看似合理但錯誤**的數字 —— 比沒有端點更危險。

**已修正的誠實做法**：`Any()` 的註解明說「這是『看一個活著的戰鬥』，
不是你正在跑的那個」，而不是假裝它是「最新的那個」。
（GUID 字串沒有大小序，「最新」在這裡不成立。）

---

## 7. 驗收（純 MCP，非 curl）

```
【玩家狀態】HP 50/50  等級 1  攻擊力 0.29
【交戰】怪 0100100 x=19  角色 x=90
打了1擊  傷害 3  mobId 0100100  (6,371)  miss=False

接下來只觀察 100 幀（不還手、不移動）:
  f24:  玩家受傷 1  hp 49
  f100: 玩家受傷 1  hp 48

玩家 HP 50 → 48/50   被攻擊 2 次
```

**雙向戰鬥成立。** 玩家能打怪（3 點傷害、怪 HP 8→0、45 幀死亡動畫），
怪能打玩家（HP 50→48）。

間隔 76 幀而非設定的 60：怪被打時 Stun 中斷了冷卻計數，
且 `AttackCooldownLeft` 每幀遞減但只在 `Aggro` 且存活時遞減。

---

## 8. 動到的檔案

```
新增  MapleBench/Services/Simulate/PlayerStatus.cs
修改  MapleBench/Services/Simulate/MapSimSessionStore.cs   Status, PendingPlayerDamage, MobSwingLog, Any()
修改  MapleBench/Services/MapEditor/MobPatrol.cs           Aggro, AttackPlayer, MobDamage, AttackCooldown, AttackReach
修改  MapleBench/Api/SimulatorEndpoints.cs                 /debug/mob-swing
新增  docs/對標考核-MapleNecrocer.md
```

建置 `MapleBenchX.csproj` 0 錯誤。**未 commit。**
後端 `:5590`（`bin/Release/net10.0-windows`，DLL 10:14:43）。

---

## 9. 往後的驗收規則（本次新增）

**禁止再只列「我做了什麼」。** 每輪驗收必須附三樣：

1. `docs/對標考核-MapleNecrocer.md` 對應行的狀態變化
2. 該行的實測輸出（數字，不是「已實作」）
3. 仍未做的項數

理由：一輪的「六迴路全通」之所以能被說出口，
就是因為當時手上沒有一張表要求我回答「對標有的我沒有的有哪些」。
沒有那張表，「全通」只是我數自己做了幾件事。