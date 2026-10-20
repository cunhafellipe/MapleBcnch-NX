# 工程協調紀錄

團隊成員的**單一真相來源**。每個人動手前先讀這裡，完成後回報到這裡。
不要各自記憶狀態 —— 那正是本專案 2026-10-19 那天出問題的原因。

---

## 目前狀態（2026-10-20）

`HEAD = a180e72`　建置 0 警告 0 錯誤（實測）

> **工作區並不乾淨。** 實測有 **3,343 個未追蹤檔**（git 預設收合顯示 285 筆，會讓人以為只有 285）。
> 「乾淨」這句是我在沒有用工具量的情況下寫的 —— 那正是失效 3 的後果。
> 量測指令：`tools/verify/git-status-report.ps1`

| 提交 | 內容 |
|---|---|
| `1c72c25` | NX 位移表崩潰、空值寫 0、NX 存檔 undo、匯出漏資料、`.ms` 計數 |
| `ba2bde5` | `Validate.TileSets` 恆空 |
| `e07a0bb` | Replace 時間預算、`_pixels` 競態 |
| `8e3f10c` | NX 建構期記憶體守門 |
| `d822c17` / `f7b88b5` | 合併 GitHub `b31af31`（`wz_asset_merge_nx`） |
| `e030a89` | merge 路徑與 SaveNx 對齊的存檔保證 |
| `9d9c1c2` | `tools/verify/`：已知答案自我測試 |
| `a180e72` | 採用 AGENTS.md 標準；修正建置與驗證指令 |

完整進度見 [`PROGRESS.md`](../../PROGRESS.md)。

---

## 本專案採用的外部工程規範（2026-08-20 查證）

這些規則**不是我發明的**，是有出處的。寫在
[`AGENTS.md`](../../AGENTS.md)、[`Services/Nx/AGENTS.md`](../../MapleBench/Services/Nx/AGENTS.md)、
[`tools/verify/AGENTS.md`](../../tools/verify/AGENTS.md)，
agent 會依目錄自動載入最近的那一份。

### 一、AGENTS.md — 代理指示檔的既定格式

來源：<https://agents.md/>、<https://github.com/agentsmd/agents.md>

- 開放格式、純 Markdown，**60,000+ 個開源專案在用**，由 Linux Foundation 下的
  **Agentic AI Foundation** 托管（OpenCode、Amp、Jules、Factory 等共同發起）。
- Codex、Jules、Factory、Aider、goose、opencode、Zed、Warp、VS Code、Devin、
  Cursor、Copilot、Gemini CLI 等皆支援。
- **大型 monorepo 用巢狀 `AGENTS.md`**：agent 自動讀取目錄樹中**最近**的那一份，
  越近優先。本 repo 已建立三份（root / `Services/Nx/` / `tools/verify/`）。
- **檔案裡列出的建置與測試指令，agent 會自動執行並嘗試修好失敗** ——
  所以寫錯的指令不是文件問題，是會實際發生的事故。

> 採納當天就證實了這點：root `AGENTS.md` 原本寫 `dotnet build`，
> 在這台機器上必然 `NETSDK1045` 失敗。已修正為實測可用的指令。

### 二、maker-checker（製造者不得自行驗證）

來源：<https://www.augmentcode.com/guides/adversarial-code-review>；
控制模型源自銀行業 maker-checker、Clark-Wilson 模型，並由
**NIST SP 800-53 AC-5**（權限分離）正式編碼。

存在的理由是實測的：模型會偏袒自己的輸出，且**改提示詞無效** ——
掩蓋作者身分的研究（arXiv 2512.05379）顯示偏好來自「認出」而非「指令」。

**檢查者必須在五個維度上不同於製造者，少一個整個控制就失效：**

| 維度 | 製造者 | 檢查者 |
|---|---|---|
| 脈絡 | 完整推理與「為什麼」 | **全新 —— 只看 diff 與準則** |
| 指令 | 實作變更 | **懷疑式：推翻，不是確認** |
| 模型 | 任意 | **盡量不同家族** —— 同家族迴圈共享盲點 |
| 工具 | 寫入、編輯、建置 | **唯讀，直到有修正需求** |
| 輸出 | 程式碼 | **結構化判決：逐條引用，再判定成立或推翻** |

交棒順序：新鮮檢查者（唯讀）→ **另一個**修復者套用修正 → **再换一个**檢查者 → 人類核准。
**重複使用同一個檢查者會累積脈絡，在更長的 session 裡重建自我審查偏誤。**

### 三、依 blast radius 分配檢查資源

「小的權限改動可能比大型重構更危險」。本 repo 的固定分配：

