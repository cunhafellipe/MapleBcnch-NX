# 2026-10-14 10:45 — 動畫異常偵測器：建立，並推翻自己的第一條規則

接續 [1040](2026-10-14-1040-skill-cast-chain.md)（同輪，不覆蓋）。
計畫：`docs/計畫-技能特效動畫異常.md` 第 6 項（使用者指定用途：動畫是否有異常）。

---

## 1. 宣告：自設計

**對標專案沒有動畫審計器。** MapleNecrocer 是編輯器客戶端，姿勢在那裡是「對」或「不存在」，
從來沒有被量測過。所以這五條規則是我寫的，來源與門檻都記在原始碼註解裡。

**但輸入全部不自立**——每個數字都由既有的 `AnimationService.Describe()` 從 archive 讀出。
審計器不自己重讀 archive，因���那樣它可能和渲染器對「一個幀是什麼」有不同理解，
屆時查出來的問題就是審計器的問題。

---

## 2. 量測對象（來自 WZ，不是選的）

實測才發現兩種資料形狀：

| | 怪幀 | 角色幀 |
|---|---|---|
| 內容 | `head` `lt` `origin` `rb` | `body` `arm` `face` `delay` |
| `delay` | **無** | 有 |
| `origin` | 有 | **無**（在 layer 上） |

`f7/0100100.img/stand/0` 有 `origin`/`lt`/`rb`/`head` 而**沒有 delay**；
`f2/00002000.img/stand1/0` 有 `delay` 而沒有 `origin`（錨點在各 layer 上）。

---

## 3. 為了量 missingDelay，先讓「有沒有 delay」變得可見

`AnimationService.ReadDelay` 在讀不到時回落 `DefaultDelayMs = 100`。
**那個 100 和 archive 寫的 100 數值完全相同、不可區分**——所以這條規則原本無法存在。

加 `AnimationFrameDto.DelayFromArchive`（附加欄位，不破壞既有呼叫）：

```
TryReadDelay(WzCanvasProperty, out int delay) -> bool   // 舊 ReadDelay 委派給它
TryReadDelay(WzImageProperty, out int delay) -> bool
FrameLayers.DelayFromArchive                            // NX 容器分支也要帶
```

五個 DTO 建構點全部標記（216 / 331 / 504 / 611 / 396）。
`ReadDelay` 一度變成回傳 bool 的表達式本體（`=> TryReadDelay(..., out _)`），
建置抓到 2 個 CS0029，改成明確的 block body。

---

## 4. 五條規則

| 規則 | 等級 | 抓什麼 |
|---|---|---|
| `zeroDelay` | error | 幀時長 ≤ 0，時鐘永遠離不開它 |
| `missingDelay` | warn | 多幀姿勢裡某幀 delay 來自預設而非 archive |
| `sizeJump` | warn | 幀面積變化 > 該姿勢自身中位數的 3 倍（地板 8px） |
| `originOutsideCanvas` | warn | 錨點落在畫布外 |
| `layerSetChanged` | warn | 幀與幀的層集合不同 |

`sizeJump` 的門檻用**該姿勢自己的中位數**而非固定像素：800×600 的全屏技能與 26×16 的武器姿勢
差兩個數量級，單一門檻對兩者都不誠實。

---

## 5. 路由

```
GET /api/animation/audit/rules            五條規則 + 門檻 + 等級定義 + 自設計宣告
GET /api/animation/audit?path=            單一姿勢
GET /api/animation/audit/image?path=&stances=   一個 image 的多姿勢 + missing 清單
```

`missing` 是刻意回的：**「不存在」和「審過且乾淨」是兩個不同的答案**，
只回乾淨的會讓數量對不上。

---

## 6. 實測結果

