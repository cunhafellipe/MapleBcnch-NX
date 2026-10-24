---
id: W-096
title: Create an empty .nx / .wz file so an MCP agent can build test fixtures into it
state: done
priority: 70
owner: 
opened: 2026-10-24
closed: 2026-10-24
depends: []
refs: [dependencies/MapleLib/WzLib/WzFile.cs:132; dependencies/MapleLib/WzLib/WzFile.cs:560; MapleBench/Services/Nx/NxWriter.cs:306; MapleBench/Api/Endpoints.cs:326; MapleBench/Services/WzSessionService.cs:228; MapleMcp/AssetTools.cs:35; pm/PROJECT.yaml:71; pm/SPEC.yaml:552]
blocked_on: 
evidence: EmptyArchiveService + POST /api/files/create-empty + MCP tool wz_asset_create_empty + suite empty-archive.py (SPEC + gate.ps1 Run line). Measured 2026-10-24: empty .wz 62B PKG1 and empty .nx 82B PKG4 both created (200) and opened back through POST /api/files/open with 0 children (close 204); refusals measured 400 (overwrite / format-name mismatch / path inside the folder of the open archives = C:\MUWORK\GAME\MPSHS, no file left). MCP tools/list+tools/call verified against a fresh maple-mcp.dll. Gate 2026-10-24 15:57: GATE_SUITES_DECLARED=35 OWNED=34 RAN=34, RED SUITES: 0, exit 0.
---
## Intent

An MCP agent can open an existing archive, add nodes, edit scalars and save —
but it cannot START one. Every fixture it wants to build has to be carved out
of a real archive first, which means writing into the live data root and
mutating the archives the game is running on. After this, an agent can ask for
an **empty** `.nx` or `.wz` container, get a path to it, and build its test
fixtures into that, with the data root untouched.

## Reference

**Own-design** (there is no upstream to port): the pieces exist, the entry
points do not. Measured by reading the tree (2026-10-24):

**What already works, and can be used as-is**

- `public WzFile(short gameVersion, WzMapleVersion version)` —
  `dependencies/MapleLib/WzLib/WzFile.cs:132` — builds a **brand new empty**
  archive in memory (`new WzDirectory()` + `WzHeader.GetDefault()`, :134-135).
  The app already constructs one this way: `MapleBench/Services/Nx/NxToWzFile.cs:138`.
- `public void SaveToDisk(string path, bool? override_saveAs64BitWZ = null,
  WzMapleVersion savingToPreferredWzVer = ..., byte[] overrideWzIv = null)` —
  `dependencies/MapleLib/WzLib/WzFile.cs:560` — writes classic `.wz` bytes:
  header (:631-645), directory block (:648), image data (:653).
- `WzHeader.GetDefault()` — `dependencies/MapleLib/WzLib/WzHeader.cs:49`:
  ident `"PKG1"` (:53), `fstart = 60` (:55).
- Tree building: `WzDirectory.AddImage(WzImage)` `WzDirectory.cs:698`,
  `AddDirectory(WzDirectory)` `:708`, `WzImage(string name)` `WzImage.cs:71`.
- `NxWriter.Write(WzFile, …)` — `MapleBench/Services/Nx/NxWriter.cs:306,335` —
  writes `.nx` from a `WzFile`; the writer always emits a root node at index 0
  (`:519`, `:546`).
- MapleLib's own `.wz`→`.nx` converter:
  `WzToNxSerializer.SerializeFile(WzFile, string)` —
  `dependencies/MapleLib/WzLib/Serializer/WzToNxSerializer.cs:42` (writes `PKG4`
  at :51). **The app does not use it**; `NxWriter` is the app's writer.

**What does not exist**

- No `WzIOFactory`, no `WzFile.Create`, no `WzDirectory.Create`, no `SaveAs`
  anywhere in MapleLib (grepped). The nearest equivalent is `SaveToDisk`.
