# 2026-10-11 — 接手 MapleBcnch-NX

專案已從 `追風WZ編輯器\agent-wz\MapleBenchX` 搬到 `MapleBcnch-NX`，
分支 `map-generation-baseline`，HEAD `ec3e03c`，工作區乾淨。
本文記錄接手時的驗證結果，以及上一輪未完成的那件事。

---

## 一、交接狀態已驗證

`HANDOVER.md` 的說法我沒有直接採信，逐項跑過：

| 宣稱 | 驗證 |
|---|---|
| 我的調色盤修復（`EnumerateChildren` / `PropertiesOf`）在 | 5 處，比對相符 |
| 放置後存檔不再殺行程 | acceptance 15/15 PASS，`save verifies — 12.72s reopenMs=85` |
| `MapleMcp` 10 個地圖工具 | 存在，`1da3f35` |
| `/validate` `/batch` | 存在且通過 |
| 上一輪未提交的 5 檔改動 | **已 commit**，工作區乾淨 |

**我上一輪離開時正在追的死結也解決了**：`ReopenNx` 現在在 session gate 區塊**之外**
（line 1456，gate 在 1353），不會再造成鎖住整個 session 的停頓。
`file.Name` 也不再被覆寫成空字串——那是我上一輪改壞的，
它讓 `RoleRoot()` 全部回傳 null，調色盤清空、放置全被拒。

---

## 二、冷啟動驗證（我重跑了一遍）

```
place  t|ancientForest|bsc/0 @ -350,200
save   verified:true  seconds:11.6  reopenMs:88  differences:[]
--- 行程重啟 ---
read   冷啟動後 layer 0 tiles: 1  →  t|ancientForest|bsc|0 @ -350,200
```

一次放置、一次存檔、重啟後**剛好一個**圖塊，位置與素材都對。

---

## 三、我自己犯的第二個錯，和 HANDOVER §4.1 同一類

第一次驗證時我看到 layer 0 有**兩個**圖塊，位置與素材完全相同，
立刻以為是放置的重複寫入 bug。

**錯了。** `acceptance.mjs` 每次執行都在**同一個座標**放一次圖塊，
而我為了驗證跑了兩遍套件。

**教訓（HANDOVER §4.1 已經寫過，我重蹈）**：
懷疑程式碼之前，先確認自己的測試有沒有做對。
一個「重複寫入」的 bug 假設，實際上是「測試沒有冪等」。

---

## 四、下一件事：foothold 生成器

`HANDOVER.md` §3.1 指出的缺口——**鋪完地面沒有東西讓角色站住**——
我獨立驗證過，結論相同：

```
f1/Tile/ancientForest.img/bsc/0 的子節點：
  origin | Vector
  z      | Long
```

**沒有 foothold Convex**。`/auto-foothold` 現有的行為是正確的：
它拒絕猜測，回覆「寧可什麼都不做」。
所以這不是修 bug，是要**新增一種幾何來源**。

實測基準（來自 `tools/anatomy/sample-10.json`，10 張地圖）：
深海峡谷 156 條 foothold，`slope=142 / wall=14`，`hasNext=138`。

`docs/MAP-STRUCTURE.md` §5.2 的三條規則是這件事的規格：
foothold layer 跟著視覺圖層、prev/next 零斷裂、斜坡是主流。
其中規則一（layer 不固定）是上一任 agent 抽第二張地圖才推翻的。

---

## 五、這輪還沒做的

- ** foothold 生成器本身。** §4 的規則我讀完了，程式碼還沒寫。
- **`/maps` 端點會讓行程消失。** 列出 5366 張地圖時行程沒了，
  21.9 GB 記憶體是空的，log 沒有錯誤。跟 P0-1 是同一類問題
  （大量工作 → 沒回應／沒輸出），但還沒查。
- **`Mob.nx`（818 MB）未測。** `HANDOVER.md` §4.4 的記憶體警告成立，
  本機 63.9 GB 中閒置 21.9 GB，硬測有風險。
