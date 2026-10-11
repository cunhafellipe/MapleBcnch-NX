// Locates real .wz and .nx archives on this machine, read-only.
//
// The map editor's NX path has never been run end to end: opening one, editing,
// and saving. Every claim about it in the codebase is a comment. This finds the
// fixtures so the next step can execute rather than assert.
//
// Never writes, never copies, never opens the user's live client for editing.
// It only reports paths and sizes.

import { readdirSync, statSync, existsSync } from 'node:fs';
import { join } from 'node:path';

const ROOTS = [
  'C:/MUWORK/GAME/MPSH',
  'C:/MUWORK/GAME/gamania Games',
  'C:/MUWORK/GAME/MAPLESOTRY',
];

const found = { nx: [], wz: [] };

const walk = (dir, depth, out) => {
  if (depth > 7 || out.length > 400) return;
  let entries;
  try { entries = readdirSync(dir, { withFileTypes: true }); }
  catch { return; }
  for (const e of entries) {
    const p = join(dir, e.name);
    if (e.isDirectory()) {
      const lower = e.name.toLowerCase();
      // Skip trees that cannot contain game data and are expensive to walk.
      if (['node_modules', '.git', 'bin', 'obj', 'packages', 'temp', 'tmp'].includes(lower)) continue;
      walk(p, depth + 1, out);
      continue;
    }
    const lower = e.name.toLowerCase();
    if (lower.endsWith('.nx') && out.nx.length < 60) {
      try { const s = statSync(p); out.nx.push({ path: p, mb: (s.size / 1048576).toFixed(1) }); } catch { /* ignore */ }
    } else if (lower.endsWith('.wz') && /(map|etc|string)/.test(lower) && out.wz.length < 60) {
      try { const s = statSync(p); out.wz.push({ path: p, mb: (s.size / 1048576).toFixed(1) }); } catch { /* ignore */ }
    }
  }
};

for (const root of ROOTS) {
  if (existsSync(root)) walk(root, 0, found);
}

const report = (title, list) => {
  console.log(`\n=== ${title} (${list.length}) ===`);
  for (const f of list.sort((a, b) => parseFloat(b.mb) - parseFloat(a.mb)).slice(0, 25)) {
    console.log(`  ${String(f.mb).padStart(9)} MB  ${f.path}`);
  }
};

console.log('Scanned read-only. Nothing was opened for writing, copied, or modified.');
report('.nx archives', found.nx);
report('.wz map/etc/string archives', found.wz);
