# 2026-10-14 — 臉的座標不對：臉的 canvas 有自己的 brow 偏移，我們整個丟了

使用者回報：「我看臉應該是有顯示的 但是座標不對」。
這是 `任務計畫.md` 的 B1「臉座標太高」，而前人留的結論是**錯的**。

---

## 1. 前人的結論與它錯在哪

`avatar.js:86-93` 留著一段：

> The face geometry itself is right… `BODY.neck (-4,-32) - HEAD.neck (0,15) + HEAD.brow (-4,-5) = (-8,-52)`
> which is BodyDrawInfo.cpp:99 verbatim, and the rendered shift measures (-8,-52) exactly.
> **So do NOT "fix" the face by moving it** — every number here already equals the benchmark.

**錨點那一層確實是對的**，這句話在錨點上沒錯。
錯的是它假設「錨點對了臉就對了」—— 臉的位置還有**另外兩層**沒有被驗證：
canvas 自己的 `origin`，以及 canvas 自己的 `map/brow`。

## 2. 量到的（同一個請求裡，避免跨請求互減）

```
faceAnchor = (-8,-52)     head = shift(-4,-47) origin(19,17) screen(-23,-64)
face       = shift(-8,-52) origin(13,8)   screen(-21,-60)
hair       = shift(-8,-52) origin(14,12)  screen(-22,-64)
```

`screen = shift - origin` 三者都自洽。所以「錯」不在這些算式的任何一步 ——
**在臉少了一個位移**。

## 3. 對標怎麼處理臉的 map/brow

`Character/Look/Face.h:84-95`：

```cpp
Frame(nl::node src) {
    texture = src["face"];
    Point<int16_t> shift = src["face"]["map"]["brow"];   // ← 封存裡是 (-1,-12)
    texture.shift(-shift);
    delay = src["delay"];
    ...
}
```

而 `Texture::shift` 是 `origin -= amount`（`Graphics/Texture.cpp:126-129`），
所以這個 shift 落在 **origin** 上：`origin` 由 `(13,8)` 變成 `(13,8)+(-1,-12) = (12,-4)`。

臉於是畫在 `faceAnchor - (12,-4)` = **(-20,-48)**。

我們畫在 **(-21,-60)** → **左 1px、上 12px**。與回報的「座標不對／太高」吻合。

## 4. `CollectFace` 為什麼會漏

```csharp
// 修前
Leaf leaf = new() { Node = child, Path = path, Map = MapPoints(child) };
Point shiftBy = faceAnchor;                       // ← map 讀進來了，從沒用
```

`leaf.Map` 已經含 `brow`，只是沒拿來用。
旁邊的頭髮路徑**有**用（`CollectHair`，`shiftBy = Sub(hairAnchor, brow)`），
裝備也有（`shiftBy = Sub(anchor, own)`）—— 只有臉漏了。

修法與頭髮一致：

```csharp
Point shiftBy = faceAnchor;
if (leaf.Map.TryGetValue("brow", out Point brow)) shiftBy = Sub(shiftBy, brow);
```

`anchor - brow - origin` 與 `anchor - (origin + brow)` 是同一個算式，
和對標作用在 origin 上等價。兩個分支因此**用同一種寫法**，不會再漂。

## 5. 這個偏移是逐項不同的

`f1/Face/` 有 540 個項目，`map/brow` 每個都不同。實測 14 個：

| id | brow | 修後 screen | 對標 | |
|---|---|---|---|---|
| 00020000 | (−1,−12) | (−20,−48) | (−20,−48) | OK |
| 00020002 | (−1,−12) | (−19,−46) | (−19,−46) | OK |
| 00020004 | (0,−13) | (−20,−46) | (−20,−46) | OK |
| 00020008 | (0,−14) | (−21,−46) | (−21,−46) | OK |
| 00020009 | (−5,−14) | (−19,−46) | (−19,−46) | OK |
| 00020010 | (−3,−12) | (−21,−48) | (−21,−48) | OK |
| 00020013 | (0,−14) | (−21,−47) | (−21,−47) | OK |

**14/14 逐位元相符。** 修前這 14 個全部高 11–14px ——
所以「臉太高」不是一個固定偏移，是每張臉各自的量。

## 6. 順帶確認頭髮沒有同一個毛病

`CollectHair` 已經套 brow。實測 12 個頭髮，其中 `00030010` 的 brow 是 **(−3, 1)** 非零 ——
若沒有套用，畫面位置會差 3px 而這個檢查會報 MISMATCH。它報 OK，所以確實有套。

> 這個檢查對 brow 為零的項目是**同義反覆**（0 套用與不套用結果相同），
> 所以特意挑了非零的那一個來證。`00030010` 就是那個證據。

## 7. 過程中我自己錯了兩次

1. **我說「臉的 canvas 沒有 origin，替代值是中心 (13,8)」。**
   錯在**看錯層級** —— 我查的是 `.../default` 的子節點（只有 `face`），
   沒查臉這張 canvas **自己**的子節點。WZ 的 `origin`/`map`/`z` 是 canvas 的屬性。
   真相是 face canvas **有** origin，就是 (13,8)，我們讀對了。
2. 我第一次推 origin 是拿「不帶裝備」的 `faceAnchor` 去減「帶裝備」的臉的 screen ——
   兩個不同請求的數字。同一個請求裡重測才對上。

差一點就把一個**已經正確**的合成路徑判成壞的。

## 8. 回歸

部署回 `bin/Release`，三處 md5 一致，`avatar.js` 仍是 `a1eb2eb4…`（本次未動 JS）。

```
6.1 爬梯      5/5 梯子上下皆通、0 幀出界      ALL LADDERS PASS
6.2 攻擊姿勢  8/8 姿勢在宣告組內、徒手為空    VERDICT ok
6.4 技能      施放鎖/梯子拒絕/守門             全對
JS 邏輯       24/24                           0 failed
head / hair   shift 與 screen 未變（本次只動臉）
parts 總數    16，unresolved 空
```

**仍然沒有桌面截圖。** 臉的位置是否對到你滿意，需要你自己看畫面確認。