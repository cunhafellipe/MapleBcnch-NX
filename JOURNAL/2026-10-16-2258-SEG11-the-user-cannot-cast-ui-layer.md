# 2026-10-16 22:58–00:05 · SEG11 — 使用者說「我按了沒反應」：找 UI 那一段

> 結論先講：**使用者是對的，我之前證明的東西對他的問題沒有幫助。**
> API 證明技能邏輯正確，但使用者的路徑還有一個 UI 層的斷點，而且**沒有任何 HTTP 測試
> 可能抓到它**。

---

## 1. 使用者的原話改變了問題

> 「有證據我知道 有實現我知道 但我用 沒有辦法施放技能 這是重點」

前面整段我都在證明 `POST /frames {"skillId":"1121008"}` 會造成傷害。
那**從來不是他的路徑**。他的路徑是：

```
點「Skills…」→ 在下拉選單選一個鍵 → 按那個鍵
```

我這段做的是把「面板綁定 → 按鍵解析 → 施放 → 傷害」整條鏈路用**面板實際會發的請求格式**
走完，並讀取**實際載入的** `livewalk.js` 證明 `onKeyDown` 裝的是 `skill.skillId`。
結論是：**這條鏈路本身是對的**。所以斷點在鏈路**之外**——UI 層。

---

## 2. 找到的 UI 斷點（W-071）

### 面板用 `<select>` 綁定，而 `skillFor` 刻意拒絕 SELECT 焦點

```javascript
// walkview.js:1910 —— 每個技能一列下拉選單
const pick = el('select', { class: 'field-input no-icon', ... });

// walkview.js:1939 —— 改變就立刻綁定
pick.addEventListener('change', () => { ... submit(...) });

// walkview.js:1957 —— 而且面板綁定後不會關閉（actions 只有「Clear all」）
```

```javascript
// livewalk.js:999 —— 焦點在輸入元件上就不施放
if (target && (target.tagName === 'INPUT' || target.tagName === 'TEXTAREA'
              || target.tagName === 'SELECT')) return null;
```

**這個守則是對的，而且有測試保護**（`sim-skills-check` 有
`picking in a select does not` 且通過）——在文字欄位裡打字不該觸發技能。

**所以使用者的流程是：**

1. 點「Skills…」→ modal 打開
2. 從下拉選單選 K → **綁定成功**，toast 顯示「1 bound」
3. **焦點還留在那個 `<select>` 上，modal 還開著**
4. 按 K → **鍵被下拉選單吃掉**，`skillFor` 回 null

沒有錯誤。沒有警告。面板說綁好了，技能就是不會施放。

**這正是為什麼 19 條斷言 + 26 個 suite + 端到端證明全部是綠的，
而使用者按下去什麼都沒有。**

### 修法：結束這次互動，不是削弱守則

綁定成功後把焦點從面板移走（`active.blur()`），讓下一次按鍵的 target
既不是 INPUT 也不是 SELECT，能走到 walk。守則不動。

---

## 3. 讓診斷一眼可見

**HUD 現在永遠顯示綁定的按鍵**（`walkview.js` `renderHud`）：

```
skills: K=1121008
```

或沒有綁定時顯示：

```
no skill is bound to a key
```

**為什麼這重要**：一個「按不到」的技能，和「根本沒綁定」在使用者的畫面上長得一樣，
而綁定清單原本只存在於一個必須打開的 modal。把它放上每一幀都看得見的 HUD，
「該按哪個鍵」變成看一眼的事。

---

## 4. 順帶查過、**不是**原因的地方

| 假設 | 量測結果 |
|---|---|
| `bindSkills` 讀錯欄位，`skillKeys` 是空的 | **否**。`livewalk.js:853` 讀 `r.bindings`，實測回應 `{'K': '1121008'}`，正確 |
| `attach()` 從沒被呼叫，整個鍵盤都沒反應 | **否**。`simulator.js:313` 在開地圖時 `live.start()` → `attach()`，方向鍵會動 |
| 伺服器拒絕了綁定 | **否**。`rejected: []`，`count: 1` |
| 傷害沒算 | **否**。103 ≤ 封存的 `level/1/damage = 135`，怪的 HP 350 → 247 |

---

## 5. 狀態

```
check   -> 59 items, 0 problem(s)
deploy  -> 59 wwwroot 檔全等，avatar.js md5 未變
walkview.js node --check -> ok
gate    -> RED SUITES: 1，而且只有 1（ball-art，W-070 已知未結）
```

**這個 gate 不是全綠，我不會說它是。** ball-art 在乾淨伺服器上 3/3 BAD=1，
[W-054](pm/work/W-054-a-mob-s-projectile-is-invisible-the-ball-art.md) 說它修好了。

---

## 6. 還沒證明的

**焦點這個修法沒有在瀏覽器裡驗過**——瀏覽器腳本被拒絕三次，我不再嘗試。
它是從程式碼推出來的，不是量出來的。

所以下一步需要使用者回答一個可以用一眼回答的問題，見對話。

---

## 7. 給下一個 agent 的一句話

**API 全綠和使用者按不出來，可以同時為真。**
HTTP 測試永遠不會碰到「焦點還在下拉選單上」。
這個專案已經有五個工作項目卡在「只有畫面能回答」——W-037、W-050、
以及現在這個。**下一次寫判官的時候，先問「這條路徑上使用者實際按的是什麼」。**