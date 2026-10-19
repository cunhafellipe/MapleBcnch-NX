# Copilot instructions

**`AGENTS.md` at the repository root is the single source of truth** for how an
AI works in this repo: backend startup, MCP server registration, tool inventory,
and the rules that must not be broken. Read it first.

Quick facts:

- **Backend:** `MapleBench/`, ASP.NET Minimal API, default `http://127.0.0.1:5100`.
- **MCP server:** `MapleMcp/` (name `maplebench-assets`), a **stdio** server
  launched by `wz-mcp-map.cmd`. It forwards to the backend over HTTP.
- **VS Code / Copilot agent mode:** `.vscode/mcp.json` is committed; it starts
  the server with `${workspaceFolder}/wz-mcp-map.cmd`.
- **34 tools** are auto-discovered once connected: `wz_map_*` (11) and
  `wz_asset_*` (23, including `wz_asset_merge_nx`).
- **Mutating tools default to `commit=false`** — preview, read the result, then
  commit. `wz_map_save` and `wz_asset_merge_nx` rewrite a whole archive.
- **Do not trust a tool's own success report.** Verify with
  `tools/probe/mcp-probe.mjs` / `mcp-flow.mjs`, or an independent NX parser.
