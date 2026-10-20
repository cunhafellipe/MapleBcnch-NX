# MapleBcnch-NX 工程進度表

**基準**：`4816856`（2026-10-12，含模擬器）→ `30e2c4b`（2026-08-20）
**工作目錄**：`C:\MUWORK\GAME\MAPLESOTRY\wz工具\MapleBcnch-NX-audit`
**狀態符號**：`✅ 已修並實測` · `🟡 已定位未修` · `⚪ 未開始` · `🔄 進行中`

---

## A. 已完成（每條都有實測的前→後數字）

| # | 缺陷 | 影響 | 證據 | 提交 |
|---|---|---|---|---|
| A1 | NX 位移表項未驗證就解參照 | **程序崩潰** `0xC0000005` | 159 bytes 畸形檔：開檔成功、目錄正常、點圖即死 | `1c72c25` |
| A2 | 空運算式被當成字面值 | 所有 Int 節點寫成 **0** | `tradeBlock 12345 → 0`，預覽顯示空白 | `1c72c25` |
| A3 | NPC 批次 `set` 不驗證值 | 同上 | 同上，另一次獨立重現 | `1c72c25` |
| A4 | NX 存檔後 undo 未清 | 存檔再 Ctrl+Z：**磁碟與記憶體分歧，檔案自稱乾淨** | 記憶體 10 / 磁碟 4242，`/api/changes` 不列此檔 | `1c72c25` |
| A5 | NX 存檔無 `FlushToDisk` | 斷電可能得到截斷檔 | 靜態 + 該檔自身註解自述此危害 | `1c72c25` |
| A6 | 存檔復原失敗被吞 | 使用者不知檔案已在 `.bak` | 靜態 | `1c72c25` |
| A7 | XML 匯出漏檔卻回報完整 | **靜默資料遺失** | Quest.nx 6 張只出 1 張，`truncated:false` | `1c72c25` |
| A8 | `.ms` 計數含未存檔假象 | 剛開檔顯示上萬筆假變更 | 註解自述症狀 + `WzMsFile.cs:631` 無條件設 `Changed` | `1c72c25` |
| A9 | 取代功能無時間預算 | **全域鎖被按死** | 無關請求被鎖 **19,943 ms** | `e07a0bb` |
| A10 | `_pixels` 跨執行緒競態 | NRE | `ImageMemoryService:351` 在 gate 外 | `e07a0bb` |
| A11 | `Validate.TileSets` 恆空 | 呼叫者拿不到圖磚集 | 出廠地圖回 `[]` → 修後 `["allblackTile"]` | `ba2bde5` |
| A12 | NX 開檔記憶體估計過期 10 個月 | **拒絕開得起來的檔案** | Map.nx 預測 13,458 MB，**實測 185 MB** | `9f0d3d0` |
| A13 | 建構期無記憶體守門 | 整包建構無保護 | 實測建構 363–739 bytes/節點 | `8e3f10c` |
| A14 | `NxMergeService` 重複三個存檔缺陷 | 斷電截斷 + 復原失敗無提示 | 與 `SaveNx` 逐行比對 | `e030a89` |
| A15 | 字串位移表 entry 未驗證 | **程序崩潰** `0xC0000005` | 11 個畸形檔，**行程死亡 0 次**（修前審查員重現 AV 死亡） | `30e2c4b` |
| A16 | `DecodedLength` int 溢出 | 32768² 靜默解碼成空圖 | 40000×40000 → 6.4e9 bytes 拋出 | `30e2c4b` |
| A17 | `Validate` 讀載入期快照 | **壞地圖可靜默通過驗證** | 刪 foothold 後：13→12、斷鏈 0→4 | `30e2c4b` |

> A12 修正值：`NxBytesPerOpenNode = 96`（對上實測 22–96 bytes/node）、地板 384 MB、
> `byBytes = size / 4`。Map.nx = 840 萬節點 × 96 = 806 MB，遠低於原本預測的 13,458 MB。

**合計**：17 條

---

