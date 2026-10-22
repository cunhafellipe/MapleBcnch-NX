# 工作日誌 2026-10-13（第二段）— 指揮模式

## 摘要

上一段我記的是「我怎麼搞砸的」。這一段記的是**換成指揮模式之後怎麼做**。

差別很具體：**差距清單不再來自我的記憶，而是 omp 帶著 LSP 讀完兩棵程式樹比對出來的。**

---

## 一、指揮模式的三個角色

| 角色 | 誰 | 產出 |
|---|---|---|
| 規劃 | omp（實讀兩棵樹） | 排序後的差距清單，每項標 file:line + 工作量 |
| 執行 | omp（一次一項任務） | 程式碼 + 自稱的驗證 |
| 審核 | **我** | 獨立重驗，不看自評、不看截圖 |

**審核不是讀它的報告。** 是自己開檔、自己 `git diff`、自己跑探針。

---

## 二、差距清單（omp 實讀，非我臆測）

它先糾正了我的前提——我給它的任務描述說「B 只播折線」，錯了。

| # | 差距 | A（規格） | B（要改的） | 工作量 |
|---|---|---|---|---|
| 1 | 角色是火柴人 vs WZ 貼圖合成 | `MapleCharacter.cs:200,272` | `walkview.js:534` | L |
| 2 | **沒有自主遊戲迴圈** | `RenderFormDraw.cs:195` accumulator | 每次 HTTP 才走一幀 | L |
| 3 | 怪物 AI/HP/死亡沒接進 session | `Mob.cs:158,806` | `MobPatrol.cs` 存在但沒接 | M |
| 4 | 鏡頭不跟隨 | `MapleCharacter.cs:749` | `walkview.js:382` | M |
| 5 | NPC/portal/reactor 沒模擬 | `MapleMap.cs:273` | 只讀 portal 當出生點 | M |
| 6 | 兩個真缺陷 | — | 見下 | S |
| 7 | 沒有 Z 深度排序 | `SpriteEngine.cs:463` | 平面依序畫 | M |

**並糾正我一個事實錯誤**：我原本說物理常數只是「大致對齊」，它逐字核對後確認**完全匹配**（`0.6f`/`9.5f`/`8`、`1.5f` 加速/`2.5f` 上限/`0.25f` 減速、邊界 `EdgeMargin=20`、sentinel `99999`）。出生點邏輯也確認正確（portal `Type==0` + 2px 探針）。

### 最重要的結構差異

> A 擁有它的世界；B 借用一次計算。

A 的身體活在算繪行程裡，60Hz 自己跳。A 的地圖是「載入然後你在裡面」。
B 的身體活在 HTTP 之間**凍結著的 session**。分頁睡著世界就睡著。

**所以「像折線播放器」不是物理被重播，是時鐘長在 socket 另一端。**
其餘每個結構性差距都是同一個根的下游。

---

## 三、TASK 1 — 修兩個缺陷

### 缺陷 1：session 從不釋放

`livewalk.js:101` 打 `POST /session/{id}/end`，但只有 `MapDelete("/session/{id}")`（`SimulatorEndpoints.cs:147`）。

omp 沒有只改一行，它用**路由歧異性**證明：

- `PUT /session/NOPE` → **405**（路由存在，動詞不對）
- `PUT /session/NOPE/end` → **404**（這個路由從來不存在）
- 修後 `stop()` 實際發出 `DELETE /api/simulate/session/TESTID`
- 舊碼實測 → `404` 被 `.catch(() => {})` 吞掉 = 洩漏重現

### 缺陷 2：批次覆蓋即時

`simulator.js` 的 `loadWalk()` 排在 `live.start()` 之後，覆蓋 `viewer.ticks`、歸零 `frame`。

修法：`releaseWalk()` + 明確的單一 writer 優先權。實測交接：`ticks 5 frame 0 playing true` → `ticks 0 frame 0 playing false` → `ticks 2 frame 1`。

**審核結論：成立。** 我自己開檔確認 `livewalk.js:118` 確實改成 `del('/api/simulate/session/${id}')`，`api.js:115` 確實有 `const del = (url) => request('DELETE', url)`。

### omp 糾正我三個錯前提

這比它修的 bug 更有價值：

1. **`MapClient.cs` 存在**，但 web 端的 helper 在 `api.js:115`，不在那裡。我指錯地方。
2. **有 `Sweep()` idle 回收**（`MapSimSessionStore.cs:314`）→ 我寫的「洩漏到伺服器結束」是**錯的**，實際有上限。
3. **`MapleMcp/SessionTools.cs:92` 早就用對 DELETE** → 錯的只有 web 客戶端，MCP 路徑一直是對的。

**教訓**：我給的「已驗證」描述本身需要被質疑。我說「端點 4 條」時是對的，但說「洩漏到伺服器結束」時是憑印象。

