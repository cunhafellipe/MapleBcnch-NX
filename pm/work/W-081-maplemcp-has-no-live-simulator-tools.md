---
id: W-081
title: MapleMcp is registered but has no live-simulator tools, so agents still hand-write HTTP
state: done
priority: 90
owner: 
opened: 2026-10-17
closed: 2026-10-17 
depends: []
refs: MapleMcp/SimulatorTools.cs; MapleMcp/SessionTools.cs; MapleMcp/MapClient.cs; pm/decisions/ADR-0002
blocked_on: 
evidence: |
  tools/list reports 52 tools; none of them reach /api/simulate/session/*.
  The python suites under %LOCALAPPDATA%\hermes\cache\scratch post to those endpoints by hand.
workstream: infra
kind: feature
risk: med
---

## Intent

An agent that loads this workspace gets `maplebench` in its tool list and should
be able to ask "put a character on this map, walk it into range of a mob, cast
1121008, tell me the mob's hit points" without writing a single HTTP call. Today
that is impossible, which is why a dozen suites in the scratch harness post to
`/api/simulate/session/*` by hand and why an agent that never checks the registry
reaches for `urllib` or a raw CDP client instead.

## Reference

No upstream to port. `MapleMcp/SessionTools.cs` (`wz_map_session_open/step/close`)
is the map-editor physics walker, a different feature from the live simulator.

## Approach

1. Add a `SimClient.cs` beside `MapClient.cs` covering what the suites actually
   post: open a session, step frames, summon/despawn a mob, cast a skill, read the
   session (mobs carry `hp`/`maxHp`), set skills, invulnerable, revive.
2. Register them as `wz_map_live_*` in `SimulatorTools.cs`:
   `live_open` `live_step` `live_cast` `live_summon` `live_state` `live_close`.
   `wz_map_simulate*` keeps its current meaning; the `live_` prefix is what says
   "this drives the session the viewer drives".
3. Every mutating tool follows the existing rule: `commit` defaults to `false`
   and only reports what it would do.

## Acceptance

```
node tools/probe/mcp-probe.mjs <dotnet> <maple-mcp.dll>   # tool count 52 -> 58
# and one flow that needs no HTTP:
#   wz_map_live_open -> wz_map_live_summon -> wz_map_live_cast -> wz_map_live_state
#   reporting hp 1000 -> 900 for mob 9400101
```

## Result

(Filled in when closed.)

---

## 現況（2026-10-17 完成）

後端 234 條路由，MapleMcp 從 52 個工具補到 **90 個**。設計原則：
**每條路由一個工具會把 agent 淹死**，所以每個家族用 `kind`／`action` 分流。

| 家族 | 個數 | 覆蓋 |
|---|---|---|
| `wz_sim_*` | 12 | `/api/simulate/*` 現場模擬器（新增） |
| `wz_data_*` | 6 | skill / mob / npc / string / cash shop（新增） |
| `wz_job_*` | 6 | audit / dump / compose / import / repair×4 共 56 條路由（新增） |
| `wz_render_*` | 6 | canvas / animation / sound / preview / thumb / export（新增） |
| `wz_diag_*` | 5 | health / clientlog / debug / facts / audit（新增） |
| `wz_save_*` | 3 | preflight / commit / memory（新增） |
| `wz_map_*` `wz_asset_*` `wz_archive_*` | 52 | 原有 |

### 實測

```
node tools/probe/mcp-probe.mjs <dotnet10> <maple-mcp.dll> --script @flow.json
  wz_sim_open -> wz_sim_skills -> wz_sim_summon -> wz_sim_step
  -> wz_sim_cast -> wz_sim_state
  action='skill'  skillStance='brandish1'  1 skillEffect (9 frames, origin (30,23), z=0)
  every mob maxHp=1000
```

`mcp-probe.mjs` 本身也擴充了：`--call`／`--args @file`／`--script @file`，
`{{sessionId}}` 會自動帶入前一步回報的 id。**這是擴充專案既有的 probe，
不是另寫一支 client。**

### 順手記錄下來的一個坑

**檔案 id 不穩定**：`f4/Map/...` 的 `f4` 是封存的工作階段 id，依開啟順序發號。
實測同一個 `Map.nx` 一次是 `f4`、下一次是 `f25`。寫死會得到
`No open file with id 'f4'` —— 看起來像地圖不見，其實不是。
已寫進 `wz_sim_open` 的說明與 README。