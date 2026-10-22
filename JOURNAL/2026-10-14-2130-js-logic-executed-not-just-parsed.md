# 2026-10-14 — 第三輪：把 JS 從「只過語法檢查」變成「真的跑過」

前兩輪的 JS 驗證只有 `node --check`。它證明檔案能解析，
不證明任何邏輯正確 —— 熱鍵→技能的解析、「這個鍵是不是行走按鍵」的判斷、
技能→姿勢的映射，**從來沒有被執行過一次**。

新增 `MapleBench/tools/sim-skills-check.mjs`（24 項），在 Node 裡直接載入
`avatar.js` / `livewalk.js` / `walkview.js` 跑它們匯出的純邏輯。

**這不能取代桌面截圖。** 它回答「邏輯是不是照它宣稱的做」，
不回答「使用者看不看得到面板」—— 工作室守則 §2.1 講得很清楚。

---

## 1. 測試自己也錯了兩次，而且兩次都是「錯得很有說服力」

### 1a. 我挑的「可用熱鍵」是 D —— 而 D 是走路的右移鍵

```
livewalk.js:883    case 'd': case 'D': return 'right';
```

第一版斷言「綁定 D 應該可以施放技能」，結果 `skillFor` 回 null，測試失敗。
**產品是對的**：綁了 D 就吃掉了 WASD 的右移，`skillFor` 拒絕它正是設計。

而我給「陰影鍵」��清單寫的是 `['Left','Right','Up','Down',...]` ——
寫的是 `event.key` 的**箭頭**名稱，漏了 `a/A d/D w/W s/S` 這些**字母**名稱。
所以那條「沒有任何行走鍵可以被綁」的測試，在缺少字母的情況下**空洞地通過**了。

修正：正樣本改用 K / M（`keyFor` 不認的鍵），陰影清單改成讀原型上的
`keyFor` 真正的鍵集合。

### 1b. 「徒手揮擊不該畫成姿勢」失敗了 —— 因為我沒說它在地面上

```
avatar.js:219-221
  const moving = action === 'walk'
    || (action === null && (tick?.speed ?? 0) > 0.01)
    || (!tick?.onGround && !tick?.onLadder);
```

我傳的 tick 沒有 `onGround`，於是 `!undefined` 為 true → `moving` 為 true → `walk1`。
**這是正確行為**：一個沒有說自己在地面上的幀，就是空中的幀。

這一條後來保留成正向斷言，因為它是對的。

> 這是這個專案第三次「量測工具自己壞掉」。
> 前兩次是 PowerShell（`$input` 自動變數、`@($null).Count` 回 1）。
> 第三次是 JavaScript 測試本身。**每一次都被校準擋下來，每一次校準條件自己也曾經錯過。**

---

## 2. 24 項涵蓋的東西

| 群組 | 內容 |
|---|---|
| `skillFor()` | 已綁定的字母解析（含小寫、含 Shift）、未綁定的鍵、方向鍵、功能鍵、Ctrl/Alt/Meta 組合、在 input/textarea/select 裡打字 —— 全部不解發 |
| 陰影防護 | 逐一嘗試綁定 `ArrowLeft a A ArrowRight d D ArrowUp w W ArrowDown s S ' ' Spacebar Control z Z`，**一個都不能成為熱鍵** |
| `actionFor()` | 揮擊用伺服器給的姿勢名；徒手在地面上是 `stand1`；沒說在地面上的幀算空中 |
| `skillPoseTick()` | 施放幀被改寫到既有 swing 路徑；**原 tick 不被就地修改**（是複本）；改寫後 `actionFor` 真的畫出該姿勢；沒有姿勢名就原樣通過；行走幀原樣通過 |
| 姿勢名存在性 | **連線到 `http://127.0.0.1:5591` 實查** `brandish1` 確實是開啟封存裡的姿勢且 frameCount > 0 |

最後一項是對的關鍵一環：伺服器送來的姿勢名如果不存在，畫面會**安靜地什麼都不畫**。

---

## 3. 這個檢查會不會自己說謊

```
$ node MapleBench/tools/sim-skills-check.mjs
24 passed, 0 failed            exit 0

$ BASE=http://127.0.0.1:59999 node MapleBench/tools/sim-skills-check.mjs
exit 1                          （連線失敗，測試中止，不是「通過」）
```

沒有伺服器時它失敗退出，而不是把那 23 項印成通過。
路徑是從腳本自身位置算出來的，所以從任何工作目錄跑都一樣。

---

## 4. 這一輪補掉的驗證缺口

| 之前 | 現在 |
|---|---|
| JS 只做過 `node --check`（能解析）| 純邏輯 **24 項真的執行過** |
| 方法名跨檔一致（讀碼確認）| 同上，另加欄位名與端點 payload 一致性（第二輪）|
| 伺服器送的姿勢名「應該存在」| **實查存在**（連線驗證）|

**仍然沒有桌面截圖。** 面板與按鈕「看得到」這件事我依然不能宣稱。