---

## 四、TASK 2 — 鏡頭跟隨

### 審核：逐行對照通過

| 參考 `MapleCharacter.cs` | 本專案 `walkview.js` | 判定 |
|---|---|---|
| `:749` `DestX = DisplaySize.X/2 - X + Camera.X` | `:537` `destX = half.x - x + cam.x` | ✅ |
| `:750-751` `if (Abs>1) Camera.X -= DestX*(12f/800f)` | `:538` `if (Abs>1) cam.x -= destX*(12/800)` | ✅ 係數精確 |
| `:753` `DestY = DisplaySize.Y/2 + (OffsetY+60) - Y` | `:543` `destY = half.y + 60 - y` | ✅ 保留 `+60` |
| `:754-755` `if(Abs>1) Camera.Y -= 0.01f*(DestY+Camera.Y)` | `:544` 同 | ✅ |
| `:757-764` 四道 clamp | `rectify()` | ✅ |
| `:766-767` 小於視窗時置中 | `rectify()` | ✅ |

**唯一改動且被要求聲明**：固定 1024×768（`MapleMap.cs:42`）→ canvas 自身半徑。它自己算了誤差佐證不變形：參考穩態落後比 0.3255，實測 0.3220。

**它保留而不整理的**：參考的軸向不對稱（水平測 `DestX`、垂直測 `DestY + Camera.Y`）。

**它自己抓到的一個測試錯誤**：第一版測試斷言「X 軸可能小於視窗」而通過是**空洞的**——因為 fit-to-width 保證 X 永遠不窄於視窗。它把測試移到 Y 軸。這是好的自我糾正。

**它誠實標記未驗**：沒有真 WZ 地圖（全用合成幾何）、`/api/simulate/geometry` 的欄位形狀是**讀 DTO 推測**的、0.015/0.01 與幀率相依（與參考相同）。

---

## 五、執行順序

1. ✅ 修兩個缺陷
2. ✅ 鏡頭跟隨
3. 🔄 Z 深度排序
4. 怪物接進 session
5. 時鐘搬到伺服器（自主迴圈，真正的根）
6. WZ 角色貼圖

每發我獨立審核。

---

## 四之二、TASK 3 — Z 深度排序（第一次失敗，重派後通過）

### 失敗紀錄

第一次派出去，**600 秒全花在讀資料形狀，沒寫任何程式碼**。0 個 Z 常數、無 `sort`。

它追的東西：`page` 到不到瀏覽器、`upperFoothold` 是什麼、`MapGeometryLadderDto` 的欄位。

**原因是我委託寫得鬆**：任務說「複製參考算術」但沒說「找不到欄位怎麼辦」。它於是無限探索。

修法：加 **SCOPE LIMIT** 段落，明寫「前半段預算內必須動手」、「欄位缺失就用替代值並在註解說明，不要卡住」。第二次 402 秒完成。

### 審核：Z 算式對照參考

| 項目 | 參考 | 本專案 | 判定 |
|---|---|---|---|
| 磁磚 | `MapTile.cs:33` `Layer*100000 + wzZ + 1000` | `:54` `Z_TILE` | ✅ |
| 物件 | `MapObj.cs:82,101` | `:55` `Z_OBJ` | ✅ |
| 角色 | `MapleCharacter.cs:561` `FH.Z*100000 + 60000` | `:56` `Z_CHAR` | ✅ **取腳下的 foothold** |
| 梯子 | `MapleCharacter.cs:587` `Page*100000 + 60000` | `:57` `Z_LADDER` | ✅ |
| Portal | `MapPortal.cs:107` 固定 `1000000` | `:62` `Z_MARKER` | ✅ |

**關鍵細節**：參考是**二分插入**不是排序（`SpriteEngine.cs:466-478`），跨越 `z <= 新來者` 的項目，所以**同深度時角色畫在前面**。它照抄了這個不對稱。

### 效能選擇

**每張地圖排一次，不是每幀**。`buildDrawList()` 產生 Z 遞增的 `this.scene`，`draw()` 只做 O(n) 走訪把角色插入 `charZ`。

實測 20k 段 × 60 幀：每幀重排 1.35ms vs 緩存走訪 0.11ms（**12 倍差距**），預算 16.67ms。理由正確——地磚/物件不動，只有角色和軌跡動。

### 它推翻自己的測試

第一版 harness 用 **foothold 當遮擋物**，那是錯的——foothold 的 Z 只是層號，永遠遮不住任何東西。真正的遮擋物是磁磚和物件。它自己發現並改掉。

這是好的：它的第一個測試**通過了但是空洞的**，它沒接受這個結果。

### 它順手抓到一個真 bug

