// Strips a UTF-8 BOM from the files that have one, and reports what it did.
//
// Node's writeFileSync(..., 'utf8') does not add a BOM, so these three are
// better described as "carried one that should not have survived" than as
// "gained one". Either way the repo has no BOM in HEAD and .gitattributes says
// text=auto eol=lf, so a leading EF BB BF is a difference nobody asked for and
// that shows up in every diff that touches the file.
//
// Reports before/after so the change is auditable rather than assumed.

import { readFileSync, writeFileSync, readdirSync, statSync } from 'node:fs';
import { join, extname, resolve, dirname, relative } from 'node:path';
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

let stripped = 0;
const BOM = 0xFEFF;

for (const file of walk(WWW)) {
  const buf = readFileSync(file);
  if (buf.length < 3 || buf[0] !== 0xEF || buf[1] !== 0xBB || buf[2] !== 0xBF) continue;
  writeFileSync(file, buf.subarray(3));
  stripped += 1;
  console.log(`  stripped BOM  ${relative(WWW, file)}`);
}

console.log(stripped ? `${stripped} file(s) fixed` : 'no BOM found anywhere');

// A check that can fail this time.
let bad = 0;
for (const file of walk(WWW)) {
  const b = readFileSync(file);
  if (b[0] === 0xEF && b[1] === 0xBB && b[2] === 0xBF) {
    console.log(`FAIL  ${relative(WWW, file)} still starts with EF BB BF`);
    bad += 1;
  }
}
console.log(bad ? 'FAIL' : 'OK   wwwroot is BOM-free');
process.exit(bad ? 1 : 0);