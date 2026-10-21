# PM v2 — 設計文件

> 這份文件先於實作寫出。它是規格，不是事後紀錄。
> 寫於 2026-10-16 21:40，實作依此進行。

---

## 1. 問題陳述

`pm/` 現在有 54 個工作項目、19 條規則、4 份規格文件、一支 584 行的 `pm.py`。
它解決了「狀態存在哪裡」，但沒有解決以下五件事——每一件都是 2026-10-16
實測出來的，不是推測：

| # | 缺口 | 實測證據 |
|---|---|---|
| P1 | **驗收條件不會被執行。** `acceptance:` 是一段散文裡的反引號註解。 | W-035 / W-036 的 acceptance 是空反引號、W-057 是 `# TODO`，但它們都曾出現在 `pm.py next` 的「接下來三件事」裡。`check` 只查「有沒有 TODO 字樣」，不查「能不能跑」。 |
| P2 | **`check: 0 problems` 不等於狀態完整。** | W-055 因 UTF-8 BOM 對狀態工具**完全隱形**，而當時 `check` 回 0 problems——因為它根本沒載入到該項目。 |
| P3 | **測試覆蓋率不存在。** | 234 條 HTTP 路由、25 個 gate suite，**100% 只打 `/api/simulate`**。`/api/repair`(27)、`/api/dump`(11)、`/api/icon`(1) 等 194 條路由 **0 判官**。這是我手動掃出來的，工具裡沒有。 |
| P4 | **判官本身會說謊，而系統無法分辨。** | `attack-after` 印出「the gate is wrong」之後**仍然計成 BAD=2**；`mob-attack-verify` 曾對一隻「打中了玩家、還把玩家打死」的怪印 `>>> RED: the mob never connected`。 |
| P5 | **沒有工作流劃分。** 54 個項目平鋪在一個清單裡。對一個含 16 個封存格式、234 條路由、40 個服務的專案，「下一步做什麼」沒有答案。 |

---

## 2. 設計原則

1. **能被機器驗證的，就不該只寫成散文。** 驗收條件是命令，不是註解。
2. **狀態只能由一個寫入者產生，且那個寫入者必須驗證自己讀到的每一個項目。**
   `check` 回 0 的前提是「所有項目都被讀到了」——這件事本身要被檢查。
3. **每個判官必須自我聲明**：覆蓋哪個區域、用什麼標記報綠、能不能報紅。
   這三件事寫在資料檔裡，gate 消費它，而不是維護一份手寫清單。
4. **未被覆蓋的區域必須是命令輸出裡的一個洞，不是記憶裡的一個念頭。**
5. **任何新 agent 冷啟動只需要一條命令。** 那一條命令的輸出必須是「現在做什麼」，
   而不是「去哪裡讀文件」。

---

## 3. 目標架構

```
pm/
  SPEC.yaml         【新增·唯一真實來源】區域、suite 註冊表、不變量
  START-HERE.md     【產生】冷啟動協定：一條命令的輸出
  COVERAGE.md       【產生】區域 × 判官矩陣，洞會被點名
  PORTFOLIO.md      【產生】工作流視圖 + 關鍵路徑
  STATE.md          【既有·產生】
  RULES.md GATE.md TRAPS.md PARITY.md WORKFLOW.md   【既有】
  work/W-###.md     【既有·升級 frontmatter】
  decisions/ADR-*.md【既有】
tools/
  pm.py             【升級】schema 驗證 + verify + coverage + critical + doctor + start
```

### 3.1 `pm/SPEC.yaml` — 唯一真實來源

```yaml
areas:                       # 系統的「功能組件」劃分
  - id: save
    title: 存檔鏈路
    routes: ["/api/files/save", "/api/files/*/preflight"]
    criticality: high         # 壞了會吃掉使用者的原始資料
  - id: simulate
    routes: ["/api/simulate/*"]
    criticality: med
  ...

suites:                      # 判官註冊表；gate.ps1 消費它
  - id: skill-test
    file: skill-test.ps1
    runner: powershell
    verdict: "VERDICT: skill-test"
    red_marker: ">>> RED"
    asserts: true             # 能不能報紅——W-057 的核心欄位
    areas: [simulate, skill]
    one_shot: false           # 是否每個伺服器只能跑一次（見 D3）
  - id: save-audit
    file: save-audit.py
    runner: python
    verdict: "VERDICT: save-audit"
    asserts: true
    areas: [save]
    one_shot: true            # 實測：固定路徑每個伺服器只能跑一次
    fresh_server: true        # 因此必須排在 build-gate 之後

invariants:                  # 不可破壞的東西，gate 必須檢查
  - id: avatar-md5
    file: MapleBench/wwwroot/js/avatar.js
    md5: a1eb2eb47af42e07ee048762c2592456
    why: 這個專案不應該碰它；變了代表有東西伸進了不該碰的檔案
  - id: data-root-readonly
    path: 'C:\MUWORK\GAME\MPSHS'
    why: 測試一律用副本；改這個目錄等於改使用者的原始資料
```