## B. 已定位、未修

| # | 缺陷 | 證據強度 | 狀態 |
|---|---|---|---|
| B2 | `NxWriter` 位置計數是 `int`，≥1 GiB 行為異常 | 讀碼 | 🔄 **task-8**（PROGRESS 舊描述「無限迴圈」可能不準，正在查證） |
| B3 | `PatchUInt64((int)偏移)`，≥2 GB 寫錯位置 | 讀碼 | 🔄 **task-8** |
| B4 | `(ushort)子節點數` 截斷 | 讀碼。語料最大 8,947（7.3 倍餘裕），但 .wz 使用者可做出更大的 | 🟡 |
| B6 | `.wz` 畫布寬高截 ushort | 讀碼 | 🟡 |
| B7 | `Validate` 的 touching 規則可能誤報出廠地圖 | **未量測**，語料記 38/784,735 例外 | 🟡 |
| B8 | 單筆 `delete` 留下斷鏈（批次刪有做） | **實測**：每次刪除產生 4 個斷鏈錯誤，懸空 id 會解析到其他圖層同名 id | 🟡 |
| B9 | `NxArchive.GetAudioOffset` 回傳未驗證 offset | 唯讀。今日無活洞（消費者都走 `NxAudio.EncodedRange`） | 🟡 |
| B10 | `tools/NxMergeMap/Program.cs` 無 flush 保護 | 靜態 | 🔄 **task-12** |

---

## C. 進行中

| 工單 | 內容 | 負責 |
|---|---|---|
| task-7 (T7a) | `NxArchive.Validate` 註解錯誤 + Node record 驗證缺口（先分析） | `analyst-nx` |
| task-8 (TB2) | `NxWriter` ≥1 GiB 的實際行為與可達性 | `verifier-nx` |
| task-4 (TC2-1) | `MapSimSessionStore.cs` 3,779 行 session 生命週期 | `analyst-reverify` |
| task-9 (TC2-2) | `SkillCastService` + `CombatService` 4,527 行 NaN / 每 tick 配置 | `analyst-map` |
| task-10 (TC2-3) | Simulate 其餘 9 檔 2,125 行，含 SimClock `try` 位置查證 | `verifier-comments` |
| task-5 (TC1) | MapEditor 快照讀取點全清點 | `reviewer-nx` |
| task-11 (T404) | `InvalidDataException` 進例外階梯（**本輪唯一寫入者**） | `implementer-nx` |
| task-12 (T405) | `tools/NxMergeMap/Program.cs` 存檔保證比對 | `tooling-auditor` |

---

## D. 完全未開始

| # | 項目 | 規模 |
|---|---|---|
| task-6 | 第一輪約 40 條未讀發現重新取證 | 全部 |
| — | 前端 30,000 行 JS | 全部 |

**未讀比例：86,000 行 C# 中約 40,000 行從未被任何 agent 或人讀過**，前端 30,000 行同樣未讀。
`Simulate/` 11,431 行與 `MapEditor/` 16,091 行已於 2026-08-20 分派（task-4/5/9/10）。

---

## E. 驗證工具失效記錄

已發生 **11 次**，全部記錄在 [`docs/ENGINEERING/STATE.md`](docs/ENGINEERING/STATE.md) 與
[`tools/verify/AGENTS.md`](tools/verify/AGENTS.md)。

**共同點：工具壞掉時，輸出看起來像「有效的空結果」。**
強制規則：量測腳本上線前必須先跑一個已知答案的煙霧測試；
**寫進文件的指令必須在寫入的同一回合實際跑過並貼出輸出。**

---

## F. 本專案採用的外部工程規範

`AGENTS.md`（agentsmd/agents.md，Agentic AI Foundation）+ maker-checker
（NIST SP 800-53 AC-5）。來源與適用方式見
[`docs/ENGINEERING/STATE.md`](docs/ENGINEERING/STATE.md)。
規則寫在 repo 根與兩個子目錄的 `AGENTS.md`，agent 會自動載入最近的那一份。