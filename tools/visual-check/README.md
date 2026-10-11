# 視覺驗收工具

這三支腳本用 Chrome DevTools Protocol 直接驅動頁面，不依賴 puppeteer。
存在的理由只有一個：**API 回 200 不等於畫面上有東西**。

這個專案已經被「回傳值正確但畫面是空的」騙過一次——素材調色盤回傳了
109 組資料，但沒有人看過格子裡到底有沒有圖。

## 為什麼不用 puppeteer

CDP 用內建的 `fetch` + `WebSocket` 就夠了，少一個套件就少一份環境依賴。
更重要的是：腳本能在同一輪裡同時回報**截圖**與**DOM 事實**——
包括直接讀 canvas 像素確認有沒有上色。空白畫布和有圖的畫布，
肉眼在截圖上常常分不出來，讀像素不會騙人。

## 先備

```bash
# 1. 建置
cd MapleBcnch-NX
dotnet build MapleBenchX.sln -c Release

# 2. 關掉任何還在跑的舊行程（會鎖住 DLL）
powershell -NoProfile -Command "Stop-Process -Name dotnet -Force -EA SilentlyContinue"

# 3. 啟動伺服器
cd MapleBench/bin/Release/net10.0-windows
dotnet MapleBenchX.dll --port 5302 --no-warmup

# 4. 啟動 headless Chrome（另開一個終端機）
chrome --headless=new --remote-debugging-port=9222 \
       --user-data-dir=<某個臨時資料夾> --no-first-run --disable-gpu about:blank
```

Chrome 路徑通常在 `C:\Program Files\Google\Chrome\Application\chrome.exe`。

## 用法

**注意：輸出路徑請用相對路徑或純 ASCII。** 路徑含中文時，
Node 收到的 `process.argv` 編碼會壞掉，截圖會被寫到磁碟機根目錄。

```bash
cd MapleBcnch-NX/tools/visual-check

# 一般頁面截圖 + DOM 探測
node shot.mjs http://127.0.0.1:5302/ out.png 8000

# 開地圖編輯器、載入地圖、確認畫布渲染
node editor.mjs http://127.0.0.1:5302 f1/Map/Map0/000000001.img canvas.png

# 調色盤：確認格子有圖 + 語言切換
node palette.mjs http://127.0.0.1:5302 pal
```

## 每支腳本會回報什麼

| 腳本 | 關鍵輸出 |
|---|---|
| `shot.mjs` | `probe:` canvas 尺寸/可見性、img 數量、頁面文字、`paintedPct` |
| `editor.mjs` | 分頁點擊結果、地圖載入結果、`paintedPct: [主畫布, 小地圖]` |
| `palette.mjs` | 三個 kind 的 API 數量、縮圖載入數、語言切換前後對照 |

## 選擇器的兩個坑

1. **別用中文文字找元素。** 語言可以切換，文字選擇器會跟著壞。
   用 `data-i18n="Editors"` 這種穩定 hook。分頁按鈕就是這個屬性。
2. **要點最深層的那個匹配。** 用 `textContent === '地圖編輯器'` 找會同時
   命中外層 app shell，點了等於沒點。腳本取 `hits[hits.length - 1]`。

## 地圖編輯器要怎麼開起來

編輯器分頁預設停在 Mob，地圖編輯器是左側 rail 的其中一項。
載入地圖要走 section 自己的 `openMap(path)`，直接打 API 會讓 UI 空白：

```js
const me = (window.app || globalThis.app)?.mapedit;
await me.openMap('f1/Map/Map0/000000001.img');
```

調色盤是覆蓋在畫布上的抽屜，要另外開：

```js
me.togglePanel('pal', true);
```