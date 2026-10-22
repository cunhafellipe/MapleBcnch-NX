# MapleNecrocer → MapleBench 對標考核表

建立：2026-10-14 10:20
對標專案：`C:\MUWORK\GAME\_necrocer_src\MapleNecrocer`（C# / WinForms，`.sln`，120 個項目）
被考核：`C:\MUWORK\GAME\MAPLESOTRY\wz工具\MapleBcnch-NX-audit`（C# / ASP.NET + MCP + 瀏覽器前端）

**這份表之前不存在。** 先前的驗收全部是「我做了什麼、我測了什麼」，
從沒回答過「對標專案有的，我沒有的有哪些」。使用者點名要求後才建立。

---

## 0. 方法

對標專案的能力面由靜態掃描取得，不是猜的：

```
Necrocer/Client/*.cs        30 檔，合計 ~10,700 行
Necrocer/CharaSim/*.cs       5 檔，合計 ~1,790 行（角色屬性/裝備/技能計算）
Necrocer/CharaSimControl/*  30 檔，合計 ~14,000 行（UI + tooltip 渲染）
```

MapleBench 對應面：

```
MapleBench/Services/Simulate/   CombatService DamageRoll MapSimSessionStore MobAnimation SimClockService
MapleBench/Services/MapEditor/  CharacterPhysics FootholdPhysics FootholdWalk MobPatrol MobSpriteService + 9 個 MapEditorService.*
MapleBench/Services/Look/       CharLookComposer EquipSlotTable LookService LookDtos
MapleBench/Services/            MobService NpcService SkillService CashShopService …
MapleBench/wwwroot/js/          40 檔（含 simulator.js livewalk.js mapedit-view.js damagenumber.js）
MapleMcp/                       52 個 MCP 工具
```

---

## 1. 逐項對表

### 1.1 Client/ 地圖層（Necrocer 11 個類）

| Necrocer 類 | 行數 | 做什麼 | MapleBench 對應 | 判定 |
|---|---|---|---|---|
| `Map` | 311 | 載入地圖、量文字、建立資源載入器 | `MapEditorService` + `MapDocument` + `MapCorpus` | ✅ 有（更強：支援 5366 張批量） |
| `MapBack` | **626** | 背景層：視差、滾動、平鋪、剪裁 | `MapPainter.DrawBacks`（`MapleMcp`） | ⚠️ 有，深度不足 |
| `MapTile` | 48 | 瓷磚層繪製 | `mapedit-view.js` tile 繪製 | ✅ 有 |
| `MapObj` / `SpineObj` | 355 | 地圖前景物件（柱、招牌） | `MapPainter.DrawObjs`？ | ❌ **未確認** |
| `MapPortal` | 181 | **傳送門建立/尋找/繪製** | `FindPortal` + `TakePortal`（本輪新寫） | ✅ 有（2026-10-14 10:00） |
| `MapParticle` | 116 | 地圖粒子 | — | ❌ **無** |
| `Footholds` | 275 | foothold 樹、`FindBelow`、`IsWall`、`ClosePlatform` | `FootholdPhysics` 275+ 行，含鏈表 | ✅ 有 |
| `LadderRopes` | 50 | 梯子繩索尋找 | `FootholdPhysics` ladder | ✅ 有 |
| `MapleTV` | 88 | 地圖電視機關 | — | ❌ **無** |
| `Reactor` | 89 | 反應爐（釀造機關） | — | ❌ **無** |
| `MapleChair` | 323 | 椅子（可坐） | — | ❌ **無** |

### 1.2 Client/ 角色層

