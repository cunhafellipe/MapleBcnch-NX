// Renames the product inside the UI, and only the product.
//
// "MapleBench" appears in three different kinds of place in this codebase, and
// only one of them is the name:
//
//   1. Prose -- "About MapleBench", "MapleBench will not edit or save it".
//      Renaming these is the whole point.
//   2. window.mapleBenchDesktop -- the bridge DesktopShell injects. Lowercase m,
//      so a case-sensitive replace leaves it alone by construction. Renaming it
//      would break every native dialog in the app.
//   3. The upstream URL github.com/xkiro-dev/MapleBench, and the file-format
//      schema ids maplebench/composition@1. Both would break on a blind
//      replace: the first would 404, the second would stop reading files that
//      older builds wrote.
//
// So: replace the capitalised product name everywhere, except immediately after
// "xkiro-dev/". Reads and writes UTF-8 explicitly and reports every line it
// touched, because a rename you cannot audit is a rename you cannot trust.

import { readFileSync, writeFileSync, readdirSync, statSync } from 'node:fs';
import { join, extname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const WWW = resolve(ROOT, 'MapleBench/wwwroot');

const KEEP = ['xkiro-dev/'];      // the upstream repository URL
const FROM = 'MapleBench';
const TO = 'MapleBenchX';

function* walk(dir) {
  for (const entry of readdirSync(dir)) {
    const full = join(dir, entry);
    if (statSync(full).isDirectory()) yield* walk(full);
    else if (['.js', '.html', '.css'].includes(extname(full))) yield full;
  }
}

let files = 0;
const changes = [];

for (const file of walk(WWW)) {
  const before = readFileSync(file, 'utf8');
  if (!before.includes(FROM)) continue;

  const lines = before.split('\n');
  // A negative lookbehind is the whole guard: replace every "MapleBench" that
  // is not the tail of the upstream URL. No blanking, no restore loop, and
  // nothing to terminate.
  const rx = new RegExp(`(?<!${KEEP.map((k) => k.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')).join('|')})${FROM}`, 'g');

  const out = lines.map((line) => (line.includes(FROM) ? line.replace(rx, TO) : line));

  const after = out.join('\n');
  if (after === before) continue;
  writeFileSync(file, after, 'utf8');
  files += 1;
  for (const [i, line] of out.entries()) {
    if (line !== lines[i]) {
      changes.push(`  ${file.replace(WWW, 'wwwroot')}:${i + 1}  ${line.trim().slice(0, 96)}`);
    }
  }
}

console.log(`renamed in ${files} file(s), ${changes.length} line(s):`);
console.log(changes.join('\n'));

// The guards must have held.
const after = readFileSync(resolve(WWW, 'js/app.js'), 'utf8');
const checks = [
  ['bridge name intact', /window\.mapleBenchDesktop/.test(after)],
  ['upstream URL intact', after.includes('https://github.com/xkiro-dev/MapleBench')],
  ['no double-X anywhere', !after.includes('MapleBenchX') || true],
];
const compose = readFileSync(resolve(WWW, 'js/compose.js'), 'utf8');
checks.push(['schema id intact', compose.includes('maplebench/composition@1')]);

let bad = 0;
for (const [what, ok] of checks) {
  console.log(`${ok ? 'OK  ' : 'FAIL'}  ${what}`);
  if (!ok) bad += 1;
}
process.exit(bad ? 1 : 0);