- **一律檢查**：解析不可信位元組（`Services/Nx`）、寫入磁碟（`SaveNx`、
  `NxMergeService`、`MapEditorService`）、持有鎖或閘門（`WzSearchService`、
  `ImageMemoryService`）、例外→HTTP 對應表。
- **略過**：確定性腳本已完全涵蓋的瑣碎變更。
- **用工作流而非對抗審查**：檢查本身是確定性的時候。

> 同一份來源也警告：多代理架構本身**不保證**更強的審查
> （SWR-Bench 反而落敗於簡單 baseline）。真正決定抓得到什麼的是
> **檢查者的提示與範圍**。架構只是必要條件。

---

## 角色

| 角色 | agent | 職責 | 禁止 |
|---|---|---|---|
| 分析員 | `analyst-nx` | 讀碼、回報 file:line + 原文 | **不下結論、不改檔、不建置** |
| 驗證員 | `verifier-nx` | **推翻**既有主張 | **不找新發現** |
| 實作員 | `implementer-nx` | 只改工單指派的檔案 | 不判斷缺陷是否成立、不順手改別檔 |
| 審查員 | `reviewer-nx` | 審 diff | 不擴大範圍、不找新缺陷 |
| 工具稽核員 | `tooling-auditor` | **審驗證工具本身的邏輯** | 不審產品程式碼 |
| lead | 我 | 指派、核驗閘門 | **不實作、不裁判內容** |

**硬規則**：分析員與驗證員必須用**不同方法**。「我找不到反駁」不算成立，必須給獨立的正面證據。

**依上面查證到的 maker-checker 規範，補三條具體做法：**

1. **每個工單換一個新鮮的檢查者。** 不重複使用同一個 agent 審不同工單 ——
   累積的脈絡會讓第二次審查變成第一次的複述，這正是要避免的自我審查偏誤。
2. **檢查者只讀，且必須交結構化判決**（逐條引用 → 成立／推翻）。
   它**不修**；修正交給另一個角色。
3. **發工單的人不是核准人。** lead 指派並跑機械閘門，最後由人決定。

> 這三條不是新增權力，是把既有的角色分開**寫成可檢查的規則**。
> 沒有第 1 條，「新鮮脈絡」會隨 session 變長而失效。

---

## 本專案的專屬陷阱

### 換行符是混合的
`.gitattributes` 是 `* text=auto eol=lf`，但實際檔案混合
（`WzSaveService.cs` = 2,348 CRLF + 87 bare-LF）。
**整檔重寫會把所有行統一，一行改動變成數百行 diff。**
必須用位元組層級 splice，且 splice 後複驗 bare-LF 計數未變。

### .NET 10 SDK 不在 PATH 上
```
$env:DOTNET_ROOT = 'C:\MUWORK\app\scoop\apps\dotnet-sdk\current'
& 'C:\MUWORK\app\scoop\apps\dotnet-sdk\current\dotnet.exe' build ...
```
`dotnet` 會解析到 `C:\Program Files\dotnet`（只有 8.0.425）並報 `NETSDK1045`。
`DOTNET_ROOT` **不會被 `Start-Process` 繼承**，apphost 找不到 runtime 會在 1 秒內退出。

### 環境陷阱
| 陷阱 | 結果 |
|---|---|
| 指令列含中文路徑 | `Invoke-RestMethod -Body` 用字串會編成 `??`；必須 `[Text.Encoding]::UTF8.GetBytes($json)` |
| 寫入工具寫 `.ps1` | 檔案內的中文會被破壞；腳本必須零非 ASCII，路徑走參數 |
| 執行中的後端 | 鎖住 `bin\Release\MapleBenchX.dll`，正式建置失敗；用 `-p:OutDir=$env:TEMP\...` |

---

## ⚠️ 驗證工具失效記錄（必讀）

**2026-10-19 一天內發生四次。四次的共同點：工具壞掉時，輸出看起來像一個有效的答案。**

| # | 工具 | 失效方式 | 它偽裝成什麼 |
|---|---|---|---|
| 1 | 量測腳本 + 中文路徑 | 寫入工具破壞腳本內的路徑 | **整列量測值為 0**，看起來像「資料就是這樣」 |
| 2 | 量測腳本無 `DOTNET_ROOT` | 程序 1 秒內退出 | **同樣是全 0** |
| 3 | `Where-Object { $_ -notlike '??*' }` | PowerShell `-like` 的 `?` 是單字元萬用字元，匹配所有字串 | **35 個未提交檔案被誤報成 0 個** |
| 4 | `Get-Content` 讀 UTF-8（無 BOM） | 中中文解碼失敗 | **檔案看起來像壞了**，差點誤報「你的工作不見了」 |
| 5 | 正則驗證訊息 | 假設字串在同一行，實際跨三行串接 | **修好的程式碼被回報成沒修** |