| Necrocer 類 | 行數 | 做什麼 | MapleBench 對應 | 判定 |
|---|---|---|---|---|
| `MapleCharacter` | **1728** | 角色本體 + 裝備 + 方向 | `CharacterPhysics` + `CharLookComposer` | ✅ 有 |
| `MapleCharacterEx` | 390 | 玩家跳躍狀態機 | `CharacterPhysics` jump | ✅ 有 |
| `AfterImage` | 144 | 殘影（技能/攻擊拖影） | — | ❌ **無** |
| `Morph` | 181 | 變身 | — | ❌ **無** |
| `Pet` | 557 | 寵物 | — | ❌ **無** |
| `Familiar` | 444 | 小精靈 | — | ❌ **無** |
| `TamingMob` | 379 | 騎乘 | — | ❌ **無** |
| `Android` | 364 | Android 玩家 | — | ❌ **無** |
| `Npc` + `NpcText` | 307 | NPC + 對話文字 | `NpcService`（資料層） | ⚠️ 只有資料，**無 AI/對話** |
| `Mob` + `MobCollision` | **837** | 怪物 + 碰撞 + AI | `MobPatrol` + `CombatService` | ✅ 有（本輪補上戰鬥） |
| `ItemDrop` | 182 | 掉落物 | — | ❌ **無** |
| `Skill` | 581 | 技能施放 | `SkillService` + `SkillFormulaEvaluator`（**公式**） | ⚠️ 只有公式，**無施放鏈** |
| `NameTag` + `MedalTag` | 399 | 名牌 + 勳章 | `mapedit-view.js` nametag？ | ⚠️ 未確認 |
| `DamageNumber` | 171 | **傷害數字** | `MapSimDamageNumber`（本輪）+ `damagenumber.js` | ✅ 有 |
| `MapleEffect` | **620** | 特效系統（NormalEffect / SetEffect） | — | ❌ **無** |
| `ChatBalloon` | 293 | 聊天气泡 | — | ❌ **無** |
| `ToolTip` | 147 | 物件提示框 | `tooltip.js` | ✅ 有 |
| `Sound` / `Music` | 156 | 音效 / BGM | — | ❌ **無** |
| `MouseEx` | 91 | 滑鼠輸入 | 瀏覽器原生 | ✅ 有（更好） |
| `FadeScreen` | (Map.cs 內) | 轉場淡化 | — | ❌ **無** |

### 1.3 CharaSim/ 角色數值層

| Necrocer | 行數 | 做什麼 | MapleBench | 判定 |
|---|---|---|---|---|
| `Character` | **920** | 屬性計算：`UpdateProps` `CalcAttack` `CheckGearReq` `ChangeGear` | — | ❌ **無角色數值系統** |
| `CharaEquip` | 232 | 裝備槽管理 | `EquipSlotTable`（只是槽位對照表） | ⚠️ 槽位有，**無裝備計算** |
| `CharaProp` | 135 | 屬性加總 | — | ❌ **無** |
| `CharacterStatus` | 348 | 狀態欄（HP/MP/EXP） | — | ❌ **無** |

### 1.4 CharaSimControl/ 資訊呈現層

Necrocer 有 8 種 tooltip 渲染器，合計 ~5,700 行：
`GearTooltipRender2`(1707) `ItemTooltipRender2`(1218) `GearTooltipRender`(722)
`CashPackageTooltipRender`(471) `SetItemTooltipRender`(452) `SkillTooltipRender2`(462)
`MobTooltipRenderer`(379) `AfrmItem`(1116) `AfrmStat`(1022) `AfrmEquip`(573)

MapleBench 對應：`tooltip.js`（單一檔案）。

**判定：❌ 資訊深度遠落後。** 這不是「沒有 tooltip」，
是「tooltip 只有一層，而對標有十層且每層 400–1700 行」。

---

## 2. 缺口彙總

### P0 — 核心遊戲循環未完成（阻擋「模擬器」成立）

| # | 缺口 | Necrocer 依據 | MapleBench 現況 |
|---|---|---|---|
| 1 | ~~**怪物攻擊玩家**~~ | `Mob.cs` 的 `MobCollision` + AI | ✅ **2026-10-14 10:20 補上**：`MobPatrol.AttackPlayer` + `Aggro`，實測 100 幀內受傷 2 次、HP 50→48。**自設計**（客戶端不解決怪物傷害，無對標可抄） |
| 2 | ~~**玩家 HP/MP/EXP/等級**~~ | `CharacterStatus.cs` 348 行 | ✅ **2026-10-14 10:20 補上**：`PlayerStatus.cs`，HP/MP/EXP/AP/POP/四屬性/攻防，MaxHp=50 與 `(str*4+dex)*1.43*0.01` 照抄 `Character.cs` |
| 3 | **傷害數字 vs 血條** | `DamageNumber` + 角色狀態 | 數字有，HP 條沒有 |
| 4 | **XP 與升級** | `Character.cs UpdateProps` | **無** |
| 5 | **技能施放鏈** | `Skill.cs` 581 行 | 只有公式求值，**按不出技能** |

