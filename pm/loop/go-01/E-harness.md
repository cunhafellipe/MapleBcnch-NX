# E — 驗證工具：常駐形狀 suite 與 gate 時序根因

開於 2026-10-18。E 線兩個缺口的結論：**E1 修好了**，**E2 根因已量到，產品碼的修法不在我的寫入範圍，規格寫在下面第 3 節**。

---

## 1. E1 — `skillshape-verify.mjs`（常駐 66 技能形狀掃描）

檔案：`%LOCALAPPDATA%\hermes\cache\scratch\skillshape-verify.mjs`（新檔）

### 怎麼用

```
node %LOCALAPPDATA%\hermes\cache\scratch\skillshape-verify.mjs
  BASE=http://127.0.0.1:5591    預設就是這個
  ONLY=1121008,2301002          只跑指定技能，快速看
  MULTIMOB=0                    跳過 nearest-N 那一段
```

接進 gate（**gate.ps1 我沒有動**，需要總管加一行）：

```powershell
Run 'skillshape' { node "$S\skillshape-verify.mjs" }
```

放哪一行沒有差別，但建議放在 `ball-art` 之後、`browser-verify` 之前：它每次開 66 個 session，會自己全部關掉，不會加重後面 suite 的負載。

### 它印什麼

每個技能一行：`id / name / mobCount / 命中框 / damageNode / 掉幾隻 / 掉多少 hp / 打到的是哪一組`。
最後印 `BAD=n`。gate.ps1 認的是 `BAD=[1-9]`，不用加新規則。

### 紅了代表什麼（以及不代表什麼）

| 類別 | 意思 | 進 BAD？ |
| --- | --- | --- |
| `BAD=n` | 有技能在**怪確實站在框裡**的情況下沒掉血。這是真缺口。 | 是 |
| `WHITELIST` | `2301002` 治療。每跑一次重新證明一次理由，理由不成立就變成紅。 | 否 |
| `KNOWN GAP` | `5101002` 回馬，兩邊都不算。 | 否 |
| `VOID` | 三次嘗試都沒有怪在框裡。**沒有被問到，不是不會。** | 否 |
| `MARGINAL` | 只有動畫邊界擦到框，怪的原點不在框內。施放後一幀才讀邊界，差一格就翻面，不可判定。 | 否 |
| `TIMING` | 背景時鐘插進了幀。這一輪的結論被污染，是 flake 不是邏輯錯。 | 否，但會標紅提示 |
| `SELECTION PARITY` | 數量對，但**挑中的不是最近的**。見第 4 節。 | 否 |

後三項是刻意的。把「框裡根本沒怪」算成失敗，就會讓 gate 對量測假象鳴笛；gate 一旦學會亂叫，就沒有價值了。

### 白名單與已知缺口（照規定，不假裝是通過）

- **`2301002` 群体治愈** — 理由每次重算，不是註解背書。`/api/skill-cast/plan` 的 `levelKeys` 是伺服器自己讀到的鍵：`[hp,hs,lt,mobCount,mpCon,rb]`。有 `hp`、**沒有** damage node，所以不掉血是正確結果。若哪天 `damageNode` 長出來，白名單立刻破掉並轉紅。量測值：`levelKeys=[hp,hs,lt,mobCount,mpCon,rb]`，`damageNode=''`，三次施放掉 0 hp。
- **`5101002` 回马** — 帶真的 damage node（`damage=88`、`mobCount=3`、框 `0,-50..140,0`），正在 A 支線調查中。**既不當通過也不當失敗**，不進 BAD 所以不會被洗掉，也會一直印出來所以不會被忘掉。

### 多隻怪（就是 A2 要的那個檢查）

挑 `mobCount` 在 2..6、每個 damage node 各一個，跑 `N+2` 隻，要求掉血的**正好是 N 隻**。

工具自己算框，不假設「距離最近」就是答案：照 `MapSimSessionStore.cs:2192-2200` 的公式算技能框，用 `/api/simulate/debug/mob-frame` 算每隻怪的動畫框，在**施放那一幀**上做重疊測試。然後同時導出兩個期望集合並印出來：

