# PLAN-SIMULATOR-2 — 收尾：修掉 18,000 幀，同步與推送

寫於 2026-10-12 · 副本 `MapleBcnch-NX-audit` · 前置 `2026-10-12-mob-ai-and-full-sweep.md`

---

## 0. 現在在哪裡

```
階段 A  可達性          ✅
階段 B  梯子/門/怪物AI  ✅
階段 C  MCP/批量/建議  ✅
階段 D  UI/驗收/文件    ✅
客戶端全量驗證           ✅ 10,732 次，46 秒，零可避免錯誤
唯一未解                 ❌ 610030510.img 走滿 18,000 幀
```

工作區乾淨，主目錄零變動。

---

## 一、唯一未解決的問題

### 症狀

```
POST /api/simulate/character {"path":"f1/Map/Map6/610030510.img","direction":-1}

ticks = 18000        （MaxWalkTicks 上限）
fellOut = false
reason = ""          （空：走完沒觸發任何 break）
```

### 已量測到的事實

**trace 逐幀（`/api/simulate/trace`）**

```
stp       x       y    gnd  fh  reachedEnd  below  action
 95    -235     155  True  153      False    153   walk
 96    -237     155 False  153       True       walk   ← 離開鏈
 97    -237     155 False  153       True       fall
 98    -237     156 False  153       True       fall
 ...
108    -237     201 False  153       True       fall

第 96 步後：224 幀連續 fall，y 從 155 單調下降到 1889
```

**probe 查 x=-237 處的所有 foothold**

```
y=202 覆蓋 = [-2604, -2244, -1944, -1644, -1344, -1044, -744, -444, -144, -143]
y=1500 覆蓋 = （同上，完全相同）
FindBelow(落下) = None
FindBelow(站立) = None
```

**所有 foothold 表面都在 -143 及以上，全部在角色上方。**
角色往下掉，下面沒有任何地面——它**應該**永遠落空。
而它確實落空了 224 幀。

### 已試過的修法

| # | 做法 | 結果 |
|---|---|---|
| 1 | `airborne` 計數 + 120 幀門檻 | 無效 |
| 2 | `Fall()` 不准重新落在同一個 foothold（`_lastGroundId`） | 無效 |
| 3 | 加診斷欄位 `AirborneHighWater` 測量 | **量到 0**，證明我的推理錯了，但沒指出正確修法 |

### 決定性實驗

把 `MaxFallFrames` 從 120 改成 **3**（不可能不觸發的門檻）：

```
ticks = 18000, fellOut = false, reason = ""
```

**門檻降到 3 幀仍然走滿。** 那段程式碼根本沒有執行。

### 排除掉的

| 假設 | 排除依據 |
|---|---|
| 有第二個 `WalkCharacter` | `grep` 全檔案，只有一個 |
| 部署的 dll 是舊版 | dll 08:24:26 > 原始碼 08:24:19，含新字串，行程路徑正確 |
| `Fall()` 每幀重新著地 | probe 證明 x=-237 下方無地面 |
| `airborne` 被其他分支重置 | 加診斷欄位實測為 0，說明它從未累積 |

**剩下的矛盾**：程式碼正確、路徑正確、dll 正確，但那段不執行。
這不是我能靠讀程式碼解決的問題——**我已經推理錯了五次。**

---

## 二、下一輪怎麼查（診斷優先，不再推理）

前一輪的教訓：**推理到第 5 次時已不可靠，診斷欄位卻到最後才加。**
這一輪先埋點，再看數據。

### 步驟

**1. 確認迴圈是否真的跑到那裡**

在 `airborne` 檢查處無條件設定一個旗標，跑完回報 `ExitPath`：

```csharp
// 每一次 break 都記錄是從哪裡出去的
result.ExitPath = "airborne-budget" / "ladder" / "edge" / "portal" / "tick-cap";
```

`/character` 現在回報空 reason，正是因為**沒有任何 break 被執行**，
只走完了 tick 上限。有了 `ExitPath` 就能確認是「沒跑到」還是「跑了但條件為假」。

**2. 印出每一幀的 `airborne` 值**

```csharp
if (who.Ticks <= 200) track.Add(... 加上 airborne 欄位);
```

200 幀足以覆蓋第 96 步的落離與後續 104 幀。這會直接回答
「`airborne` 到底有沒有累積」。

**3. 檢查 `OnGround` 的讀取時機**

