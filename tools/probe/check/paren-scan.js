// Finds where parentheses stop balancing, ignoring anything inside a string,
// a template literal or a comment.
//
// The eye is bad at this: audit.js after an interrupted edit had a doubled
// close that looked fine in isolation, and eyeballing the reported line sent
// the search in the wrong direction twice. The imbalance starts well before
// the line the parser complains about -- the parser only notices when it hits
// the end of a construct -- so this reports the first line where the running
// depth departs from where it should be.

import { readFileSync } from 'node:fs';

const file = process.argv[2];
const src = readFileSync(file, 'utf8');

let depth = 0;
let line = 1;
let state = 'code';   // code | single | double | template | line-comment | block-comment
let quote = '';
let templateDepth = 0;   // ${ } nesting inside a template

const report = [];
let prev = 0;

for (let i = 0; i < src.length; i += 1) {
  const c = src[i];
  const n = src[i + 1];

  if (c === '\n') { line += 1; if (state === 'line-comment') state = 'code'; prev = depth; continue; }

  if (state === 'line-comment') continue;
  if (state === 'block-comment') { if (c === '*' && n === '/') { state = 'code'; i += 1; } continue; }
  if (state === 'single' || state === 'double') {
    if (c === '\\') { i += 1; continue; }
    if (c === quote) state = 'code';
    continue;
  }
  if (state === 'template') {
    if (c === '\\') { i += 1; continue; }
    if (c === '$' && n === '{') { state = 'code'; templateDepth += 1; depth += 1; i += 1; continue; }
    if (c === '}' && templateDepth > 0) { state = 'template'; templateDepth -= 1; depth -= 1; continue; }
    if (c === '`') state = 'code';
    continue;
  }

  // code
  if (c === '/' && n === '/') { state = 'line-comment'; i += 1; continue; }
  if (c === '/' && n === '*') { state = 'block-comment'; i += 1; continue; }
  if (c === "'") { state = 'single'; quote = "'"; continue; }
  if (c === '"') { state = 'double'; quote = '"'; continue; }
  if (c === '`') { state = 'template'; continue; }
  if (c === '(' || c === '[' || c === '{') { depth += 1; continue; }
  if (c === ')' || c === ']' || c === '}') {
    depth -= 1;
    if (depth < 0) { report.push(`line ${line}: closes below zero — ${src.slice(Math.max(0, i - 60), i + 10).split('\n').pop()}`); break; }
  }
}

console.log(`final depth: ${depth}  (0 = balanced)`);
const lines = src.split('\n');
// Show the neighbourhood of every line whose depth went below what it was at
// the end of the previous line -- which is where a stray ) or a missing ( shows.
let d = 0, s = 'code', q = '', td = 0;
for (let ln = 1; ln <= lines.length; ln += 1) {
  const before = d;
  for (const c of lines[ln - 1]) {
    if (s === 'sq' || s === 'dq') { if (c === '\\') continue; if (c === q) s = 'code'; continue; }
    if (s === 'tp') { if (c === '`' && td === 0) s = 'code'; continue; }
    if (s === 'lc') continue;
    if (c === '/' && false) continue;
    if (c === "'") { s = 'sq'; q = "'"; continue; }
    if (c === '"') { s = 'dq'; q = '"'; continue; }
    if (c === '`') { s = 'tp'; continue; }
    if (c === '(' || c === '[' || c === '{') d += 1;
    else if (c === ')' || c === ']' || c === '}') d -= 1;
  }
  if (d < 0 && report.length === 0) { report.push(`line ${ln}: depth went negative -> ${lines[ln - 1].trim().slice(0, 90)}`); }
  if (ln > 1 && Math.abs(d - before) > 2 && ln < 60) {
    report.push(`line ${ln}: depth jumped by ${d - before} -> ${lines[ln - 1].trim().slice(0, 90)}`);
  }
  if (ln === lines.length) report.push(`end of file: depth ${d}`);
}
console.log(report.slice(0, 12).join('\n'));