### 3.2 工作項目 frontmatter v2

新增欄位，每個都有機械可檢查的意義：

```yaml
id: W-061
title: ...
state: todo | measuring | building | gating | done | blocked | dropped
workstream: save | simulate | archive | editor-ui | infra | parity
kind: defect | feature | audit | decision | spike
risk: high | med | low
verify:                      # 【核心新增】可執行的驗收
  - cmd: "python $S/save-audit.py"
    expect: "15 assertions, 0 failed"
```

`check` 的新規則：

- `state` 進入 `done` 的路徑上，**必須有 ≥1 條 `verify`**，且 `cmd` 非空。
- `workstream` / `kind` / `risk` 必須是枚舉值。
- **`load_items()` 讀到的每一個檔案都必須有非空 `id`**——這就是 P2 的修法：
  一個讀不出來的項目是**硬錯誤**，不是空字典。
- `depends` 必須形成 DAG；成環即報錯。

### 3.3 `pm.py` 新命令

| 命令 | 回答的問題 |
|---|---|
| `start` | 我是新的 agent，現在做什麼？（唯一入口，輸出即答案） |
| `doctor` | 環境與狀態是否可信？（伺服器、封存、judge 漂移、未解析項目、BOM、資料根是否被改過） |
| `coverage` | 哪些功能組件沒有判官？（P3） |
| `verify <id>` | 這個項目的驗收真的通過嗎？（P1） |
| `critical` | 先做哪一個才能解開最多？（P5） |
| `stream <name>` | 這個工作流現在是什麼狀態？ |
| `check` | 結構是否完整？（升級，含 P2） |

### 3.4 產生文件

`start` 同時產生 `START-HERE.md`；`coverage` 產生 `COVERAGE.md`；`critical` 產生
`PORTFOLIO.md`。全部標記 GENERATED，不可手改（沿用 STATE.md 的既有規則）。

---

## 4. 為什麼不做的事

- **不引入 PyYAML 或任何相依套件。** 既有理由成立：冷啟動的 agent 不該需要
  一個 parser 與一個 schema 才能讀狀態。SPEC.yaml 沿用同一個最小子集解析器。
- **不做看板、不做網頁 UI。** 這個專案的介面已經很多，再加一個沒人會開的
  儀表板是負債不是資產。
- **不自動關閉工作項目。** gate 通過 ≠ 行為正確；那個判斷仍然是人的（沿用
  `pm/README.md` 的既有立場）。
- **不重寫 `pm/` 既有文件。** RULES/GATE/TRAPS/PARITY 已經被引用了幾十次，
  改名等於重置所有引用。

---

## 5. 實作順序與驗收

| 步驟 | 產出 | 驗收命令與必須看到的輸出 |
|---|---|---|
| D1 | `parse_frontmatter` 回傳解析狀態；`load_items` 對解析失敗**硬報錯** | `python tools/pm.py doctor` → `unparsed work items: 0` |
| D2 | 工作項目 frontmatter v2 + `check` 升級（枚舉驗證、DAG、verify 必填） | `python tools/pm.py check` → `0 problem(s)`；故意去掉一條 `verify` 後 → 報錯 |
| D3 | `pm/SPEC.yaml` + suite 註冊表；`gate.ps1` 改為消費它 | `python tools/pm.py coverage` → 列出 area × suite 矩陣與洞 |
| D4 | `verify` 執行器 | `python tools/pm.py verify W-066` → 跑該項目的 `cmd` 並比對 `expect` |
| D5 | `doctor`（含不變量檢查、one-shot suite 提醒） | `python tools/pm.py doctor` → 資料根 md5 未變、`avatar.js` md5 相符 |
| D6 | `start` + 產生的 `START-HERE.md` / `COVERAGE.md` / `PORTFOLIO.md` | `python tools/pm.py start` → 一個新 agent 只跑這一條就能知道下一步 |

**完成定義**：D1–D6 全部通過，且 `python tools/pm.py gate` 仍為 `RED SUITES: 0`
（save-audit 因 `one_shot` 標記而被 gate 正確排除，並在 doctor 中提示）。

---

## 6. 風險

| 風險 | 緩解 |
|---|---|
| 54 個既有項目沒有 v2 欄位，升級會讓 `check` 全紅 | `check` 對**缺少** v2 欄位給出明確升級清單與 `--migrate` 指令，而不是只報錯 |
| 改壞 `pm.py` 等於讓整個專案失去狀態 | 每一個命令改動後立即跑 `check` + `status`，兩者都必須與改動前的項目數一致 |
| `SPEC.yaml` 與 `gate.ps1` 分歧 | `check` 交叉驗證：註冊表裡的每個 suite 都必須在 gate 裡，反之亦然 |