- `nearest-N` — 依 `Combat.cpp:1226-1272 find_closest` 的最近優先
- `list-order` — 依 `MapSimSessionStore.cs:2208` 的實作

**2026-10-18 實測（三次，三次一致）**：`1111008`、`1210106` 都是 8 隻全在框內、`want=6`，伺服器打的是 `[24,25,26,27,28,29]`，而最近 6 隻是 `[31,26,30,27,25,28]` 這類組合。**伺服器永遠打 list-order。** 這是第 4 節的發現，不是 flake。

### 這一輪的乾淨結果（連續三次）

```
measured       : 66
landed damage  : 61/61 skills under test        <- 前兩次是 63/63、62/62
VOID           : 3  15001001, 5001001, 5201004
WHITELIST      : 1  2301002 群体治愈
KNOWN GAP      : 1  5101002 回马
SELECTION PARITY: 2  1111008, 12101006
TIMING         : clean -- every one of 276 /frames batches advanced
                   exactly the frames it asked for
BAD=0          (exit 0)
```

**`VOID` 的清單每次不同**（三次依序：1 個 / 2 個 / 3 個技能），因為每個 session 的 RNG 不同（見第 5 節）。這是誠實的：框太窄加上怪的位置隨機，本來就會有技能這一輪沒被問到。**沒有任何一個技能在「怪確定在框內」的情況下掉 0 血**——三次都是 `BAD=0`。`VOID` 較多時直接重跑，它們換一批就會被問到。

跑滿 66 個大約 6 分鐘。每個 session 在 `finally` 裡 `DELETE` —— 不是整潔問題，理由見第 2 節。

---

## 2. E2 — gate 時序假紅：根因

### 結論

**背景時鐘的抑制租約是固定 250 ms，而假紅取決於「兩次 `/frames` 之間**客戶端**花多久」，不是請求延遲、不是負載、不是 port 搶占。**

### 證據（`lease-probe.py`，同一台伺服器、同一次跑）

`MapSimSessionStore.cs:3698-3714`：

```csharp
long num = (value.LastInputMs = Environment.TickCount64);
value.ManualUntilMs = num + 250;      // 固定 250 ms 租約
Advance(value, input);                // 剛好一幀
```

`MapSimSessionStore.cs:1431-1454`：`now < ManualUntilMs` 就把時鐘停掉，否則用**持有中的輸入**每 16.667 ms 補一幀進去。

| 條件 | 注入幀數 | 請求延遲 p50 | 超過 250 ms 的請求 |
| --- | --- | --- | --- |
| A：無 think time，0 隻閒置 session | **0 / 60** | 1.6 ms | 0 |
| B：**200 ms think time**，0 隻閒置 session | **2 / 60，共 +10 幀**（一次 +10） | 2.1 ms | **0** |
| C：0 think time，40 隻閒置 session | 2 / 200，+2 幀 | 143.9 ms | 5% |

**B 是關鍵**：延遲分布跟 A 幾乎一樣（p50 2.1 ms），一次都沒超過 250 ms，注入卻發生了。所以「請求太慢所以超時」被排除；真正的視窗是**伺服器沒有在處理請求的那段空檔**——客戶端解析回應、決定下一步送什麼的時間。

租約到期後時鐘每 16.667 ms 補一幀，所以 200 ms 的空檔剛好對得上「一次 +10 幀」。

### 為什麼偏偏是 knockback 與 ball-art

不是巧合，是形狀：這兩個是 gate 裡**每幀發一次 HTTP、每次都在客戶端做實質處理**的 suite。

- `ballart-verify.py` — `for i in range(1800)`（**行號已過期**：MEASURED 2026-10-22，該檔現在一場最多打 8 遍，所以一輪最多 14,400 個 POST），每幀解析一個帶全部 mob 的 frame 並跑 Python 分支。
- `knockback-verify.py:84/109` — 40 幀閒置校準 + 每次嘗試 45 幀掃描，而且每次 `new()` 重跑最多 200 幀的走位迴圈。

其他 suite 的迴圈短得多，撞上租約的機會就低。C 顯示 40 隻 session 會把延遲從 1.6 ms 推到 143.9 ms；gate 的 suite 一個接一個、彼此不刪 session，累積負載讓「空檔」變長，於是這兩條先紅。