```
怪 0100100   審4  clean2  errors0  異常2  缺[alert, attack1, regen]
  stand  frames=1  100ms   CLEAN
  move   frames=5  900ms   sizeJump@1: 62px (中位 -18, 門檻 54)
  hit1   frames=1  600ms   CLEAN
  die1   frames=9  1260ms  sizeJump@2: 500px (中位 -132, 門檻 396)

怪 0100101   審4  clean4  errors0  異常0
怪 0100200   審0（全部姿勢不存在，如實回報）

角色 00002000 審10 clean6 errors0 異常10
  stand1/walk1/prone/ladder/swingO1/proneStab  CLEAN
  alert   sizeJump×2 + originOutsideCanvas×3 (lHand origin (15,24) outside 8x9)
  sit     originOutsideCanvas: body origin (19,28) outside 25x25
  jump    originOutsideCanvas: lHand
  swingT1 sizeJump×2 + layerSetChanged
```

**`errors = 0` 是正確結果，不是沒抓到東西**——
下面第 7 節說明我原本抓到一個，量測後證明那是我錯。

---

## 7. 我的第一版規則錯了，量測推翻它

`layerSetChanged` 原本是 **error**，規則是「同一姿勢的每幀必須由相同的層組成」。
它在 `swingT1` 第 2 幀報錯：`[arm, armOverHair, body] -> [arm, body]`。

直接讀 archive 驗證：

```
swingT1/frame0  delay=300  arm(16x20)  armOverHair(18x24)  body(22x31)
swingT1/frame1  delay=150  arm(18x15)  armOverHair(20x19)  body(28x31)
swingT1/frame2  delay=350  arm(17x22)  body(39x28)          ← armOverHair 不見了
```

再掃 `00002001.img` 全部 86 幀：

```
有 arm/armOverHair 的幀: 86
  兩者都有    5
  只有 arm   81
  只有 a-o-h  0
```

**結論：`arm` 單獨出現是常態（81/86），`armOverHair` 是例外（5/86）。**
`armOverHair` 是手臂在頭髮上方的 z 層變體，不是必備層。
而且 `swingT2` 三幀**全部沒有 arm**。

**層集合本來就會變。這是這套美術的畫法，不是缺陷。**
我的規則會在 archive 上製造數百個假錯誤。已降級為 warn，量測數字寫進註解。

**同一個錯誤第三次出現**：位置/名稱（10:35 猜 `default/cap`）、名稱/位置（10:40 猜技能書第一個子節點）、
**規則/資料（10:45 假設層集合必須一致）**。前兩次猜的是名字，這次猜的是語意——
但成因相同：**我沒有先去量，就先下了判斷。**

---

## 8. 另一個坑：Describe 對不存在的節點是丟出，不是回 null

`AnimationService.Describe("f7/0100100.img/alert")` 會拋
`'alert' not found under 'f7/0100100.img'.`，
於是審 7 個姿勢時，只要怪物沒有其中一個，**整個 image 請求就 404**。

`Alert` 改成自己 try/catch 並回 null（姿勢不存在是「缺席」不是「壞掉」，
跳過是審計器的責任），另外收 `MissingStances` 回報。
量測證明這個修法必要：`0100100` 沒有 `alert`/`attack1`/`regen`。

---

## 9. 現況

- 後端 :5590，pid 28120，DLL 10:42 / 2,440,704B，bin == obj
- 16/16 nx 全開
- 新增：`Services/Simulate/AnimationAuditService.cs`、`Api/AnimationAuditEndpoints.cs`
- 修改：`Services/AnimationService.cs`（`DelayFromArchive` 旗標）、`Program.cs`、`Api/Endpoints.cs`

---

## 10. 剩下（使用者指定用途的覆蓋）

| 用途 | 狀態 |
|---|---|
| 測試地圖 | ✅ `wz_map_cast` / `wz_map_render` / `wz_map_patrol_mob` / `wz_map_advise` |
| 測試裝備 | ✅ `CharLookComposer`，119/119 部件像素解碼 |
| 測試技能 | ✅ `SkillCastService`，65 本技能書、特效 11/11 像素解碼 |
| **測試特效** | ⚠️ 技能特效已通；`Effect.nx` 的 17 個分類（BasicEff/Direction/SetEff…）**尚無瀏覽端點** |
| **測試怪物行動** | ⚠️ 有巡邏/還手；**無行為異常檢查**（動作卡住、離地、反覆） |
| 動畫異常 | ✅ 本次 |

下一個單位：`Effect.nx` 分類瀏覽與渲染，以及怪物行為異常檢查。