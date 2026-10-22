# 2026-10-14 · 自治驗證工作流程：soak.py + 雙代理檢察

**日期**：2026-10-14
**觸發**：使用者指出「你 指派omp進行測試 讓他擔任bug檢察官 同時你自己進行分析 我要的是一套可以自主驗證發現問題主動修復的工作流程 我已經講很多次了」

這份日誌記錄的不是「修了什麼」，而是**這套流程怎麼運作、它自己找出了什麼**。

---

## 1. 為什麼需要這套流程

過去的做法是：我手動開一個 session、送幾百幀、看幾個數字。這套方法有個結構性盲點：

> **單次 session 的觀察看不到「跑了 233 場之後伺服器死掉」這種東西。**

所以這次改成：規則由程式執行、數量由程式取得、結論由規則的成立與否決定，不由我的耐心決定。

---

## 2. 兩個部分

### 2.1 `tools/soak.py` — 自治驗證引擎

自己開伺服器、開 16 個 NX、挑地圖、跑 session，對**不變量**而不是「回 200 嗎」。

六條規則：

| 規則 | 斷言 |
|---|---|
| `idle-drift` | 不按任何鍵，x 位移 < 3px |
| `out-of-bounds` | x 永遠在 `[left+20, right-20]`，y 在地板以上 |
| `mob-flip-thrash` | 怪在移動幀裡的轉向次數 / 淨行程 有上限 |
| `dead-mob-drawn` | `dead=true` 的怪不出現在清單裡 |
| `swing-never-lands` | 在怪 110px 內揮擊 ≥30 幀 → 必須有傷害事件 |
| `mob-airborne` | 巡邏中的怪不該大量離地 |

用法：`python tools/soak.py --seconds 180 --json out.json`

### 2.2 omp 作為 bug 檢察官

只准寫 `FINDINGS.md`，不准動任何 `.cs` / `.js`。給它四條領域規則（別猜 WZ 路徑、註解行號不保證、量測本身要驗、跑起來不等於驗證），並明講「我要你找**我沒找的**，不是複述我已知的」。

---

## 3. 流程找出來的東西

### 3.1 soak 獨家：伺服器在第 233 場之後消失

第一輪 150 秒 / 233 場，末尾連續 4 次 `WinError 10061 無法連線，因為目標電腦拒絕連線`；`tasklist` 確認 `dotnet.exe` 與 `MapleBenchX.exe` 都不在了。

手動測試永遠看不到這個 —— 一次 session 撐不到 233 場。

**狀態：未重現。** 換新版後三輪（299 / 331 / 309 場）皆無此現象，長 soak 仍在跑。誠實標記為「未確認修好」。

### 3.2 omp 找到、我逐條驗過的三條

| 編號 | 主張 | 我的驗證 | 結果 |
|---|---|---|---|
| F1 | 攻擊框鏡像方向寫反 | 讀對標 `AfterImage.cs:62-72` + `MapleCharacter.cs:1649-1652` | **成立** |
| F2 | 武器名重複接 `.img` | `attack-box?weapon=axe.img` → 回 `barehands.img` | **成立，且比我以為的嚴重** |
| F3 | `direction` 參數完全無效 | request `direction=-1` ×3 → 回 `facing=+1` ×3 | **成立** |
| F4 | 我的「無輸入漂移」觀察有誤 | — | **我錯了**，它說得對 |

#### F1 — 攻擊框方向寫反（`CombatService.cs` `AttackBox.At`）

對標 `AfterImage.cs:62-72` 配合 `MapleCharacter.cs:1649-1652`（按右鍵 → `FlipX = true`，所以 **FlipX=true 是面對右**）：

- `FlipX=false`（左）→ `Left = X + LT.X`，`Right = X + RB.X` → 裸手 `lt=-64, rb=-27` 得 `X-64..X-27`
- `FlipX=true`（右）→ `Left = X - RB.X`，`Right = X - LT.X + 18` → 得 `X+27..X+82`

兩者都在**身體前方**。所以檔案裡的 `lt/rb` 是**左向形**，右向形是它的鏡像 —— 與原本的 `facingLeft ? Box.MirrorX() : Box` **正好相反**，每一次揮擊都落在身體背後。

另外原碼漏了對標右向分支的 `+18`。

**修**：`facingLeft ? Box : new Box(-Box.Right, Box.Top, -Box.Left + 18, Box.Bottom)`

#### F2 — 副檔名重複（`CombatService.Candidates`）

無條件接 `.img` → `axe.img` 變成 `axe.img.img`（404）→ 候選全部落空 → 退回 barehands。

**這一條同時解釋了我長期查不出來的原因**：

```
axe          → axe.img/stabO1     ← 對
axe.img      → barehands.img/stabO1  ← 錯（修前）
1302000.img  → barehands.img/stabO1  ← 錯（修前）
```

瀏覽器送的就是帶 `.img` 的全名。barehands 是全遊戲最小的攻擊框（37×15），所以我之前所有戰鬥測試都在用最小的框 —— 難怪打不到。

**修**：`if (w.EndsWith(".img", OrdinalIgnoreCase)) w = w[..^4];`

#### F3 — `direction` 參數無效（`MapSimSessionStore.Start`）

`LastX` 從未初始化（`float` 預設 0），而每幀用 `dx = b.X - session.LastX` 推導面向。於是**第一幀的 dx 就是出生座標本身**：

```
startX=-3252 → facing=-1
startX=  900 → facing=+1
```

