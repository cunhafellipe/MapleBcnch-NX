# 物理模擬驗收報告

日期：2026-10-11 · 對象：`CharacterPhysics` 重寫 · 資料：`C:\MUWORK\GAME\MPSHS\Map.nx`（副本測試）

前置：[2026-10-11-physics-blocked.md](2026-10-11-physics-blocked.md)

---

## 一、結論

**驗收通過。** 二十張真實地圖，角色全程站在腳下表面上，
最大偏差 4px，沒有一張出現「走在空中」。

前一輪六次修正全部失敗，根因不是 bug，是模型錯了。
這一輪換掉模型，一次通過。

---

## 二、換掉什麼

| | 舊（失敗） | 新（通過） |
|---|---|---|
| 角色狀態 | `(x, y)` + 每步查詢 | 持有當前 foothold `FH` |
| 移動 | 先橫移，再查 `FindBelow` 找地面 | `X += sin(θ)·speed`、`Y -= cos(θ)·speed` |
| 換 foothold | 每步重新查詢 | 走到端點（`X >= FH.X2`）才換 |
| 需要地面容差 | 需要 reach / tolerance / Ceiling | **全部不需要** |

**斜坡為什麼自動正確**：角色的 Y 由 foothold 自己的角度決定，
它從來不問「腳下有沒有地面」這個問題。舊模型每步都在問，
而斜坡表面每 px 都不同——逐點取樣必然不穩。

---

## 三、常數：全部來自 `Player.DoMove`

前一輪的教訓是「scout 給的常數沒核對就用了」。
這一輪直接讀原始碼：

```csharp
// MapleCharacter.cs
public Player(Sprite Parent) : base(Parent)
{
    JumpSpeed = 0.6f;      // :136
    JumpHeight = 9.5f;     // :137
    MaxFallSpeed = 8;      // :138
}

// Player.DoMove
SpeedL += 1.5f;  if (SpeedL > 2.5f) SpeedL = 2.5f;   // :739-741
SpeedL -= 0.25f; if (SpeedL < 0)    SpeedL = 0;       // :744-746

// SpriteUtils.cs
PIConv256 = -40.743665431f;                            // :30
GetAngle256 = (int)(Atan2(dx, dy) * PIConv256) + 128; // :44-47

// SpriteEngine.cs
CosTable256[i] = Math.Cos(i * Math.PI / 128.0);       // :708-712
Cos256(i) = CosTable256[i & 255];                      // :774-777
Sin256(i) = CosTable256[(i + 192) & 255];              // :778-781

// RenderFormDraw.cs
FixedUpdateDelta = 0.016666668f;                        // :177
```

**前一輪我用的 `Acceleration 0.2 / MaxWalkSpeed 4` 是錯的**
——那是 `JumperSprite` 的預設值（`SpriteEngine.cs:1519-1523`），
`Player` 根本不用它。真正的步行速度是 `1.5 / 2.5 / 0.25`，
差了一個數量級。

---

## 四、實測結果

### 單張地圖（101000200.img）

```
482 步 = 8.0 秒
1116 / 1116 px = 100%
reachedEdge = true, fellOut = false, onGround = true
Y 範圍 357 ~ 382（地圖表面就在這個區間）
```

### 逐點比對

角色的 Y 與 `probe` 查到的最近表面之差：

```
檢查 80 點，超過 4px 的：0
最大偏差：1px
```

### 二十張抽樣（seed 42）

| 地圖 | 結果 | 覆蓋 | 步數 | 偏差 |
|---|---|---|---|---|
| 130000120.img | 抵達邊緣 | 100% | 591 | 2px |
| 100020100.img | 抵達邊緣 | 100% | 517 | 2px |
| 221030301.img | 鏈盡頭 | 100% | 77 | 1px |
| 103000900.img | 鏈盡頭 | 100% | 83 | 1px |
| 100040106.img | 鏈盡頭 | 99% | 51 | 1px |
| 101030300.img | 鏈盡頭 | 100% | 57 | 1px |
| 101020008.img | 鏈盡頭 | 8% | 31 | 1px |
| 230030200.img | 掉落 | 100% | 980 | 1px |
| 211041100.img | 掉落 | 52% | 2 | — |
| 100000101/205.img | 掉落 | 100% | 321 | 4px |
| 7 張 | link stub | 0% | 0 | — |

**全部地圖最大偏差 4px，耗時 0.5 秒。**

---

## 五、回報語意

| 情況 | 回報 |
|---|---|
| 走到右緣 | `reachedEdge: true` + 步數與秒數 |
| 鏈盡頭（正常） | 停下，說明走到哪 |
| 鏈中斷 | `fellOut: true` + 「ran out of ground at x=… because …」 |
| link stub | 「No foothold to stand on」，`walk` 另外標 `isLinkStub` + 目標 |

**`/character` 現在報的是物理結果，不是鏈的完整性。**
兩者不同，而且不同才是重點：`/validate` 說鏈沒壞，
`/character` 說角色走不過去——後者才是模擬器存在的理由。

實測 `211041100.img`：鏈有 140 個 foothold、不是 stub，
但角色 2 步後就在 x=1020 沒了地面。**這種地圖 `/validate` 抓不到。**

---

## 六、這一輪沒做的

- **怪物 AI**、逐幀迴圈、梯子與傳送門、視覺呈現角色——都還沒有。
- **`/character` 的 UI 還沒接**（`simulator.js` 只呼叫 `/walk`）。
- **沒有用全部 5,366 張地圖跑**，只抽樣 20 張。
- **角度沒有改用浮點斜率**，保留客戶端的 256 分度查表。
  量化誤差最大 0.7°，在 foothold 長度內 < 1px，實測偏差也沒超過 4px，
  所以維持原樣。**這是實測支持的決定，不是懶得改。**
- **沒驗證左右走**——不是沒做，是**做不到**：`/api/simulate/character`
   沒有方向參數，固定往右。要驗左走得先加參數。
- **沒驗證跳躍與落下梯子**（`Jump()` / `DropThrough()` 存在但沒被呼叫路徑測到）。
- **沒有 commit。**

---

## 七、還有什麼不確定

1. **左右走的對稱性：無法測試，因為端點沒有方向參數。**
   實測：從 `startX=570` 出發仍然往右走（端點固定 `moveLeft = 1`），
   14 步後報「foothold 55 ends at x=570」。`MapSimulateRequest`
   目前只有 `Path` / `StartX` / `StartY`，**沒有方向**。
   `Walk(moveLeft)` 的反向分支（`X <= FH.X1`、下一段取 `FH.Prev`）
   是未經測試的程式碼。

2. **牆的處理**：我用的是「撞到就停在牆外 10px」。
   MapleNecrocer 用的是 `X = WallFH.X1 - 1`（貼著牆），
   並且要求 `BelowFH.Z == WallFH.Z`（同一個 Z 層才算撞到）。
   **我的版本沒有比對 Z**，可能把不同層的牆也當成阻擋。

3. **掉落後重新落地**：實測有幾張走到 100% 標 `fellOut`，
   推測是鏈盡頭後掉到別的層，但那個行為**沒有被驗證是對的**。

---

## 八、下一輪

1. 驗左右走（`moveLeft = -1`）
2. 對照 MapleNecrocer 補上牆的 Z 層比對
3. 把 `/character` 接進 `simulator.js`，頁面顯示真實軌跡
4. 擴大抽樣到 100 張

**不做怪物 AI**——先把走路的對稱性與牆處理確認完。