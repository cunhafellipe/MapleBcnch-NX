// The authoritative i18n inventory: every t() key as the RUNTIME sees it, plus
// every user-visible string that never passes through t().
//
// Why this exists instead of a regex: a call like t('a ' + 'b') looks up the
// JOINED string, but a regex over the source only ever sees fragment 1. The
// dictionary had 112 entries that are fragments of longer strings, so every one
// of those call sites silently rendered English while the coverage script
// reported them translated. Folding constant concatenation is the difference
// between the report and what a user actually sees.

import { readdirSync, readFileSync, writeFileSync } from 'node:fs';
import { join, resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { Parser } from 'file:///C:/i18ntools/node_modules/acorn/dist/acorn.mjs';
import * as walk from 'file:///C:/i18ntools/node_modules/acorn-walk/dist/walk.mjs';

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '../../..');
const WWW = resolve(ROOT, 'MapleBench/wwwroot');
const OUT = resolve(ROOT, 'tools/probe/i18n-inventory.txt');

/** Folds `'a' + 'b'`, `\`a${'b'}\``, and literal-only templates into one string. */
function fold(node) {
  if (!node) return null;
  if (node.type === 'Literal' && typeof node.value === 'string') return node.value;
  if (node.type === 'TemplateLiteral') {
    if (node.expressions.length === 0) return node.quasis[0].value.cooked ?? '';
    return null;                                  // real interpolation: not a constant
  }
  if (node.type === 'BinaryExpression' && node.operator === '+') {
    const left = fold(node.left);
    if (left === null) return null;
    const right = fold(node.right);
    if (right === null) return null;
    return left + right;
  }
  return null;
}

const unescape = (s) => s
  .replace(/\\n/g, '\n').replace(/\\r/g, '\r').replace(/\\t/g, '\t')
  .replace(/\\'/g, "'").replace(/\\\\/g, '\\');

const dictSrc = readFileSync(join(WWW, 'js/locale.zh-Hant.js'), 'utf8');
const keyList = [...dictSrc.matchAll(/^\s{2}'((?:[^'\\]|\\.)*)':/gm)].map((m) => unescape(m[1]));
const have = new Set(keyList);

const html = readFileSync(join(WWW, 'index.html'), 'utf8');
const files = readdirSync(join(WWW, 'js'))
  .filter((f) => f.endsWith('.js') && !f.startsWith('locale') && f !== 'i18n.js');

const callSites = new Map();   // folded runtime key -> [file:line]
const live = new Set();

for (const file of files) {
  const src = readFileSync(join(WWW, 'js', file), 'utf8');
  const ast = Parser.parse(src, { ecmaVersion: 'latest', sourceType: 'module', locations: true });

  walk.simple(ast, {
    CallExpression(node) {
      if (node.callee.type !== 'Identifier' || node.callee.name !== 't') return;
      const folded = fold(node.arguments[0]);
      if (folded === null) return;
      if (!callSites.has(folded)) callSites.set(folded, []);
      callSites.get(folded).push(`${file}:${node.loc.start.line}`);
    },
  });

  for (const m of src.matchAll(/\bt\(\s*'((?:[^'\\]|\\.)*)'/g)) {
    // Fragment as the checker sees it; kept only to report the discrepancy.
    live.add(unescape(m[1]));
  }
}

