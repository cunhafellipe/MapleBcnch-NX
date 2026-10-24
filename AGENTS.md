# AGENTS.md — read this first

This repository is **MapleBench-NX**: a MapleStory map editor with an NX (PKG4)
reader/writer and an **MCP server** that exposes the editor to an AI agent.

If you are an AI agent, this file tells you how to start the backend, connect
the MCP server, what the tools are, and the rules you must not break.

---

## Layout

| Path | What it is |
|---|---|
| `MapleBench/` | The backend: ASP.NET Minimal API + map editor + NX reader/writer. Default `http://127.0.0.1:5100`. |
| `MapleMcp/` | The MCP server (`maplebench-assets`). Speaks to the backend over HTTP; it does not open files itself. |
| `dependencies/MapleLib/` | WZ format parser. |
| `docs/MAP-STRUCTURE.md` | Map structure spec, reverse-engineered from real maps. |
| `docs/CLI-AND-MCP.md` | Current state of the CLI and MCP surfaces. |
| `tools/` | Verification tools (probes, visual checks) — not product code. |

---

## Build and test

The project targets `net10.0-windows`. **Do not run a bare `dotnet build`** —
two traps make it fail on this machine. This is the command that works,
verified 2026-08-20 (0 warnings, 0 errors):

```powershell
& 'C:\MUWORK\app\scoop\apps\dotnet-sdk\current\dotnet.exe' build `
    MapleBench\MapleBenchX.csproj -c Release -v minimal --nologo
```

**Trap 1 — wrong SDK on `PATH`.** `dotnet` on `PATH` is **8.0.425**; the only
.NET 10 SDK is the scoop one (**10.0.401**). A bare `dotnet build` fails with
`NETSDK1045`. There is **no `global.json`** here, so nothing redirects you.

**Setting `DOTNET_ROOT` is NOT enough** — verified 2026-10-22: with
`DOTNET_ROOT` exported, `dotnet` on `PATH` still resolved to
`C:\Program Files\dotnet\dotnet.exe` (8.0.425) and the build failed with
`NETSDK1045`. **Invoke the 10.0.401 muxer by absolute path**, as above.
(Pinning `DOTNET_ROOT` only affects which runtime an *apphost* picks up; it
does not change which SDK the `dotnet` command resolves.)

**A `global.json` pinning 10.0.401 would remove the ambiguity** — it does not
exist yet, and adding one is a separate decision because it changes what
happens for anyone using the PATH `dotnet`.

**Trap 2 — a running backend locks the build output.** `MSB3021`/`MSB3027`
"cannot access … being used by another process" means a backend is still alive.
The message names the holder (`檔案鎖定者: "…"`). Three ways out — in order of
preference:

```powershell
# 1. build to a private output dir; disturbs nothing
& "$env:DOTNET_ROOT\dotnet.exe" build MapleBench\MapleBenchX.csproj -c Release `
    -p:OutDir="$env:TEMP\mb-build\"

# 2. stop the apphost-started backend
Get-Process MapleBenchX -ErrorAction SilentlyContinue | Stop-Process -Force

# 3. a backend started as `dotnet MapleBenchX.dll` shows as ".NET Host" / dotnet.exe
Get-Process dotnet -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -like '*MapleBcnch-NX*' } | Stop-Process -Force
```

Start the backend (build first; you cannot build into `bin/` while it runs):

```powershell
MapleBench\bin\Release\net10.0-windows\MapleBenchX.exe --no-browser --port 5100
```

Verification scripts. These run on **Windows PowerShell 5.1**. Check the real
signature with `Get-Help <script> -Full` before passing switches — several take
no `-RepoRoot` at all.

**Use forward slashes in the path.** `powershell -File tools\verify\self-test.ps1`
breaks in a shell that rewrites backslashes: the argument arrives as
`toolsverifyself-test.ps1`. Forward slashes work identically on Windows and
survive that rewrite.

```powershell
# every tool in this directory, against fixtures whose correct answer is known
# in advance. Takes NO -RepoRoot.
# Expected counts depend on the environment (git present? DOTNET_ROOT usable?) --
# read the case list, not the totals; the branches are documented in
# tools/verify/AGENTS.md.
powershell -File tools/verify/self-test.ps1

# refuse to start a build whose preconditions are not met
powershell -File tools/verify/prebuild-check.ps1 -RepoRoot . -Project MapleBench/MapleBenchX.csproj

# line-ending churn detector. -Path is MANDATORY and is a string[]
powershell -File tools/verify/eol-check.ps1 -RepoRoot . -Path <file>

# assert exact bytes are present. Note: -Path, not -File; no -RepoRoot
powershell -File tools/verify/content-assert.ps1 -Path <file> -NeedleFile <file>
```

**`eol-check.ps1` compares `git diff --numstat` against `--ignore-all-space`, and
they must match — with one exception that is easy to mistake for a defect.**
On a LARGE rewrite the two runs can differ because git picks a different
alignment for the same edit, not because any line's whitespace changed.
Tell the two apart before you "fix" anything:

- A real whitespace change is stable: the delta keeps the same sign and size
  under `--diff-algorithm=myers`, `patience` and `histogram`. **A delta that
  changes sign between algorithms is git's alignment choice, not the file.**
- A real whitespace change shows up in a whitespace-insensitive LCS as a pair of
  lines matching after trimming. Zero such pairs means there is nothing to fix.
- For a small change the two numstat runs must be equal. For a rewrite, read the
  diff before concluding the file is damaged.

---

## Two ways to use this

### 1. As an MCP client (recommended)

The MCP server is a **stdio** server. Register it, and you get all 92 tools
(in 9 families) with their descriptions and schemas — you do not need to read
any markdown to call them. **The backend must be running first**, because the MCP
server only forwards to it.

VS Code / Copilot agent mode reads `.vscode/mcp.json` (already committed).
Claude Desktop / Cursor / generic clients:

```jsonc
{
  "mcpServers": {
    "maplebench": {
      "command": "<repo>/wz-mcp-map.cmd",          // Windows; on other OSes run the exe
      "env": { "WZ_SERVER": "http://127.0.0.1:5100" }
    }
  }
}
```

`WZ_SERVER` overrides the backend address. `wz-mcp-map.cmd` builds the server
on first run if the binary is missing.

### 2. As a repo reader (no MCP)

Read `MapleMcp/README.md` for the tool table and safety model, and
`docs/MAP-STRUCTURE.md` for the map rules.

---

## The tools

<!-- INVENTORY -- do not hand-write the numbers or the names below.  They are
     generated by `node tools/probe/mcp-tool-census.mjs --markdown`: re-run
     that command and paste its output instead of editing this table.  The
     census reads the sources and, with --handshake, asks a running server the
     same question, and it prints whether the two answers agree.  A number
     typed by hand is a claim; this one has already been wrong. -->

**MCP tools: 92**, in 9 families.  Counted by
`node tools/probe/mcp-tool-census.mjs --markdown`
(source scan of `MapleMcp/**/*.cs`: 17 file(s) read,
truncated=False).  To ask a
running server the same question, add `--handshake <path-to-the-mcp-exe>`:
the census then prints the handshake answer next to this one and says whether
they agree.

| Family | Tools | Names |
|---|---|---|
| `wz_archive_*` | 8 | `wz_archive_capabilities`, `wz_archive_children`, `wz_archive_close`, `wz_archive_export_img`, `wz_archive_node`, `wz_archive_open`, `wz_archive_scan`, `wz_archive_split` |
| `wz_asset_*` | 24 | `wz_asset_add`, `wz_asset_changes`, `wz_asset_children`, `wz_asset_close`, `wz_asset_create_empty`, `wz_asset_delete`, `wz_asset_export_canvas`, `wz_asset_export_img`, `wz_asset_files`, `wz_asset_history`, `wz_asset_merge_nx`, `wz_asset_node`, `wz_asset_open`, `wz_asset_open_source`, `wz_asset_rename`, `wz_asset_replace_canvas`, `wz_asset_save_as`, `wz_asset_search`, `wz_asset_set_value`, `wz_asset_set_values`, `wz_asset_sources`, `wz_asset_transfer`, `wz_asset_tree`, `wz_asset_undo` |
| `wz_data_*` | 6 | `wz_data_bulk`, `wz_data_capabilities`, `wz_data_detail`, `wz_data_list`, `wz_data_search`, `wz_data_write` |
| `wz_diag_*` | 5 | `wz_diag_audit`, `wz_diag_clientlog`, `wz_diag_facts`, `wz_diag_health`, `wz_diag_sim` |
| `wz_job_*` | 6 | `wz_job_cancel`, `wz_job_capabilities`, `wz_job_progress`, `wz_job_report`, `wz_job_result`, `wz_job_start` |
| `wz_map_*` | 22 | `wz_map_advise`, `wz_map_batch`, `wz_map_capabilities`, `wz_map_cast`, `wz_map_create`, `wz_map_doc`, `wz_map_ground`, `wz_map_list`, `wz_map_node`, `wz_map_open`, `wz_map_palette`, `wz_map_patrol_mob`, `wz_map_place`, `wz_map_render`, `wz_map_render_mob`, `wz_map_save`, `wz_map_session_close`, `wz_map_session_open`, `wz_map_session_step`, `wz_map_simulate`, `wz_map_simulate_batch`, `wz_map_validate` |
| `wz_render_*` | 6 | `wz_render_animation`, `wz_render_dump`, `wz_render_export`, `wz_render_preview`, `wz_render_thumb`, `wz_render_view` |
| `wz_save_*` | 3 | `wz_save_commit`, `wz_save_memory`, `wz_save_preflight` |
| `wz_sim_*` | 12 | `wz_sim_cast`, `wz_sim_catalog`, `wz_sim_close`, `wz_sim_control`, `wz_sim_despawn`, `wz_sim_equip`, `wz_sim_open`, `wz_sim_probe`, `wz_sim_skills`, `wz_sim_state`, `wz_sim_step`, `wz_sim_summon` |

