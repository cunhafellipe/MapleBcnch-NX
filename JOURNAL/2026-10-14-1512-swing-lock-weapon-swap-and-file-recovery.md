# 攻擊期間硬鎖 + 武器切換；中途弄壞 MapSimSessionStore.cs 及其復原

時間：2026-10-14 15:12（追加，不覆蓋）

## 一、揮擊鎖：對標三道鎖的合併

Q3 的答案是「不能被打斷」，而且不是一個條件，是三道：

- `PlayerStates.cpp:112-113`（站）、`:174-175`（走）、`:219-220`（跳）、
  `:364-365`（飛）—— 攻擊中 `update()` 直接 return
- `Player.cpp:1214-1217` `set_direction` 在攻擊中忽略參數
- `Player.cpp:1220-1224` `set_state` 在攻擊中忽略參數
- `Player.h:59-61` 註解明寫意圖：「Set flipped ignore if attacking」

最強的證據是方向鍵**根本讀不到**：`PlayerWalkState::update` 在 `:174` return，
而 `hasrightinput` 的判斷在 `:177` 之後 —— 繞過 setter 也沒用。

實作放在 `StepCore` 最前面，任何寫入之前就拒絕這一次 tick：

```csharp
if (AttackPoseTicks > 0) {
    AttackPoseTicks--;
    if (AttackPoseTicks > 0) { LastAction = "attack"; return; }
}
```

計時器仍然遞減，因為對標是「動畫結束才解除」：`Player.cpp:868-870` 在
`aniend` 清 `attacking`，而 `aniend` 是 `CharLook.cpp:273-274` 的 `stframe == 0`
—— 影格時鐘歸零。放在 `if` 裡而不是整段 return，讓最後一幀直接落到下一個
輸入，不會多凍一幀。

順手刪掉原本的舊分支（`else if (AttackPoseTicks > 0 && OnGround ...)`）——
被頂部取代後是死碼，留著會讓下一個讀的人以為兩條路徑都有效。

**實測**：按 Z 的同時按住右，攻擊期間 58 幀 x 位移 **0px**；
放開方向鍵後立刻恢復行走（-3231 → -3169）。

## 二、武器切換：stance 是武器的物理事實，不只是圖層

裝備欄本來就能換 `Weapon-1`，但只影響畫出來的圖層 —— 揮擊姿勢是伺服器
從武器自己的 `info/attack` 推出來的，所以換了劍卻還在揮斧的姿勢。
兩邊要分開才能對。

新增：
- `MapSimSession.EquippedWeapon` 從 `init` 改成 `set`
- `MapSimSessionStore.SetEquippedWeapon(id, weapon)`
- `POST /api/simulate/session/{id}/weapon`，body `{"equippedWeapon":"01402000"}`
- `livewalk.js` `setEquippedWeapon()` + `LiveWalk.setWeapon()`
- `LiveWalk` 建構子把 `view.live = this`，`walkview.chooseEquip` 在
  `slot === 'Weapon-1'` 時呼叫

`view.live` 宣告在建構子**而不是 `load()`**：`load()` 是在 driver 建好之後
才跑的，放那裡會在每次換圖時把註冊清掉 —— 第一次就是這樣。

**實測**：
```
01302000 (attack=1) → swingO1 / swingO2 / stabO1 / swingO3
換 01402000 (attack=5) → swingT1 / swingT2          ✓ 組跟著武器變
卸下武器                → 完全沒有 attack 動作，只有 stand
```
最後一項是對標行為（`CharLook.cpp:594` 沒武器就沒有攻擊姿勢），
不是「壞掉」—— 這也是為什麼卸下要送 `null` 而不是留空字串。

## 三、我把 MapSimSessionStore.cs 弄壞了，然後救回來

### 怎麼壞的
我要加 `SetEquippedWeapon`，用 `t.rindex('\n}')` 找類別收尾再插入。
但那個檔案裡第一個 `\n}` 是 `Sweep()` 的收尾，不是類別的，於是 splice 把
類別前半段整段切掉：**1950 行 → 599 行**，`class MapSimSessionStore`、
`MapSimSession`、`MapSimFrame`、`Swing()`、`PortalDto`、`MapTransferDto`
全部消失，而且檔案開頭直接是 `Hold(`。

檔案**不在 git**（今天新建、未追蹤），`git stash list` 空，
`obj/` 沒有 .cs 副本，csproj 沒開 `EmbedAllSources` → 磁碟上沒有備份。

### 怎麼救的
`ilspycmd` 已經裝在 `C:\Users\e7896\.dotnet\tools\`，從 14:39 那次
**成功建置的 DLL** 反組譯：

```bash
export DOTNET_ROOT="C:/Users/e7896/AppData/Local/dotnet10"
export PATH="$DOTNET_ROOT:$PATH"          # 沒有這行會報缺 .NET 10
ilspycmd -t MapleBench.Services.Simulate.MapSimSessionStore MapleBenchX.dll -o out
ilspycmd -l c MapleBenchX.dll | grep Simulate   # 列出還有哪些型別要拿
```

`-l c` 是關鍵：用來發現 `PortalDto` / `MapTransferDto` /
`MapSimBatchRequest` 也在遺失範圍裡，而且只有編譯錯誤會告訴你它們不見了。

**結果：全部 10 個型別找回，C# 0 error，行為一致**
（trace 平地 y 振幅 0px，與損壞前相同；今天的 prone / swing pose /
EquippedWeapon 都在反組譯碼裡，因為 DLL 是修好之後建的）。

### 真正的損失
**所有註解沒了。** IL 不攜帶註解，反組譯出來的 838 行對原本 1950 行，
差的全是我寫的對標引用。那些是這個專案的治理規則要求的
（「每行新邏輯要引用一個對標函式」），要靠 JOURNAL 和 skill 補回來。

### 以後的做法
- **不要用 `rindex`／模糊索引去 splice 原始碼。** 用 `patch`（有唯一錨點比對）
  或先讀出確切行號再寫行。`rindex('\n}')` 在一個有 40 個方法的方法體裡
  沒有語義意義。
- **改大檔案前先確認有備份。** 這個檔案不在 git 裡，代表它沒有 rollback。
  真正該做的是先 `cp` 一份到 scratch。
- **反組譯是有效的手術手段**，前提是 (1) 有一個用當前原始碼成功建置出來的
  DLL，(2) 知道要拿回哪些型別。它拿回行為，拿不回註解。

## 驗證
```
build: 0 errors, 0 warnings
server pid 19300, 16 個 nx 開啟
trace 平地 y 振幅 0px（左右）
揮擊鎖: 攻擊中 58 幀位移 0px，放開後恢復行走
武器切換: attack=1 → swingO 組；attack=5 → swingT 組；卸下 → 無 attack
ESM 40/40
```

## 還沒做
- `sit` 依對標是坐椅子道具（`Player.cpp:481-485`），不是按鍵，不該接鍵盤
- ALERT / DEAD / FLY / SHOOT* stance 未接線
- `wholeMapZoom` 算出 0.1837 但 `scale` 恆為 1
- MapSimSessionStore.cs 的對標註解需要補回