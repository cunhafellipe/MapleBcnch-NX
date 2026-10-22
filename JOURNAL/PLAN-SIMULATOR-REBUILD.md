# 計畫：把「模擬器」做成真正的模擬器

2026-10-12 · 副本 `MapleBcnch-NX-audit` · 分支 `map-generation-baseline`
對標：`Elem8100/MapleNecrocer` commit `2aeb7db for TMS v278`（本機 `%LOCALAPPDATA%\hermes\cache\necro-ref`）

---

## 0. 為什麼要重寫這份計畫

上一份計畫（`PLAN-COMPLETION-necrocer.md`）的重點是「補齊與 MapleNecrocer 的功能差距」。
**那個方向是錯的。**

它把對標理解成「公式一致」，於是全部工作在追 `FindWall`、`Angle256`、
梯子半徑這些細節——那是**手段**，不是**目的**。
現有 1,556 行的物理確實忠實，但忠實地算出一條沒人想看的折線，沒有價值。

**真正的目標是功能一致**：MapleNecrocer 載入一張地圖後，
你能在裡面用鍵盤操控一個角色走動、跳躍、爬梯子、進傳送門。
那才是「模擬器」。

---

## 1. 參考專案實際是什麼

```
MainForm.cs      視窗：地圖清單 + 圖層開關（Tile/Obj/Back/Front/Npc/Reactor/Mob…）
  └ LoadMapButton  按下 → 開一個遊戲視窗
RenderFormDraw.cs  MonoGame 視窗，AlwaysEnableKeyboardInput = true
  ├ UpdateGame()  固定 1/60s 迴圈，讀鍵盤現況驅動角色
  └ Player.SpawnNew()  → 真正的 sprite + 動畫；寵物、精靈跟著
```

**它是「進去玩」，不是「看它跑」。** 角色身上沒有計時器、不播軌跡，
它只知道「左右鍵現在有沒有按著」（`MapleCharacter.cs:715/732`）。

grep 全 repo 確認：`trace` / `Simulate` / `Walk` / `Playback` / `回放` **零命中**。
唯一命中的是 `Sound.Play()`。**「播放一條預算軌跡」是本專案發明的，參考專案沒有。**

---

## 2. 現在的成品是什麼

| | MapleNecrocer | 現有「模擬器」 |
|---|---|---|
| 進入方式 | 選地圖 → 開遊戲視窗 | 選地圖 → 看折線動畫 |
| 角色 | sprite + 動畫 + 轉向 | 一個座標點 |
| 操作 | **鍵盤即時操控** | 只能按「播放」 |
| 判斷 | 你自己走、自己看 | 程式回報 98.0% |
| 互動 | 可停下、可轉向、可試跳 | 無 |

**它是客戶端的截圖，不是客戶端。**

---

## 3. 技術根因：每幀重建整張地圖

`MapEditorService.SimulateWalk.cs:356-365`

```csharp
public MapStepProbeDto StepOnce(string path, int x, int y, int dir, int climb)
{
    FootholdPhysics physics = new(LoadForReading(path));   // ← 每次重解析整張地圖
    CharacterPhysics who = new(physics, start.Value, dir); // ← 角色也重建
```

`/step` 每幀丟掉上幀狀態（速度、跳躍階段、是否在梯子上）。
所以不可能有「按住右鍵持續走」——只能批次預算成 `/trace` 的陣列，
再由前端播放。**播放鍵是這個架構的必然產物，不是設計選擇。**

---

## 4. 要做成什麼

### 4.1 目標

在瀏覽器裡載入一張地圖，然後**用鍵盤左右操控角色走動**，
行為與真實客戶端一致。畫面上看得見角色、走得動、能轉身、能跳、能爬梯子。

### 4.2 架構決定：物理在哪裡跑

| 方案 | 說明 | 評估 |
|---|---|---|
| **A. 伺服器保留角色狀態** | 新增 `/simulate/session`，伺服器持有 `CharacterPhysics` 實例，客戶端送「目前按什麼鍵」，伺服器推進一幀回傳位置 | ✅ **採此案** |
| B. 每幀呼叫 `/step` | 現況 | ❌ 角色狀態每幀重建，無法持續操控 |
| C. 物理搬到 JS | 前端重寫一份物理 | ❌ 兩份實作必然分歧，且要重驗 1,556 行 |

A 案的理由：物理維持單一實作（C#），與已驗證的 parity 修正共用；
客戶端只負責輸入與繪圖。`CharacterPhysics` 已經是**逐幀步進**的設計
（`Step(int moveLeft, int climb = 0)`），本來就適合當迴圈主體。

