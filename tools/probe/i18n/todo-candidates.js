// Splits the AST scanner's candidate list into "definitely user prose" and
// "review by hand", and prints each with its source line so a translator can
// judge rather than guess.

import { readFileSync } from 'node:fs';
import { join, resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '../../..');
const WWW = resolve(ROOT, 'MapleBench/wwwroot');

const rows = readFileSync(resolve(ROOT, 'tools/probe/bare-strings.txt'), 'utf8')
  .split('\n').filter(Boolean).map((l) => {
    const [file, line, kind, reason, ...rest] = l.split('\t');
    return { file, line: +line, kind, reason, value: rest.join('\t') };
  });

/** Reasons that mean the literal is definitely rendered as text. */
const DEFINITE = new Set([
  'key:text', 'key:label', 'key:title', 'key:message', 'key:placeholder', 'key:tip',
  'key:note', 'key:subtitle', 'key:aria-label', 'key:caption', 'key:summary',
  'key:hint', 'key:description',
  'arg0:toast', 'arg1:toastError', 'arg1:emptyState', 'arg2:emptyState',
  'arg1:busyButton', 'arg0:busyPanel', 'arg0:statChip', 'arg0:tip',
]);

const want = process.argv[2] ?? 'definite';
const rowsWanted = want === 'table'
  ? rows.filter((r) => r.kind === 'TABLE')
  : rows.filter((r) => r.kind === 'CODE' && (
    want === 'definite' ? DEFINITE.has(r.reason) : !DEFINITE.has(r.reason)));

const cache = new Map();
const source = (file) => {
  if (!cache.has(file)) cache.set(file, readFileSync(join(WWW, 'js', file), 'utf8').split('\n'));
  return cache.get(file);
};

for (const r of rowsWanted) {
  console.log(`${r.file}:${r.line}  [${r.reason}]  ${JSON.stringify(r.value)}`);
  console.log(`      ${(source(r.file)[r.line - 1] || '').trim().slice(0, 170)}`);
}
console.log(`--- ${rowsWanted.length} rows ---`);