`draw()` 讀 `g.ladders` / `g.portals` / `g.bounds`（小寫），但 `load()` 正規化成 `Ladders`/`Portals`/`Bounds`（大寫）。所以 `g.ladders` 是 `undefined`，**梯子迴圈每幀都丟例外**。原註解標的那段是死碼。

**審核驗證**：我 grep 到的第 877 行小寫是在**註解裡**（它把這件事寫進註解留給後人），程式碼本身用 `g.Bounds` 正確。

### 未驗證（它自己標記的）

- **沒有瀏覽器**：Chromium 在它環境啟不來（`omp.browser.headless failed exit=21`），用自製光柵化 2D context 代替，抗鋸齒/縮放/字體都是近似
- **沒有真地圖**：`/simulate/geometry` 沒打到活的封存
- **梯子的 `page` 拿不到**：DTO 只有 `x/top/bottom/upperFoothold`，每條繩都是 page 0。`characterZ()` 讀 `l?.page ?? 0`，DTO 哪天帶了就會自動生效

---

## 五之二、三發之後的真實狀態

建置 0 錯誤 0 警告。三個 JS 檔 `node --check` 全過。

```
walkview.js    1090 行   (+586/-17 累積)
livewalk.js     255 行
simulator.js    466 行   (+52)
```

**但仍然沒有一項在真瀏覽器裡跑過。** 全部是合成幾何 + 靜態檢查。

## 五之三、TASK 4 — 怪物接進 session

### 審核抓到幽靈問題（第三次）

第一次驗證回 `mobs=0`。**原因是我測在舊行程上**——`:5497` 是四發之前啟動的 PID 33208，載著 TASK 4 之前的 DLL。

這是今天第三次同樣的錯（第一次是 PID 33652 的 3.1GB 幽靈）。

殺掉、乾淨重建、啟動 `:5498`，重測：

```
=== 乾淨 build 驗證怪物 (f1/Map/Map2/261010002.img) ===
  step1 mobs=29
```

### 審核第二次抓到：我的測試方式錯了

29 隻確認存在，但**四幀完全沒動**。查程式碼後確認是**我的測試錯，不是程式錯**：

`MapSimSessionStore.cs:409-424` 有明確的設計理由註解——

> 怪物與身體在同一幀、同一把鎖、同一條規則下推進：一次呼叫一次 tick，不用牆鐘。
> 它們**不是**獨立 loop，它們因為角色前進而前進，所以重播同一組輸入會重播同一組怪物。
> 用計時器跑怪物會把身體放到同一段重播後面的幀永遠產生的位置。

四幀不動是因為參考的 `Random.Next(200)` 轉向機率——**200 幀才有機率**。

### 審核驗證：怪物真的在走

```
=== 200 幀 ===
  角色+怪物數 @幀: [(0, -20.5, 29), (50, 104.5, 29), (100, 228.0, 29), (199, 473.5, 29)]

  有動的怪物: 3 / 3
    3110302  範圍 -756.0..679.9 (994 個不同位置)
    3110300  範圍 -635.0..373.0 (316 個不同位置)
    3110303  範圍 -754.0..712.0 (599 個不同位置)
```

**三隻全部來回巡邏，角色同時前進。** 這是走 MCP/session 真路徑，不是它的截圖。

### omp 從參考專案挖出的兩個真 bug

1. **`FlySpeed` 是壞的**（`Mob.cs:154-155`）——`GetInt("flySpeed")/100` 少了 `(float)` 轉型，整數除法讓 0-99 的值截斷成 0，所以 `FlySpeed` 永遠剛好是 2。它沒照抄這個 bug（Fly 根本沒實作）。
2. **`Mob.cs:799` 只在 `if (HP > 0)` 裡扣 HP**——所以怪物被打一下就翻成 `Die`，而 HP 是 `Int64`，2,000,000 扣最大 599,999 永遠扣不到零。**怪物實質上打不死。**

**誠實標記未驗**：只有一張地圖有怪物、沒有戰鬥（沒人呼叫 `TakeHit()`）、沒拍到怪物被前景磁磚遮擋、拒絕跑完整驗收因為那會寫入 711MB 封存。

### 它自己發現的舊問題

`MapEditorService.SimulateWalk.cs:601-620` 沒傳 `spawnX`，所以 `/simulate/mob` 的怪物仍從 foothold 左端出發。它的 `spawnX` 預設值讓舊端點繼續工作，但沒修根因——在「不要破壞 `/simulate/mob`」範圍外。

## 五之四、TASK 5 — 時鐘搬到伺服器（根因）

### 這是最大的結構差距

> A 擁有它的世界；B 借用一次計算。

之前 `Step()` 是唯一變更狀態的方法，只能從 HTTP 呼叫進來。關掉分頁角色就停在半步。

### 審核：我自己測的，不看它的截圖

