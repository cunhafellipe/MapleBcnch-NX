# 2026-10-14 11:30 — 使用者回報的五個異常：四個實測修好，一個修好但驗不到

接續 [1045](2026-10-14-1045-animation-audit-and-a-wrong-rule.md)（同輪，不覆蓋）。
使用者回報五個既有異常，逐項查現行程式碼、對標、修改、**實測**。

---

## 結論表

| # | 報告 | 狀態 | 證據 |
|---|---|---|---|
| 1 | 怪物無限左右瘋狂翻轉 | ✅ **修好並實測** | 4/13 → **0/12** |
| 2 | 往左走但動畫撥往右 | ✅ **修好並實測** | 鏡像不符像素 **318 → 0** |
| 3 | 玩家臉/手有位移 | ✅ **修好並實測** | 同 #2，同一根因 |
| 4 | 人物掉落到地圖外面 | ✅ **修好並實測** | x 精確夾在 `[-3232, 2026]` |
| 5 | 怪物死掉動畫卡最後幀 | ⚠️ **程式已修，無法端到端驗證** | 見第 6 節 |

外加**兩個新發現的缺陷**（第 7 節）。

---

## 1. 怪物無限左右瘋狂翻轉

### 根因不是 Turn()，是 Roll()

`reversals` 全程 0，轉向根本沒經過 `Turn()`。對標 `Mob.cs:354`：

```csharp
switch (Random.Next(200))
{
    case 50:  FlipX = false; MoveDirection = Left;  break;
    case 100: FlipX = true;  MoveDirection = Right; break;
    case 150: MoveDirection = None;                 break;
    case 199: if (!GetHit1) { if (MoveType == MoveType.Jump) DoJump = true; } break;
}
```

**四個精確值、無 default** — 196/200 的結果什麼都不做，怪保持原方向。
本專案寫成了 `roll < 50` / `roll < 100` / `roll < 150` **四個區間**，
命中率 **100%** 而不是 **2%**。每 30 ticks 重抽一次方向，走 30 ticks 再抽，
永遠走不到任何東西的盡頭 —— 在原地左右振盪。

**註解寫的行號是對的，程式碼不是。** 註解說「four cases over the same 200-wide range」，
程式卻是範圍比較。

### 附帶修的：foothold 盡頭的轉向

`Mob.cs:568-582` / `:647-660` 的比較是**嚴格**不等式（`X < FallEdge` / `X > FallEdge`），
foothold 交接（`:621-630` / `:698-706`）才是 `<=` / `>=`。
原程式兩者都用 `>=`/`<=`，釘在邊界後下一幀又成立 → 每幀翻一次。

另外：完全沒有下一段 foothold 時 `FallEdge = FH.X2 + 5`（5px 外伸），
遇到牆才是 `FH.X2`。原本兩者同一個邊界。

`Turn()` 加了 `Direction == 0` 防呆：`-0 == 0` 會把 `Facing` 一起清成 0。

### 實測（f6/Map/Map0/000000002.img，3000 幀）

```
修前  4/13 瘋狂翻轉，最差 65 次翻轉/120 採樣，淨位移 536px
修後  0/12 瘋狂翻轉，12 隻中 5 隻有實際移動
```

剩下的不動的是 Stand 型 —— `Mob.cs:352` 對 `MoveType.Stand`/`Fly` 根本不重抽，正確。

**探針本身也錯過一次**：第一版用 `(mobId, foothold)` 當 key，
兩隻同 id 同 foothold 的怪互相覆蓋，測出「x 在 732/995 完美對調」——那是兩隻怪不是一隻。
改用起始索引配對後才對。**量測本身也要驗。**

---

## 2 + 3. 往左但撥往右、臉手位移（同一根因）

### 位置一直是对的，圖從來沒翻

`avatar.js` 的 `rectOf` 早就有位置鏡像：
```js
x: at.flip ? at.cx - dx - part.width : at.cx + dx,
```
用 server 送的真實合成資料驗算，三個部件的鏡像位置**全部精確正確**
（`body` 朝右 `[984,1005]` → 朝左 `[995,1016]`，正是以 cx 為軸的鏡像）。

怪物一直有 `ctx.scale(-1,1)`。**`drawAvatar` 沒有** — 每個部件都正著畫。
所以位置翻了、圖沒翻：臉飄到另一邊但還是朝右。

對標 `MapleCharacter.cs:1337-1356`：`FlipX` 時 `this.Flip = -1`，
**部件的繪製本身也翻**，不只是 origin。

### 修法