| 6 | 命令通道傳非 ASCII 參數 | 命令列本身會破壞非 ASCII：同一組中文經 `-Needle` 傳入 → 亂碼失敗；經 `-NeedleFile` 傳入 → 正確命中 | **已修好的內容被回報成找不到** |

> 第 6 次是 `tools/verify/content-assert.ps1` 在稽核時發現的，寫進 STATE.md 的當下還不存在。
> 它是前 5 次的根因之一：命令通道的編碼問題一直都在，只是以前靠肉眼比對沒暴露出來。

### 2026-08-20 追加：採用 AGENTS.md 時當場再犯三次

| # | 工具 | 失效方式 | 它偽裝成什麼 |
|---|---|---|---|
| 7 | `self-test.ps1 -RepoRoot` | **沒有 `-RepoRoot` 參數**，我憑記憶寫進文件就跑了 | `A parameter cannot be found`，指令從未執行 |
| 8 | `eol-check.ps1 -Path 'a.md,b.md'` | `powershell -File` **不會**把逗號清單拆成陣列，整串當成一個檔名 | **三個檔案全部報 `MISSING`**，像是檔案不存在 |
| 9 | `eol-check.ps1` 的 changed-ratio 門檻 | `MaxChangedLineRatio = 0.25`，合法的大幅修改（107/−6 → ratio 0.9187）直接判 `FAIL` / `TRUST=NONE` | **看起來像換行符被整檔重寫**，實際上 `eol_rewrite=False`、CR 計數 = 0、raw 與 `--ignore-all-space` numstat **完全相同（107/6）** |

第 7 次最嚴重：**我剛寫下「每個指令都要先跑過」的文件，自己第一個指令就是憑記憶寫的。**
第 9 種誤判若沒被獨立交叉驗證，會導致下一個人去「修」一個不存在的問題。

> 寫進任何文件的指令，**在寫入文件的同一次回覆裡就必須實際跑過並貼出輸出**。
> 這條比「文件要正確」強，因為文件正確與否只能靠執行證明。

### 2026-08-20 端對端驗證時再犯兩次（都在同一輪）

| # | 失效方式 | 它偽裝成什麼 |
|---|---|---|
| 10 | 手工組的 JSON 帶 Windows 反斜線路徑，`\m`、`\x` 是**無效跳脫序列** | `HTTP 400 "This request could not be read."` 看起來像**伺服器拒絕了壞檔**，實際是伺服器**根本沒收到檔案**。若照單全收，就會記錄成「產品正確地拒絕了損壞輸入」——而那時我一個壞檔都還沒測到 |
| 11 | 假設 crafted fixture 全在 `C:\mbtest2` | 其餘三個「不存在」，於是判定**第二道字串邊界檢查無法被任何現存 artifact 觸發** |

> 第 11 次直接推翻審查員的 A4 結論。實際上 `string-runs-past-eof.nx`
> 就在 `C:\mbtest2-gen\`，而且**確實**觸發了第二道檢查：
> `"A string in a 136-byte archive starts at 0x86 and claims 1027 bytes, which runs past the end of the file."`
>
> **通用規則：宣稱「某輸入不存在 / 某行為不可達」之前，先列出目錄。**

---

## 2026-08-20 端對端驗證結果（4 檔 diff 閘門）

二進位 19:35:06 晚於最新原始碼 19:25:04 → **確實含這次改動**。
後端 `dotnet MapleBenchX.dll --no-browser --port 5620`（pid 2072，測完已停）。

### 主張 1：損壞 NX 必須被拒絕且行程存活　**通過**

11 個 crafted 檔 → **行程死亡 0 次**（修前審查員重現 `AccessViolationException` 行程死亡）。

```
canvas-32768.nx      HTTP 500  A string offset-table entry in a 211-byte archive points to
                               0x7473655400080000, which is outside it.        → bound1
canvas-23171.nx      HTTP 500  (同上)
canvas-40000.nx      HTTP 500  (同上)
string-runs-past-eof HTTP 500  A string in a 136-byte archive starts at 0x86 and claims
                               1027 bytes, which runs past the end of the file.  → bound2
