# 2026-10-14 09:16 — 背景只顯示 2 塊：根因是三個猜錯的欄位

接續 [2026-10-14-0910-mcp-render-tools.md](2026-10-14-0910-mcp-render-tools.md)（同一輪，不覆蓋）。
使用者看圖後回報：**「背景為什麼只有 2 塊有顯示」**。

---

## 1. 結論

背景不是「畫了 2 塊」，是**6 筆 Back 全部畫到同一張 906B 的空白圖**，
而那張空白圖在螢幕上看起來就像「只有 2 塊」。三個欄位全部猜錯。

真實資料（`f1/Map/Map0/000010000.img`，6 筆 Back）：

```
0: b|grassySoil|0|0  type=3  x=0    y=0     a=96   → 256×256    906B  空白填充
1: b|grassySoil|0|1  type=4  x=100  y=-203  a=255  → 1024×579 357KB  藍天白雲
2: b|grassySoil|0|2  type=0  x=300  y=55    a=255  → 2493×340 475KB  遠景樹線
3: b|grassySoil|0|3  type=0  x=179  y=-249  a=255  → 315×268   60KB  物件
4: b|grassySoil|0|5  type=0  x=-400 y=218   a=255  → 1024×257 202KB  物件
5: b|grassySoil|0|6  type=0  x=624  y=200   a=255  → 1024×294 217KB  物件
```

---

## 2. 三個錯

### (1) frame 索引取錯欄位 ← **這就是「只有 2 塊」的原因**

`art` 的格式是 `b|<set>|<variant>|<frame>`。**第三段恆為 0，第四段才是影格。**

我原本寫 `Back/<set>.img/back/<variant>` → 六筆全部去抓 `back/0`（906B 空白），
而 `back/0` 是 256×256 的半透明填充（`a=96`），鋪在深灰底上看起來幾乎不存在。

該 set 的九個影格差異極大：

| 影格 | 尺寸 | 大小 | 內容 |
|---|---|---|---|
| back/0 | 256×256 | 906B | 空白填充 |
| back/1 | 1024×579 | 357KB | **藍天白雲（彩虹島的天空）** |
| back/2 | 2493×340 | 475KB | 遠景樹線 |
| back/3 | 315×268 | 60KB | 裝飾物 |
| back/4 | 1024×257 | 192KB | 裝飾物 |
| back/7 | 2497×343 | 483KB | 裝飾物 |
| back/8 | 200×200 | 635B | 空白 |

### (2) 錨點：應該是 `x - ox`，我寫成置中

Back canvas 自帶 origin（`back/0` 是 `origin=(128,128)`）。
瀏覽器實作 `mapedit-view.js:971`：

```js
const bx = back.x - (back.rx * wl) / 100 - (meta.ox ?? 0);
```

我寫的是 `- img.Width / 2`。對 `ox == w/2` 的剛好相等，但 `back/5` 是
`ox=512, w=1024`（相等）、`back/3` 是 `ox=157, w=315`（相差 0.5px，無害）——
**這項其實沒造成錯**，但仍是靠巧合對上，不是靠理解。已改成照抄。

### (3) 沒有平鋪 ← **真正的視覺缺口**

`type` 決定重複方式（`mapedit-view.js:987-990`）：

```js
const tileX = type === 1 || type === 3 || type === 4 || type === 6 || type === 7;
const tileY = type === 2 || type === 3 || type === 5 || type === 6 || type === 7;
```

這張地圖的 type 3（雙向平鋪，256×256 天空填充）與 type 4（橫向平鋪，1024×579 雲層）
**都靠平鋪才鋪得滿**。我只畫一張就停，天空自然只覆蓋左上角一塊。

另外還漏了 `cx`/`cy`（自訂平鋪間距，實測皆為 0 → 用 `w`/`h`）、
`a`（透明度 96 = 半透明）、`f`（水平翻轉）、以及反向迭代
（Back 在 v83 是由遠到近排序，`backs.Reverse()`）。

**修完 `drawn`：271 → 332**（+61 = 平鋪複本）。0.5 秒。