for (const m of html.matchAll(/data-i18n="([^"]+)"/g)) {
  const k = m[1].replace(/&amp;/g, '&');
  if (!callSites.has(k)) callSites.set(k, []);
  callSites.get(k).push('index.html');
}
for (const m of html.matchAll(/data-i18n-attr="([^"]+)"/g)) {
  for (const pair of m[1].split(';')) {
    const at = pair.indexOf(':');
    if (at < 0) continue;
    const k = pair.slice(at + 1).trim().replace(/&amp;/g, '&');
    if (!callSites.has(k)) callSites.set(k, []);
    callSites.get(k).push('index.html');
  }
}
// aria-label="..." is a call site with no data-i18n on it. Scanning only
// data-i18n* reported 193 entries as "unreachable" that index.html plainly
// uses -- the report was wrong about the dictionary, not the other way round.
for (const m of html.matchAll(/\baria-label="([^"]+)"/g)) {
  const k = m[1].replace(/&amp;/g, '&');
  if (!callSites.has(k)) callSites.set(k, []);
  callSites.get(k).push('index.html:aria-label');
}
// data-tip="..." is a call site too, and it is a common one to miss: the
// inventory read it as title= and reported keys that no markup on the page
// actually carries.
for (const m of html.matchAll(/\bdata-tip="([^"]+)"/g)) {
  const k = m[1].replace(/&amp;/g, '&');
  if (!callSites.has(k)) callSites.set(k, []);
  callSites.get(k).push('index.html:data-tip');
}
// The KEY side of data-i18n-attr. Without this the report said
// "UNTRANSLATED 0" while index.html pointed t() at six keys the dictionary
// does not contain -- a hook pointing at nothing is exactly the gap this
// script exists to catch, and the report called it clean.
for (const m of html.matchAll(/\bdata-i18n-attr="([^"]+)"/g)) {
  for (const pair of m[1].split(';')) {
    const at = pair.indexOf(':');
    if (at < 0) continue;
    const k = pair.slice(at + 1).trim().replace(/&amp;/g, '&');
    if (!callSites.has(k)) callSites.set(k, []);
    callSites.get(k).push('index.html:i18n-attr');
  }
}

const untranslated = [...callSites.entries()].filter(([k]) => !have.has(k))
  .sort((a, b) => a[0].length - b[0].length || a[0].localeCompare(b[0]));

// A dictionary entry no call site ever produces: unreachable, and the reason a
// previous round "translated" a fragment and believed it done.
const orphans = keyList.filter((k) => !callSites.has(k));

const dupes = keyList.filter((k, i, a) => a.indexOf(k) !== i);

// Placeholder parity, checked on the key the runtime uses.
const dict = {};
for (const m of dictSrc.matchAll(/^\s{2}'((?:[^'\\]|\\.)*)':\s*'((?:[^'\\]|\\.)*)',/gm)) {
  dict[unescape(m[1])] = unescape(m[2]);
}
const bad = [];
for (const [k] of callSites) {
  const zh = dict[k];
  if (!zh) continue;
  const en = new Set([...k.matchAll(/\{(\w+)\}/g)].map((m) => m[1]));
  const cn = new Set([...zh.matchAll(/\{(\w+)\}/g)].map((m) => m[1]));
  for (const p of en) if (!cn.has(p)) bad.push(`${k} -> ${zh} (missing {${p}})`);
  for (const p of cn) if (!en.has(p)) bad.push(`${k} -> ${zh} (extra {${p}})`);
}

writeFileSync(OUT, untranslated.map(([k, where]) => `${where.join(',')}\t${k}`).join('\n') + '\n', 'utf8');

const byFile = {};
for (const [, where] of untranslated) for (const w of new Set(where.map((s) => s.split(':')[0]))) {
  byFile[w] = (byFile[w] || 0) + 1;
}
console.log(`dictionary entries      : ${have.size}`);
console.log(`runtime call sites      : ${callSites.size}`);
console.log(`UNTRANSLATED (runtime)  : ${untranslated.length}`);
for (const [f, n] of Object.entries(byFile).sort((a, b) => b[1] - a[1])) console.log(`    ${f} ${n}`);
console.log(`unreachable entries     : ${orphans.length}`);
for (const o of orphans.slice(0, 15)) console.log(`    ${JSON.stringify(o)}`);
console.log(`duplicate keys          : ${dupes.length}`);
console.log(`placeholder mismatches  : ${bad.length}`);
for (const b of bad.slice(0, 10)) console.log(`    ${b}`);
console.log(`written to : ${OUT}`);
process.exit(untranslated.length || dupes.length || bad.length ? 1 : 0);