乾淨重建、啟動 `:5499`，**推一次「右」之後完全不再推 frame**：

```
=== 時鐘自己跑（不再推 frame）===
  t=+0.00s  x=-315.00  frame=0    speed=0.00
  t=+0.70s  x=-212.50  frame=42   speed=2.50
  t=+1.40s  x=-112.51  frame=83   speed=2.50
  t=+2.11s  x=-10.01   frame=125  speed=2.50
  t=+2.83s  x=97.48    frame=169  speed=2.50
```

角色**自己走**。這是核心主張，現在有實測數字。

**幀率**：179 幀 / 3.00 秒 = **59.6 fps**（目標 60，`FixedUpdateDelta = 0.016666668f`）。

**回歸**：`sim-walk.mjs` **25/25 全過**（比之前多 2 項，因為多了 input 端點的檢查）。

### 設計選擇

沿用專案**既有的**先例：`MemoryPressureService : BackgroundService`。沒有發明新抽象。

- `SimClockService`（`Program.cs:138`）singleton + hosted
- `MapSimSessionStore.Tick()`（`:359`）每個 session 一個固定步
- `IdleTimeoutMs = 120_000`（`:293`）——閒置回收
- `ManualHoldMs = 250`（`:313`）——手動推幀的租約，避免時鐘和手動互相打架

### 它抓到並修掉的 bug

**`Hold()` 不更新閒置計時器**——壓力測試時發現的：每 120ms 呼叫一次會讓 session 永遠不被回收。

### 它自己推翻的計算

第一次量幀率算出 668 幀 / 2 秒 = 5.5 倍過快。它沒有直接改程式，而是**檢查自己的探針**——發現 poll 每次要 3 秒、總共 34 秒，它把「假設的 3 秒」當分母了。改用真實時間戳後得到 56.8 fps。

**這是對的**：不動被測物去配合壞的量測。

### 誠實標記

超時在 600 秒被砍，**閒置回收的 120 秒實測沒有跑完**。程式碼在（`IdleTimeoutMs = 120_000`、`SweepIdle` 邏輯），但沒有親眼看到 120 秒後 session 消失。

## 五之五、TASK 6 — 只會自己跳的 session 永遠不會被回收

### 這個 bug 是我審核 TASK 5 時抓到的

omp 在 TASK 5 裡**自己標記**了這個風險（「這是把有界限的洩漏變成無界限的唯一途徑」），但它超時沒跑完實測。我補跑，就中了。

```
=== 130 秒實測（只讀不推幀）===
  +  8s  alive  frame=477
  + 64s  alive  frame=3508
  +128s  alive  frame=7042      ← 從沒被回收
```

### 根因

`MapSimSessionStore.cs:775` — `Peek()` 會刷新 `LastTouchedMs`。

**所以會讀的客戶端永遠不會被回收。** 而這恰恰是自我矛盾的：TASK 5 的整個重點是「角色因為時間流逝而移動，不是因為有人看」，但「看」仍然讓它永生。開著的瀏覽器分頁會整晚持有 session 和它解析的地圖。

omp 的註解說得更狠：**兩個性質互相抵消——客戶端看得越仔細，它的 session 越確定不會被回收。**

### 修法與設計

`Peek()` 不再更新租約。租約只在 `Step()` 和 `Hold()` 移動。

**代價被明寫出來**：只觀察不操作的客戶端，超過 `IdleTimeoutMs` 會在觀看中途失去 session、下一次 poll 拿到 404。註解辯護這是對的選擇，並指出瀏覽器客戶端不會踩到（`livewalk.js` 每個 animation frame 都推一幀，那是 steer）。

**`IdleTimeoutMs` 保持 120 秒沒有縮短**——我在委託裡明令不准為了讓測試快而改它。

### 審核：我自己等真 120 秒

乾淨重建 `:5501`，只讀不推幀：

```
  session e0aac550463b4e 建立於 t=0
    + 15s  alive  frame=883
    ...
    +105s  alive  frame=6148
    +120s  ★★ 已被回收 (HTTP 404)
  結論: 按 IdleTimeout 回收 ✓  實耗 120 秒
```

### 回歸 16/16

~~`sim-walk.mjs` 在這台機器上跑不起來（`stdin is not a tty`／`stdout is not a tty`，pty 也不解決）~~

**更正（2026-10-13 晚）**：這是我的錯誤結論。`sim-walk.mjs` **從來沒壞過**。真正的問題是我呼叫它的方式——`| tail` 管線讓 node 覺得 stdout 不是終端。改成重導向到檔案就正常：

```bash
WZ_SERVER=... node tools/probe/sim-walk.mjs .simdata/Map.nx 910020202 > out.txt 2>&1
# EXIT=0，all checks passed
```

