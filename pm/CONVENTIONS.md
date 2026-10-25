# 慣例與原則：一張地圖

> 這個 repo 有 **6 份規則檔、4 套編號、7 份 ADR**。這份文件只做三件事：
> **告訴你從哪裡開始讀、每一件事的權威是哪個檔、衝突時誰贏。**
> 規則**本身**不在這裡重複——重複就會漂移，而漂移正是這個專案吃過的虧。
>
> **★ 這份地圖上的每個數字都有量它的指令**（見 §4 的「量測指令」）。
> 數字會過期，指令不會：重跑那一行就知道現在是幾個。

---

## 0. 三十秒版

| 你要做的事 | 先讀哪裡 |
|---|---|
| 第一次進這個 repo | `工作室守則.md`（根）— 最高的開發守則 |
| 冷啟動、要下一個工作 | `pm/README.md` → `python tools/pm.py status` / `next` |
| 跑起來 / 驗證 / 改程式 | `AGENTS.md`（根）— 建置、指令、不可破的規則 |
| 想知道**功能有沒有被驗證過** | `pm/SPEC.yaml`（宣告）＋ `pm/PROJECT-STATUS.md`（現況，自動產生） |
| 想知道**還缺什麼** | `pm/OPEN-PROBLEMS.md`（白話清單） |
| 想知道**接下來做什麼、什麼順序** | `pm/ROADMAP.md` |
| 改**驗證裝置**（`tools/verify/`） | `tools/verify/AGENTS.md` — 27 條規則 + 17 筆實例 |
| 改**產品碼** | `工作室守則.md`（根）— 權威；`pm/RULES.md` — 可引用的操作子集 |
| 想知道**踩過什麼坑** | `pm/TRAPS.md` — T-0..T-33 |
| 想知道**兩個參考來源誰贏** | `pm/PARITY.md` |
| 想知道**為什麼是這個設計** | `pm/decisions/ADR-0001..0007` |
| 想知道**工作項怎麼從開到關** | `pm/WORKFLOW.md` |
| 想知道**上一次做到哪** | `pm/STATE.md`（自動產生）＋ `pm/loop/go-01/` |
| 想知道**我這次犯過什麼錯** | `C:\MUWORK\GAME\MPSH\PROJECT\FAILURES.md` |

---

## 1. 這份地圖與 `pm/README.md` 的分工

兩份都叫「地圖」，管的不是同一件事：

| | 管什麼 |
|---|---|
| `pm/README.md` | **控制面**：`pm/` 這個目錄怎麼運作——五個指令、三條不變量、狀態只能由 `tools/pm.py` 寫 |
| **這份** | **權威面**：九份文件各自講哪個問題、彼此衝突時誰贏、編號系統怎麼分 |

⇒ 不會重疊，也不會漂移。**要下命令看 `pm/README.md`；要知道那份命令的規則在哪，看這份。**

---

## 2. 權威順序（兩份文件講同一件事而且不一致時）

```
1. 工作室守則.md          最高的開發守則
2. 程式碼（實際行為）        現況是什麼
3. pm/SPEC.yaml           宣告「應該是什麼」
4. pm/decisions/ADR-*     為什麼是這個樣子（改設計前先看它）
5. 其它文件                敘述
```

**★ 2 與 3 不一致時：程式碼是現況，SPEC 是待修。** 而且**不要默默把 SPEC 改成配合程式碼**——
先問「是程式碼錯了，還是宣告錯了」，然後把答案寫成一筆工作項。

**★ 1 與任何東西不一致時：`工作室守則.md` 贏，而且它是**根目錄**的檔——
新 agent 進來第一個讀的就是它。

---

## 3. 十條原則（跨檔案的那幾條）

每一條都給「**誰強制它**」與「**怎麼證明它被遵守**」——沒有實作位置的原則不是原則，是願望。