`ctx.translate(px(r.x)+dw, py(r.y)); ctx.scale(-1,1); drawImage(img,0,0,dw,dh)`。
以目標矩形右緣為鏡像軸，矩形位置不變，兩半不會打架。

### 實測（真實瀏覽器裡跑真正的 `WalkViewer.drawAvatar`）

只 stub 網路層（`api.look` 資料來自 server、圖來自 `/api/canvas`），
繪製程式碼是頁面上跑的那一份。左右各畫一張 64×80，比對「朝左 == 水平翻轉(朝右)」：

```
新版   inkR=1586 inkL=1586  不符像素 0    最大 alpha 差 0
舊版   inkR=1586 inkL=1582  不符像素 318  最大 alpha 差 255
```

對照組跑了同一個測試的舊寫法 —— **測試抓得到原本的 bug**，這比單看新版通過更重要。

---

## 4. 人物掉出地圖

對標 `MapleCharacter.cs:777` / `:829` 是**位置性**測試：

```csharp
if ((X < Map.Left + 20) || (Attack) && (JumpState == JumpState.jsNone)) SpeedL = 0;
```

條件在 `if (FaceDir == Left && SpeedR == 0 && !InLadder)` 裡，與鍵盤無關。
本專案寫成 `if (moveLeft < 0 && _x < bd.Left + EdgeMargin)` — **要求正在按方向鍵**。
沒有按鍵時誰也不會修正位置，而且對標靠**夾住攝影機**（`:688-691`, `:757-760`）把這個洞藏起來。

改成位置性歸零速度，**外加夾住位置**（對標不需要，因為出界的狀態永遠不會被畫出來；
這裡會被畫出來 —— 跟垂直的地板夾制同一個理由）。

### 實測

```
往右 3000 幀：x ∈ [-3210.5, 2026.0]，合法區間 [-3232, 2026]  → 未越界 ✓
往左 3000 幀：x = -3232.0（精確停在左界）                        → 未越界 ✓
y ∈ [180, 213.9]，bounds.bottom=322 → 未越界 ✓
```

---

## 5. 怪物死掉卡最後幀 —— 程式修了，驗不到

對標 `SpriteEngine.cs:486-492` 把 sprite 加進 `DeadList`，
`:282-287` 直接 `Free()` —— **屍體不存在**，不是留在畫面上。

本專案一直送 `Dead = true`，客戶端 `mobsprite.js:75` 對 dead 的怪回
`{action:'die1', animated:false}` —— 一張永遠停著的靜態最後一幀。

已改：`Describe` 組 `result.Mobs` 時 `if (patrol.Dead) continue;`。
EXP、等級、死亡計數都在 frame 上、不在這個清單裡，沒有損失；
對標的碰撞測試也跳過 Deaded 的 sprite（`:623`, `:646`）。

### 為什麼驗不到

**1200+ 幀攻擊，傷害恆為 0，沒有任何一隻怪死過。**
角色站在怪 28px 處（射程內）、`onGround=true`、`facing` 正確、四種武器都試過，
`damage` 永遠是 `[]`，`action` 永遠是 `walk`/`stand`。

所以我**不能**說 #5 端到端驗過了。程式改了，觀察不到。

---

## 7. 順帶發現的兩個缺陷（未修）

**A. 攻擊從不生效。** 角色貼著怪揮擊，`Swing()` 的命中永遠不成立。
`Swing()` 本身看起來合理（用 `MobAnimation.For` 取得畫面上那一幀的框，
而不是猜 `stand/0`），問題出在更前面 —— 揮擊動作根本沒啟動，
`action` 不會離開 `walk`/`stand`。這不是這五個報告之一，但它讓 #5 無法驗證。

**B. 無輸入時角色會漂移。** 送 `{}`（什麼都沒按），x 仍以約 2.2px/幀 右移，
`action` 顯示 `walk`。有殘留速度或斜坡角度沒有歸零。

---

## 8. 檔案

- `Services/MapEditor/MobPatrol.cs` — `Move()` 依 `Mob.cs:568-582`/`:621-630`/`:647-660`/`:698-706` 重寫；
  `Roll()` 改精確比對；`Turn()` 防 `Direction==0`
- `Services/MapEditor/CharacterPhysics.cs` — 水平邊界改位置性 + 夾位置
- `Services/Simulate/MapSimSessionStore.cs` — `Describe` 不再送 dead 的怪
- `wwwroot/js/walkview.js` — `drawAvatar` 鏡像圖層
- 動畫審計器部分見 [1045 日誌](2026-10-14-1045-animation-audit-and-a-wrong-rule.md)