# 2026-10-16 20:36–21:15 · SEG8 — 編輯器覆蓋率盤點 + 存檔鏈路審查

> 結論先講：**同意需要審查，缺口是量出來的。存檔鏈路的 happy path 是成立的，
> 但你的封存全部是 `.nx`，所以你從來沒走過 README 描述的那條管線。**
> 另外抓到兩個會「亂叫」的 gate suite。

---

## 0. 使用者問「你同意需要深度審查嗎」——同意，但不同意「一次做完」

### 覆蓋率是量出來的

234 條路由，25 個 gate suite，**100% 只打 `/api/simulate`**：

```
25 suites  /api/simulate      8 suites  /api/inspect
 3 suites  /api/files          2 suites  /api/skill-cast      1 suites  /api/node
```

**編輯器本體 0 判官**：`/api/repair`(27)、`/api/dump`(11)、`/api/files/open`(9)、
`/api/family`(7)、`/api/import`·`/api/export`·`/api/compose`(各 6)、
`/api/mapedit/*` 的 validate·batch·save·place·foothold-chain·undo·redo(14)、
`/api/node`·`children`·`inspect`·`search`·`replace`·`edit`(19)、`/api/icon`(1)。

**現成的反證**：W-059 `/api/cashshop/icon/{id}` 對**每一個** id 都 404，至今 open。
沒有任何 suite 碰過 `/api/icon`。

### 但「一次深度解析」會踩到本專案自己的坑

R-7（沒有逐項 acceptance 的審查會生出四件永遠收尾不了的工作）、
TRAPS §2.2（跑幾千筆不等於嚴謹）、以及最要命的：**W-037 明說六項只有程式碼證明沒有畫面**，
而「每個功能組件有沒有異常」有一半是「使用者看到什麼」。

→ 決定：**分區、每區一個 work item、每區一條真斷言**，順序按「壞掉的後果 × 沒有判官」。
使用者選定：**先存檔鏈路**，並**授權瀏覽器驗證**。

---

## 1. 存檔鏈路：happy path 成立，已量測

新 suite：`%LOCALAPPDATA%\hermes\cache\scratch\save-audit.py`，**15 條斷言全綠**，
全程只在**副本**上操作，`C:\MUWORK\GAME\MPSHS` 的 md5 前後一致。

| README 的承諾 | 實測 |
|---|---|
| 編輯留在記憶體直到存檔 | **ok — 磁碟上檔案一個位元組都沒動** |
| 存檔前先驗證輸出 | ok |
| 產生時間戳備份 | **ok — 備份 md5 == 存檔前的原檔** |
| 存檔後可重開且內容正確 | **ok — 重開後編輯在檔案裡** |

同時實測確認一個程式碼發現：**`SaveResult.ReopenMs` 在每一條路徑都是 0**，是死欄位。

---

## 2. 最重要的發現：你的封存全是 `.nx`，你走的不是 README 那條管線

`C:\MUWORK\GAME\MPSHS` 裡 **16 個封存全部是 `.nx`，沒有任何 `.wz`**。
而 `Save()` 在 `WzSaveService.cs:409` 判斷 `file.Kind == "nx"` 就轉 `SaveNx`。

| | `SaveArchive`（README 描述的） | `SaveNx`（你實際走的） |
|---|---|---|
| 驗證 | 檔頭長度 + 鏡像數 + `BlockSize`/`Checksum` + 重寫鏡像可解析非空 | **只有節點數**（`:146`） |
| fsync | 有（`:1022`，註解明講為什麼） | **無**（`File.WriteAllBytes`，`:142`） |
| 磁碟空間檢查 | 有 | **無** |
| 目標是否被鎖 | 有（Restart Manager 探測 `:1079`） | **無** |
| 覆寫 | `File.Replace` 單一原子操作 | **兩次 `File.Move`**（`:175`、`:178`），**中間目標檔不存在** |
| 備份清理 | 保留 3 份 | **無，永遠累積** |

**兩條會靜默毀資料的路徑：**

- `:219-224` — `report.EncodingErrors > 0` 時**那些 canvas 被寫成空白**，
  只變成 `warnings` 裡一行字，存檔照跑。
- `:175-178` — 兩次 Move 之間目標不存在；catch 只在 `IOException` 且行程活著時才還原。

**被審的那條路本身也有洞**：`VerifyParseBudget`（20 秒，`:334`）會**靜默截斷**
「重寫鏡像」檢查，且訊息進 `warnings` 不進 `defects`，**所以不會中止存檔**。
重寫鏡像只驗**存在**不驗**內容**——一個掉了 800 個屬性的鏡像會乾淨通過。

---

## 3. 為什麼這個 suite 沒有裝進 gate（這是結論，不是遺漏）

**沒有 `POST /api/files/close`**（實測：`/files/*` 只有 `open`、`open-img-folder`、
`open-many`、`save`、`readonly`）。每個開過的封存都在行程存活期間保持記憶體映射，
而映射中的檔案**任何外部程序都開不了寫入**——對上一輪存檔的檔案做
`open(f,'wb')` 會回 `Errno 22`。

因此兩種做法都被量過：

- **每輪唯一路徑** → 每輪洩漏一個封存。實測跑幾輪後伺服器開著 **36 個封存**，
  接著 `sim-health` 在 gate 裡 `BAD=1`，單跑 3/3 全綠。
- **固定路徑** → 每個伺服器只能跑**一次**。實測：第 1 次綠（封存 16→17），
  第 2–4 次全部在複製那一步 `Errno 22`。

`gate.ps1` 因此**沒有**接上它，並把理由寫在檔案裡。
依 `pm/GATE.md:78-83`，**會亂叫的 gate 比沒有 gate 更糟**。
正確的跑法是跟文件既有迴圈一致：`build-gate`（會停伺服器）→ `deploy` →
`open-archives` → `save-audit`。

---

## 4. 順帶抓到：兩個會亂叫的 suite

| suite | 現況 | 說明 |
|---|---|---|
| `attack-after` | **BAD=2** | 輸出自己說「**the gate is wrong**」。它斷言「entry 應該 96 幀才可測」，而產品是第 7 幀就讓它可測（就是 W-040 記錄並已 close 的那件事）。它印完「我是錯的」之後**仍然計成 BAD**。 |
| `sim-health` | gate 裡 BAD=1，**單跑 BAD=0** | **尚未查出原因，不猜。** |

`attack-after` 在同一個 build 上時而 BAD=0 時而 BAD=2，屬間歇性。
兩者都屬於 W-057 那一類：**一個判官不能在「產品壞了」與「產品對但期望過時」之間做出區分。**

---

## 5. 這一頁沒做的事

- **沒有**動任何產品程式碼。整段是審查 + 新增一個 harness。
- **沒有**用瀏覽器（雖然使用者授權了）——W-037 的畫面驗證仍然空白。
- **沒有**查清 `sim-health` 為何在 gate 上下文紅。
- 沒有修 `SaveNx` 的任何一條缺口（fsync、空間檢查、鎖定探測、EncodingErrors）。
- 沒有 commit。

---

## 6. 給下一個 agent 的三句話

1. **讀 README 的安全承諾之前，先確認使用者的資料走的是哪條程式路徑。**
   這個專案裡 README 描述的那條對 `.nx` 使用者來說是死碼。
2. **沒有 close 端點，是這個專案裡最被低估的約束。** 它決定了 harness 能怎麼寫、
   gate 能怎麼重複跑、以及記憶體會長成什麼樣。
3. **一個 suite 印出「the gate is wrong」之後還計 BAD，是最惡劣的一種狀況**：
   它把「我錯了」翻譯成「產品錯了」。