> **第 2 項曾是最嚴重的。** 10:05 的驗收「傷害 2/2/3/3、hp 8→0」是**怪物**的 HP；
> 當時玩家沒有 HP，「戰鬥鏈通了」只對單向半邊成立。
> **10:20 已補上**，`docs/對標考核-MapleNecrocer.md` 第 2 與第 1 項現為 ✅。
> 日誌：`JOURNAL/2026-10-14-1020-benchmark-audit-player-status.md`。

### P1 — 表現層缺失

| # | 缺口 | Necrocer 行數 |
|---|---|---|
| 6 | 特效系統 `MapleEffect` | 620 |
| 7 | 音效 / BGM `Sound`+`Music` | 156 |
| 8 | 地圖粒子 `MapParticle` | 116 |
| 9 | 殘影 `AfterImage` | 144 |
| 10 | 名牌 / 勳章 `NameTag`+`MedalTag` | 399 |
| 11 | 掉落物 `ItemDrop` | 182 |
| 12 | 轉場淡入淡出 `FadeScreen` | — |

### P2 — 物件互動缺失

| # | 缺口 | Necrocer 行數 |
|---|---|---|
| 13 | NPC AI / 對話 | `Npc.cs` 307 |
| 14 | 寵物 / 精靈 / 騎乘 / 變身 / Android | 364–557 各一 |
| 15 | 椅子 / 電視 / 反應爐 | 89–323 |

### P3 — 資訊深度

| # | 缺口 | 差距 |
|---|---|---|
| 16 | 物品 tooltip 分級渲染 | 1 檔 vs 8 檔 5,700 行 |
| 17 | 裝備 / 技能 / 商城 / 套裝 tooltip | 各缺一 |
| 18 | 屬性面板 `AfrmStat` | 缺 1,022 行對應 |
| 19 | 傷害公式（依職業/攻擊力） | Necrocer `CalcAttack` 有，MapleBench 用自設計常數 |

---

## 3. 對標的結論

**以「模擬器」為標準，MapleBench 目前仍不是完成的模擬器。**

10:05 的驗收「六迴路全通」只覆蓋戰鬥的單向半邊；
10:20 補上玩家狀態與怪物還手後，戰鬥鏈才真正是雙向的。
**仍然缺**：XP 與升級、技能施放鏈、全部表現層、全部物件互動、tooltip 深度。

**完成度（以 Necrocer 的 Client+CharaSim 為分母）：約 25% → 28%。**

| 已完成 | 缺口 |
|---|---|
| 地圖與物理 ✅ | 角色狀態 ✅（部分：XP 曲線與武器分支自設計） |
| 角色 ✅ | 怪物還手 ✅（自設計） |
| 怪物巡邏 ✅ | XP 升級 ❌ |
| 單向戰鬥 ✅ | 雙向戰鬥 ✅ |
| 傳送 ✅ | 技能鏈 ❌ |
| | 特效/音效/粒子/殘影/名牌/掉落 ❌ |
| | NPC AI/寵物/精靈/騎乘/變身/椅子/電視/反應爐 ❌ |
| | 8 種 tooltip 渲染器 ❌ |

---

## 4. 這份表本身的用途

之後每修一項，在此標記並附上**實測輸出**，不是「已實作」。
依 工作室守則 2.1–2.3：合成截圖不算證據、資料量不等於驗證、探針回傳 undefined 是探針壞掉。

## 5. 未來驗收新規則

**禁止再只列「我做了什麼」。** 每輪驗收必須附：
1. 本表對應行的狀態變化
2. 該行的實測輸出（數字）
3. 仍未做的項數