| # | 原則 | 誰強制它 | 怎麼證明 |
|---|---|---|---|
| **P1** | **一個數字要附上量它的指令與那個指令的輸出。** 手打的常數是主張，不是量測。 | `pm/RULES.md` R-2／`tools/verify/AGENTS.md` 23 | `pm/PROJECT-STATUS.md` 的每個數字都由 `C:\mbtest2\make-project-status.py` 產生 |
| **P2** | **量不到就拒絕，不要猜。** 寧可明說「量不到」，也不要填 0 或 null 當答案。 | `pm/RULES.md` R-5（absent 是 null 不是 0）／`tools/verify/AGENTS.md` 1 | `attack-after` 的「no shot ever reached its delay」是**具名拒絕**而不是假綠 |
| **P3** | **判官不能從它正在判的那個東西取答案。** 期望值必須獨立。 | `pm/RULES.md` R-9／`tools/verify/AGENTS.md` 15 | `skillshape-verify` 讀封存的數值、套參照算式，不跟 port 互比 |
| **P4** | **對照組必須會翻轉。** 不會翻轉的檢查不是檢查。 | `tools/verify/AGENTS.md` 3、22／`pm/RULES.md` R-8 | `banned-port-check.ps1` 的 ARM A（守）與 ARM B（會停） |
| **P5** | **「我沒看到」不等於「不是真的」。** | `pm/TRAPS.md` T-0（舊 server 回答得一模一樣）／`tools/verify/AGENTS.md` 18 | `attack-after` 那一幀從沒被記錄過——不是沒有 |
| **P6** | **maker 不簽自己的收工。** 改的人、查的人、批准的人分開。 | `AGENTS.md`（根）maker-checker 段 | `pm/ROADMAP.md` 第二節的九步流程 |
| **P7** | **改了敘述不等於修好了。** 推導與文字必須同一個來源。 | `pm/RULES.md` R-6（自己的設計要在程式碼裡宣告理由）／ADR-0007 | `skillshape` 抄錯程式的算式：**註解抄錯、實作就跟著錯** |
| **P8** | **一次改動配一次閘門。** 閘門判的是**樹**，不是 commit。 | `pm/RULES.md` R-13／`tools/verify/AGENTS.md` 17 | 閘門對**由這棵樹建出來的 instance** 跑，並印 `GATE_INSTANCE_STALE` |
| **P9** | **可重測的產物必須落盤**（digest、指令、原始輸出）。只有結論的紀錄是主張。 | `pm/RULES.md` R-20（工作項要帶證據才能關） | `pm/work/W-106-*.md`（canvas 複製移除的前後 digest） |
| **P10** | **文件與程式碼不一致時，要寫清楚「量在哪一棵樹上」。** | `pm/RULES.md` R-1（引用要連呼叫端一起讀）／`tools/verify/AGENTS.md` 23 | 每個工作項的 `evidence:` 都帶 commit 與數字 |

---

## 4. 規則地圖（現在有哪幾套、各管什麼）

| 編號體系 | 檔案 | 條數 | 管的範圍 | 讀它多久 |
|---|---|---:|---|---|
| （不分編號） | `工作室守則.md`（根，294 行 / 6 節） | — | 開發紀律、提交與驗證的最高原則 | 第一次接觸這個 repo |
| （不分編號） | `AGENTS.md`（根） | 8 條不可破的規則 ＋ maker-checker 5 面向 | 產品行為的紅線（mutating 要 commit、merge 要先關檔…） | 任何時候 |
| （不分編號） | `pm/README.md` | 3 條不變量 | `pm/` 控制面本身（狀態只能由 `tools/pm.py` 寫） | 用 `pm.py` 之前 |
| **R-n** | `pm/RULES.md` | **20**（R-1..R-20） | 可引用的操作規則，每條附一個讓它成規則的實例 | 要動產品碼前 |
| **1–27** | `tools/verify/AGENTS.md` | **27 條規則** ＋ **17 筆實例**（id 到 23，6–11 未使用） | 驗證裝置自己的行為 | 要動 `tools/verify/` 前（**必讀**） |
| **T-n** | `pm/TRAPS.md` | **34**（T-0..T-33） | 踩過的坑，附成因 | 寫量測腳本前 |
| **ADR-n** | `pm/decisions/` | **7**（ADR-0001..0007） | 架構決策與理由 | 要改設計前 |

