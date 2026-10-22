# 修正：攻擊沒有動作 —— 從「鍵沒接」到「stance 查表沒接」共五層

時間：2026-10-14 14:47（追加，不覆蓋）

## 症狀
按 Z（或 Ctrl）四次，伺服器從頭到尾沒有發過任何 attack 動作。
一開始以為是按鍵沒接到 —— 不是。

## 五層，逐層挖

### 1. `LastAction` 從來沒有 attack 這個值
`MapSimSessionStore.Swing()` 解析成功之後沒有設任何姿勢，
`CharacterPhysics.StepCore` 的分支裡也沒有 attack。
動作清單只有 stand / walk / climb / climb-top / rise / fall / prone。

### 2. 即使加了 attack，還是畫不出來 —— 姿勢名要從武器資料來
對標 `CharLook::getattackstance`（clean-client `CharLook.cpp:648`，
Beidou `:580`）**不是**用武器外觀 enum，是拿武器 WZ 的
`info/attack` 整數（`WeaponData.cpp:35`）去索引一個靜態表
（`CharLook.cpp:682-696`）：

    0 -> {NONE}          1 -> {stabO1, stabO2, swingO1, swingO2, swingO3}
    2 -> {stabT1, swingP1}  3 -> {shoot1}     4 -> {shoot2}
    5 -> {stabO1, stabO2, swingT1, swingT2, swingT3}
    6 -> {swingO1, swingO2} 7 -> {swingO1, swingO2}  8 -> {stabO1, stabO2}
    9 -> {SHOT}

每次攻擊從組裡**隨機挑一個**（`CharLook.cpp:706`
`randomizer.next_int(stances.size())`），不是照順序播完。
`twohanded` flag（`WeaponData.cpp:38`）`getattackstance` 根本沒讀。

### 3. 沒武器就完全沒有攻擊姿勢
`CharLook.cpp:594` 在索引表之前就 return，stance 根本不變。
所以角色沒武器時**不應該**有揮擊動作 —— 這是對標行為，不是缺漏。
實測 `livewalk.js` 建 session 時只傳
`{path, startX, startY, direction}`，沒有武器。

### 4. 兩個「武器」是不同命名空間
`MapSimSession.Weapon` 是 **Afterimage 圖名**（決定 hit box，缺值 fallback
`barehands.img`）；決定 stance 組的是**裝備的 WZ 武器 img 的 `info/attack`**。
合成一個欄位會得到錯的答案，所以拆成 `EquippedWeapon`。

實測這份檔案的武器：
    Weapon/01302000.img/info/attack = 1  -> swingO 組
    Weapon/01402000.img/info/attack = 5  -> swingT 組

### 5. 最陰險的一層：DTO 有欄位，但 live 端組 tick 時沒帶過去
修好伺服器後實測 `action:"attack"` 有送出、stance 也有，
但畫面不動。查到 `livewalk.js` 的 `paint()` 自己組 tick 物件：

    v.ticks = this.trail.map((p, i) => ({
      x, y, action, onGround, dir, step        ← 沒有 attackStance
    }));

`actionFor()` 是從 tick 讀 `attackStance` 的，讀不到就 fallback，
於是維持 stand。**這個症狀跟「沒實作攻擊」長得一模一樣**，
只靠看 action 字會誤判成伺服器沒送。

## 持續時間：用實測不用猜
`/api/animation/audit` 量 `f2/00002000.img/<stance>`：

    stabO1/stabO2/swingO1/swingO2/swingO3/swingT1/swingT2/swingT3
      全部 totalMs = 800

800ms ÷ 60Hz = 48 tick → `MapSimSession.SwingPoseTicks = 48`。
影格之間的推進用每個 frame 自己的 WZ delay（`BodyDrawInfo.cpp:194`），
由 `avatar.step` 負責。沒有計時器的話 attack 鍵是 edge，姿勢只存在 1 tick。

## 驗證

### 路由（實測 HTTP 回應）
    weapon=01302000  -> ["stabO1","stabO2","swingO1","swingO2","swingO3"]  armed=true
    weapon=01402000  -> ["stabO1","stabO2","swingT1","swingT2","swingT3"]  armed=true
    weapon=99999999  -> []  armed=false      (查不到武器，不是「沒資料」)
    attack=0         -> []  armed=false      (0 不在對標表裡)
    無參數           -> []  armed=false      (裸手 = CharLook.cpp:594)

### 實際模擬器（按 Z 四次）
    action/stance 序列:
    attack/swingO1 -> stand -> attack/swingO2 -> stand
                   -> attack/stabO1 -> stand -> attack/swingO3 -> stand
    四次挑到四個不同 stance，全在 attack=1 的組內 —— 隨機挑選生效。

### 畫面
    avatarState: stand1 -> stabO1(2 frames) -> stand1
    影格推進: stabO1#0 -> stabO1#1 -> #0(循環) -> stand1#0
    畫布截圖（應用程式自己的 canvas，swingO2 第 0 影格）：
      劍畫在右手、水平延伸；站立姿勢不會有劍，所以劍的出現本身就是證據。
      臉可見、腳踩在橋面上。

### 無 regression
    trace 平地 y 振幅 0px（左右皆然），與修前相同。

## 踩到的坑
1. **patch 多打一個右括號** —— `})));` 寫成 `}))));`，
   編譯器只在那一行報 CS1002，看起來像路由寫錯。改回就好。
2. **`TryReadString` 不存在** —— `WzSessionService` 只有 `TryResolve`。
   正確形狀是既有的 `TryReadIntChild(node, "info", "attack", out int)`。
   用字串讀會把非數值當成合法值，錯在離開檔案庫很遠的地方。
3. **`SwingPoseTicks` 命名空間** —— 常數在 `MapSimSession`，
   `Swing()` 在 `MapSimSessionStore`，要限定。

## 對標路徑修正（重要）
`C:/_necrocer_src/MapleNecrocer/` **不存在**。
實際在 `C:/MUWORK/GAME/_necrocer_src/MapleNecrocer/`。

## 還沒接的
`sit` 不是按鍵 —— 對標裡 `Player.cpp:481-485` 是坐椅子道具觸發。
DOWN 全部導向 PRONE（`PlayerStates.cpp:64/131/192/252/396`，兩份 diff 全 0）。
另外 Q3：攻擊中 `set_state`/`set_direction` 是硬鎖的
（`Player.cpp:1216/1222`），狀態機在 `is_attacking()` 時直接 return ——
**目前的實作還沒做這個鎖**，攻擊中按方向鍵會立刻中斷揮擊。這是已知缺口。
