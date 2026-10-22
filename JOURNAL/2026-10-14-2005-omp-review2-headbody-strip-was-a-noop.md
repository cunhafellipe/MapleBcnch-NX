# 2026-10-14 20:05 — omp review #2 的兩個發現：Head/Body 的「剝除」按鈕是無效的

omp 審查 Head/Body picker 的改動後回報兩個缺陷。兩個都成立，都已修。

## [1] HIGH — 剝除按鈕按了沒反應

### 根因不在前端，在協議

`walkview.chooseEquip(slot, '')` 原本是 `delete next[slot]`。
`avatar.slotMap()` 送出去的 map 就少了一個 key。
然後伺服器端 `LookService.cs:166-168`：

```csharp
body ??= BodyPath(fileId);      // 呼叫端沒提 Body → 補 00002000.img
if (body is null) return null;
head ??= HeadPath(fileId);      // 呼叫端沒提 Head → 補 00012000.img
```

**對「部分 map」是對的，對「剝除」是錯的。** 兩者在協議上是同一件事：缺席。
所以剝掉頭皮 → 伺服器補回 `00012000.img` → 按鈕等於沒按。

### 為什麼 `stripped` 陣列救不了

前端本來就有 `stripped` 機制，但 `avatar.js:325-326` 是：

```js
this.stripped = Object.keys(this.equip)
  .filter((s) => !next[s] && !DEFAULT_EQUIP[s]);
```

`!DEFAULT_EQUIP[s]` 這條是為了保護「只傳部分 map 的呼叫端不要把所有沒提到的槽都當成剝除」。
**但 Head 和 Body 都在 `DEFAULT_EQUIP` 裡**，所以這兩個槽無論使用者點什麼都不可能進 `stripped`。

### 修法：讓剝除在線路上成為一個事實

| 檔案 | 改動 |
|---|---|
| `walkview.js` `chooseEquip` | `delete next[slot]` → `next[slot] = ''`，並直接維護 `this.avatar.stripped`（不再依賴 `setEquip` 的推斷） |
| `avatar.js` `slotMap` | `delete map[slot]` → `map[slot] = ''` |
| `LookService.cs` `Compose` | 新增 `strippedSlots`，收到空字串就記錄並 `continue`；`??=` 兩行加上 `!strippedSlots.Contains(...)` 守衛 |

### 實測（:5591，`POST /api/simulate/look`）

| 輸入 | 用到的 sheet | |
|---|---|---|
| `{Coat}` | `00002000, 00012000` | 基準 |
| `{Coat, Head:""}` | `00002000` | **頭皮真的脫掉了** |
| `{Coat, Body:""}` | *(空)* | 無體型可合成 → 回 null |
| `{Coat, Head:"", Body:""}` | *(空)* | 同上 |
| `{Coat, Head:"00012009.img"}` | `00002000, 00012009` | 換皮不受影響 |
| `{Coat, Body:"00002009.img"}` | `00002009, 00012000` | 換皮不受影響 |
| `{Coat}`（不提 Head/Body） | `00002000, 00012000` | **部分 map 仍有預設** ← 關鍵回歸 |

最後一行是這次修改的風險點：把「缺席」與「明示剝除」分開之後，
**沒提到的槽必須仍然拿到預設**。實測成立。

`Body:''` 回空是誠實的答案而不是缺陷 —— 沒有體型 sheet 就沒有東西可合成
（`Compose` 的 `if (body is null) return null;`）。

## [2] LOW — `RootImages` 在根層只有資料夾時提前返回

```csharp
// 改前
if (names.Count > 0) return names;      // 有「任何」子節點就返回
// 改後
if (sawImage) return names;             // 只有真的有 .img 才返回
```

archive 根層同時放著兩張 body sheet 和 Cap/Coat/... 資料夾。
**根層只有資料夾、沒有 sheet 的 archive，會在第一次探測就返回，永遠試不到
`{fileId}/Character`** —— 而那是 WZ 版把 sheet 放的位置。

沒有改成「兩條路徑都列舉再串接」，因為 NX archive 兩個層級都有時會讓每張 sheet 出現兩次。
現在的條件同時滿足兩邊：根層有 sheet 就用根層，沒有才往下探。

實測（`/api/simulate/look/slots?fileId=f1`）：`Head=9, Body=9`，
其他槽 `Cap-1` / `Coat` / `Weapon-1` / `Hair-1` / `Face-1` 全部正常，沒有回歸。

## 順帶修掉的：靜默截斷（上一輪查到）

`MapSimFrame` 加 `TicksRequested` / `TicksRun` / `Clamped`。
`/session/{id}/frames` 的 60 幀上限是瀏覽器補幀設計、刻意保留；
**但靜默截斷**害我花了半小時追一個不存在的 bug，現在說得出來了。

一開始我把回應包成 `{frame, ticksRequested, ...}`，那會打破瀏覽器 ——
`livewalk.js:253-255` 是 `return f`（整個 body），所有呼叫端讀 `f.x` / `f.mobs`。
最後是把欄位塞在 frame 本體上，保持扁平。

## 部署

- `avatar.js` md5 `44903703e625df26cd18b63b5dec80d1`
- `walkview.js` md5 `33d2bc077b5f3571d9fcf2765a1be5de`
- 兩者已同步到 `bin/Release/net10.0-windows/wwwroot/js/`，`node --check` 通過
- C# 建置 0 error 0 warning；`bin` 仍待主線自然重啟（5590 pid 19300 持有 DLL）