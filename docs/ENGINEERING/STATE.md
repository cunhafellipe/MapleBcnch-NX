# 工程協調紀錄

團隊成員的**單一真相來源**。每個人動手前先讀這裡，完成後回報到這裡。
不要各自記憶狀態 —— 那正是本專案 2026-10-19 那天出問題的原因。

---

## 目前狀態（2026-10-20）

`HEAD = e030a89`　建置 0 警告 0 錯誤

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

完整進度見 [`PROGRESS.md`](../../PROGRESS.md)。

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

- `tools/NxMergeMap/Program.cs` 是獨立工具，自己做 backup + `File.Move`，**沒走已加固的 flush 路徑**。是否開工單？
- PID 29852：同 repo 的後端跑在 port 5600，鎖住 `bin\Release`。歸屬不明，未動。
- task-2：模擬器 10,581 行的無界工作與每 tick 配置量測，已開單未派。

---

## 回報格式

每個人完成工作後，必須包含：
1. **改了哪些檔案、每處幾行**
2. **建置輸出**（0 警告 0 錯誤，或明確說失敗）
3. **沒做但發現的問題**（只列，不動手）
4. **用過哪些工具、哪裡可能不可靠**