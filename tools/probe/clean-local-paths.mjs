// Removes machine-specific absolute paths from files about to be published.
//
// A public repo should not carry the author's local folder layout — including a
// game-install path, and including one path that points at a directory which no
// longer exists (the project's old name). Each file used its paths differently
// (a dotnet constant, an argv default, a literal fixture list), so the
// replacement is per-file rather than one blanket substitution: a blanket
// replace would put an environment variable where a literal is expected and
// break the script.
//
// Every replacement keeps the script runnable — argv first, then an environment
// variable, then a documented default.

import { readFileSync, writeFileSync } from 'node:fs';
import { join, resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..');

const edits = [];
const apply = (rel, pairs) => {
  const path = join(ROOT, rel);
  let text = readFileSync(path, 'utf8');
  const before = text;
  for (const [from, to] of pairs) text = text.split(from).join(to);
  if (text !== before) {
    writeFileSync(path, text, 'utf8');
    edits.push(rel);
  }
};

// ---- dotnet runtime: a constant in JS, or argv[$5] ----
const DOTNET = "process.env.DOTNET_PATH || 'dotnet'";
for (const rel of [
  'tools/probe/check/clean-save-check.js',
  'tools/probe/check/gate-free-check.js',
  'tools/probe/check/mcp-close-check.js',
  'tools/probe/check/orphan-sweep-check.js',
  'tools/probe/nx/nx-open-race-check.js',
  'tools/probe/stress-data-loss.js',
]) {
  apply(rel, [
    [`'C:/MUWORK/app/scoop/apps/dotnet-sdk/current/dotnet.exe'`, DOTNET],
    [`"C:/MUWORK/app/scoop/apps/dotnet-sdk/current/dotnet.exe"`, DOTNET],
  ]);
}
apply('tools/probe/check/mcp-close-check.js', [
  [`process.argv[5] ?? ${DOTNET}`, DOTNET],
  ['C:/MUWORK/app/scoop/apps/dotnet-sdk/current/dotnet.exe', DOTNET],
]);

// ---- fixture roots: the search list ----
apply('tools/probe/find-nx-fixtures.js', [
  [`  'C:/MUWORK/GAME/MPSH',
  'C:/MUWORK/GAME/gamania Games',
  'C:/MUWORK/GAME/MAPLESOTRY',`,
    `  // Override rather than edit: the defaults below were one machine's layout.
  ...(process.env.NX_FIXTURE_ROOTS ?? '')
    .split(';').map((p) => p.trim()).filter(Boolean),`],
]);

// ---- shell + powershell round-trip drivers ----
apply('tools/probe/roundtrip.sh', [
  [`DOTNET='C:/MUWORK/app/scoop/apps/dotnet-sdk/current/dotnet.exe'`,
   `DOTNET="\${DOTNET_PATH:-dotnet}"`],
  [`VERIFY='C:/MUWORK/GAME/MAPLESOTRY/wz工具/追風WZ編輯器/agent-wz/src/NxVerify/bin/Release/net10.0-windows/NxVerify.dll'`,
   `VERIFY="\${NXVERIFY_DLL:-tools/NxVerify/bin/Release/net10.0-windows/NxVerify.dll}"`],
  [`SRC='C:/MUWORK/GAME/MPSH'`, `SRC="\${NX_FIXTURE_DIR:-}"`],
]);
apply('tools/probe/roundtrip.ps1', [
  [`$dotnet = 'C:\\MUWORK\\app\\scoop\\apps\\dotnet-sdk\\current\\dotnet.exe'`,
   `$dotnet = if ($env:DOTNET_PATH) { $env:DOTNET_PATH } else { 'dotnet' }`],
  [`$exe = 'C:\\MUWORK\\GAME\\MAPLESOTRY\\wz工具\\追風WZ編輯器\\agent-wz\\src\\NxVerify\\bin\\Release\\net10.0-windows\\NxVerify.exe'`,
   `$exe = if ($env:NXVERIFY_EXE) { $env:NXVERIFY_EXE } else { 'tools/NxVerify/bin/Release/net10.0-windows/NxVerify.exe' }`],
  [`$src  = 'C:\\MUWORK\\GAME\\MPSH'`, `$src  = $env:NX_FIXTURE_DIR`],
]);

// ---- a comment naming a game install directory ----
apply('tools/probe/inplace.sh', [
  ['# The real archives in C:\\MUWORK\\GAME\\MPSH are never written to.',
   '# The real archives under $NX_FIXTURE_DIR are never written to.'],
]);

// ---- python ----
apply('tools/probe/nx/shared.py', [
  [`for p in [r"C:\\MUWORK\\GAME\\MPSH\\Morph.nx", r"C:\\MUWORK\\GAME\\MPSH\\Item.nx", r"C:\\MUWORK\\GAME\\MPSH\\Character.nx"]:`, null],
].filter((p) => p));
apply('tools/probe/nx/shared.py', [
  ['for p in [r"C:\\MUWORK\\GAME\\MPSH\\Morph.nx", r"C:\\MUWORK\\GAME\\MPSH\\Item.nx", r"C:\\MUWORK\\GAME\\MPSH\\Character.nx"]:',
   'for p in [f\'{os.environ.get("NX_FIXTURE_DIR", ".")}\\\\{n}.nx\' for n in ("Morph", "Item", "Character")]:'],
]);
apply('tools/probe/nx/depthchk.py', [
  ['r"C:\\MUWORK\\GAME\\MPSH\\Character.nx"',
   'os.environ.get("NX_CHARACTER", "Character.nx")'],
]);
apply('tools/probe/nx/nxparse.py', [
  [`sys.argv[2:] or [r"C:\\MUWORK\\GAME\\MPSH\\Base.nx"]`,
   'sys.argv[2:] or [os.environ.get("NX_BASE", "Base.nx")]'],
]);

// ---- points at the project's OLD directory, which no longer exists ----
apply('tools/probe/undo-emdash.py', [
  [`spec = importlib.util.spec_from_file_location("fx", r"C:\\MUWORK\\GAME\\MAPLESOTRY\\wz工具\\追風WZ編輯器\\nx-probe\\fix-emdash.py")`,
   `# This referenced the project's previous directory name; that path no longer exists.
spec = importlib.util.spec_from_file_location("fx", os.environ.get("FIX_EMDASH", "fix-emdash.py"))`],
]);

// ---- markdown: state the path as a placeholder, keep the meaning ----
apply('HANDOVER.md', [
  ['**專案位置**：`C:\\MUWORK\\GAME\\MAPLESOTRY\\wz工具\\MapleBcnch-NX`',
   '**專案位置**：本倉庫根目錄'],
  ['.NET SDK 10.0（dotnet 在 C:\\MUWORK\\app\\scoop\\apps\\dotnet-sdk\\current\\）',
   '.NET SDK 10.0（`dotnet` 需在 PATH，或設 `DOTNET_PATH`）'],
]);
apply('JOURNAL/2026-10-10.md', [
  ['`C:\\MUWORK\\GAME\\MPSH\\` 一個位元都沒寫入。',
   '封存副本所在目錄一個位元都沒寫入。'],
]);

console.log(`cleaned ${edits.length} files:`);
for (const e of edits) console.log('  ' + e);
