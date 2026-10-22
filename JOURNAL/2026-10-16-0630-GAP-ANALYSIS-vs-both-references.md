# 2026-10-16 06:30 — 目前版本 vs 兩個對標：還缺什麼

> 重新盤點。**每一項都先問「存檔裡有沒有這份資料」**（守則 §2.8），
> 沒有資料、或參考本身也沒有的，就不列為缺口。
> 引用一律連**呼叫端**一起看——上一輪最大的教訓是每一行都有引用、整條架構卻是錯的。

## 0. 盤點到的東西

| | 規模 |
|---|---|
| 對標 C# `MapleNecrocer` | `Client\` 72 檔 + 39 個 WinForms 表單 |
| 對標 C++ `clean-client` | `Gameplay\` 19,766 行、`Character\` 13,612、`IO\` 51,203、`Net\` 12,241 |
| 本專案 | `Services\` 最大 `PortService.cs` 540 KB；前端 39 個 JS 模組 |

模擬器面向的 HTTP 路由共 33 條（`SimulatorEndpoints.cs`）：
session／幀／輸入／走動／幾何／召喚／裝備／技能／外觀／傳送門 + 9 條除錯斷言。

## 0.1 對標 C# 的性質 —— 這條改變了一半的結論

**`MapleNecrocer` 不是遊戲客戶端，是 WZ 檢視器／私服地圖與素材檢查工具**
（建在 WzComparerR2 的 WzLib 上）。沒有伺服器、沒有封包層、沒有經濟系統。

而且它的「背包／裝備／狀態視窗／角色資料模型」是**編譯進去的死碼**：

| 類別 | 行數 | 實際狀態 |
|---|---|---|
| `CharaSimControl\AfrmItem.cs`（背包視窗） | 1,116 | **從未被 new** |
| `CharaSimControl\AfrmEquip.cs`（裝備視窗） | 572 | **從未被 new** |
| `CharaSimControl\AfrmStat.cs`（屬性／經驗條） | 1,022 | **從未被 new** |
| `CharaSimControl\CharaSimControlGroup.cs`（串接上面三個） | 292 | **零個呼叫點** |
| `CharaSim\Character.cs`（角色資料模型） | 919 | **零個 `new Character()`** |
| `CharaSim\CharaEquip.cs`（66 個裝備槽） | 231 | 只被死碼引用 |

唯一被 `MainForm.cs:55` 真正使用的是 **`AfrmTooltip`**。

**所以「參考有背包系統」是錯的**——那是從 WzComparerR2 繼承來、從沒被接上的死碼。
我第一版差集分析把它列成缺口，這裡撤回。

---

## 1. 先講「不是缺口」的（資料不存在，或參考本身也沒有）

| 項目 | 對標 | 量測 | 判定 |
|---|---|---|---|
| **道具掉落** | C# `ItemDrop.cs` 181 行 | 地圖頂層 `[0..7, ToolTip, back, foothold, info, ladderRope, life, miniMap, portal, reactor]`——**沒有 `drop`**；三隻怪的 `info` 也沒有 drop 節點。參考自己也只是**視覺**：`DoMove` :129 落地彈跳後 :172-176 淡出刪除，**沒有拾取**；呼叫端只有 `ConsumeForm:237`／`EtcForm:169`／`CashForm:159` 三個表單按鈕手動丟，且都傳 `Num = 0` | **不是缺口** |
| **經驗曲線** | `CharaSim\Character.cs:635` `ExpToNextLevel` | 那是一張**寫死的 200 級表、不讀任何 WZ 節點**；唯一消費者 `AfrmStat.cs:614` **從未被 new**。存檔也沒有 Exp.nx，`Etc.nx` 23 個 img 也沒有經驗表。`Mob.HP` 在 `Mob.cs:158` 寫死 2,000,000，`maxHP` 的讀取被註解掉（:157），殺怪**不給經驗** | **不是缺口** |
| **背包視窗** | `AfrmItem.cs` | **死碼**，見 §0.1 | **不是缺口** |
| **公會／任務／豆子／存檔／快捷鍵設定** | — | `Guild` 只是 `Character.cs:62` 的一個 string；任務只出現在道具 tooltip 的 `ItemPropType.quest`（`ItemTooltipRender.cs:125`）與粒子 WZ 格式的 `quest` 節點；`Quest/*.img` **完全沒被讀** | **不是缺口** |

---

## 2. 機制缺口（真正的，資料都在）

### 2.1 裝備只有外觀，沒有屬性 —— **最大的一項**

對標有 `CharaProp.cs:134`（`BaseVal/GearAdd/BuffAdd` 屬性累加器）、
`CharaEquip.cs:231`（66 個裝備槽）。視窗是死碼，但**資料模型與欄位是真的**。

我們：`POST /session/{id}/weapon` → `SetEquippedWeapon()`（`MapSimSessionStore.cs:1399`）
只存下 id。戰鬥端讀整份武器存檔只有兩個節點：

```csharp
// CombatService.cs:259   姿勢群
TryReadIntChild(image, "info", "attack", out int attack)
// CombatService.cs:827   出手速度
TryReadIntChild(image, "info", "attackSpeed", out int speed)
```

**沒有傷害、命中、爆擊、防禦。** 換裝備只換外觀與姿勢。

連帶後果寫在 `DamageRoll.cs`：六條棄用理由全是「這個角色沒有 X」。
補上「裝備 → 屬性 → 傷害」這條鏈，那些理由才會逐一失效，傷害公式才有機會回到參考。

### 2.2 屬性相剋沒有套用
`ElementDamage.h` 54 行；資料在——抽樣 6 隻怪裡 **4230101 的 `info/elemAttr='H3'`**。
`DamageRoll` 註解明說棄用 `adjust_element_damage`，理由同 2.1。

### 2.3 NPC 聊天氣球 —— 資料就在眼前，我們完全沒畫
參考：`Npc.cs:122-130` 把 `String/Npc.img/<id>` 底下**兩個字元的鍵**收進 `Npc.Msgs`，
`ChatBalloon.cs:32` 隨機播一則。

量測：`String.nx` 的 `Npc.img/10000` 子節點是 **`['d0','d1','n0','n1','n2','name']`**。
我們 `walkview.js` / `livewalk.js` 裡**完全沒有 balloon 相關字串**。

### 2.4 地圖 BGM —— 資料也在，我們沒播
`MapleMap.cs:284` 讀 `.../info/bgm` 餵給 `Sound.cs:114` 的 `Music`。
量測：測試地圖 **`info/bgm = 'Bgm00/FloralLife'`**，`Sound.nx` 開啟中。

### 2.5 反應爐沒有被模擬
`Reactor.cs:88`、C++ `Reactor.cpp:132`。`MapNodes.cs:538` 記錄客戶端共 8,366 個反應爐。
編輯器能擺放預覽（`mapedit.js` 的 `armReactor`），**行走模擬器裡沒有這個物件**。
測試地圖的 `reactor` 容器存在但**是空的（0 筆）**。

### 2.6 寵物／精靈／坐騎／變身／椅子 —— 五個真機制，一個都沒有
`Pet.cs` 557（含寵物裝備）、`Familiar.cs` 444、`TamingMob.cs` 378、
`Morph.cs` 181、`MapleChair.cs` 322。五個表單都是**活的**
（`MainForm.MobButton_Click` :998-1039 統一派送），`TamingMob.nx` 也開著。

### 2.7 我們的「召喚」是除錯生成，不是遊戲召喚
`POST /session/{id}/summon` 的註解寫著它是 `MobForm.cs:126-143` 的 Remove 按鈕——
**編輯器工具**。參考的 `MobForm.cs:119-120` 語意接近（從圖鑑挑一隻生出來），
但目的地不同：它進 `Mob.SummonedList`，我們進 session 的測試清單。

---

## 3. UI 缺口

| 項目 | 參考 | 我們 | 判定 |
|---|---|---|---|
| **怪物圖鑑 tooltip** | `MobTooltipRenderer.cs:21`（378 行：屬性、HP、EXP 表、`GetMobCategoryName` :334） | 有 `/api/simulate/mob-catalog`（2053 列）與選擇器，**沒有這種資訊面板** | **真缺口**，正是段 3 |
| **技能 tooltip** | `SkillTooltipRender2.cs:19`（等級、延遲、需求技能、CD） | 清單有 id／書／圖示／名稱／姿勢，**沒有等級、CD、需求** | **真缺口** |
| **小地圖** | `MiniMap.cs` 396 行 | **只在地圖編輯器有** | **真缺口**（資料齊全） |
| **名牌／勳章／稱號／戒指標籤** | `NameTag.cs` 399 行 | 只有 `【LVxx】名稱` | **真缺口**，資料在 `UI/NameTag.img` |
| NPC 名字＋功能 | `NpcText.cs:246` | 有（2/6 有名） | 已有 |
| 傷害數字 | `DamageNumber.cs:170` | 有 | 已有 |

---

## 4. 地圖工具鏈不是缺口（量過）

- 5366 張地圖可列
- 幾何載入抽 60 張 **60/60 全過、0 錯誤**，平均 87 條 foothold
- 美術解碼抽 40 張 **927 槽缺 8（0.9%）**，全是梯子繩
- 圖層深度這輪修掉了「所有地磚 artZ 被讀成 0」的 bug（`MapEditorService.cs:2537`）

---

## 5. 建議順序

```
段 3  怪物圖鑑面板（屬性／HP／EXP 表／分類）  ← 有明確對標 MobTooltipRenderer.cs
段 5  裝備 → 屬性 → 傷害                       ← 解開 DamageRoll 六條棄用理由的前置
段 6  NPC 聊天氣球 + 地圖 BGM                   ← 資料現成
段 7  反應爐模擬 / 行走視圖小地圖 / 名牌系統
段 8  寵物・精靈・坐騎・變身・椅子
```

---

## 6. 這一輪的量測教訓

1. **看到一個類名不代表有那個機制。** `AfrmItem` 1,116 行的背包視窗、
   `CharaEquip` 231 行的 66 槽資料模型、`Character.cs` 919 行的角色模型——
   **全部零呼叫點**。必須去數 `new` 出現幾次，不能讀到類名就記一筆。
   這一次就讓我憑空多列了三項缺口。
2. **`ItemDrop.cs` 看起來是掉落系統**——實際是三個表單的「丟一個道具到地圖上預覽」工具，
   而且只淡出刪除、沒有拾取。
3. **傷害公式的註解可能比現況舊**——`DamageRoll` 說「角色沒有裝備」，
   實際是「沒有屬性系統，裝備只有外觀」。同一個詞，兩件事。
4. **死碼會讓對標看起來比它大。** 對標 39 個表單裡，活的只有一個 tooltip。
5. **`Client\Foetholds.cs` 在這個檔案系統上大小寫敏感地讀不到**，
   要用 `Client\foetholds.cs`。目錄列舉與 grep 都看得到。