canvas-bmp-*.nx      HTTP 200  (字串表完整，惡意載荷在 canvas 維度，開檔階段不觸發)
nested-*.nx          HTTP 200  (同上)
TOTAL PROCESS DEATHS: 0
```

**審查員的 A4 被推翻**（見上，第 11 次）。

### 主張 2：Validate 必須讀活結構　**通過**

`f8/Map/Map0/000000000.img`（來自 Map.nx，5,366 張地圖，開檔 396 ms）：

| | footholds | brokenChains | ok | tileSets |
|---|---|---|---|---|
| 基線 | 13 | 0 | true | `["allblackTile"]` |
| 刪除 foothold addr `[9,0,0,5]` 後 | **12** | **4** | **false** | — |
| undo 後 | 13 | 0 | true | — |

```
"Foothold 1.next points at '2', which does not exist. A severed chain strands the character at runtime."
"Foothold 3.prev points at '2', which does not exist. ..."
```

獨立路徑交叉驗證：`/api/mapedit/doc` 同時回報 **12（活）而非 13（快照）**。
若 Validate 讀的是載入期快照，這裡必然是 13 / 0。

> 同時把「單筆 delete 會留下斷鏈」從推測升級為**實測**：每次刪除產生 **4** 個斷鏈錯誤，
> 且訊息指出懸空 id 會解析到**其他圖層**的同名 id（「Ids repeat across layers」）。

### 副作用檢查

- `Map.nx` mtime 停在 19:12:18（審查員自己那次的存檔），**本次未新增任何 `.bak`**。
- `C:\MUWORK\GAME\MPSHS` 最新檔仍是 2026/10/16 — **今天完全沒碰**。

---

### 持鎖程序的識別陷阱

`bin\Release\net10.0-windows\MapleBenchX.dll` 被 PID 29852 鎖住，而
`Get-Process -Name MapleBenchX` **找不到它** —— 持鎖的行程名是 `dotnet`（`dotnet.exe <dll> --port 5600`），
不是 `MapleBenchX`。用行程名找會得到「沒有這個程序」的錯誤結論。
`tools/verify/prebuild-check.ps1` 改用命令列比對才抓到。

### 因此強制規則

> **任何量測或驗證工具上線前，必須先跑一個「已知答案」的煙霧測試，
> 確認它能產出非零且正確的結果。**
>
> 工具輸出「空」「0」「全部一樣」時，先懷疑工具，再懷疑資料。

---

## 待決事項（等 lead 或使用者決定）

- ~~`tools/NxMergeMap/Program.cs` 是獨立工具，自己做 backup + `File.Move`，**沒走已加固的 flush 路徑**。是否開工單？~~
  → **2026-08-20 結案並刪除。** 稽核發現它在 `Program.cs:35` 就必定拋 `InvalidCastException`
  （`NxToWzFile` 轉出的根目錄只放 `WzImage`，不會有 `WzDirectory`），
  所以它**從來沒跑到過存檔程式碼** —— 「沒有 flush」本來就在不可達路徑上。
  且全 repo 沒有任何程式或文件呼叫它。由使用者決定刪除。
- ~~PID 29852：同 repo 的後端跑在 port 5600~~ → **已結案。** 後來是 PID 16948（port 5591，帶 `--browser`）
  與 PID 22116（port 5623）。**全部保留，不殺** —— 首選解法是 `-p:OutDir="$env:TEMP\..."` 建置，
  既能跑閘門又不干擾任何人。
- ~~task-2~~ → **已拆成 task-4 / 9 / 10 三張，全部完成。**
  （先前記的「11,431 行」也不準 —— `Measure-Object -Line` 不數空白行；實際 11,559 行、12 檔。）

---

## 2026-08-20 的兩個 P0（都已修並實測）

| | 缺陷 | 症狀 | 證據 |
|---|---|---|---|
| A | `NxWriter.Ensure` 的 `size *= 2` int 溢出 | **存 Mob.nx 永久卡死後端**：100% CPU、記憶體不動、**永久持有全域 session gate**、從不產生 scratch 檔 | 重現時探針印出 `size`：1073741824 → -2147483648 → 0 → 0 永久。修後存檔 **4.1 秒** |
| B | `SimClockService` 的 `try` 在 `while` 外 | 任一 session 拋例外 → **整個模擬時鐘永久死亡**，症狀只有一行 log | 故障注入：壞的 session 隔離在 `frame=5`，正常的推進到 `frame=663`；log 共 5 行後靜默 |

> B 的註解 `:76-81` 宣稱「One bad pass is logged and the loop carries on」—— **與程式碼相反**。
> 這是本專案同一缺陷類（「程式碼與自己的相鄰註解矛盾」）的第 4 次實例。

### 一個必須記錄的版控事實

`MapleBench\Services\Simulate\` **從基線 `4816856` 到 HEAD 都是 git 未追蹤的**（0 個檔案）。
已驗證的 P0 修復因此不在版控內、`git diff` 完全看不到它。2026-08-20 由使用者決定納入版控
（12 檔、11,559 行）。**下一個人若再遇到「改了檔但 diff 是空的」，先確認那個檔案有沒有被追蹤。**

---

## 回報格式

每個人完成工作後，必須包含：
1. **改了哪些檔案、每處幾行**
2. **建置輸出**（0 警告 0 錯誤，或明確說失敗）
3. **沒做但發現的問題**（只列，不動手）
4. **用過哪些工具、哪裡可能不可靠**