教訓：**我把「我呼叫工具的方式有問題」誤判成「工具壞了」**，還寫進日誌當成事實。改用 MCP 驗 16 項（當時有效的做法），但原因記錯了。：開啟、位置、foothold、按住右、speed、frame 計數、facing、轉身、變號、跳躍、離地、上升、落下、落地、close、關閉後拒絕 step——**全過**。

## 五之六、TASK 7 — WZ 角色貼圖

### 最重要的發現：這個封存裡沒有角色圖

omp 主動探了封存，回報 **NX 封存根目錄沒有 `Character` image**。我獨立驗證：

```
/api/animation?path=Character/00002000.img/stand1        → 404
/api/animation?path=Character/00002001.img/stand1        → 404
/api/animation?path=Character/00002000.img               → 404

對照組：
/api/animation?path=f1/Map/Map9/910020202.img/0 → OK 但 width:0 frames:[]
```

**連地圖美術都是空的。** `.simdata/Map.nx` 是純幾何檔，沒有美術。

所以「畫出真角色」在這個封存上**物理上不可能**——不是程式沒寫，是資料不存在。

### 寫了什麼

- `MapleBench/wwwroot/js/avatar.js`（322 行，新檔）— 走既有的 `/api/animation` 端點，`BODY_IMG = 'Character/00002000.img'`，`:251` 有 `00002001.img` 的 fallback（照 `MapleCharacter.cs:199-202`）
- `walkview.js` 接到 `drawAvatar()` / `stepAvatar()`
- HUD 會**明說**現在畫的是真貼圖還是火柴人 fallback

### 它抓到的真 bug

**閒置時動畫仍在跑**。它第一版是 idle 也播 `stand1`，自己發現並修掉。

### 它推翻自己四次的測試

1. 用 `null` 當第一個 loop 的參數（死碼）
2. 量「場景墨水」而不是「角色墨水」
3. 兩個斷言本身是錯的（flip 目的地公式、40ms hold 計數 off-by-one）
4. **合成幀是實心矩形，所以鏡像前後像素完全相同**——量不出翻轉。最後改用不對稱幀量質心才測對

第 4 條特別值得記：**測試通過是因為測不出東西，不是因為東西正確。**

### 回歸 11/11 + 29 隻怪物照常

```
  PASS  open / 有位置 / 在 foothold / 按住右會動 / frame=30 / 轉身
  PASS  跳躍 / 上升 / 落下 / 落地 / close
  11/11
  怪物仍回報: 29
```

---

## 六、七發總結

| 發 | 任務 | 審核結果 |
|---|---|---|
| 1 | 修兩個缺陷 | ✅ 路由歧異性驗證（405 vs 404） |
| 2 | 鏡頭跟隨 | ✅ 逐行對照 `MapleCharacter.cs:749-767` |
| 3 | Z 深度排序 | ✅ Z 算式對照參考；它推翻自己的空洞測試 |
| 4 | 怪物接進 session | ✅ 200 幀實測，三隻全部巡邏 |
| 5 | 時鐘搬到伺服器 | ⚠️ 59.6 fps + 25/25，但審核抓到 TASK 6 的 bug |
| 6 | 修正閒置回收 | ✅ 120 秒整回收 + 16/16 |
| 7 | WZ 角色貼圖 | ✅ 程式完成（**原因找錯了**，見 TASK 8） |
| 8 | **NX Canvas 解碼** | ✅ 21×31 角色 + 90×60 地磚真像素 |
| 9C | **角色上畫面** | ✅ `placement` NaN 修好，角色可見 |
| 16a | **即時輸入** | ✅ 按住會走、放開會停、動作跟狀態切 |
| 17 | **組合角色** | ✅ 頭/身/手臂，低 47px 已修 |
| 18 | **Slot 仲裁** | ✅ z 76/51/45、mask 1/1/2、prio 150/150/149 |

**最終狀態**：一顆活的地圖。角色因為時間流逝而移動，不是因为有人推幀。

### 仍然沒做到

- **這個封存沒有美術**——`.simdata/Map.nx` 是純幾何檔。角色貼圖程式已完成（`avatar.js` 322 行），但沒有 `Character/*.img` 與 `Map/Tile/*` 就畫不出來。**要看到真角色需要一個完整 WZ 封存**，這是資料限制不是程式問題。
- NPC、portal 使用、reactor 沒模擬（差距 #5）。
- 鏡頭、Z 排序、怪物、角色貼圖**都沒有視覺驗證過**——全是靜態檢查 + 合成幾何。沒有美術就算跑起來也看不出差異，所以這兩件事綁在一起。

---

## 五之七、TASK 8 — NX Canvas 解碼（真正的瓶頸）

### 之前為什麼畫面是空的

節點樹讀得出來，但**每一個 canvas 都回空**：

