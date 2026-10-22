# 2026-10-17 00:03–00:25 · SEG12 — 找到 UI 那一段的斷點並修掉

> 結論先講：**斷點在「焦點」。綁定用的下拉選單把剛綁的那個鍵吃掉了。**
> 已修，已部署，並且新增的斷言在舊行為下會紅。

---

## 1. 使用者的兩個回答把範圍縮到只剩一個可能

| 問題 | 使用者回答 | 排除了什麼 |
|---|---|---|
| HUD 顯示什麼 | `skills: K=1121008` | 綁定**有寫進** `skillKeys` |
| 方向鍵會走嗎 | 會 | 鍵盤**有掛上**，`onKeyDown` **有收到事件** |

於是 `skillFor()` 在「按鍵進得來」與「技能找得到」之間回傳 null。
它只剩下三道關，全都不該擋住 K：

```javascript
if (e.ctrlKey || e.metaKey || e.altKey) return null;              // 沒有按修飾鍵
if (e.key.length !== 1) return null;                              // K 是一個字元
if (this.keyFor(e.key)) return null;                              // keyFor 只有方向鍵/空白/Z/Ctrl
if (this.skillKeys.get('K')) ...                                  // 有，就是 1121008
```

**剩下唯一會擋的：`e.target.tagName === 'SELECT'`。**

---

## 2. 真正的機制

面板用 `<select>` 讓你選按鍵：

- `walkview.js:1910` 每個技能一列 `const pick = el('select', ...)`
- `walkview.js:1939` `pick.addEventListener('change', ...)` → **立刻提交綁定**
- `walkview.js:1957` 面板是 modal，而 **actions 只有「Clear all」，綁定後不會關閉**

而 `skillFor` 明確拒絕焦點在 SELECT 的按鍵。
**所以：選完鍵 → 綁定成功 → toast 顯示「1 bound」→ 焦點還在那個下拉選單上 → 你按的那個鍵被自己綁定的控制項吃掉。**

沒有錯誤。沒有警告。這就是 26 個綠 suite、19 條斷言、端到端證明全綠，
而一個人按下去什麼都沒有的原因。

**HTTP 測試永遠碰不到焦點。** 這是 W-037、W-050 之後第三個同類的洞。

---

## 3. 修法

**不削弱守則，改成排除這個面板自己的下拉選單。**

那條守則本身是對的，而且有測試保護：編輯器別處的下拉選單不該觸發技能。
但熱鍵面板**整張都是 `<select>`，沒有任何文字輸入框**，而使用它的流程就是
「在這裡選鍵、去那裡按它」。所以：

```javascript
// livewalk.js skillFor()
const inSkillPanel = typeof target?.closest === 'function'
  && target.closest('[data-skill-panel]');
if (target && !inSkillPanel
    && (target.tagName === 'INPUT' || target.tagName === 'TEXTAREA'
        || target.tagName === 'SELECT')) return null;
```

面板的 body 加上 `data-skill-panel` 標記（`walkview.js` `openSkillPanel`）。
另外保留先前的 `blur()`——綁定後把焦點移走，兩道都做。

### 兩條同時成立，已驗

```
PASS  picking in a select does not                      <- 舊守則仍在，別處的下拉選單照樣擋
PASS  a select INSIDE the hotkey panel does not swallow the key
PASS  a target without closest() is handled
35 passed, 0 failed
```

### 回歸測試有牙齒

把 `inSkillPanel` 改回 `false`：

```
FAIL  a select INSIDE the hotkey panel does not swallow the key
33 passed, 2 failed
>>> RED (2 failed)
```

還原後 `35 passed, 0 failed`。**一個抓不到原本錯的測試不算測試。**

---

## 4. 順帶讓診斷一眼可見

HUD 每一幀都顯示綁定的按鍵（`renderHud`）：

```
skills: K=1121008
```

或沒有綁定時 `no skill is bound to a key`。
綁定清單原本只存在於一個必須打開的 modal；一個「按不到」的技能和
「根本沒綁定」在使用者的畫面上長得一樣。現在不用開任何東西就知道該按哪個鍵。

---

## 5. 誠實狀態

```
check   -> 59 items, 0 problem(s)
deploy  -> 59 wwwroot 檔全等；avatar.js md5 未變
node --check livewalk.js / walkview.js -> ok
gate    -> ball-art 紅（W-070，未結）；sim-health 間歇性（SPEC 標 flaky）
```

**gate 不是全綠，我不會說它是。**

---

## 6. 還沒證明的

**這個修法沒有在瀏覽器裡實測。** 瀏覽器腳本被拒絕三次，我不再嘗試。
它是從程式碼推出來的，並且用會在舊行為下變紅的斷言固定住。
**「按下去會不會真的施放」需要使用者自己確認。**

---

## 7. 給下一個 agent 的一句話

**先問「使用者實際按的是什麼」，再寫測試。**
26 個 suite 全綠和使用者按不出來，可以同時為真——
因為 HTTP 測試不會模擬「焦點還在自己綁定的下拉選單上」。
這個專案已經第三次在同一個地方撞到它：W-037、W-050、W-071。