Total: **92** tools in 9 families.

The two families the editor uses most: `wz_map_*` (22 — open a map, read and
place its nodes, validate, save) and `wz_asset_*` (24 — open/close WZ/IMG/NX,
read and edit nodes, export/replace canvases, save-as, undo/history, transfer
between sources, and `wz_asset_merge_nx`, which grafts loose `.img` files into
an NX archive).

---

## Rules you must not break

- **Every mutating tool takes `commit` and defaults to `false`.** Preview, read
  the result, then commit. Nothing touches disk until `commit=true`.
- **`wz_map_save` and `wz_asset_merge_nx` rewrite a whole archive.** They keep a
  timestamped backup, but it is still a rewrite. Do not run them on a whim.
- **A map image inside an NX is a `WzSubProperty`, not a `WzImage`.** Only root
  images are archive entries; everything below them is a property. Graft
  children, not images.
- **Only leaf nodes are editable**; containers, vectors and binary payloads are
  rejected.
- **A node's type cannot change** (an `Int` stays an `Int`).
- **`wz_asset_merge_nx` refuses an archive that is open in the session.** Close
  it first — the merge rewrites the file under the live mapping.
- **`wz_asset_*` previews only report intent; they do not validate the backend
  or encryption.** Synthetic IMG roundtrip is tested; production WZ/NX and game
  compatibility are not.
- Never claim that saving NX synchronized its source IMG.

---

## End-to-end authoring flow

1. Start the backend, open the target archive (`wz_asset_open` / `wz_map_*`).
2. Build or edit the map (`wz_map_open` → `wz_map_place` / `wz_map_batch`).
3. `wz_map_validate` — footholds must chain (`prev`/`next`); the tile material
   carries no foothold geometry, so footholds must be placed explicitly.
4. `wz_map_save` to write edits back into the open archive.
5. For a **finished candidate `.img`** that must go into a client archive, use
   `wz_asset_merge_nx` — and close the archive first.

---

## Verify before you claim

This repo has been burned by "HTTP 200 but the screen is empty". Do not trust a
tool's own success report:

- `tools/probe/mcp-probe.mjs` — real JSON-RPC handshake, lists tools.
- `tools/probe/mcp-flow.mjs` — full map flow through MCP only.
- For an NX merge, re-read the written file with an **independent** NX parser
  and confirm the new nodes exist and the old ones are untouched.

**A passing build is not a passing fix.** Compile is necessary, not sufficient.

### Maker-checker: the agent that writes a change never certifies it

This repo's rules are not my invention — they are the banking *maker-checker*
control, formalised in the Clark-Wilson model and codified in **NIST SP 800-53
AC-5** (separation of duties), ported to agent pipelines. The reason it exists:
LLMs measurably favour their own output. Prompt wording does not repair it;
splitting the roles across separate contexts does.

The checker must differ from the maker on **five dimensions**. Any one missing
collapses the control:

| Dimension | Maker | Checker |
|---|---|---|
| Context | Full reasoning, the "why" | **Fresh — only the diff plus criteria** |
| Instructions | Implement the change | **Skeptical: refute, do not confirm** |
| Model | Any | **Different family where possible** — same-family loops share blind spots |
| Tools | Write, edit, build | **Read-only until a fix is warranted** |
| Output | Code | **Structured verdict: each claim quoted, then upheld or refuted** |

Handoff order, in this order:

1. A **fresh** reviewer sees only the diff and the criteria — never the
   implementer's reasoning, which would just restate the first opinion.
2. The checker is **read-only** and must produce findings, not patches.
3. A **separate fixer** applies corrections. The checker never fixes.
4. A **new** reviewer takes another pass. Reusing one reviewer across items
   accumulates context and recreates the self-review bias in a longer session.
5. A **human** approves. Whoever asked for the fix is not the approver.

**Route the checker by blast radius**, not uniformly. A small change to a
bounds check can be far more dangerous than a large refactor:

- **Always** review: anything parsing untrusted bytes (`Services/Nx`), anything
  that writes to disk (`SaveNx`, `NxMergeService`, `MapEditorService`), anything
  holding a lock or a gate (`WzSearchService`, `ImageMemoryService`), and any
  change to exception→HTTP mapping.
- **Skip** the checker for trivial, fully-reproducible changes that the
  deterministic scripts already cover.
- Use a **workflow** rather than an adversarial reviewer where the check is
  deterministic; spend adversarial budget where judgment is actually required.

**Do not report a fix as done until you have run it.** An agent's claim that it
edited a file is evidence, not proof — read the file, or run the command.

---

## Committing

New files are added with `git add`; run `git status` before pushing. The working
tree may hold untracked tool directories that are not part of the product.