```
/api/node/facts?path=f2/00002000.img/stand1/0/body  → applicable:false, facts:{}
/api/animation?path=f2/00002000.img/stand1        → width:0 frames:[]
```

地圖磁磚也一樣。**這不是角色專屬問題，是 NX 的像素從來沒被解出來過。**

### 審核：我用 MPSHS 的真檔驗

乾淨重建 `:5504`，開 `Map.nx` + `Character.nx`：

```
facts body: {"applicable": true, "facts": {"...body": {
   "canvas": true, "width": 21, "height": 31,
   "originX": 16, "originY": 31, "format": "Format2", "layer": "body"}}}

animation stand1: isAnimation=true  width=23 height=41 anchor=(18,31) totalMs=1500
  frame 0: delay=500
    layers: [ arm  10×19 origin(5,9) | body 21×31 origin(16,31) ]

tile ThemeGL.img/bsc/0: canvas=true 90×60
```

**`/api/canvas` 吐出真 PNG**：`89504e470d0a1a0a` 是真的 magic。

用視覺確認不是雜訊：

- **body 21×31**：435/651 不透明像素、22 色 → 深褐輪廓 + 米白軀幹 + 橙褐手臂
- **tile 90×60**：5400/5400 不透明、24 色 → **石板地磚紋理**，橫向錯縫排列

### omp 反過來的資料格式（對照原始 bytes）

| 屬性 | 住在哪 | 值 |
|---|---|---|
| `delay` | **幀容器**（`stand1/0`），不是 canvas | 500 |
| `origin` | canvas | 16, 31 |
| `z` | canvas | 字串 `"body"` |
| `group` | canvas | — |

**NX 階層是四層**：`img → action → frame → canvas`（`MapleCharacter.cs` 讀 `delay` 在第三層，確認）。

`z` 的語意：**值越大越靠後，先畫**（從 `Base/zmap.img` 取得分層表）。

### 它抓到並修掉的 delay bug

回報 100ms，封存裡其實是 **500ms**——delay 讀錯層。它從原始 bytes 比對出來的。

### 一次程式錯誤（自己發現並修）

`ConditionalWeakTable` 沒有 `Values`——它把分層表改成單一全域。

### 我第一次測錯節點

我拿 `f1/Map/Map9/910020202.img/0` 當 canvas 測，回空——**那是 `{info, obj, tile}` 容器不是 canvas**。真正的磁磚在 `f1/Tile/ThemeGL.img/bsc/0`。

**我又一次用錯節點下了「還沒解碼」的結論**，差點重演前兩次。好在改抓了 `ThemeGL` 才看到 90×60。

---

## 六、TASK 9C — `placement()` NaN：角色終於畫出來了

### 我自己找到的 bug

```
avatar.js:390   placement(x, y, dir)          ← 三個參數
walkview.js:1318  avatar.placement(cur)        ← 只給一個 tick
```

`x + ox` 把物件字串化、`y + oy` 是 NaN → `drawImage` 什麼都不畫。
頁面上量到的實際值：

```
placement → { x: "[object Object]-21", y: null, w: 27, h: 41 }
```

**後端完全正常**：`/api/canvas` 回 200、`naturalWidth 27x32 / 12x17` 真 PNG。
就是少了兩個參數，整段繪製被跳過。

### 我自己的驗收腳本也抓出一個假陽性

`verify-char.mjs` 報 `walking 109050001` 但截圖是**檔案總覽**。
原因：`sim.open()` 只載資料，沒有切換可見 section——模擬器在背景跑，
截圖拍的是 Mob 頁。

修法（已寫進腳本）：

```js
await app.sections.open('simulator');   // 這才會切換畫面
```

並加了一道斷言：截圖前檢查 `sections.active === 'simulator'`，
不是就報「無效證據」。

> **這代表我先前判「地圖美術已上畫面」的那張截圖也不可信。**
> 重新驗證後地圖美術確實有畫，但那次的證據是無效的。

### 驗證結果

```
section=simulator   visibleCanvases=["walkview-canvas"]
viewer: hasPart=true frames=4 bitmaps=8   mapPath=f1/Map/Map1/109050001.img
```

截圖裡角色**看得到**，站在平台上，視覺判斷「幾乎落地但可能略浮」。

**狀態：角色已上畫面，接地還差一點。** 下一步是 TASK 11 的 origin 語意。

---

## 七、規格來源找到了：MPSH 的 C++ 原始碼