`airborne = who.OnGround ? 0 : airborne + 1` 讀在 `Fall()` 之後，
而 `Fall()` 會設定 `OnGround`。**兩者之間的順序可能就是問題。**
印出 `Step()` 前後的 `OnGround` 就能確認。

> 這三項都是「量」，不是「猜」。前一輪我讀了五次程式碼，五次都自洽
> 卻與實測不符——那證明讀程式碼在這個問題上無效。

---

## 三、備案（若診斷後仍無法解釋）

### 備案 A：不用 airbone，改用位置判斷

`airborne` 不穩定的話，直接比較 Y：

```csharp
float lastFallY = who.Y;
...
if (who.Y - lastFallY < 0.5f && !who.OnGround) {
    // Y 沒變但不在地面上 → 位置被某處凍結了
    result.FellOut = true;
    result.Reason = "the character's position stopped changing while airborne";
}
```

**好處**：不依賴 `OnGround` 或 `airborne` 任何一個。
X 與 Y 都不動就是卡住了——這是觀察，不是推論。

### 備案 B：降低 `MaxWalkTicks`

18,000 幀 = 300 秒的模擬時間，對一張 2,068px 寬的地圖綽綽有餘。
降到 3,000（50 秒）仍遠超任何真實地圖所需，
但能把最壞情況的成本砍到六分之一。

**這是掩蓋，不是修好**，只能搭配備案 A 一起做。

### 備案 C：認帳並記錄

如果診斷後仍無法修，就照實寫進日誌：
「1 張地圖、10,732 次中的 1 次（0.009%）無法終止」，
並附上已量測的完整數據與三種試過的修法。

**不可接受的做法**：為了讓數字好看而調大門檻，
讓那張地圖從「空 reason」變成「假的 fellOut」。

---

## 四、S-1 同步與推送（與 S-0 無關，可並行）

副本領先主目錄 14 個 commit，且主目錄那 4 個未提交檔案的 MD5
與副本已提交的版本完全相同——主目錄沒有副本沒有的東西。

### 步驟

| # | 步驟 | 完成定義 |
|---|---|---|
| S-1 | 確認主目錄的其他 agent 已停止 | 使用者確認 |
| S-2 | 重驗主目錄工作區 MD4 與副本一致 | 若有差異，先 diff |
| S-3 | 在主目錄 fetch 副本分支並 fast-forward | 不用 rebase，不覆蓋 |
| S-4 | 主目錄建置 0 警告 0 錯誤 | 兩個專案都要 |
| S-5 | 主目錄跑一次 acceptance | 23/23 |
| S-6 | 同步文件（README、CLI-AND-MCP、JOURNAL 索引） | 與程式碼一致 |

### 推送

| # | 步驟 |
|---|---|
| P-1 | 副本 push 到 `public/map-generation-baseline` |
| P-2 | 遠端與本地 commit 一致 |
| P-3 | 遠端的兩個資料摧毀缺陷已修（`ExportService`、`ArchiveTools`） |

**推送前必須確認**：`ExportService.cs` 與 `ArchiveTools.cs` 的修正都在。
遠端那份程式碼還帶著「匯出刪掉封存內容」與「dry run 關掉使用者封存」兩個缺陷。

---

## 五、明確不做（延續上一輪）

| 不做 | 理由 |
|---|---|
| 怪物 HP 與傷害數值 | 不影響地圖是否健全，只影響戰鬥演出 |
| 怪物技能 | `Mob.cs` 只到移動與碰撞，沒有更多資訊 |
| 飛行型怪物 | 需要 `CosY` 與粒子系統，超出「能不能走」 |
| 逐幀動畫迴圈 | 幀率耦合會讓 per-frame 常數失真 |
| 重寫渲染器 | 已有 2,224 行 canvas 渲染器 |
| 改 NX 格式或存檔路徑 | 不在目標內，風險最高 |

---

## 六、建議的下一輪順序

1. **S-0**：埋三個診斷點，跑一次 `610030510.img`，讀 `ExitPath` 與逐幀 `airborne`
2. 依數據選修法（主修法或備案 A/B），**不靠推理**
3. 修完重跑全量驗證（10,732 次）確認沒有迴歸
4. S-1：同步主目錄（需使用者確認其他 agent 已停）
5. P：推送

**第 1 步不能跳。** 前一輪我跳過了，然後花了五次推理。