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
- **92 tools** are auto-discovered once connected, in 9 families:
  `wz_map_*` (22), `wz_asset_*` (24), `wz_sim_*` (12), `wz_archive_*` (8),
  `wz_data_*` (6), `wz_job_*` (6), `wz_render_*` (6), `wz_diag_*` (5),
  `wz_save_*` (3).
  (source scan of `MapleMcp/**/*.cs`: 17 file(s) read, truncated=False)
  Counted by `node tools/probe/mcp-tool-census.mjs --brief` — do not hand-write
  these numbers.
- **Mutating tools default to `commit=false`** — preview, read the result, then
  commit. `wz_map_save` and `wz_asset_merge_nx` rewrite a whole archive.
- **Do not trust a tool's own success report.** Verify with
  `tools/probe/mcp-probe.mjs` / `mcp-flow.mjs`, or an independent NX parser.
