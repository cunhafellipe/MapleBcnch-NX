# Orz → MapleBench MCP migration (phases 1–3)

Source checkout: e78-png/MapleBcnch-NX, map-generation-baseline.
This is NOT a complete replacement. No Pi settings were changed and Orz remains intact.

## Delivered

The original ten wz_map_* tools remain. Twenty-two wz_asset_* tools expose the existing generic backend:
files, open, close, node, children, search, tree, set_value, rename, add,
transfer, delete, changes, history, undo, save_as, export_img, export_canvas,
replace_canvas, set_values, sources, open_source.
Binary exports stream through a bounded temporary file, refuse overwrite and move to
an explicit new candidate destination. Uploads use multipart image data (32 MiB ceiling).
Exported bytes still require independent content verification.
All new mutations default commit=false. Previews report intent only; they do not validate source files.
Open defaults readOnly=true. Save-as requires an explicit candidate path and enables backups.
Backend errors are MCP-safe exceptions so agents receive the backend's actual diagnostic.

## Orz source inventory and parity

Compared against local Orz Java tool classes, not a live tools/list response.

| Orz capability | Current replacement | Remaining gap |
|---|---|---|
| list_loaded_roots | wz_asset_files | Different ids and metadata |
| load_files | wz_asset_open | One file per call; key/profile compatibility not verified |
| get_node_detail / list_children | wz_asset_node / children | Different response schema |
| get_node_tree_json | wz_asset_tree | Depth limited to 8 |
| find_node / search_node / query_nodes / batch_find_nodes | node / search | Full query predicates and batch reads not ported |
| create_child_node / delete_node / mutate_nodes | add / delete / rename / set_value | Vector/binary mutations and atomic batch parity pending |
| copy_nodes / paste_nodes | wz_asset_transfer | Immediate transfer; no clipboard, no SKIP policy parity |
| save_node / save_as | wz_asset_save_as / export_img | Standalone synthetic IMG reparse verified; real NX archive save still pending |
| unload_node / unload_all | wz_asset_close | No unload-all intentionally |
| create_session / create_img_file / create_wz_file | Not yet exposed | Backend creation workflow needs investigation |
| batch_update_nodes | wz_asset_set_values | Same-value scalar bulk only; heterogeneous batch pending |
| asset_open | wz_asset_sources / open_source | Trusted configured aliases and standalone IMG lookup; actual Orz profile migration and split-WZ lookup pending |
| nx_edit | Generic NX node tools, canvas export/replacement | Real NX media/save verification and edit-session parity |
| nx_build | Not ported | convert, batch_convert, merge, patch, publish, status |

Do not expose dummy tool names for missing features or proxy Orz and call that an independent replacement.

## Next phases

1. Synthetic standalone IMG real-backend test passes, including export/reopen, scalar/non-target/origin checks and every PNG pixel. NX fixtures and archive save-as still pending.
2. Candidate IMG export, canvas replacement and same-value scalar bulk are exposed and tested on a synthetic IMG. Creation and heterogeneous batch remain pending.
3. Trusted source aliases are implemented. Orz's base64 user keys cannot simply be mapped to a MapleBench profile label; migrate and verify each real source profile separately.
4. Add job-based NX publishing with configured converter/HDiffPatch binaries, candidate conversion, subtree semantic/media verification, restored patch hash checks and optional installation guarded by expected old hashes.
5. Add semantic map-material tags, map templates and OpenStory-specific movement reachability validation. Existing map validation is NOT proof of playable reachability.

Never alter production assets during migration tests. Server-side RPG content is a separate task requiring approval.

## Build and contract test

```powershell
dotnet build MapleBenchX.sln -c Release
node tools/probe/mcp-assets.mjs MapleMcp/bin/Release/net10.0-windows/maple-mcp.exe
```

The contract test uses its own temporary loopback backend and verifies tool discovery,
mutation previews making zero requests, read-only defaults, Unicode paths, HTTP methods,
backup defaults, argument bounds, propagated backend errors, binary export,
refused overwrite and multipart upload. The binary payload in this test is synthetic. It is NOT an actual
WZ/NX parsing or game runtime test.

## Real backend roundtrip test

```powershell
dotnet build tools/AssetFixture -c Release
node tools/probe/mcp-assets-real.mjs
```

Run from the checkout root. Test starts an isolated headless backend on 15973 with
`--allow-multiple`, refuses an occupied port, and stops only its own spawned processes.
The fixture includes scalar 7, an untouched string, red canvas and origin (1,2).
It changes scalar to 42 and canvas to blue, exports IMG/PNG, independently reparses
IMG with MapleLib, checks all pixels and origin, reopens the candidate in the backend,
checks read-only edit rejection and verifies the original source bytes did not change.
Successful fixtures are removed; failures are retained with logs.
Loose IMG root is the returned file id itself, e.g. `f1/sprite`, NOT `f1/Fixture.img/sprite`.
This proves a synthetic BMS IMG workflow, not all WZ encryption or game compatibility.

## Trusted source aliases

Set `MAPLEBENCH_SOURCES` to an operator-controlled JSON file; see `sources.example.json`.
Paths must be absolute. Entries may configure mapleVersion, iv (4-byte hex), gameVersion
and readOnly (default true). No real source/key configuration is silently installed.
`wz_asset_sources` lists aliases; `wz_asset_open_source` opens a configured file or
finds a plain IMG filename inside a folder. Traversal and directory reparse points are
excluded; ambiguous matches require explicit candidateIndex. Lookup inside split WZ
is not supported yet. Previews are not proof that an encryption profile works.

## Manual MCP registration (not automatically applied)

Run MapleBench separately, then configure a stdio MCP entry:

```json
{
  "command": "E:/MapleBench-NX/MapleMcp/bin/Release/net10.0-windows/maple-mcp.exe",
  "env": { "WZ_SERVER": "http://127.0.0.1:5100" }
}
```

Replace WZ_SERVER with the running backend's actual address. Do not assume the default port is free.