**★ 四套編號互不重疊，也互不引用同一件事。** 所以：
**引用規則時要寫全**（`tools/verify/AGENTS.md` rule 14、`pm/RULES.md` R-13、`pm/TRAPS.md` T-1），
不要只寫「規則 14」——那可能是四份文件裡的四條不同的規則。

**★ 尤其要注意 `tools/verify/AGENTS.md` 自己就有兩套編號：**
它的 `## Rules` 是 **1..27**，而它上面的實例表第一欄是**另一套 id**（17 列，最高 23）。
**表的第 14 列不是 rule 14。** 這份檔案自己寫明了這件事。

### 量測指令（重跑就知道現在是幾個）

```powershell
# R-n（20）
(Get-Content pm/RULES.md -Encoding UTF8 | Select-String '^\*\*R-\d+').Count

# tools/verify/AGENTS.md 的規則（27）與實例列（17）
(Get-Content tools/verify/AGENTS.md -Encoding UTF8 | Select-String '^(\d+)\. \*\*').Count
(Get-Content tools/verify/AGENTS.md -Encoding UTF8 | Select-String '^\| (\d+) \|').Count

# T-n（34）
(Get-Content pm/TRAPS.md -Encoding UTF8 | Select-String '^\*\*T-\d+').Count

# ADR（7）
(Get-ChildItem pm/decisions -Filter 'ADR-*.md').Count

# 根 AGENTS.md 的不可破規則（8）
$a = Get-Content AGENTS.md -Encoding UTF8
$s = ($a | Select-String '^## Rules you must not break').LineNumber
$e = ($a | Select-String '^## End-to-end authoring flow').LineNumber
($a[$s..($e-2)] | Select-String '^- ').Count
```

---

## 5. 目錄與權威對照

| 目錄 | 放什麼 | 那件事的權威檔 |
|---|---|---|
| `MapleBench/` | 後端／產品：API、地圖編輯、模擬器、NX 讀寫、存檔 | 程式碼本身；宣告在 `pm/SPEC.yaml` |
| `MapleMcp/` | MCP 伺服器（把後端能力變成工具） | 工具表**由 `tools/probe/mcp-tool-census.mjs` 產生**，`AGENTS.md` 的 INVENTORY 區塊是貼上去的 |
| `dependencies/MapleLib/` | WZ／NX 格式解析 | 程式碼 |
| `tools/verify/` | 判官（suite）、自測、閘門 | `tools/verify/AGENTS.md`（27 條規則）＋ `pm/SPEC.yaml`（宣告哪支是判官） |
| `tools/probe/` | 一次性量測腳本 | — |
| `docs/` | 對外文件 | — |
| `pm/` | 專案管理 | 見下表 |
| `pm/work/` | 工作項（每件事一檔） | 工作項的 `state:` 欄位 |
| `pm/decisions/` | 架構決策（ADR） | ADR 本身 |
| `pm/loop/go-01/` | 這一輪的分解與控制組 | `00-control-group.md` / `00-README.md` |

### `pm/` 裡的每一份文件各管什麼

| 檔案 | 管的問題 | 產出方式 | 會不會過期 |
|---|---|---|---|
| `README.md` | `pm/` 控制面怎麼運作 | 手寫 | 很少 |
| `START-HERE.md` | 冷啟動一句話 | **自動產生**（`python tools/pm.py start`） | 每次重跑 |
| `RULES.md` | 可引用的操作規則（R-1..R-20） | 手寫 | 很少 |
| `TRAPS.md` | 踩過的坑（T-0..T-33） | 手寫 | 很少 |
| `GATE.md` | 閘門怎麼跑、它判什麼 | 手寫 | 隨程式碼變 |
| `PARITY.md` | 兩個參考來源誰贏、已驗證的對照表 | 手寫 | 隨程式碼變 |
| `WORKFLOW.md` | 一個工作項從開到關 | 手寫 | 很少 |
| `COVERAGE.md` | 區域 × 判官 的覆蓋表 | 手寫 | 隨程式碼變 |
| `SPEC.yaml` | 功能分區、判官宣告、不變量 | 手寫 | 隨程式碼變 |
| `STATE.md` | 專案狀態 | **自動產生**（`tools/pm.py`） | 每次重跑 |
| `PROJECT-STATUS.md` | 現況（數字量出來的） | **自動產生**（`C:\mbtest2\make-project-status.py`） | 每次重跑 |
| `OPEN-PROBLEMS.md` | 還缺什麼（白話） | 手寫 | 每波更新 |
| `ROADMAP.md` | 接下來的順序、流程、門檻 | 手寫 | 每波更新 |
| `CONVENTIONS.md` | **這份**：地圖與優先序 | 手寫 | 很少 |
| `archive-index.md` | `JOURNAL/` 是什麼、為什麼還留著 | 手寫 | 很少 |