我今天一直在猜的三件事，`C:\MUWORK\GAME\MPSH\clean-client\src\` 裡都有答案。
594 檔 / 125,988 行的真實 v83 客戶端。

| 我猜錯的 | 真實規格 |
|---|---|
| 層順序「先後順序」 | `CharLook.cpp:175-179` **z 遞減 + stable_sort** |
| 所有層同一個 anchor | `CharLook.cpp:98` marker 是**肚臍**不是腳；臉部用 `getfacepos()` 獨立偏移 |
| 只做 delay | `Animation.cpp:58-102` 還有 `a0/a1` 透明度對、`z0/z1` 縮放對、`delay==0 → 100` |
| 沒做 slot 仲裁 | `CharLook.cpp:155-173` 64 slot 位元遮罩，**priority 小者勝** |

`MPSH\AGENTS.md` 開頭就寫著作者自己的教訓：
「症狀 → 先讀原始碼找出路徑 → 再下結論」、「『這個訊息看起來像 X』不等於『就是 X』」
——**正是我今天犯的錯**。

`ENVIRONMENTS.md` 更正過 AGENTS.md 的錯誤框架：
`MPSH\OpenStory.exe` 和 `MPSHS\OpenStory.exe` **SHA256 完全相同**，
兩個目錄的差異是被後來的備份快照造成的迷思。

### ROADMAP 已寫好：`tools/probe/ROADMAP.md`

R1 接地 → R2 層順序/slot → R3 動作集 → R4 怪物美術 →
R5 NPC → R6 小地圖 → R7 天候 → R8 音效 → R9（遊戲功能，停用）

任務檔已寫好排隊：TASK11-LAYERS / TASK12-ACTIONS / TASK13-MOBS

---

## 八、TASK 16 — 即時輸入通了（使用者最原始的需求）

### 真正的 bug 只有一個：LastAction 寫死

`CharacterPhysics.cs:219` 原本是 `LastAction = "walk"` 無條件賦值。
所以**不管按什麼鍵，action 永遠是 walk** — 這正是我量到七個場景
`action=walk` 全一樣的原因。

改成依速度判斷：

```csharp
LastAction = Math.Abs(_speed) > 0.01f ? "walk" : "stand";
```

### 我獨立驗證（乾淨重建 :5507，真封存）

```
新 session 起點 x=-90.00

A) 每幀送輸入 /frame {right:true}
   x: −66.01 → −41.01 → −18.01 → +6.99     單調增加 ✅

B) Hold(right) 後只用 GET 讀，讓 clock 推進 ← 瀏覽器實際走的路
   1.2 秒內: −76.0 → 26.98 → 50.73 → 54.5
   單調增加 ✅  總位移 130.5 px  action='walk'  speed=2.50

放開 0.8 秒後:  action='stand'  speed=0.00  ✅ 停住
```

**兩條路都通。放開會停、按住會走、動作跟著狀態切換。**

### 我第三次自己製造假陽性

第一輪測「按住不動」，原因是**我密集 POST `/frame`**，
每幀都把 `MapSimSessionStore.cs:802` 的
`session.ManualUntilMs = now + ManualHoldMs(250)` 重設，
clock 永遠被擋住，`HeldInput` 那條路根本沒跑到。

> 程式沒有 bug，是我的測試方式觸發了手動優先機制。
> **第三次了。** 前兩次是推測代替驗證，這次是測試方法干擾了被測系統。

方法修正：驗 level-triggered 行為時**只能 Hold 一次 + GET 讀取**，
不能用密集 POST，這會把 clock 排除掉。

### 另外兩個端點語意（都正確，只是我原本搞混）

| 端點 | 語意 | 用途 |
|---|---|---|
| `/session/{id}/frame` | **單幀步進**，傳什麼走什麼 | MCP、批次 |
| `/session/{id}/input` | **持續按住**（level） | 瀏覽器 ✅ |

### TASK 11 超時，但結論有用

它的數字（我尚未獨立複驗，標記為待驗）：
> reference feet at y+0, ours at y+22, head 47px low

單層 body 接地正確（`feetΔ=0`），**組合**不對 — 頭/臉/手臂沒有各自的
anchor 偏移。它還抓到 **NX 的 `z` 是字串不是整數**。

look composer 寫到一半超時，**全部沒落盤**（第三次寫程式階段超時）。

---

## 九、TASK 17 — 組合角色：頭/身體/手臂，頭不再低 47 像素

### 它找到的公式（我已獨立驗證輸出）

```
head = body.neck − head.neck = (−4,−32) − (0,15) = (−4,−47)
```

**47 像素確認為真。** 根因是 `Body.cpp` 用 `get_head_position`，
我們把所有層壓在同一個 anchor。

還抓到：**NX 的 `z` 是字串不是整數** — 先前的 probe 當整數排序，
順序是亂的。

### 新的 look composer 端點

```
GET /api/simulate/look?fileId=f2&stance=stand1&frame=0
```

（**注意路徑**：`/api/simulate/look`，不是 `/api/look` — 我第一次打錯，
404 浪費了一次。）

實測回傳三個 part、每幀獨立 anchor：

```
frame 0:  bodyAnchor (0,0)   headAnchor (0,−15)
          faceAnchor (−4,−20) hairAnchor (−4,−20)
  arm   f2/00002000.img/stand1/0/arm    10×19  origin(5,9)   shift(13,1)
  body  f2/00002000.img/stand1/0/body   21×31  origin(16,31) shift(8,21)
  head  f2/00012000.img/stand1/0/head   39×35  origin(19,17) shift(0,−15)
  bounds: (18,2) → (58,83)
