# 2026-10-14 10:05 — 這才是模擬器：攻擊鏈與傳送鏈原本整條斷在外面

接續 [0910](2026-10-14-0910-mcp-render-tools.md)、[0916](2026-10-14-0916-back-layer-tiling-fix.md)（同一輪，不覆蓋）。

使用者問：**「你確定你是在做模擬器嗎？」**

答案：前兩輪不是。那兩輪做的是**渲染器**（把地圖和怪物畫成 PNG）。
本輪把焦點拉回模擬器本身，並且發現了一個比渲染嚴重得多的事實。

---

## 1. 先前的錯誤結論

前兩輪的日誌宣稱：

> 「怪物驗收 12/12 留在地圖、12/12 有被擊反應（`attackerRedirected: true`）」

這是**怪物 AI 的巡邏反應**，不是戰鬥。`wz_mob_patrol_batch` 回報的
`hitReacted` 指的是「被攻擊者的轉向/後退 AI 有跑」，跟玩家能不能造成傷害無關。

而「`wz_map_session_attack`」這個工具**從來不存在**。先前日誌列的工具清單是錯的。

實際清單（52 個）裡，與戰鬥有關的是 **0 個**：

```
wz_asset_transfer   ← 這個是「把檔案搬到別處」，不是攻擊
```

---

## 2. 斷點在哪：三層

### 第一層：後端邏輯是完整的，但從未被呼叫

`MapSimSessionStore.cs` 有完整的 `Swing()`：命中判定是**矩形重疊**
（`Box.At` 鏡像攻擊框 × 怪物當前影格的 `lt`/`rb`），傷害走 `DamageRoll`，
有 `MapSimDamageNumber`（值、座標、miss）。註解引的是 `Combat.cpp:662-679`、
`Animation.cpp:60-68`、`Rectangle::overlaps` —— 是照著客戶端 C++ 抄的。

**但 `Swing()` 的第一個條件就會擋掉所有呼叫：**

```csharp
AttackBox weapon = _combat.AttackBox(session.Weapon);
if (weapon.Box.IsEmpty)
    return;
```

而 `session.Weapon` **從未被賦值**。`MapSimStartRequest` 沒有 weapon 欄位，
`Start()` 沒有參數，`new MapSimSession()` 沒有設它 → 永遠 null →
`AttackBox(null)` 回空框 → **每一記揮擊都是無動作**。

這不是「功能沒寫」，是「寫了但永遠走不到」。

### 第二層：MCP 沒有 attack 參數

`SessionTools.Step()` 的參數是 `left/right/up/down/jump`。
`MapSimInput` 有 `Attack`，但 MCP 沒有暴露 → **即使後端修好，agent 也按不出攻擊鍵**。

### 第三層：傳送完全沒寫

`grep -n "Portal" MapSimSessionStore.cs` 只有 spawn portal 的讀取。
`pt=2` 的真門（`west00 → east00, map 40000`）**沒有任何一行處理**。
`MapSimFrame` 也沒有任何回報傳送的欄位。

---

## 3. 修了什麼

### 後端 `MapleBench/Services/Simulate/MapSimSessionStore.cs`

| 改動 | 內容 |
|---|---|
| `Start()` | 加 `string? weapon = null`，寫入 `session.Weapon` |
| `MapSimSession.Archive` | 記錄封存 id，供傳送找同檔兄弟地圖 |
| `Path`/`Map`/`Body` | `init` → `set`（傳送會中途替換這三個）|
| `Advance()` | 每幀算 `session.Map.FindPortal(body.X, body.Y)`；`Up` 且 `toMap != 999999999` 時觸發 `TakePortal` |
| `TakePortal()` | 新方法：載目的地 → 找 spawn portal → `FindBelow(x, y-2)` → 換地圖、換身體、重生怪 |
| `MapSimFrame.Portal` | 新欄位：**每幀**回報腳下的門（客戶端得先畫門才能走進去）|
| `MapSimFrame.Transfer` | 新欄位：**單幀事件**，下一幀清空（否則輪詢會永遠重複報到）|
| `PortalDto` / `MapTransferDto` | 新 record |
| `GET /debug/portal` | 診斷端點，回報傳送失敗原因 |

### MCP `MapleMcp/SessionTools.cs`

| 改動 | 內容 |
|---|---|
| `wz_map_session_open` | 加 `weapon` 參數 |
| `wz_map_session_step` | 加 `attack` 參數（邊緣觸發，與 jump 同）|

---

## 4. 驗收：完整迴路 0.2 秒

```
【1 載入】000040001.img  (-90,395)  26 隻怪
【2 走位】frame 2  相對距離 -71px
【3 戰鬥】傷害 [3, 2, 3]  hp 8->0  擊殺 @frame 50  (45幀 die1 動畫)
【4a 走到門】{"name":"west00","toName":"east00","toMap":40000,"type":2}
【4b 傳送】000040001.img -> 000040000.img  落點 (0,215)  新地圖 5 隻怪
```

傷害數字帶座標（怪物頭部位置），`miss=false`。
`stun=20`（受擊硬直，來自 `hit1` 動畫時長）。
`die1` 播 45 幀後 `dead=true`，怪物從地圖移除（26 → 25）。

