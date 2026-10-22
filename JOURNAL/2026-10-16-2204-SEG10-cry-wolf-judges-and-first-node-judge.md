# 2026-10-16 22:04–22:40 · SEG10 — 關掉會亂叫的判官，補上第一個空洞，修好驗收執行器

> 結論先講：**gate 從 2 紅變 0 紅；兩個紅燈都是判官自己的錯，不是產品的錯。**
> 同時為 `node` 區域寫出第一個判官，並把「驗收條件不會被執行」那個洞補上——
> 過程中驗收執行器自己壞了四次，每一次都被它自己抓出來。

---

## 1. W-067 · attack-after：判官對**正確**的行為報錯

### 症狀

```
>>> the entry became testable 7 frames in, not 96.0
>>> ... and yet it was testable; the gate is wrong
BAD=2
```

第二行最嚴重：**它印出「我是錯的」，然後把它翻譯成產品有錯。**
會亂叫的 gate 會被忽略，被忽略的 gate 什麼都保護不了（`pm/GATE.md:78-83`）。

### 成因（用它自己的輸出證明，不是猜）

```
created on frame 0 at elapsed=1383ms          <- 第一次「看到」時已經 1383ms 老
first testable frame: 13 (elapsed=1600ms of delay=1600ms)
that is 13 frames after it was FIRST OBSERVED; the archive's delay is 96.0 frames
```

**它把「探針第一次看到 entry 的幀」當成「entry 被建立的幀」。**
entry 在被看到前已存在 1383ms（89 幀），所以「建立後 7 幀」是錯的量測。

正確答案是 `(delay - age) / frame`：修後實測

```
correct answer is (1600 - 1383) / 16.67 = 13.0 frames
first testable frame: 13
attackAfter is honoured exactly
ok    no frame before the delay was testable
BAD=0
```

**公式預測 13.0，實測 13。** 斷言會自我驗證。

第二個自我矛盾的檢查（印「未到延遲」卻因「已到延遲」而計 BAD）已刪除，
換成唯一真正重要的不變量，直接從幀資料檢查：**延遲到之前沒有任何一幀可測。**

---

## 2. sim-health：查了，**沒有**重現，不假裝查到了

**這個 suite 不是種子可重現的**：`sessionId` 是 GUID
（`MapSimSessionStore.cs:2311`），`CombatRandom = new Random(StableSeed(id+":combat"))`
（`:2320`）。**「單跑 3/3 綠」是運氣不是確定性。**

| 判別實驗 | 結果 |
|---|---|
| A：同一伺服器連跑 3 次 | 3/3 綠 |
| B：先跑 ladder-all + attack-test + skill-test（製造 ≥13 個洩漏 session）再跑 2 次 | 2/2 綠 |

**兩個主要假設都沒重現。** 誠實結論：原因仍未確定，已標 `flaky: true`
記在 `pm/SPEC.yaml`，不會被當成已解決。

---

## 3. W-068 · `node` 區域的第一個判官

`coverage` 說 `node` 是 high criticality 且 **NO JUDGE**。現在有了：
`node-verify.py`，**13 條斷言，3/3 綠**，已進 gate。它斷言的是承諾：

| 斷言 | 背後的承諾 |
|---|---|
| `/api/children` 的 descriptor **不得帶 `value` 鍵** | R-2：最常見的量測錯誤 |
| 同一節點用**任意大小寫**都解析得到同一值 | T-10：`incLUk` 是真實存在的小寫 k |
| 未知路徑是**錯誤**而非靜靜的 null | R-5 |
| 編輯只留記憶體、且 `/api/changes` 標記 dirty | README |

**不開封存、不存檔。**

### 判官抓到**自己**兩個 bug

1. 選到空的 `SubProperty`（`.../reactor` 沒有子節點，也不是能當文字編輯的值，
   編輯端點回 HTTP 400）。修正：類型必須在 SCALARS 裡，不能只看「沒子節點」。
2. **`/api/changes` 讀錯兩次**：先假設裸陣列（拿到 None），再從外層讀 `id`
   （實際每筆嵌套在 `{"file": {...}}`）。**兩次都是對正確回答的路由報紅。**

---

## 4. W-057 關閉：npc-label-check 也會斷言了

原本 28 行全是 `Invoke-RestMethod` 與 `Write-Host`，零比較零 throw。
還有**第三種更安靜的失敗**：迴圈在第一個有 NPC 的區域 `break`、
沒有的 `continue`，所以「整張圖都沒有 NPC」時它**什麼都沒驗就通過**。

現在三條斷言，且證明報得出紅：

```
-Map 000000002.img  -> 3 assertions, 0 failed   (3/3 綠)
-Map 999999999.img  -> FAIL a map with NPCs was actually examined
                       >>> RED, exit 1
```

`coverage` 現在印 **`suites that cannot report red: (none)`**。

---

## 5. 驗收執行器自己壞了四次，每一次都被它自己抓出來

`pm.py verify` 是這一輪才真正被使用的東西，而它第一次跑就紅——**因為它自己**：

| # | 症狀 | 真相 |
|---|---|---|
| 1 | 每個 powershell 步驟都 `expect NOT FOUND` | `sp.call()` 只回傳 exit code，文字直接噴到 console，所以永遠比不到 |
| 2 | `-File -NoProfile` | 我把 `-File` 之前的參數丟掉了，PowerShell 讀成「執行名叫 -NoProfile 的檔案」 |
| 3 | 腳本 exit=0 但輸出空 | **`UnicodeDecodeError: 'cp950'`**——腳本印 NPC 中文名，capture 執行緒在 subprocess 裡直接死掉 |
| 4 | 地圖名變成 `'000000002.img` | **frontmatter 解析器無條件 `.strip("'\"")`**，把一個「只開頭有引號」的值的結尾引號吃掉，再原樣寫回 |

第 4 個最嚴重：**它在 round trip 過程中損壞了儲存的值，而項目看起來仍然格式正確。**
已改成只在兩端都有引號時才解開。

修完之後，**八個可執行驗收全綠**：

```
W-057 GREEN steps=2   W-063 GREEN steps=1   W-067 GREEN steps=1
W-061 GREEN steps=1   W-064 GREEN steps=1   W-068 GREEN steps=1
W-062 GREEN steps=1   W-065 GREEN steps=1
```

（W-066 的 verify 是 save-audit，**每個伺服器只能跑一次**，故不在此列。）

---

## 6. 狀態

```
python tools/pm.py check     -> 56 items, 0 problem(s)
python tools/pm.py doctor    -> 0 problem(s)
python tools/pm.py coverage  -> 27 suites
                                high 無判官的區域只剩 mapedit
                                無法報紅的：(none)
                                flaky：sim-health
gate                          -> RED SUITES: 0
```

---

## 7. 下一個目標（已排好，尚未做）

1. **`mapedit`** —— 最後一個 high criticality 空洞（validate/batch/undo/save/foothold-chain）
2. **`import`** —— med，NO JUDGE
3. **W-059** `/api/cashshop/icon/{id}` 對每個 id 都 404
4. **SaveNx** 四個缺口：fsync、空間檢查、鎖定探測、EncodingErrors 應拒絕而非提示

---

## 8. 給下一個 agent 的一句話

**這一輪有三次「紅燈是判官的錯」，而三次都是判官自己先抓到的。**
`attack-after` 是靠改對算式自己抓到的，`node-verify` 是靠斷言抓到的，
`pm.py verify` 是靠自己報紅抓到的。**沒有斷言的東西不會抓到自己。**