回傳的面向由出生點決定，與呼叫端要求無關。`direction` 從未被讀取。

**修**：`LastX = body.X`，`Facing = direction >= 0 ? 1 : -1`

---

## 4. 修復後的實測

### 戰鬥（這是三條修完才有的）

| 位置 | 結果 |
|---|---|
| 怪右側 30px，面朝左 | 怪 12→**11**，hp 96→88，首擊第 0 幀 |
| 怪左側 30px，面朝右 | 怪 12→**10**，hp 96→80，首擊第 1 幀 |

**兩個方向都命中** —— 這正是 F1 修好的證據（修前面朝左時框在 `X+27..X+64`，怪在左邊，永遠碰不到）。

### soak 四輪

| 輪次 | 場數 | 發現 |
|---|---|---|
| 1 | 233 | 伺服器崩潰 ×4、`swing-never-lands` |
| 2 | 299 | `swing-never-lands`（崩潰未再現） |
| 3 | 331 | `out-of-bounds` x=2028（超界 2px）、`swing-never-lands` |
| 4 | 309 | **零發現** |

---

## 5. 我在這輪犯的兩個錯，都記下來

### 5.1 把規則的前提當成程式的錯

第 2 輪 soak 在「攻擊已修好」之後仍報 `swing-never-lands`。實際原因是**規則本身有缺陷**：soak 在地圖預設出生點，離最近的怪 215px，本來就碰不到，零傷害是正確行為。

> 一條會對**已經正確的程式**亮紅燈的規則，比沒有規則更糟 —— 它會讓人再去改已經對的程式碼。

修法不是放寬門檻，是補上前提：`in_reach >= 30`（揮擊時確實有怪在 110px 內）才檢查；並讓 soak 改為**在怪附近出生**，否則這條規則永遠不會觸發，等於死碼。

### 5.2 我先前的「無輸入漂移 2.2px/幀」是錯的

soak 的 `idle-drift` 從未報過。我量到的其實是「走動後的殘留速度」，不是靜止漂移。omp 的 F4 指出了這點，說得對。

---

## 6. `out-of-bounds` 2px：抓到的第二個真 bug

第 3 輪 soak 報 13/260 幀 x=2028（合法上限 2026）。

查 `CharacterPhysics` 發現夾制只加在 `Walk()` 裡，但 **`DriftWhileFalling`(:599)、wall 推出(:439/:444)、爬梯進入(:838/:887/:899) 都會直接賦值 `_x`** 而不經過它，而且 `Step()` 在爬梯分支會**提前 return** —— 所以「在方法尾端夾制」照樣會在最需要的地方被跳過。

修法：**單一權威夾制點**，`Step()` 拆成外層 `Step` → `StepCore` + `ClampToBounds()`，任何路徑都逃不掉。

（同時保留 `Walk()` 內的夾制，它額外還會歸零速度，有實際作用。）

---

## 7. 後續：第 5 輪 soak 與瀏覽器端新缺陷

### 第 5 輪 soak

`--seconds 420 --frames-per-session 300` → **697 場 / 697 個 session / 420 秒，零發現**。

崩潰發生在第 1 輪的第 233 場；此後四輪（299 / 331 / 309 / 697）都沒再出現。
但**一次沒發生不等於不會發生** —— 這一條維持「未確認修好」。

### 瀏覽器：walkview 停在舊幀（新缺陷，未修）

把頁面和伺服器對同一個 session 放在一起看：

| | frame | x |
|---|---|---|
| 頁面 `live.state` | **0** | -3252 |
| 伺服器 `GET /session/{id}` | **6080** | -2973 |

rAF 迴圈**還在跑**（`live.last` 單調增加），session 也活著（200），但沒有再發任何幀請求，畫布一直畫第 0 幀。

觸發路徑：分頁被隱藏過。`document.hidden = true` 會節流 rAF，`Page.bringToFront` 讓它重新可見後，迴圈沒有恢復輪詢。

**這條的意義大於它本身**：只讀頁面會得到「畫面正常」，只有把伺服器同一個 session 的視圖一起讀出來才看得出來。往後判斷瀏覽器表現，一律兩個視圖對照。

另外記一筆環境因素：soak 在跑時瀏覽器的 `POST /api/simulate/session` 會逾時。同一瞬間 Python 打同一個端點 **0.002 秒**成功 —— 單一模擬閘門被佔滿，不是程式缺陷。

## 8. 這套流程的邊界

誠實記下還沒解決的：

- **伺服器崩潰未重現、未確認修好**。第 4 輪 309 場沒再發生，但一次沒發生不等於不會發生。
- **怪 0002000（x=-2869）仍然打不到**，而同一張圖上另外兩隻怪打得死。戰鬥系統通了，但這個個案還沒解釋。
- soak 的六條規則覆蓋的是「可觀察的不變量」。**規則沒寫到的東西，它一樣不會報**。這不是「全綠就沒問題」，是「這六件事現在成立」。

---

## 9. 產出檔案

- `tools/soak.py` — 自治驗證引擎（新增）
- `INSPECTOR-TASK.md` — 給檢察官的任務書，含領域規則（新增）
- `FINDINGS.md` — 檢察官產出（新增）
- `Services/Simulate/CombatService.cs` — F1 + F2
- `Services/Simulate/MapSimSessionStore.cs` — F3
- `Services/MapEditor/CharacterPhysics.cs` — 單一權威夾制點