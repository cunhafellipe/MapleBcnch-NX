# 地圖切換後 renderer 卡死：60Hz 的補載掃描

時間：2026-10-14 16:10

## 症狀

快速切換地圖後頁面整個沒反應。`Runtime.evaluate` 連 `1+1` 都逾時，Chrome 與
dotnet 的 CPU 增量都是 0（2 秒視窗量測）—— 不是忙迴圈，是零 CPU 的掛起。
伺服器全程健康（`/api/files` 3ms）。

## 排查路徑（與被推翻的假設）

| 假設 | 量測 | 結果 |
|---|---|---|
| 面板重建太重 | `render()` 平均 2.1ms | 推翻 |
| 伺服器端慢 | `/api/simulate/character` 3–7ms | 推翻 |
| 60Hz 輪詢洩漏 | 切圖前後皆 47 req/s | 推翻 |
| 某張特定地圖 | 401 張隨機取樣，第 1–3 張就掛 | 推翻 |
| alert/confirm/Atomics.wait/debugbar/同步 XHR | 原始碼全無 | 推翻 |
| 圖片請求風暴 | `/api/canvas` 計數 = 0 | 推翻 |

**量測陷阱（記下來）**：頁面變成背景分頁時 Chrome 會凍結其計時器——曾量到兩次
`walk` 之間相隔 21.9 秒、期間無任何事件。量測前必須 `Page.bringToFront()` 且
確保只有一個分頁。

## 真正的二分法

在頁面內停用元件後連續切圖：

| 停用什麼 | 結果 |
|---|---|
| `render()`（不重建面板） | 第 1 次切圖即掛死 |
| `live.start()` + tick 迴圈 | **5/5 成功** |
| 只停 `viewer.draw()`（保留網路迴圈） | **6/6 成功** |

→ 兇手是 `viewer.draw()`，由 live walk 的 60Hz rAF 迴圈呼叫。

## 根因

`walkview.js` 的 `draw()` 裡有一段「找出走進視野但還沒有點陣圖的磚塊」的掃描
（`if (!this._visibleLoading)` 區塊）。它會走訪**每一個圖層的每一個 tile 與 obj**
來決定要不要補載，而它跑在 live walk 每秒 60 次的 `draw()` 上。

磚塊補載是低頻事件——一個批次來回約 300ms——每 0.016 秒重掃一次整張地圖的圖層
結構沒有任何意義。

## 修法

1. **補載掃描節流到 5Hz**（`_visibleCheckAt`，200ms 門檻）。200ms 遠小於一個磚塊
   批次的來回時間，所以磚塊落地後下一次掃描就會補上，視覺上無差異。
2. **移除每幀的陣列配置**：`[...(layer.tiles), ...(layer.objs)]` 每圖層每幀配置
   一個新陣列；改為直接走訪兩組清單。`loadVisible()` 內同一處一併修。
3. **`_visibleDone` 從未被寫入**卻被 `draw()` 讀取（恆為 `undefined`）——移除。
4. **livewalk `tick()` 的非同步世代守衛**（獨立的真實缺陷）：`tick()` 是 async 且
   會停在 `await stepFrames()`。切圖時 `stop()` 把 `running` 設 false，隨即
   `start()` 又設回 true；那個仍停在 await 的舊 tick 恢復後在結尾的
   `if (this.running)` 會看到 true 而再排一格 rAF，留下 `stop()` 永遠取消不到的
   孤兒迴圈。加 `this.gen` 世代號，`tick()` 進場時記錄、外出前比對。

   實測此修正有效：切圖後 ticks/s 維持 60.0（未修正時會累加）。

## 驗證

修補前：401 張地圖隨機取樣，第 1–3 次切圖必掛。
修補後：**連續切圖 20/20 全部成功**（3505–3544 ms，含每次 3.5s 等待）。

回歸（`f16/Map/Map1/100000003.img`，445 個圖項）：初始 389 張已載入 → 行走 6 秒後
433 張。節流未造成缺圖。

## 附帶

`_visibleCheckAt` 的初值 `-1e9` 確保第一次 `draw()` 一定會掃描。