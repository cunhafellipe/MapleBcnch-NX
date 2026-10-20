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

## Two ways to use this

### 1. As an MCP client (recommended)

The MCP server is a **stdio** server. Register it, and you get all 34 tools
(`wz_map_*`, `wz_asset_*`) with their descriptions and schemas — you do not need
to read any markdown to call them. **The backend must be running first**, because
the MCP server only forwards to it.

```bash
# build + start the backend
dotnet build MapleBench/MapleBenchX.csproj -c Release
MapleBench/bin/Release/net10.0-windows/MapleBenchX.exe --port 5100
```

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

**Map (11):** `wz_map_capabilities`, `wz_map_list`, `wz_map_open`,
`wz_map_create`, `wz_map_doc`, `wz_map_node`, `wz_map_palette`,
`wz_map_place`, `wz_map_batch`, `wz_map_validate`, `wz_map_save`.

**Asset (23):** `wz_asset_*` — open/close WZ/IMG/NX, read and edit nodes,
export/replace canvases, save-as, undo/history, transfer between sources, and
`wz_asset_merge_nx` (graft loose `.img` files into an NX archive).

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

---

## Committing

New files are added with `git add`; run `git status` before pushing. The working
tree may hold untracked tool directories that are not part of the product.