補充量測：`knockback-verify.py` 每跑一次洩漏最多 5 個 session，**不呼叫 `DELETE`**。**（`ballart-verify.py` 這一項 MEASURED 2026-10-22 為 FALSE：它確實呼叫 `DELETE`（W-089 那段），而且現在會把它開的每一個 session 都清掉並印出清掉幾個。）**`MapSimSessionStore.cs:3933` 只在閒置 120 秒後才清（`IdleTimeoutMs`），所以 2 分鐘內的累積是真的。

### 為什麼不能只把 timeout 調大

因為調大不解決任何東西，只是把機率推遠。真正的問題是**每一次手動步進都在跟一個自由運轉的時鐘賽跑，而那個時鐘沒有「這個 session 正在被逐幀驅動」的狀態**。`ManualUntilMs` 是唯一表達那個狀態的欄位，它 250 ms 之後就過期，伺服器就分不出「我在逐幀量測」和「我在用瀏覽器即時跑」。

---

## 3. E2 的修法（產品碼，總管執行）

我沒有改 `MapSimSessionStore.cs`（A/C 支線的檔案），也沒有改 `gate.ps1`、`knockback-verify.py`、`ballart-verify.py`（總管保留）。以下是量測支持的規格，不是猜測。

### 3.1 首選：讓租約跟著「量測意圖」走

`Step` 已經在更新 `LastInputMs`。把抑制條件從「距上次 Step 250 ms」改成「距上次 Step **N** ms，且 N 明顯大於一次往返」——但**光調大 N 只是把問題推遠**，真正的修法是讓抑制不再靠時間：

```csharp
// 現在：value.ManualUntilMs = num + 250;   (:3708)
```

改成一個明確的「手動模式」旗標，由 `/frames` 進來時開、由閒置計時器（既有 `IdleTimeoutMs`，`:3933`）關掉。手動模式下 `TickSession` 完全不碰這個 session，不管過了多久。這樣：

- 量測是**確定性**的，不再是「大概不會再發生」。
- 瀏覽器那條路走的是 `HeldInput`，`ManualUntilMs` 在 `Start` 裡被設成 0（`:3091`），行為不變。

如果不想加旗標，次選是把租約調到「一次來回的最大值 × 安全邊際」並**把它印出來**——但那仍然是靠運氣，只是運氣好一點。

### 3.2 我自己的工具怎麼避開（已在 `skillshape-verify.mjs` 內實作）

1. **批次**：`SimulatorEndpoints.cs:341-342` 把 `count` clamp 在 `[1,60]`。用 `count:60` 而不是 60 次 `count:1`，往返次數少 60 倍，撞租約的機會同比減少。`settlePose()` 全程批次。
2. **不洩漏 session**：每個 session 在 `finally` 裡 `DELETE`。洩漏的 session 是 240 Hz 的永久背景負載，會把**下一個** suite 的延遲推高——E2 的故障模式會自我複製。
3. **自己量時序**：每個 `/frames` 回應都帶 `frame` 與 `ticksRun`，所以 `(frame_now - frame_before) !== ticksRun` 是免費的、直接的證據。suite 會把這種幀單獨計數、標成 `TIMING`，跟邏輯失敗分開印。
4. **量延遲分布**並對照 250 ms 印出 `lease margin`。

### 3.3 兩個既有 suite 需要的三處改動（各 3 行，總管或原持有者套用）

- `ballart-verify.py` `for i in range(1800)`（**行號已過期**）→ 改成 30 次 `count:60` 的批次迴圈（`ticksRun` 已在回應裡，可用來確認沒被注入）。
- `knockback-verify.py:84/109` 同理，45 幀掃描改成一次 `count:45`。
- 兩者結束時補 `DELETE /api/simulate/session/{sid}`。

### 3.4 順帶量到的第二個問題（產品碼，報告不動手）

`SimClockService.cs:40-49` 的註解宣稱「物理走編輯器的 `Gate`，所以時鐘 tick 和 POST 幀會在鎖上序列化，不會競爭」。**對 `MapSimSessionStore.Step` 這句話不成立**：

