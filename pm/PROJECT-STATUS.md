# 專案現狀（量出來的，不是記出來的）

> 產生時間 2026-10-25 09:23。
> 每一個數字都由 `C:\mbtest2\make-project-status.py` 從工作樹量出來；量錯的地方在該腳本的檔頭列著。
> 對象：`MapleBench-NX`，branch `map-generation-baseline`，HEAD `b2f4b42`，GitHub behind/ahead = **0/0**。

---

## 1. 這是什麼專案

一個 **MapleStory 楓之谷地圖編輯器**，加上一個讓 AI agent 用的 **MCP 介面**。
後端是 ASP.NET（.NET 10／Windows），能讀寫 NX（PKG4）與 WZ 封存，能編輯地圖節點，
並內建一個**走圖模擬器**：角色能在地圖上走、施放技能、怪會攻擊、傷害與擊退照封存裡的數字算。

## 2. 程式碼的形狀

| 部分 | 檔案 | 行數 | 說明 |
|---|---:|---:|---|
| MapleBench（後端／產品） | 216 | 154,121 | API 端點、地圖編輯、模擬器、NX 讀寫、存檔 |
| MapleMcp（MCP 伺服器） | 17 | 5,019 | 把後端的能力暴露成 MCP 工具給 AI agent |
| dependencies/MapleLib（WZ 解析） | 158 | 43,154 | WZ／NX 格式的解析與寫出 |
| tools/verify（驗證裝置） | 57 | 20,003 | 判官（suite）、自測、閘門 |
| tools/probe（探針） | 100 | 13,219 | 一次性量測腳本（頁面檢查、工具普查…） |
| docs（對外文件） | 11 | 2,254 | 對外文件 |
| pm（專案管理紀錄） | 112 | 12,429 | SPEC、工作項、決策紀錄、路線圖、現狀 |

## 3. 對外的三個介面

| 介面 | 數量 | 備註 |
|---|---:|---|
| HTTP 端點（`MapleBench/Api/`） | 242 條註冊、222 個相異路徑 | 編輯器、模擬器、檔案、繪圖 |
| MCP 工具 | 92 | 9 個家族；由 `tools/probe/mcp-tool-census.mjs` 自動產生，AGENTS.md 的表與它逐位元組相同 |
| 判官（gate suite） | 閘門自己的 Run 清單 34 支；SPEC 宣告 35 支 | 宣告 35 − 實跑 34 = 1 支宣告 `in_gate: false`（`save-audit`，它是「結論不是遺漏」） |

## 4. 驗證裝置現在的狀態

最近一次**整樹**閘門（對由這棵樹自己建出來的 instance）：

```
GATE_DOCTOR_EXIT=0 (suite declarations: 0 problem(s))
GATE_INSTANCE_STALE=no
GATE_SUITES_RAN=34
GATE_EXIT=0  elapsed=00:11:29.5050114
```

| suite | 判決 |
|---|---|
| `ladder-all` | GREEN? ALL LADDERS PASS |
| `attack-test` | GREEN? VERDICT: attackStance IS on the wire and always inside the declared group. 6.2's server->avatar l… |
| `skill-test` | GREEN? VERDICT: skill-test 21 assertions, 0 failed |
| `mob-attack-verify` | GREEN? VERDICT: mob-attack-verify closed the distance on its own and landed hits |
| `first-attack-verify` | GREEN? PASS: it did not pursue -- no firstAttack, never struck |
| `npc-label-check` | GREEN? VERDICT: npc-label-check 3 assertions, 0 failed |
| `sim-skills-check` | GREEN? 35 passed, 0 failed |
| `npc-chat` | GREEN? BAD=0 |
| `sim-health` | GREEN? with a connection error (WinError 10048). `BAD=[1-9]` is the gate's |
| `npc-chat-frame` | GREEN? BAD=0 |
| `knockback` | GREEN? BAD=0 |
| `attack-drop` | GREEN? BAD=0 |
| `skill-key` | GREEN? BAD=0 |
| `projectile` | GREEN? BAD=0 |
| `hold-key` | GREEN? BAD=0 |
| `npc-name` | GREEN? BAD=0 |
| `mob-fly` | GREEN? BAD=0 |
| `ball-art` | GREEN? BAD=0 |
| `attack-after` | GREEN? BAD=0 |
| `revive` | GREEN? BAD=0 |
| `mobinfo-verify` | GREEN? BAD=0 |
| `equipstats-verify` | GREEN? BAD=0 |
| `equipset-verify` | GREEN? BAD=0 |
| `sheetfields-verify` | GREEN? BAD=0 |
| `sheet-verify` | GREEN? BAD=0 |
| `empty-archive` | GREEN? BAD=0 |
| `node-verify` | GREEN? VERDICT: node-verify 13 assertions, 0 failed |
| `skill-e2e` | GREEN? VERDICT: skill-e2e 14 assertions, 0 failed |
| `position-accuracy` | GREEN? VERDICT: position-accuracy 6/6 assertions, 0 failed |
| `attack-frame-timing` | GREEN? VERDICT: attack-frame-timing 9/9 assertions, 0 failed |
| `attack-damage` | GREEN? VERDICT: attack-damage 11/11 assertions, 0 failed |
| `contact-damage` | GREEN? BAD=0 |
| `skillshape` | GREEN? BAD=0 |
| `browser-verify` | GREEN? VERDICT: browser-verify 14 assertions, 0 failed |

