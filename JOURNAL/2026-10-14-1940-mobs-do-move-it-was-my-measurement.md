# 2026-10-14 19:40 — 「怪物不動」是我的量測假象，不是 bug；但它挖出一個真的缺陷

## 起因

召喚功能寫完後，量測發現召喚的怪 120 幀位移 0px。對照組顯示**地圖原生怪也完全不動**，
於是開始追「怪物巡邏不動」。

## 結論先講：怪物動得很正常

```
f4/Map/Map1/100000004.img，分批跑滿 600 tick（10 秒）
  24 隻怪，8 隻有位移：-131, -405, +65, -424, +252, -68, -336, +552 px
逐輪取樣（每輪 30 tick）看一隻怪的 stance/frameIndex/x：
  怪#8: [stand/2/3 x=2201] [stand/2/3 x=2201] [stand/2/3 x=2201] [stand/2/3 x=2201]
        [move/0/4 x=2203] [move/3/4 x=2257] [move/2/4 x=2197] [move/2/4 x=2137]
        [move/1/4 x=2079] [move/0/4 x=2021] [move/0/4 x=1962] [move/3/4 x=1902]
  frameIndex 在幀數內循環，stand 3 幀 / move 4 幀
```

站 → 走 → 站，動畫幀正常推進。**沒有 bug。**

## 真正的根因：`/frames` 單次呼叫靜默截斷到 60 幀

```csharp
// SimulatorEndpoints.cs:222
int count = Math.Clamp(request.Count ?? 1, 1, 60);
```

60 是瀏覽器的補幀窗口，是**刻意的**（註解寫著 12-frame catch-up 的設計理由）。
問題在於**靜默**：我送 `{"count":600}`，實際只跑 60 幀，回應裡沒有任何欄位說這件事。

於是我量到的是「10 秒」裡的**1 秒**，而怪物在 60 幀內擲不到 `RollLeft`/`RollRight`
（2% 機率 × 2 次擲骰），於是「0 隻在動」。

**分批 60 幀跑滿 600 之後：13/50 隻有位移，最大 433px。**

## 修掉：把截斷講出來

`MapSimFrame` 加三個欄位（回應維持**扁平**，見下）：

```csharp
public int  TicksRequested { get; set; }
public int  TicksRun       { get; set; }
public bool Clamped        { get; set; }
```

實測：

```
POST {"count":600} → ticksRequested=600 ticksRun=60 clamped=true
POST {"count":30}  → ticksRequested=30  ticksRun=30 clamped=false
```

**一開始我把回應包成 `{ frame, ticksRequested, ... }`，那會打破瀏覽器。**
`livewalk.js:253-255` 是 `return f`（整個 body），所有呼叫端讀 `f.x` / `f.mobs`。
改成把欄位塞在 frame 本體上，`f.x` 照常可用。

## 走過的兩條死路（記下來，因為它們看起來都很像真的）

### 一、`RollEvery` 不是根因

`MobPatrol.cs:91` 的 `RollEvery = 30` 註解自承是 own-design（「讓牠走完一步再重新考慮」），
而對標 `Mob.cs:351/354` 是**每幀**擲。我算過：開局 `Direction = 0`（`Mob.cs:199-200`），
擲中率 2%，30 幀擲一次 → 期望 1500 幀 ≈ 25 秒才邁第一步。聽起來像鐵證。

實測（600 tick，分批跑滿）：

| RollEvery | 有位移的怪 | 最大位移 |
|---|---|---|
| 30（現行） | **13/50** | **433px** |
| 8 | 11/50 | 592px |
| 2 | 10/50 | 357px |

**現行值最好。改任何東西都是倒退。** 假設成立、量測推翻 —— 這就是為什麼要量。

### 二、我的儀表程式自己毀掉了 `Step()`

我加了 static 計數器想看每隻怪的分支，其中一個 regex 替換是這樣的：

```python
re.sub(r'(\n(\s+)if \(Dead\)\n\2    return;)', r'\1\n\2{\n\2    NDead++;\n\2    return;\n\2}', t)
```

原本是**沒有大括號**的 `if (Dead) return;`，替換後變成：

```csharp
if (Dead)
    return;
{                       // ← 無條件，而且整個方法到此為止
    NDead++;
    return;
}
```

結果：**每一隻怪的每一次 Step 都在這裡 return**。編譯器其實有警告
（`CS0162 偵測到執行不到的程式碼`），我第一眼只看到「0 error」就略過了。

量測結果 `NDead == NStep == 1488`，我一度以為「怪從生成就是死的」——
其實是我自己的程式碼讓它每次都走到 return。

**兩個教訓：**
1. 看建置輸出要看**警告**，不是只看錯誤碼。
2. 插儀表不要用 regex 改控制流。逐行插入、插完先讀回來看。

## 更正一條我寫錯的 skill 內容

skill 裡記著「RollEvery 從 1 改成 30」，理由是「0/24 mobs changed frameIndex」。
現在知道那個觀測本身建立在錯誤的量測上（單次 `/frames` 只跑 60 幀），
**兩次觀測都不能作為決策依據**。RollEvery 的真實效果是 13/50 vs 10/50，現行值保留。

## 部署狀態

- `MobPatrol.cs`：與調查前**逐行相同**（diff 只差空白行），零淨改動
- `MapSimSessionStore.cs`：`MapSimFrame` 加 3 個欄位；`Summon`/`DespawnSummoned` 保留
- `SimulatorEndpoints.cs`：`/frames` 填 3 個欄位；`/summon` `/despawn` 保留
- 建置 0 error 0 warning
- JS 無改動 —— 這次沒有碰瀏覽器端
- C# 仍是「bin 未含最新」：5590（pid 19300）持有 DLL，約束是不打斷主線；驗證在 :5591