**純 MCP 呼叫驗證**（非 curl）：

```
交戰: char_x=90 怪 x=19 hp=8
傷害數字: 2 / 2 / 3 / 3   座標 (6,371) (9,368)
frame 27: hp=0/8 dying=True
frame 51: 擊殺 #1
總結: 4 次命中, 1 次擊殺, 地圖 25/26 隻存活
```

---

## 5. 過程中的四個真 bug

### (1) `Map{N:00}` —— v83 的 map 根是一位數

我寫 `Map{toMap % 100:00}` 得到 `Map00`，實際 v83 是 `Map0`..`Map9`。
症狀是每次傳送都 `KeyNotFoundException: 'Map00' not found under 'f1/Map'`。

**這個 bug 靠 `/debug/portal` 端點一秒抓到。** 沒有那個端點，
症狀是「按了 Up 沒反應」——和「按鍵沒送進去」完全無法區分。

### (2) `Foot` 沒有 X/Y

`FootholdPhysics.Foot` 是線段（`X1,Y1,X2,Y2`），沒有單一 X/Y。
落地座標改從新 `CharacterPhysics.Body` 讀 —— 那才是客戶端會畫的位置。

### (3) 攻擊框 vs 怪物框的重疊條件

`barehands.img/stabO1` = `-64..-27`（37px 寬）。
鏡像後面左 = `+27..+64`。怪物 `0100100` 框 = `-18..+19`（37px 寬）。

**所以角色必須站在怪物左側 8~82 px 之間才打得到。**
測試時我反覆「揮擊不到」，其實是角色站錯位置 —— 幾何本來就對。

這條要記下來，否則下次測戰鬆會再懷疑程式：

```
攻擊框（面左）= body + 27 .. body + 64
怪物框       = mob  - 18 .. mob  + 19
重疊       ⟺ -82 ≤ (mob - body) ≤ -8
```

### (4) 某些怪永遠打不到 —— 不是 bug

40000 地圖的 `0002002` / `0002004` 在 `Mob.nx` 裡**根本不存在**
（`/api/children?path=f2/0002004.img` → 404）。
`MobFrame()` 回 null → `Swing()` continue。

v83 中文版地圖引用不存在的怪物是常見情況。這種怪不會被消滅，
因為它沒有影格就沒有框。**這是資料特性，不是模擬器缺陷。**

---

## 6. 兩個環境陷阱（都在本輪踩到）

### 建錯專案 = 假的「0 errors」

專案檔是 `MapleBench/MapleBenchX.csproj`，我前面幾次寫 `MapleBench/MapleBench.csproj`，
MSBuild 回 **MSB1009 專案檔不存在**，但我的過濾只抓 `error CS`，
於是印出「0 errors」。**連編譯都沒發生的建置，看起來跟成功一模一樣。**

### DLL 被鎖時 MSBuild 有時不報錯

後端程序還活著時，`bin/.../MapleBenchX.dll` 被鎖。
MSBuild 複製失敗會印 MSB3021，但也**有幾次完全不報、bin 保持舊檔**。
診斷方式：比對 `obj/` 與 `bin/` 的 mtime。
繞法：`dotnet build -o <新目錄>`（本輪用了 `bin/P2`）。

**「建置成功」不等於「新 DLL 生效」——永遠要驗 bin 的時間戳。**

---

## 7. 現在模擬器是什麼狀態

| 迴路 | 狀態 | 證據 |
|---|---|---|
| 載入地圖 | ✅ | 26 隻怪 + foothold + portal 全部載入 |
| 角色走/轉/跳 | ✅ | x 每幀增加、y 隨坡度變化、jump 邊緣觸發 |
| 怪物巡邏 | ✅ | 5 種怪 ID，轉向/路徑/受擊反應 |
| **攻擊 → 傷害數字** | ✅ **本輪修好** | 傷害 2/2/3/3，帶座標 |
| **怪物死亡** | ✅ **本輪修好** | hp→0, stun, die1 45幀, dead, 從地圖移除 |
| **地圖傳送** | ✅ **本輪修好** | 40001 → 40000, 落點 (0,215), 新怪生成 |

**這才是模擬器。** 前兩輪的 PNG 是附加價值，不是主體。

---

## 8. 動到的檔案

```
修改  MapleBench/Services/Simulate/MapSimSessionStore.cs   Weapon/Archive/Portal/Transfer/TakePortal
修改  MapleBench/Api/SimulatorEndpoints.cs                  MapSimStartRequest.Weapon, /debug/portal
修改  MapleMcp/SessionTools.cs                              session_open: weapon, session_step: attack
```

建置：`MapleBenchX.csproj` 0 錯誤、`MapleMcp.csproj` 0 錯誤。
**未 commit。**

後端 `:5594`（來自 `bin/P2/`，因為 `bin/Release` 的 DLL 被鎖）。
診斷端點 `/api/simulate/debug/portal` 刻意保留：它是「傳送失敗」與
「按鍵沒送到」唯一的區分手段。