// Applies the section-list locale fix to sections.js, byte-precisely.
//
// The defect: paintList() built each section's visible name and aria-label
// inside `if (!this.listBuilt)` and never reset the flag, so after switching
// English -> 繁中 the seven section names stayed English while their tooltips
// and archive notes followed along. Every other translated string in this list
// is refreshed on each paint.
//
// Written as a file with line-anchored edits rather than a series of string
// swaps: sections.js is mixed (332 CRLF / 157 LF in HEAD), and the Edit tool
// normalises it whole -- 15 real lines became a 165-line diff that way.

import { readFileSync, writeFileSync } from 'node:fs';
import { join, resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '../../..');
const FILE = join(ROOT, 'MapleBench/wwwroot/js/sections.js');

const original = readFileSync(FILE, 'utf8');
// Split AFTER each newline and keep the terminator with its line. Two earlier
// attempts did this wrongly: `split('\n')` drops the \r of a CRLF line, and a
// `/.*(\r\n|\n)/g` match does not advance past a newline because `.` never
// matches \r — both collapsed blocks of lines into one, which parses as a
// syntax error far from the real cause. `split(/(?<=\n)/)` round-trips exactly.
const lines = original.split(/(?<=\n)/);
const eolOf = (i) => (lines[i].endsWith('\r\n') ? '\r\n' : (lines[i].endsWith('\n') ? '\n' : ''));
const body = (l) => l.replace(/\r?\n$/, '');

if (lines.join('') !== original) throw new Error('split is not lossless');

const find = (needle) => {
  const i = lines.findIndex((l) => body(l) === needle);
  if (i < 0) throw new Error(`not found: ${needle}`);
  return i;
};
// `lines` holds terminators, so writing is a plain reassignment and the new
// text inherits the terminator the old line had.
const replaceLine = (i, text) => { lines[i] = text + eolOf(i); };
const insertAfter = (i, ...texts) => {
  const eol = eolOf(i);
  lines.splice(i + 1, 0, ...texts.map((t) => t + eol));
};

const changes = [];

// 1. Name the label element so the repaint can reach it.
// Replace the inline element FIRST, then insert: inserting shifts every index
// below it, so a replaceLine() holding a pre-insert index writes to the wrong
// line — which produced a duplicated label and a syntax error rather than an
// obvious failure.
{
  const i = find("              el('span', { class: 'sec-label', text: t(section.label) }),");
  const indent = body(lines[i]).match(/^\s*/)[0];
  replaceLine(i, `${indent}label,`);
  insertAfter(find("        const state = el('span', { class: 'sec-state' });"),
    `${indent}const label = el('span', { class: 'sec-label', text: t(section.label) });`);
  changes.push('label element captured and stored');
}

// 2. Keep the reference alongside entry and state.
{
  const i = find('        this.entries.set(section.id, { entry, state });');
  replaceLine(i, '        this.entries.set(section.id, { entry, state, label });');
  changes.push('label kept in the entries map');
}

// 3. Re-translate the name on every paint, not only on the first.
{
  const i = find("      refs.entry.setAttribute('aria-current', active ? 'true' : 'false');");
  insertAfter(i,
    '      // The visible name and its aria-label are built once, inside the',
    '      // listBuilt guard, while every other translated string in this list',
    '      // is refreshed on each paint. Switching language therefore left',
    '      // seven section names in English even though all seven are in the',
    '      // dictionary. Re-translating here costs nothing and leaves the guard',
    '      // doing what it exists for: not rebuilding the elements.',
    '      refs.label.textContent = t(section.label);',
    "      refs.entry.setAttribute('aria-label', t(section.label));");
  changes.push('name and aria-label re-translated on every paint');
}

const next = lines.join('');   // each line already carries its own terminator
if (next === original) {
  console.log('nothing to do: sections.js already carries the fix');
  process.exit(0);
}
writeFileSync(FILE, next, 'utf8');
console.log(`sections.js: ${changes.length} edits applied`);
for (const c of changes) console.log('  - ' + c);
