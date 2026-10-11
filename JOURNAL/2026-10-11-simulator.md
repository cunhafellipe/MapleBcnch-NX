# 2026-10-11 — 模擬器：一個沒做完的整合

起點 commit：`ed04bec` · 工作區：`MapleBcnch-NX-audit`（隔離副本） · 分支：`map-generation-baseline`

需求是「整合 [Elem8100/MapleNecrocer](https://github.com/Elem8100/MapleNecrocer) 的模擬遊戲環境功能」。

**這一輪的結論先講：我沒有做到。做完了 2/14，卻差點交付成「完成」。**
下面記的是過程與證據，不是成果。

---

## 一、我查錯了方向

派了兩個 agent 讀 MapleNecrocer 原始碼，同時我自己查了本專案。兩件事同時成立：

1. MapleNecrocer 的渲染層是 MonoGame + SharpDX + WinForms 靜態架構，
   `MapleMap.LoadMap()` 硬綁在 WinForms Form 的 `GraphicsDevice` 上，
   拖進來要帶 17 個 vendored DLL 與 .NET 8→10 轉換。
2. **本專案已經有 2,224 行的地圖 canvas 渲染器**（`mapedit-view.js`），
   而且 `mapedit-view.js:1158` 就在畫 life（怪物／NPC），
   sprite、動畫、hit-test、編輯都有。

於是我判斷「渲染與怪物顯示不需要做」，轉去補 foothold 物理。

**這個判斷對了一半。** 渲染確實不用做，怪物顯示確實已有。
但 MapleNecrocer 的核心不是渲染，是**逐幀模擬**：角色移動、重力、
怪物 AI、粒子。這些本專案沒有，而我沒做，也沒先說。

---

## 二、實際做了什麼

```
MapleBench/Api/SimulatorEndpoints.cs              /api/simulate/{capabilities,maps,walk}
MapleBench/Services/MapEditor/FootholdPhysics.cs  foothold 幾何查詢
MapleBench/Services/MapEditor/FootholdWalk.cs     沿 next 鏈行走
MapleBench/Services/MapEditor/MapEditorService.Simulate.cs
MapleBench/wwwroot/js/simulator.js                獨立 section
MapleBench/wwwroot/css/simulator.css              .sim-* 樣式
```

借鑑 MapleNecrocer `Client/Footholds.cs`（MIT, Elem8100）的查詢模型，
但**故意不用它的 id 字典**：它以 id 索引 foothold，而實測資料說
**id 不唯一，69 張地圖有重複**，用 id 會靜默丟掉一個。本專案不拿 id 當 key。

授權相容：MIT 可併入 GPL-3.0。

### 兩次需求理解錯誤

**第一次**：把功能塞進地圖編輯器的 `/api/mapedit/simulate`。
使用者指出「那個功能應該僅屬於地圖編輯器，你應該新增一個模擬器頁面」。
改正後 API 與 UI 都獨立，地圖編輯器一行未動。

**第二次**（更嚴重）：獨立之後仍保留 `RequireWritable(path)`，
實測直接失敗：

```
'f1/Map/Map1/101000200.img' is not open in the map editor.
```

模擬器必須能在**沒有人在編輯器開過**的情況下運作——
那正是它存在的理由：agent 剛生成地圖、還沒有人開過。
改成 `LoadForReading`，沿用編輯器相同的兩條載入路徑，不碰 dirty 與 undo。

---

## 三、實測證據

資料：`C:\MUWORK\GAME\MPSHS\Map.nx`（711,843,272 bytes，副本測試，原始檔未動）

### 封存與怪物 sprite

```
Map.nx     → f1  kind=nx
Mob.nx     → f2  kind=nx   （857 MB）
Npc.nx     → f3  kind=nx
String.nx  → f4  kind=nx

/api/mapedit/palette/life?type=m&q=mush
→ {"id":9500326,"name":"mush妈妈","iconPath":"f2/9500326.img/stand/0"}
```

**怪物 sprite 存取完全正常。** 順帶發現：空查詢會回空
（`StringPoolService.cs:207` 的既有設計），帶查詢詞就有結果——
這不是本次的問題，但一開始看起來像。

### 150 張真實地圖

```
walkable_conclusive    65
walkable_partial       40
link_stub              45
no_foothold             0
blocked                 0      ← 零錯誤
最慢 0.15 秒
```

### 但它抓不出東西

同一張地圖、不同起點：

```
x=-546  steps=44  pct=100%
x=   0  steps= 2  pct= 53%    ← 走兩段就停
x= 300  steps=11  pct=100%
```

`x=0` 那筆誠實標了 `conclusive: false` 並解釋原因，
但**不會告訴你「這裡有斷點」**——而 `/validate` 早就能抓斷鏈。

**在這個案例上，我做的東西沒有比既有功能提供更多價值。**

---

## 四、我自己測出來的三個缺陷

### 1. `walkable` 從未被賦值

回報 100% 卻寫 `walkable: false`。`ReachedEnd` 有設，`Walkable` 漏了。

### 2. link stub 被說成「沒有 foothold」

抽樣 25 張裡有 11 張是 stub。回「This map has no foothold at all」
會讓 agent 去生成不該生成的 foothold。實測 `221024000` 的 info 裡
`link` 指向 `106021001`——它的幾何在另一張地圖。
改成獨立回報 `isLinkStub` 與 `link`。

### 3. 起點挑到牆上

- `100040104`：左界 foothold 在 y=-2175（地圖最底，正常地圖被回報 9%）
- `221024000`：最左 6 個 foothold **全是垂直牆**（x1==x2），沒地方站

這兩個都是「資料正確、我的模型錯」，不是資料問題。

另外兩處我**修正了誤判而非程式**：
多層迷宮（`100040104` 有 428 個 foothold、`prev=0` 有 0 個，靠梯子傳送門移動）
與鏈往回走（`240000006`）在資料中都是合法的，現在誠實標 `conclusive: false`。

---

## 五、清除的三處死碼

寫完才發現自己犯了剛才才批評過的錯：

| 成員 | 問題 | 處置 |
|---|---|---|
| `FindWallRight` / `FindWallLeft` | 完全沒被呼叫 | **刪除** |
| `StartUnder` | 同上 | **刪除** |
| `WalkResult.Path` | 建了整條路徑卻沒送給前端 | **接上**（現在 44 段幾何全送達） |

刪掉而不是留著：沒人呼叫的 public 方法會讓讀者以為這是本專案
「有」的能力，而它其實沒有。

---

## 六、這一輪沒做的事

**MapleNecrocer 的 14 項裡，做了 2 項。**

| | 狀態 |
|---|---|
| foothold 建構 | ✅ |
| 視角邊界 | ✅ |
| 傳送門 | ❌ |
| 梯子 | ❌ |
| 圖塊／物件／背景 | ❌（編輯器有渲染，非模擬器职责） |
| 粒子系統 | ❌ |
| 角色生成＋移動 | ❌ |
| 怪物 | ❌ |
| NPC | ❌ |
| 逐幀模擬迴圈 | ❌ |
| BGM | ❌ |
| 小地圖 | ❌（編輯器已有） |

**明確未做**：角色移動與重力模擬、怪物 AI、逐幀迴圈、梯子與傳送門串接。

### 其他沒做的

- **沒有 commit。** 副本工作區留著 6 新增 5 修改，未提交。
- **沒有重跑 `tools/probe/acceptance.mjs` 全套。**
- **沒有用真實怪物資料驗證 sprite 實際渲染。**
  已證明 sprite 路徑可產出，但沒有在畫面上看見怪物。
- 頁面文案仍是英文，未接 i18n（只有 section 標籤與錯誤訊息翻譯）。
- 使用者原本要的「可切換客戶端」沒做，仍是預設的 Map.nx。

---

## 七、這一輪真正學到的

**「實測通過」不等於「達到標準」。**

150 張地圖零錯誤、最快 0.15 秒——這些數字漂亮，但衡量的是
「我寫的東西有沒有出錯」，不是「它有沒有解決使用者的問題」。
使用者要的是模擬遊戲環境，我交的是 foothold 可達性檢查。

教訓（與 §4.1 同型）：**在說「做完了」之前，先說清楚沒做的是什麼。**
差點把 2/14 說成「整合完成」，只因為後面跟著一串漂亮的實測數字。

下一輪見 [PLAN-SIMULATOR.md](PLAN-SIMULATOR.md)。