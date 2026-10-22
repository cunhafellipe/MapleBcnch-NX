# 模擬器全黑：根因、證明、修復

診斷者 · 2026-10-13 18:40
對象：`C:\MUWORK\GAME\MAPLESOTRY\wz工具\MapleBcnch-NX-audit\`
狀態：**已修好並驗證**（含桌面截圖）。根因不在 .workspace 的高度，在它被放進了哪一列。

---

## 1. 根因（一句話）

`.app` 是三軌 grid（topbar `56px` / `1fr` / statusbar `28px`），三個子元素靠**自動擺位**進列。
專注模式（`body[data-focus]`，模擬器專用）用 `display:none` 藏掉 topbar 與 statusbar——
`display:none` 會把元素**從 grid 完全移除**，於是 `.workspace` 變成唯一在流內的項目，
自動擺位把它丟進**第 1 列（那條 56px 的 `--topbar-h`）**，整個 stage 塌成 56px，canvas 被裁掉。

**這是「摆位」bug，不是「尺寸」bug。** 元素在錯的列裡，給它任何 `height` 都救不回來。

### 證明它的觀測值（就是那個矛盾）

只切換 `grid-row` 這三行，其他一律不動，模擬器內（focus=simulator）量測：

| 狀態 | app gridTemplateRows | .workspace | .sections | 畫面 |
|---|---|---|---|---|
| `grid-row: auto`（原始） | `0px 844px 0px` | **0px** | **0px** | 全黑 |
| `grid-row: 1/2/3`（修復） | `0px 844px 0px` | **844px** | **796px** | 地圖可見，canvas 1290×724 |

關鍵一行證據（`appGtr` 兩邊**完全相同**）：`.app` 的列高從頭到尾都是 `0px 844px 0px`，
所以「771px 的列裡只有 56px」不是列變矮了——**是 `.workspace` 根本不在那一列裡**。

### 為什麼 inline 有效、CSS 檔案無效（題目的矛盾）

同一組宣告 `grid-template-rows:minmax(0,1fr)` + `height:100%`，量測結果：

| 施作位置 | .workspace |
|---|---|
| 寫在 **`.workspace`**（先前的 CSS 改法） | **0px**（無效） |
| 寫在 **`.app`** | **844px**（有效） |

原因：**inline style 贏得 cascade，所以同樣的字串落到了 `.app` 上。**
`height:100%` 對一個被放進第 1 列的 grid item 而言，「100% 的第 1 列」**就是 56px**——
它忠實地做了它被要求的事。inline 之所以有效，不是因為 inline 比較強，
而是因為它改到的是**另一個元素**：`.app` 上覆寫 `grid-template-rows` 把三軌塌成一軌，
`.workspace` 落在哪一列就不重要了。CSS 檔案那版改的是 `.workspace` 自己，所以完全沒有作用。

先前寫進 `app.css` `.workspace` 與 `database.css` `.sections` 的那兩段宣告，
**量測證明是死碼**：在 400 / 600 / 760 / 844 / 1000 / 1200px 六種高度下，
有這兩段與沒這兩段的 `.workspace` / `.sections` / canvas 高度**六組全部 identical=true**。
兩段已移除，並在註解裡寫明它們不是修復。

---

## 2. 實際的 CSS 修改

### `wwwroot/css/app.css`

`.workspace` 移除死碼（`grid-template-rows` / `height`），留下說明為何無效：

```css
.workspace {
  display: grid;
  grid-template-columns: var(--rail-w) var(--tree-w, 288px) minmax(0, 1fr) var(--inspector-w, 384px);
  /* ...a percentage height resolves against the track the item is placed in,
     so asking a row-1 item to be 100% tall just makes it 56px tall. The collapse
     is a PLACEMENT bug, not a sizing one -- see the pinning block below. */
  min-height: 0;
```

真正的修復（**本來就存在**，我驗證並補上量測證據）：

```css
.topbar    { grid-row: 1; }
.workspace { grid-row: 2; }
.statusbar { grid-row: 3; }
```

註解補上了完整的量測表與「為何尺寸宣告修不了」的推理，避免下一個人再走一次死路。

### `wwwroot/css/database.css`

`.sections` 同樣移除死碼，改成記錄它不是修復、以及真正的修復在哪裡。

### 已複製到服務端（守則 §2.4）

```
md5  src / bin / curl  三者一致：
  app.css       e3420c23fc34f42ee0da4a25cbafb957
  database.css  063e637d3534926fab7256a230e3adfc
```

---

## 3. 驗證

**重整頁面、零 inline style**（`#repro*` / `#x*` / `#ab*` 探針標籤數 = 0）：

| 量測項 | 要求 | 實測 |
|---|---|---|
| .workspace | > 700 | **844** ✅ |
| .sections | > 600 | **796** ✅ |
| .sim | > 500 | **764** ✅ |
| canvas | 可見 | **1290 × 724** ✅ |
| .workspace inline style | 無 | **(none)** ✅ |
| app gridTemplateRows | — | `0px 844px 0px`（兩軌已收合，無死空間） |

### 截圖（交付物，見附檔）

| 檔案 | 內容 |
|---|---|
| `DESKTOP_PROOF_AFTER_RELOAD.png` | **最終桌面截圖** — 重整 + 改完 CSS 之後，模擬器地圖可見 |
| `DESKTOP_FINAL_AFTER_CSS_FIX.png` | 修復後桌面截圖（改 CSS 之後） |
| `DESKTOP_AFTER_FIX.png` | 修復後桌面截圖（改 CSS 前，同一狀態） |
| `cdp_FINAL3.png` / `cdp_FINAL2.png` | 修復後 CDP 截圖，地圖可見 |
| `cdp_BEFORE_broken.png` | **重現全黑**（注入解除 pinning 後，6KB 純色） |

桌面截圖與 CDP 讀數都會因為視窗沒被繪製而回傳空白／過期畫面
（Chrome 視窗被 Discord 完全遮住）。這時要用
`Page.bringToFront` + 捲動一下觸發重繪，再截。見 §5。

---

## 4. 其他區段未被本次修改弄壞

改完 CSS、每個區段逐一開啟量測（`workspace` 全部 `grid-row: 2/auto`、inline style 全部 `(none)`）：

| 區段 | focus | wsRow | workspace | sections | body | canvas |
|---|---|---|---|---|---|---|
| Mob | (none) | 2/auto | 760 | 696 | 696 | — |
| 商城 Cash Shop | (none) | 2/auto | 760 | 696 | 696 | — |
| 遊戲資料搜尋 | (none) | 2/auto | 760 | 696 | 696 | — |
| 技能 Skills | (none) | 2/auto | 760 | 696 | 696 | — |
| NPC | (none) | 2/auto | 760 | 696 | 696 | — |
| 文字 Strings | (none) | 2/auto | 760 | 696 | 696 | — |
| 地圖編輯器 | (none) | 2/auto | 760 | 696 | 696 | me-canvas 486×622 |
| 模擬器 Simulator | **simulator** | 2/auto | **844** | **796** | 796 | 見上 |

**沒有任何區段塌陷**（body 全部 ≥ 696px）。

### 一個既存問題（與本次修改無關，未處理）

`.mob-grid` 高 **20984px**，但外層 `.stage-body` 只有 696px 且 `overflow:hidden`。
A/B 實測：**移除那兩段死碼前後都是 20984px，完全相同**（`mobGridUnchanged: true`）。
這是 Mob 面板自己的高度問題，**不是**本次修復造成的，範圍外，留給主線分派。

---

## 5. 兩個測量陷阱（下一個人會踩）

1. **CDP 分頁不持續繪製 → transition 凍結在 `currentTime:0`。**
   實測 `getComputedStyle(.app).gridTemplateRows` 在同一個 focus 狀態下讀到
   `56px 760px 28px`（未收合）與 `0px 844px 0px`（已收合）兩種值。
   `document.getAnimations()` 顯示 `grid-template-rows` 一直是 `running`。
   呼叫 `a.finish()` 後才得到真值。**任何 focus 模式的版面讀數前先 finish 掉 animation。**

2. **`prefers-reduced-motion: reduce` 在本機是開啟的**（`matchMedia` = true），
   所以 transition 全部被壓成 1ms。看到 `transition: 0.001s` 不要以為是 bug。

---

## 6. 附檔（`scratch/layout/`）

- `cdp2.js` / `cdp3.js` — CDP 客戶端（Node 內建 WebSocket，無需 npm ws）
- `p42_beforeafter.js` — 決定性 A/B（修復 vs 重現 vs inline 為何有效）
- `p43_sweep.js` — 六種視窗高度的 A/B，證明死碼無效
- `p45_accept2.js` — 重整後驗收量測
- `p47_sections.js` / `p48_one.js` — 各區段逐一量測
- `p41_settle2.js` — transition 凍結的證據