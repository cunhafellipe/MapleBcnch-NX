# 2026-10-14 13:34 — 地圖走動時缺塊

## 問題（使用者直接指出）

「地圖沒有完整顯示 有缺塊 之類的」

## 排除掉的三個錯誤假設

| 假設 | 怎麼證偽 | 結果 |
|---|---|---|
| NX 裡 terrain 沒讀到 | `/api/children` 逐層列 | layer 0-7 的 `tile` 節點本來就是 `childCount:0`，內容全在 `obj`；144 筆 art 全在 |
| art 解析失敗（缺檔） | 掃 `doc.art` | 76 筆全部有 `path`，**0 missing** |
| 圖沒載進來 | 攔截 `drawSceneItem` | **找到了** |

## 根因

`wwwroot/js/walkview.js`

`loadVisible()` **全檔只有一個呼叫點**（`load()` 裡，第 469 行）。
它只在載入時跑一次，帶著當下相機看到的範圍。

角色走動讓相機移動 → 新區域的物件進入視野 → **沒有任何東西再去要求它們的圖**。

關鍵在於「快取槽位存在」不等於「圖到了」：每個 art key 在載入時就先建好
`img:null, ok:false, missing:false` 的槽位。
所以 `this.images.get(key)` 永遠回得到東西，但 `slot.img` 是 null，
`drawSceneItem` 就靜靜地 skip 掉那一塊 —— 不報錯、不佔位，就是不畫。

**量測**：一次右走期間 `drawSceneItem` 被呼叫 33,274 次，
其中 **11,303 次**拿到的是 `img: null` 的槽位。

## 修法

`draw()` 在 `if (!view) {{...}}` 保護之後插入：掃 `layer.tiles` + `layer.objs`，
找出「落在視野內且 `slot.img` 與 `slot.missing` 皆為空」的項目，
有就呼叫 `loadVisible()`，完成後 `this.draw()` 重畫。

用 `this._visibleLoading` 單一旗標防重入 —— `draw()` 每幀都跑，
沒有防護會同時開幾十個批次迴圈去重複請求同一批 PNG。

## 驗證

真 ESM parse（`node --check` 會把 module 語法誤判）：**40 檔 40 pass**。
md5 比對 src / bin 一致。

乾淨量測（重新載入頁面，不掛任何 prototype 攔截）：

| 時機 | 視野內項目 | 畫出 | 缺圖 |
|---|---|---|---|
| 初始 | 27 | 27 | **0** |
| 走 5 秒 | 29 | 29 | **0** |
| 停 2 秒 | 31 | 31 | **0** |

使用者確認：「ok了 地圖沒有缺塊了」。

## 量測上的兩個坑（下次別再踩）

1. **別把畫面外的項目算進去。** 我第一版攔截 `drawSceneItem` 時數了全部項目，
   其中 40,587 筆在畫布外 —— 那些本來就不該載入，數進來會把 5.6% 的真實缺圖
   稀釋成看起來還行的數字。要先扣掉 `cx/cy` 出界的。
2. **`slot` 存在 ≠ `img` 存在。** 判定必須看 `slot.img`，不能只看 `images.get()` 回不回的到。

## 未修（使用者說一次修一個，先記錄）

- `wholeMapZoom` 在 `walkview.js:882` 算出 0.1837（5378px 世界塞進 988px 畫布的正確比例），
  但沒有任何控制項把它設進 `this.zoom`，實際 `scale` 一直是 1。
  結果是永遠只看得到地圖的 1/5，要靠平移才能看完。**取景問題，不是缺塊。**