- No create-archive **endpoint**: `POST /api/files/open`
  (`MapleBench/Api/Endpoints.cs:326`) requires the file to already exist —
  `WzSessionService.cs:228-229` throws `FileNotFoundException`. `POST /api/nx/merge`
  (`MapleBench/Api/NxMergeEndpoints.cs:30`) needs an existing target
  (`NxMergeService.cs:61-64`), and `POST /api/files/save` (`Endpoints.cs:438`)
  needs an already-open `FileId`.
- No create-archive **MCP tool**: `MapleMcp/AssetTools.cs` has
  `wz_asset_files` :35, `wz_asset_open` :39, `wz_asset_add` :97,
  `wz_asset_save_as` :133 and the rest, but nothing that makes a container.
  `wz_map_create` (`MapleMcp/MapTools.cs:372`) creates a *map document inside*
  an open archive, not an archive.

**The formats**

- The format is decided by **magic, not by extension**:
  `WzSessionService.cs:233-248` → `NxArchive.IsNxFile(path)`
  (`MapleBench/Services/Nx/NxArchive.cs:183-189`), magic `0x34474B50` ("PKG4")
  at :94. `.wz` magic is `"PKG1"` (`WzHeader.cs:53`).
- Minimal `.nx`: 52-byte header (`NxArchive.cs:97`), 8 count/offset pairs at
  `0x04..0x2C` (:342-349), 20-byte node records (:103), tables in the order
  node → string → **audio → bitmap** (:554-563). `Validate` imposes no minimum
  node count (:525-564).
- Minimal `.wz`: `"PKG1"` + FSize + FStart(60) + null-terminated copyright +
  version byte (`WzFile.cs:631-645`) + directory block (:648).
- MapleLib **cannot read `.nx` at all** — the `.nx` reader is app-side
  (`MapleBench/Services/Nx/NxArchive.cs:88`).

**The rules that bind the design**

- `pm/RULES.md` R-1 (cite construct **and** caller), R-6 (own-design declared
  in code at the site), R-7 (no scope widening without a work item), R-13 (the
  gate is `python tools/pm.py gate`), R-16 (never type a CJK path into a
  script), R-20 (an item cannot close with an unfinished acceptance block).
- `pm/SPEC.yaml` `suites:` (:136) — a new suite must be declared with `file`,
  `runner`, `verdict`, `red_marker`, `asserts: true`, `areas`. Suite scripts
  live in `tools/verify/suites/` (`tools/pm.py:889`).
- `tools/verify/AGENTS.md` rule 8 — crafted fixtures live in two machine-local
  directories, `C:\mbtest2\` and `C:\mbtest2-gen\` (:259-266).
- `pm/SPEC.yaml:552-556` — the **`data-root-untouched`** invariant: any write
  under `C:\MUWORK\GAME\MPSHS` turns the doctor red. Output must land in
  `pm/PROJECT.yaml:71` `scratch_dir: '%TEMP%\maplebench-verify'`.

## Approach

(As planned; the shape did not change, and the Result says what landed.)

1. **The surface.** One service + one endpoint + one MCP tool, all taking a
   target path the caller chooses, and all refusing the folder the open
   archives live in.
2. **`.wz` first.** `new WzFile(gameVersion, version)` (`WzFile.cs:132`) ->
   `SaveToDisk(path, …)` (`:560`), with the IV passed explicitly.
3. **`.nx` from the same WzFile**, through the app's own `NxWriter.Write` — no
   second format implementation.
4. **Prove the container OPENS** through the app's own open path, and prove the
   refusals, rather than asserting them in a comment.
5. **Keep the footguns out.** `SaveToDisk` writes its scratch into the working
   directory under a name derived from the output name alone
   (`WzFile.cs:591`), so the creator uses a GUID stem (T-31).
6. **Declare the suite**, and wire it into `gate.ps1` — a declaration without
   the `Run` line is DRIFT (T-33).

## Acceptance

```
# The shape it has to take:

#   1. create an empty .wz and an empty .nx into %TEMP%\maplebench-verify
#      -> POST /api/files/create-empty answers 200, the file is on disk, and
#         the first four bytes are the magic of its format (PKG1 / PKG4)
#   2. open BOTH through the app's own open path, and read the archive list
#      -> POST /api/files/open answers 200 with an id; GET /api/children on
#         that id answers [] (an empty container holds no images)
#   3. the refusals hold, MEASURED and not asserted in a comment:
#      -> creating over an existing file answers 400, and the file is intact
#      -> a .nx name with format 'wz' answers 400, and leaves no file
#      -> a path inside the folder the OPEN archives live in answers 400, and
#         leaves no file -- this is the data-root guarantee
#   4. the whole gate stays RED SUITES: 0
```

## Result

**Landed (2026-10-24).** Four pieces and a measurement:

- `MapleBench/Services/EmptyArchiveService.cs` (new) — builds the container,
  refuses three things, and reads the container back before it gives it its
  name.
- `MapleBench/Models/EmptyArchiveDtos.cs` (new) — the request and the answer.
- `POST /api/files/create-empty` (`MapleBench/Api/Endpoints.cs`) — the entry
  point that did not exist; every other route opens something already on disk.
- `wz_asset_create_empty` (`MapleMcp/AssetTools.cs`) — the same thing for an
  agent.
- `tools/verify/suites/empty-archive.py` (new) + its `pm/SPEC.yaml` row + its
  `Run` line in `tools/verify/gate.ps1`.

**Measured, before any of it was written** (a scratch probe against the built
assemblies): an empty classic WZ is 62 bytes, magic `PKG1`, and reads back
`Success` with 0 images and 0 folders at game version -1, 0, 95, 112 and 230
alike; an empty NX is 82 bytes, magic `PKG4`, and `NxArchive.Open` counts 1
node. The round trip was the open question in the research above, and it holds
— but see T-32 for what that does NOT prove.

**Measured through the app's own paths** (`tools/verify/suites/empty-archive.py`,
run against the chain's instance with the 16 archives open):

```
create .wz   status=200 bytes=62  magic=b'PKG1'  nodes=0 images=0
create .nx   status=200 bytes=82  magic=b'PKG4'  nodes=1 images=0
open .wz     status=200 id='f19' kind='wz'   children=0  close=204
open .nx     status=200 id='f20' kind='nx'   children=0  close=204
overwrite    status=400  "... already exists. This creates a NEW container and never overwrites one ..."
mismatch     status=400  "The format 'wz' does not match the file name: 'mismatch.nx' is a NX container."
live folder  status=400  "'maplebench-empty-probe.wz' would be created inside 'C:\MUWORK\GAME\MPSHS',
                          which is where an open archive lives."
```

The live-folder refusal is the one that matters: `C:\MUWORK\GAME\MPSHS` is the
data root, the refusal was measured against the folder the open archives really
live in, and no file was left there.

**Measured through the MCP** (a stdio `initialize` -> `tools/list` ->
`tools/call` against a freshly built `maple-mcp.dll`): `wz_asset_create_empty`
is in the tool list, and calling it with `commit: true` produced an 82-byte
`PKG4` container on disk.

**Measured by the gate** (`python tools/pm.py gate`, 2026-10-24 15:57): the
chain ran build -> deploy -> open-archives -> gate and answered
`GATE_SUITES_DECLARED=35 / OWNED=34 / RAN=34`, **`RED SUITES: 0`**, exit 0.
The new suite is in that run: `=== empty-archive === ok GREEN? BAD=0`.

**One thing this cost.** The first chain run stopped BEFORE any suite ran:
the doctor answered `DRIFT empty-archive registry=True gate=False`, because a
suite declared in `pm/SPEC.yaml` is not run until `gate.ps1` has a
`Run '<id>'` line for it. Declaring a suite is half the work (T-33).

**Still open, and not in this item:** the MCP server the desktop app is holding
runs the OLD `maple-mcp.dll` — its output file is locked by the running
process, so the new tool appears only after that server is restarted. The
backend route is live and measured; only the agent-facing wrapper waits for
that restart.