---

## 3. 為什麼是「照抄」而不是「推導」

`mapedit.js` 檔名就在專案裡：
`MapleBench/bin/Release/net10.0-windows/wwwroot/js/mapedit-view.js:976` 的 `drawBacks`。

瀏覽器端已經有完整且正確的 Back 繪製。我的三個錯誤**全部**是我在沒有讀它的情況下
推導出來的。這與前一輪「用 curl 手寫 CDP」是同一類錯誤：
**專案裡已經有答案，先猜再驗證是最貴的路徑。**

現在 `MapPainter.DrawBacks` 的註解明確標註它是 `drawBacks` 的 port，
並列出三個原始錯誤，避免下次有人（或我）又把它當原創重寫。

---

## 4. 過程中的一個 build 陷阱

診斷碼加進去後 `BackLog` 永遠是空的，一度以為 `doc["backs"]` 沒進到迴圈。

真相：

```
bin/.../maple-mcp.dll   09:06:13   165,888 B   ← 舊的
obj/.../maple-mcp.dll   09:12:29   168,960 B   ← 新的
```

**舊 MCP 程序還活著、鎖住 bin 裡的 DLL，MSBuild 複製失敗卻仍回報 "0 errors"。**
（先前有跑出 MSB3021，但那次是被 kill 掉的程序沒釋放乾淨。）

診斷步驟：比較 `bin` 與 `obj` 的 mtime。修法：
用 psutil 只殺含 `maple-mcp` 的程序（**不要碰 MapleBenchX**），或直接刪掉
`bin/Release/net10.0-windows/maple-mcp.*` 再建置。

**「建置成功」不等於「新 DLL 生效」——要驗 bin 的時間戳。**

---

## 5. 另外修掉：`find_backend` 死埠優先

`tools/maple_mcp.py` 的 `find_backend()` 原本回傳 `default="http://127.0.0.1:5480"`
即使沒有任何服務在跑，於是 MCP 對著空埠講話，回報
`MapleBench is NOT answering`——一個把「沒服務」說成「服務壞掉」的錯誤。

現在：
- `WZ_SERVER` 環境變數優先，**但只在它真的有回應時**；
- 探測不到就 `raise RuntimeError`，並列出試過的埠 + 啟動指令。

另註：後端 PID 是我用 `--port 5590 --no-browser --no-warmup --allow-multiple` 起的。
既有 `:5480` 的程序是我接手前就在跑的，不屬於本輪。

---

## 6. 最終驗收（三張圖，全部含背景）

| 地圖 | drawn | foothold | 軌跡 | spawn | 耗時 | PNG |
|---|---|---|---|---|---|---|
| 000010000 小菇菇 | 332 | 83 | 8 | 3 | 0.5s | 1.01 MB |
| 000040001 蝸牛打獵場II | 614 | 133 | 263 | 27 | 0.5s | 416 KB |
| 000050000 危險的樹林 | 401 | 65 | 201 | 19 | 0.2s | 235 KB |

視覺確認（`vision_analyze`）：
- **000010000**：藍天白雲 + 遠景老樹 + 蘑菇屋 + 遠景樹線全部出現，草地未被蓋掉。
- **000040001**：純藍天空鋪滿，該地圖的 Back set 只有純色填充，**沒有雲素材是正確的**
  （`back` 目錄的九個影格本來就沒有雲）；地形、27 個怪物紅點清楚。

**殘餘的深灰空白不是 bug**：該地圖 `bounds.top = -480`，而天空只覆蓋到 `y = -203`。
上方的空白在原始遊戲裡也是黑的（v83 地圖沒有大氣層）。

---

## 7. 動到的檔案

```
修改  MapleMcp/MapPainter.cs    DrawBacks 重寫為 mapedit-view.js:976 的 port
修改  MapleMcp/RenderTools.cs   移除診斷欄位
修改  tools/maple_mcp.py        find_backend 不再回傳死埠
```

建置 0 錯誤。診斷碼（`BackLog`）已從出貨碼移除並實測確認
（`backLayers key removed: True`）。**未 commit。**