```csharp
// MapSimSessionStore.cs:3709-3713
Advance(value, input);          // :3709  在鎖外
lock (Gate) { return Describe(value, value.Frame); }   // :3710  才拿鎖
```

而 `TickSession` 的 `Advance`（`:1451`）完全沒有拿鎖。時鐘在背景執行緒、POST 在 thread pool，兩者可以在同一個 session 上**同時**跑 `Advance`。租約過期時（也就是 250 ms 之後，正是負載下必然發生的情況）這個競爭是會實際發生的。`Advance` 會遞增 `session.Frame`、寫 `PendingDamage`、改 mob 狀態。

---

## 4. 順帶量到、屬於別條線的發現

### A2（命中框／目標選取）：實作挑的是 list-order，不是最近

`MapSimSessionStore.cs:2208`：

```csharp
for (int i = 0; i < session.Mobs.Count && hits.Count < want; i++)
```

走 `session.Mobs` 的**清單順序**，湊滿 `want` 就停；`:2224` 的 `hits.Sort(...)` 發生在**選完之後**，只影響繪製順序。而它自己註解引用的 `Combat.cpp:1226-1272 find_closest` 是把 mob 依到角色的距離塞進 multimap 再取前 N，**最近優先**。

`skillshape-verify.mjs` 三次實測都得到同一個結論：`1111008`、`12101006` 在 8 隻全在框內、`want=6` 時，伺服器打的是 `[24,25,26,27,28,29]`（清單前六隻），最近六隻是別的組合。**數量對，挑的對象不對。**

這正是 A2 說的「沒人確認過」。工具已經把它變成可重跑的檢查，`MULTIMOB=1`（預設）就會跑。

### A1（5101002 回马）：現在量得到掉血了

A1 寫的是「怪在 32px（框內）正前方，左右兩個朝向都不中」。用我的工具量：**六次執行全部掉血**，依序 200 / 100 / 300 / 300 / 300 / 300 hp，**一次都沒有掉 0**。

框 `ltx=0, rbx=140`：朝右時框在 `[X-140, X]`（左側），朝左時在 `[X, X+140]`（右側）—— 左右是鏡像對稱的，這點沒問題。怪被召喚成一圈，所以朝右那一邊只要站進 3 隻就打 3 隻。

A 支線原本那次不中，比較像是**站位**而不是框映射。建議 A 用 `MULTIMOB`／`ONLY=5101002` 重跑一次，用工具印出來的框座標對照實際站位再下結論。

---

## 5. 我沒有驗到的部分

- **沒有跑完整 `gate.ps1`**。我沒有權限改它，而且它一次要跑 27 個 suite、含 browser 判官。上面第 3.3 節的三處改動套用之後 E2 是否真的不再紅，**需要總管在完整 gate 連續兩次 `RED SUITES: 0` 的那次確認**（也就是完成定義第 2 條）。
- **沒有改** `knockback-verify.py` / `ballart-verify.py` / `gate.ps1` / 任何產品碼。
- `SELECTION PARITY` 我只測了 `mobCount` 2..6、三個 damage node。**`mobCount` 12/15/30 沒跑過**（工具的 `chosen` 篩選限在 ≤6，避免一次召喚 32 隻）。A5 說 `mobCount` 與 `collisionCount` 數值相同，這條我也沒查。
- `VOID` 的技能（最後一次跑是 `15001001`、`5001001`、`5201004`）**沒有被判定**，需要重跑。這不是穩定的 void：`5201004` 在前一次跑裡掉了 1 隻 100 hp，只是那一次被算進了別的嘗試。
- `TIMING` 三次都是 clean。**這不代表 E2 消失了**——它代表這支 suite 自己的往返夠密（批次化）、不洩漏 session，所以沒有製造出那個空檔。第 2 節的 B 組實驗證明，只要客戶端 think time 超過租約，注入就會發生。
- 每個 session 的 RNG 是 `new Random(StableSeed(sessionId + ":combat"))`（`:3182`），而 sessionId 每次都是新的 GUID，**所以同一支 suite 兩次跑的怪 AI 行為本來就不一樣**。上面所有數字都是單次量測值，不是可重現的固定值。這也是 `5101002` 掉 100 或 300 隻看運氣的原因。