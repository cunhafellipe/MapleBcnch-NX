// Collapses the over-replacement left by the rename.
//
// rename-ui.js replaced the literal "MapleBench", which is also a PREFIX of the
// "MapleBenchX" I had already written by hand in index.html, app.js and the CSS
// headers. So the second pass turned MapleBenchX into MapleBenchXX and the
// index.html title into MapleBenchXXX. Its own guard for this was
// `|| true`, which passes unconditionally -- the check was decorative.
//
// This collapses any run of X after the name back to exactly one, and reports
// every line it touches so the result can be read rather than trusted.

import { readFileSync, writeFileSync, readdirSync, statSync } from 'node:fs';
import { join, extname, resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '../../..');
const WWW = resolve(ROOT, 'MapleBench/wwwroot');

function* walk(dir) {
  for (const entry of readdirSync(dir)) {
    const full = join(dir, entry);
    if (statSync(full).isDirectory()) yield* walk(full);
    else if (['.js', '.html', '.css'].includes(extname(full))) yield full;
  }
}

const BAD = /MapleBenchX{2,}/g;
const changes = [];
let files = 0;

for (const file of walk(WWW)) {
  const before = readFileSync(file, 'utf8');
  if (!BAD.test(before)) { BAD.lastIndex = 0; continue; }
  BAD.lastIndex = 0;
  const after = before.replace(BAD, 'MapleBenchX');
  writeFileSync(file, after, 'utf8');
  files += 1;
  const a = before.split('\n');
  const b = after.split('\n');
  for (let i = 0; i < a.length; i += 1) {
    if (a[i] !== b[i]) {
      changes.push(`  ${file.replace(WWW, 'wwwroot')}:${i + 1}`);
      changes.push(`    -  ${a[i].trim().slice(0, 100)}`);
      changes.push(`    +  ${b[i].trim().slice(0, 100)}`);
    }
  }
}

console.log(`collapsed in ${files} file(s), ${changes.length / 3} line(s):`);
console.log(changes.join('\n'));

// Now a check that can actually fail.
let bad = 0;
for (const file of walk(WWW)) {
  const text = readFileSync(file, 'utf8');
  const m = text.match(/MapleBenchX{2,}/);
  if (m) { console.log(`FAIL  ${file.replace(WWW, 'wwwroot')} still has ${m[0]}`); bad += 1; }
}
console.log(bad ? `${bad} file(s) still wrong` : 'OK   no over-replacement anywhere');
process.exit(bad ? 1 : 0);