**★ 標「自動產生」的三份不要手編。**
手改 `STATE.md` 或 `PROJECT-STATUS.md` 就是在把一個量測改成一句主張。

---

## 6. 新東西要放哪、要附什麼

| 新的東西 | 放在 | 必須附 |
|---|---|---|
| 一個要修的問題 | `pm/work/W-1xx-*.md` | `state:`、標題、**`evidence:`（量測的數字＋指令＋commit）**、參考檔的 file:line |
| 一個設計決定 | `pm/decisions/ADR-00xx-*.md` | 決定、理由、**被推翻的選項與為什麼**、影響面 |
| 一條新規則 | `pm/RULES.md`（R-n）或 `tools/verify/AGENTS.md`（1–27） | **那個讓它成規則的實例**（沒有實例的規則不要加） |
| 一個新的坑 | `pm/TRAPS.md`（T-n） | 成因，**不是**只有現象 |
| 一支判官 | `tools/verify/suites/` | **紅標記**（閘門只看紅標記）、`pm/SPEC.yaml` 裡的宣告、會翻轉的臂 |
| 一個工具／探針 | `tools/probe/` | 它量什麼、怎麼重跑、**它不能量什麼** |

**★ 工作項的四個必填欄位**（`pm/ROADMAP.md` 的流程就是照這四個欄位設計的）：
`state:` / `title:` / `evidence:`（帶指令與數字）/ `refs:`（file:line）。
**沒有 `evidence:` 的工作項不算完成**（`pm/RULES.md` R-20）。

---

## 7. 交付前的檢查清單

```
[ ] 這個數字是量出來的嗎？量它的指令貼在報告裡了嗎？
[ ] 這個判官/對照組會翻轉嗎？壞掉的那一臂真的會紅嗎？
[ ] 我有沒有拿「沒看到」當成「不是真的」？
[ ] 動了 tools/verify/ 嗎？→ 跑了 self-test.ps1 嗎？
[ ] 動了產品碼嗎？→ build 是 0 警告 0 錯誤嗎？
[ ] 閘門是對「由這棵樹建出來的 instance」跑的嗎？
[ ] 這份改動的檔案，有沒有別人正在改？（只 stage 自己的）
[ ] 文件裡有沒有出現新的數字？它有量它的指令嗎？它量在哪一棵樹上？
[ ] 這件事要寫進 pm/work/ 嗎？evidence 填了嗎？
[ ] 誰會簽這份收工？是我自己嗎？→ 那要換人
```

---

## 8. 這份地圖本身的規矩

**★ 它不重複任何規則內容**——只指出在哪。理由：`tools/verify/AGENTS.md` 的實例表已經示範過，
**同一件事寫在兩個地方，兩個地方就會開始不一致，而不一致的兩份文件比一份更糟。**
⇒ 這份地圖若與任何規則檔不一致，**規則檔贏**，並且這份要改。

**★ 它不重複會過期的現況**（幾支 suite、幾個 work item、幾個工具）。
那些數字在 `pm/PROJECT-STATUS.md`（自動產生）與 `pm/STATE.md`（自動產生）裡。
**這份只放「文件之間的關係」——那個比較不容易變。**