frameCount: 3   delay: 100
```

**head 來自 `00012000.img`** — 另一個角色檔案，證明 look composer
真的在跨節點組合，不是在同一個 img 裡瞎找。

### 視覺確認

截圖裡角色有：橙褐色頭髮、臉部、灰白軀幹、手臂、腿。
**頭在軀幹正上方，沒有低 47 像素。** HUD 也從 `real avatar (walk1/0)`
變成 `real avatar (stand1/0)` — 動作跟著狀態切了。

### 但 slot 仲裁等於沒做

```
"slots": "", "slotBits": 0, "priority": 32767   ← 三個 part 全一樣
```

`AvatarLayerTables.cs` 建了但沒接上實際資料。
目前三個 part 的 `z` 都是 0，靠 collection order 而非 `CharLook` 的
stable_sort。**對單角色視覺無影響，但一旦有裝備（帽子/衣服/褲子）
就會出錯。** 留給後續。

### 我的驗收腳本又抓到東西

`placement=BAD` 全紅 — 新的 look 路徑沒回填 `avatar.part`，
我的腳本讀的是舊欄位。**腳本該跟著更新，但截圖證明畫面本身是對的。**

`mushroom` 場景現在誠實報錯：
`map-not-found:106020010 (have: 0,1,2,3,4,10000,...)` — 不再撒謊。

---

## 十、TASK 18 — Slot 仲裁真的在跑了

### 之前 vs 現在（同一個端點）

```
之前:  body z=0  slots=''  mask=0  prio=32767      ← 初始值，從未計算
現在:  body z=76 slots    mask=1  prio=150
       arm  z=51         mask=1  prio=150
       head z=45         mask=2  prio=149          ← 全部從 Base.nx 算出
```

排序嚴格遞減 **76 → 51 → 45**，符合 `std::stable_sort` 降冪。

### 爭議 slot 的實測證明

它自己構造了一個衝突：帽子和頭髮都佔 `H1`（bit 1024），
帽子 priority 140 < 頭髮 148 → `strongest[H1]=140 < 148` → **頭髮被隱藏**。
完全符合 `CharLook.cpp:155-173`。

### 修掉的三個靜默 bug

`WzValue` 和 wrapper 型別比較時**全部落到 fallback**，
不報錯只是回傳預設值：

- `z` 靠 fallback 拿到 part 名稱才湊巧對
- `ZNameOf` 同樣靜默 fallback
- `codes` 全空

**這種 bug 不會拋例外，只會安靜地給你錯的答案。**

### 視覺確認

`parts=6`（不再是3）。截圖裡角色有：頭、臉、頭髮、軀幹、手臂、腿，
**臉在頭的正確位置**。8/8 場景 `placement=OK`。

### 我第四次犯同一個分類錯誤

`feetΔ=-71`：我拿 look 回傳的**局部座標** `bounds.maxY=83`
去減**世界座標** `tick.y=154`。兩個不同座標系，不能相減。

- 第1次：把 `rect.h` 當可見高度 → 幽靈的 9px
- 第2次：TASK 11 的「腳差 22px」（未驗證）
- 第3次：47px（真的，但來源不同）
- 第4次：`-71`

> **不對不同座標系的數字做算術。** 這是我今天最反覆的錯。

---

## 十一、命令






```bash
# 派一發給 omp
python "C:/Users/e7896/AppData/Local/hermes/bin/omp-bridge.py" \
  "Read tools/probe/TASKn-*.md and execute it exactly as written." \
  --cwd "C:/MUWORK/GAME/MAPLESOTRY/wz工具/MapleBcnch-NX-audit"
```

任務檔放在 workdir **內部**並在 prompt 點名——這是 omp-bridge skill 的教訓：檔案放在同層的兄弟目錄會讓 agent 讀錯檔。

```bash
# 互動迴圈驗收（重導向到檔案，不要接管線）
WZ_SERVER="http://127.0.0.1:5502" \
  node tools/probe/sim-walk.mjs ".simdata/Map.nx" 910020202 > out.txt 2>&1
echo "EXIT=$?"; tail -3 out.txt
```

`| tail` 會讓 node 判斷 stdout 不是終端並拋 `stdin is not a tty`——**這是 shell 的問題，不是腳本的問題**，我曾因此誤判工具壞掉。