### 4.3 必須達成的行為（對標行號）

| 行為 | 來源 |
|---|---|
| 左右鍵累加速度 `SpeedL/SpeedR += 1.5`，上限 2.5，減速 0.25 | `MapleCharacter.cs:715-744` |
| 步行 `X += Sin256(FaceDir) * SpeedR` | `MapleCharacter.cs:737-739` |
| 牆探測取「後方最近的牆」 | `Footholds.cs:121-151`（已修） |
| 方向鍵梯子 | `MapleCharacter.cs:541-583` |
| 跳躍 `JumpSpeed 0.6 / JumpHeight 9.5 / MaxFallSpeed 8`，落地後上限 10 | `MapleCharacter.cs:136-138, 879` |
| 地圖邊界夾制 `Left+20 / Right-20` | `MapleCharacter.cs:777, 829` |
| 角度四捨五入 | `SpriteEngine.cs:773`（已修） |

---

## 5. 分階段

每階段的完成定義都是**真的能用鍵盤操作**，不是「回傳值正確」。

### S1 — 伺服器持有角色狀態（地基）

- 新增 `MapSimSession`：建立時載入一次 `FootholdPhysics` + `CharacterPhysics`
- `POST /api/simulate/session` → 建立，回傳 sessionId + 目前位置
- `POST /api/simulate/session/{id}/frame` 收 `{left, right, up, down, jump}` → 推進一幀，回傳位置 + 狀態 + 這幀觸碰的東西
- 驗收：連續 600 次送 `{right:true}`，X 單調增加且終點與離線 `/trace` 一致
  （同樣輸入、同一物理，兩條路徑必須產出相同結果——這是迴圈正確性的判準）

### S2 — 瀏覽器互動迴圈

- 鍵盤左右 → 每幀 POST frame → 更新角色位置 → 重繪
- 保留既有地圖美術渲染（canvas 已在畫圖，2,686 行）
- 角色畫成有朝向的圖樣；**不再有「播放」鍵**
- 驗收：真人用鍵盤在畫面上左右走，角色跟著走並在牆前停住

### S3 — 梯子、跳躍、傳送門

- 上下鍵爬梯、跳躍鍵、按上鍵進門
- 驗收：在有梯子的地圖上能用鍵盤上下；跳躍弧線高度符合 9.5/0.6

### S4 — 移除播放軌跡

- `/trace` 與播放鍵降級為「診斷工具」（可保留，但不是主要介面）
- 畫面預設是互動模式

---

## 6. 明確不做

| 不做 | 理由 |
|---|---|
| 物理搬到前端 JS | 兩份實作必然分歧；且已驗證的 parity 修正會失效 |
| 粒子、技能、介面全系統 | 對「能不能走」這個目標無影響 |
| 改 NX 格式或存檔路徑 | 風險最高，不在目標內 |
| 逐幀即時迴圈的**時間源** | 客戶端用 wall-clock；我們用**呼叫次數**驅動，因為要的是可重現的結論，不是即時感 |

---

## 7. 這一輪已完成的物理修正（前置工作）

S1 之前必須先讓物理可信，否則互動了也只是一個準的錯誤。

| 缺陷 | 狀態 | 驗證 |
|---|---|---|
| `FindWall` 取牆方向相反 → 穿牆瞬移 | ✅ 已修 | 探針 7/7 與參考一致；真實地圖終點 -48 → -408，大跳躍 4 → 0 |
| `Angle256` 缺 `Math.Round` → Y 漂移 | ✅ 已修 | 910020202 / 120000202 漂移 +2~4px → +0.00px |

驗收：`acceptance.mjs` 28/29（唯一失敗 `save verifies` 為 `WzSaveService.cs:411` 拋錯，
該檔本輪未改動，待確認是否既有問題）。

---

## 8. 這一輪學到的、必須延續的規則

1. **對標的是功能，不是公式。** 公式一致只代表算得準，不代表做對了事。
2. **每個常數都要能指到原始碼行號。** 指不到的，預設是憑空發明。
3. **架構決定了可行性。** 「每幀重建」這一件事，就決定了不可能有互動——
   在追細節之前，先確認架構支撐得起目標。
4. **驗收要看結果。** paintedPct 會說謊，要看不同顏色數；要真人按鍵看角色走。
5. **被鎖住 / 沒開檔 / 重複行程，先確認是誰在擋**，不要寫成程式壞掉。