## 5. 工作項（`pm/work/`）

| 狀態 | 筆數 |
|---|---:|
| done | 65 |
| todo | 10 |
| blocked | 4 |
| building | 3 |
| open | 1 |

未完成的：

- `W-007` **blocked** — Player does not fall through walls at a low ledge boundary
- `W-033` **building** — Map background music
- `W-034` **todo** — Reactors in the walk simulation
- `W-035` **todo** — Minimap in the walk view
- `W-036` **todo** — Name tags with medal and ring slots
- `W-037` **blocked** — Visual confirmation of the changes reported as fixed
- `W-038` **blocked** — Find a map that reproduces the reported fall-through
- `W-039` **blocked** — Investigate a ladder 1px away that does not attach
- `W-041` **building** — Sweep every open mob and item for defects of the classes already found
- `W-059` **todo** — The cash-shop icon endpoint answers 404 for every item, including ones the old code found
- `W-060` **todo** — Play a map's BGM: export the sound bytes and play them on map entry
- `W-066` **building** — Audit the save pipeline: candidate -> reopen -> verify -> swap, and what 'verified' actually means
- `W-071` **todo** — A bound skill key is swallowed by the dropdown that bound it: the panel keeps focus
- `W-092` **open** — The editor's warm-up costs 8.25 GB, and memory is not returned once it is used
- `W-093` **todo** — Three gate suites take their answer from a machine-local cache file, so a different machine gives a different answer
- `W-094` **todo** — The gate's skill-shape judge rounds the hit box where the server truncates it, and the damage array is omitted on a mi
- `W-097` **todo** — pm/GATE.md and pm/RULES.md disagree with the code: the doctor's exit contract and the avatar.js tripwire md5
- `W-105` **todo** — The port's 300/250 weapon reach is a v95 shape with no v83 basis: a known divergence

## 6. 四個倉庫的版控現況

| 倉庫 | 分支 | HEAD | 未提交檔 | 遠端 | 上游 | vs 上游 | vs GitHub |
|---|---|---|---:|---|---|---|---|
| MapleBench-NX（本工具） | `map-generation-baseline` | `b2f4b42` | 5 | ghup,mainwork,origin | `origin/map-generation-baseline` | 0/170 | ghup/map-generation-baseline: 0/0 |
| 同一專案的 sibling | `recovered/uncommitted-2026-10-25` | `f1616fd` | 3 | origin,public | `(none)` | - | ghup/map-generation-baseline: 1/11 |
| OpenStory-Beidou（客戶端） | `recover/uncommitted-2026-10-25` | `5dc806b` | 0 | origin | `(none)` | - | - |
| zmzeng-plugin（伺服器） | `docs/handover-2026-10-24` | `079d67d` | 0 | (none) | `(none)` | - | - |

★ **這個 repo 的 `origin` 與 `mainwork` 指的是本機路徑**（`C:\MUWORK\GAME\MAPLESOTRY\wz工具\MapleBcnch-NX`），不是 GitHub；**只有 `ghup` 才是 GitHub**。⇒ 「vs 上游」那一欄在這個 repo 說的是「比 sibling 的舊分支多幾筆」，**不代表與 GitHub 的差距**；與 GitHub 的差距看最右一欄。

## 7. 已知落差與文件不一致

- **已知落差 1 筆**：武器射程 300/250 是 v95 的形狀、v83 沒有依據（`pm/work/W-105`）。
- **文件與程式碼不一致**：閘門／文件的 doctor 退出契約與 avatar.js 絆線 md5（`W-097`）。
- **壞掉的紀錄**：`FAILURES.md` 的頭 8 個位元組少了「FAI」三個字（本輪之前就壞）。

