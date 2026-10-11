// Collapses keys delivered by more than one agent into one, deterministically.
//
// Twenty-five agents translating in parallel will independently translate a
// shared string: 'Name (A to Z)' lives in mob.js, npc.js and skill.js, and each
// wrote its own wording. The merge refuses to guess between them, which is
// right -- but it means the batch cannot land until they agree.
//
// The rule here is the same one the merge already uses for the dictionary: an
// established value wins, and among equally-new ones the first file in sorted
// order wins. What this adds is a REPORT, so every disagreement is visible and
// reviewable rather than resolved silently inside a merge.

import { readdirSync, readFileSync, writeFileSync } from 'node:fs';
import { join, resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '../../..');
const WWW = resolve(ROOT, 'MapleBench/wwwroot');
const INBOX = resolve(ROOT, process.argv[2] ?? 'tools/probe/i18n');

const unescape = (s) => s.replace(/\\'/g, "'").replace(/\\\\/g, '\\');
const dict = new Map();
const dictSrc = readFileSync(join(WWW, 'js/locale.zh-Hant.js'), 'utf8');
for (const m of dictSrc.matchAll(/^\s{2}'((?:[^'\\]|\\.)*)':\s*'((?:[^'\\]|\\.)*)',/gm)) {
  dict.set(unescape(m[1]), unescape(m[2]));
}

// key -> [{ file, value }] in sorted file order, so the winner is stable.
const byKey = new Map();
const files = readdirSync(INBOX).filter((f) => f.endsWith('.json')).sort();
for (const name of files) {
  const data = JSON.parse(readFileSync(join(INBOX, name), 'utf8'));
  for (const [en, zh] of Object.entries(data)) {
    if (!byKey.has(en)) byKey.set(en, []);
    byKey.get(en).push({ file: name, value: zh });
  }
}

const disagreements = [];
const resolved = [];

for (const [en, entries] of byKey) {
  if (entries.length < 2) continue;
  const distinct = [...new Set(entries.map((e) => e.value))];
  if (distinct.length === 1) continue;              // they agree; nothing to do

  // An established dictionary value settles it. Otherwise the earliest file does.
  const established = dict.get(en);
  const winner = established ?? entries[0].value;
  const rule = established ? 'existing dictionary value' : `first file (${entries[0].file})`;
  resolved.push({ en, winner, rule, entries });
  disagreements.push({ en, winner, rule, distinct });
}

// Write each file back without the keys that lost, so no agent's file can
// reintroduce them at merge time.
let removed = 0;
for (const name of files) {
  const path = join(INBOX, name);
  const data = JSON.parse(readFileSync(path, 'utf8'));
  const kept = {};
  for (const [en, zh] of Object.entries(data)) {
    const conflict = resolved.find((r) => r.en === en);
    if (conflict && zh !== conflict.winner) { removed++; continue; }
    kept[en] = zh;
  }
  writeFileSync(path, JSON.stringify(kept, null, 2) + '\n', 'utf8');
}

console.log(`keys delivered by more than one file : ${[...byKey.values()].filter((v) => v.length > 1).length}`);
console.log(`  of those, all in agreement          : ${[...byKey.values()].filter((v) => v.length > 1 && new Set(v.map((e) => e.value)).size === 1).length}`);
console.log(`disagreements resolved               : ${resolved.length}`);
console.log(`duplicate entries removed             : ${removed}`);
console.log(`total entries now                     : ${[...byKey.keys()].length}`);
for (const d of disagreements) {
  console.log(`\n${JSON.stringify(d.en.slice(0, 70))}`);
  console.log(`  winner (${d.rule}): ${d.winner}`);
  for (const v of new Set(d.distinct)) if (v !== d.winner) console.log(`  dropped: ${v}`);
}
