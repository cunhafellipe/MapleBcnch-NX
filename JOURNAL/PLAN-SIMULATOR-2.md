# PLAN-SIMULATOR-2 — ✅ 已完成

寫於 2026-10-12 · 副本 `MapleBcnch-NX-audit` · 前置 `2026-10-12-unresolved.md`

> **狀態更新**：這個計畫的 S-0（「修 18,000 幀」）**已完成**。
> 解法不在本計畫裡——它在 `_necrocer_src/MapleCharacter.cs:793-801`。
> 本文件保留作為「當時怎麼想的」紀錄；
> 實際的診斷與修正見 [2026-10-12-unresolved.md](2026-10-12-unresolved.md)。

---

## 0. 當時的狀態

```
階段 A 可達性            [OK]
階段 B 梯子/門/怪物 AI     [OK]
階段 C MCP/批量/建議       [OK]
階段 D UI/驗收/文件        [OK]
客戶端全量驗證            [OK] 10,732 次,46 秒
唯一未解決               610030510.img 走滿 18,000 幀
```

---

## 一、當時判斷的問題

### 症狀

```
ticks = 18000 (上限), fellOut = false, reason = ""
```

地圖寬 2,068px，以 2.5px/步走完需 827 步，實際 18,000 步。

### 當時量到的事實

```
stp 96   x=-237  gnd=False  reachedEnd=True
stp 97+  連續 224 幀下落，y 155→1889 單調，X 凍結
probe:   x=-237 處所有 foothold 表面都在 -143 以上（全在角色上方）
```

落空是**正確的**行為。錯的是落空之後還能繼續跑。

### 當時的決定性實驗

```
MaxFallFrames: 120 → 3
結果: ticks = 18000, fellOut = false
```

門檻降到 3 幀仍走滿 → **那段程式碼根本沒有執行。**

當時排除了四個假設（沒有第二個實作、dll 舊、`Fall()` 每幀著地、
`airborne` 被重置），全部為真，然後就卡住了。

---

## 二、當時計畫的下一步（已不需要）

原本要「先埋三個診斷點（`ExitPath`、逐幀 `airborne`、`Step()` 前後的
`OnGround`），先量再修」。

**不用埋了。** 診斷點的設計是對的，但診斷對象選錯了——
埋點只能證明「程式碼沒執行」，證明不了「客戶端怎麼做」。

**答案是一行原始碼**：

```csharp
FallEdge = -999999;                  // 預設：永遠不觸發
if (FH.Prev == null)                  // 只有鏈末端才設邊界
    FallEdge = FH.X1 - 10;
if ((FH.Prev != null) && (FH.Prev.IsWall()) && (FH.Prev.Y1 > Y))
    FallEdge = FH.X1;
```

### 原本的備案 A（已採用，但理由不同）

原本想「不用 `airborne`，改用位置判斷」。
**實際上兩件都要**：
- `FallEdge` 取代 `reachedEnd`（這才是真正的 bug）
- 位置判斷取代 `MaxFallFrames`（這是 `airborne` 的替代品）

### 原本明確不可接受的做法

「調大門檻讓那張地圖從『空 reason』變成『假的 fellOut`』」——
這一條仍然正確，沒有做。

---

## 三、S-1 同步與推送

### 步驟

1. 確認其他 agent 已停止寫入主目錄
2. `MapleBcnch-NX` ← `MapleBcnch-NX-audit`（檔案層級覆蓋，保留 `.git`）
3. 主目錄跑一次完整驗收
4. 推送

### 推送

```
cd MapleBcnch-NX
git add -A && git commit
git push public map-generation-baseline
```

---

## 四、明確不做（延續上一輪）

- 不改客戶端行為（模擬器是預測工具，不是客戶端）
- 不為了讓測試通過而調參數
- 不刪除任何測試

---

## 五、這一輪的教訓

計畫本身沒問題——**方向錯了**。

我在計畫裡列出五個假設要逐一排除，全部排除完之後卡住，
因為**五個假設都在問「我的程式碼為什麼沒跑」**，
沒有一個在問「客戶端是怎麼做的」。

源碼已經在磁碟上。計畫不該假設它不存在。