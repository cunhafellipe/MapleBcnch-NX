// Removes the dead fragment entries listed by classify-orphans.js.
//
// Each is fragment 1 of a t('a ' + 'b') call whose JOINED key is in the table.
// The runtime never looks them up, so deleting them changes nothing a user sees --
// but coverage() counts entries, and leaving 116 of them makes the app report
// itself more translated than it is, which is the exact failure this round exists
// to fix. They are also a trap: the next translator greps the table, finds the key,
// and concludes the work is done.
//
// Refuses to run unless the entry is genuinely unreachable, and writes a backup
// first, because the dictionary is the one file that cannot be reconstructed by
// re-running the agents.

import { readFileSync, writeFileSync, copyFileSync } from 'node:fs';
import { Parser } from 'file:///C:/i18ntools/node_modules/acorn/dist/acorn.mjs';
import { join, resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '../../..');
const DICT = join(ROOT, 'MapleBench/wwwroot/js/locale.zh-Hant.js');

const unescape = (s) => s.replace(/\\n/g, '\n').replace(/\\r/g, '\r').replace(/\\t/g, '\t')
  .replace(/\\'/g, "'").replace(/\\\\/g, '\\');

const lines = readFileSync(DICT, 'utf8').split('\n');
const doomed = new Set(JSON.parse(process.argv[2] ?? '[]').map(unescape));

const removed = [];
const kept = [];
for (const line of lines) {
  const m = /^(\s{2})'((?:[^'\\]|\\.)*)':\s*'((?:[^'\\]|\\.)*)',\s*$/.exec(line);
  if (m && doomed.has(unescape(m[2]))) {
    removed.push(unescape(m[2]));
    continue;
  }
  kept.push(line);
}

if (removed.length !== doomed.size) {
  console.error(`REFUSING: matched ${removed.length} of ${doomed.size} target entries`);
  console.error('the dictionary layout may have changed; re-run classify-orphans.js');
  process.exit(1);
}

const backup = join(ROOT, 'tools/probe/locale.before-fragment-cleanup.js');
copyFileSync(DICT, backup);

// Parse before writing. The first version of this tool deleted a line that was
// part of an entry spanning several source lines and left the dictionary
// unparseable — the app would not have booted. A line-based edit over a file that
// contains multi-line values has to check its own output.
try {
  Parser.parse(kept.join('\n'), { ecmaVersion: 'latest', sourceType: 'module' });
} catch (e) {
  console.error(`REFUSING TO WRITE: the result does not parse — ${e.message} at line ${e.loc?.line}`);
  console.error(`the dictionary is unchanged; backup at ${backup}`);
  process.exit(1);
}

writeFileSync(DICT, kept.join('\n'), 'utf8');
console.log(`removed ${removed.length} dead fragment entries`);
console.log(`result parses: yes`);
console.log(`backup: ${backup}`);
