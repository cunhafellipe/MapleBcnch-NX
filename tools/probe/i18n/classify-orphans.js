// Splits the unreachable dictionary entries into two kinds, because they need
// opposite handling.
//
// 1. DEAD FRAGMENTS. A previous round stored fragment 1 of `t('a ' + 'b')` as
//    the key while the runtime receives the joined sentence, so 61 call sites
//    rendered English while the coverage checker called them translated. Those
//    fragments were replaced by the joined keys in this round, and the fragment
//    is now unreachable. Keeping them is harmless at runtime but they inflate
//    coverage() and they are a trap for the next translator, who will find them
//    and believe the work is done.
//
// 2. LEGITIMATE. Keys reached through t(<variable>) or a module table read at the
//    point of use -- SORT_MODES labels, section labels, GENDERS. A literal-only
//    scanner cannot see them and they must NOT be dropped.

import { readdirSync, readFileSync, writeFileSync } from 'node:fs';
import { join, resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { Parser } from 'file:///C:/i18ntools/node_modules/acorn/dist/acorn.mjs';
import * as walk from 'file:///C:/i18ntools/node_modules/acorn-walk/dist/walk.mjs';

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '../../..');
const WWW = resolve(ROOT, 'MapleBench/wwwroot');

function fold(node) {
  if (!node) return null;
  if (node.type === 'Literal' && typeof node.value === 'string') return node.value;
  if (node.type === 'TemplateLiteral') return node.expressions.length === 0 ? node.quasis[0].value.cooked : null;
  if (node.type === 'BinaryExpression' && node.operator === '+') {
    const l = fold(node.left);
    if (l === null) return null;
    const r = fold(node.right);
    if (r === null) return null;
    return l + r;
  }
  return null;
}
const unescape = (s) => s.replace(/\\n/g, '\n').replace(/\\r/g, '\r').replace(/\\t/g, '\t')
  .replace(/\\'/g, "'").replace(/\\\\/g, '\\');

const joined = new Set();
const literal = new Set();
/** Every string that lives inside a module-level table: reached via t(label). */
const tableValues = new Set();
const files = readdirSync(join(WWW, 'js')).filter((f) => f.endsWith('.js') && !f.startsWith('locale') && f !== 'i18n.js');
for (const f of files) {
  const src = readFileSync(join(WWW, 'js', f), 'utf8');
  let ast;
  try { ast = Parser.parse(src, { ecmaVersion: 'latest', sourceType: 'module' }); }
  catch { continue; }
  walk.simple(ast, {
    CallExpression(node) {
      if (node.callee.type !== 'Identifier' || node.callee.name !== 't') return;
      const k = fold(node.arguments[0]);
      if (k !== null) joined.add(k);
    },
    VariableDeclarator(node) {
      if (node.id.type !== 'Identifier') return;
      if (!node.init || (node.init.type !== 'ArrayExpression' && node.init.type !== 'ObjectExpression')) return;
      const visit = (n) => {
        if (!n) return;
        if (n.type === 'Literal' && typeof n.value === 'string') { tableValues.add(n.value); return; }
        if (n.type === 'ArrayExpression') n.elements.forEach(visit);
        else if (n.type === 'ObjectExpression') n.properties.forEach((p) => visit(p.value));
        else if (n.type === 'BinaryExpression' && n.operator === '+') visit(n.left);
      };
      visit(node.init);
    },
    Literal(node) { if (typeof node.value === 'string' && node.value.length > 1) literal.add(node.value); },
  });
}
const html = readFileSync(join(WWW, 'index.html'), 'utf8');
for (const m of html.matchAll(/data-i18n="([^"]+)"/g)) literal.add(m[1].replace(/&amp;/g, '&'));
for (const m of html.matchAll(/data-i18n-attr="([^"]+)"/g)) {
  for (const pair of m[1].split(';')) {
    const at = pair.indexOf(':');
    if (at >= 0) literal.add(pair.slice(at + 1).trim().replace(/&amp;/g, '&'));
  }
}

const dict = new Map();
const src = readFileSync(join(WWW, 'js/locale.zh-Hant.js'), 'utf8');
for (const m of src.matchAll(/^\s{2}'((?:[^'\\]|\\.)*)':\s*'((?:[^'\\]|\\.)*)',/gm)) {
  dict.set(unescape(m[1]), unescape(m[2]));
}

const dead = [];
const legit = [];
for (const k of dict.keys()) {
  if (joined.has(k)) continue;

  // Being a strict PREFIX of a longer runtime key is necessary but NOT
  // sufficient for "dead fragment". The first version used only that test and
  // deleted 'Cash Shop' and 'Map Editor' — both are SECTIONS labels reached
  // through t(section.label), and both happen to start a longer key
  // ('Cash Shop / Mobs / Game Data Search'). The app then logged
  // "[i18n] no zh-Hant translation for: Cash Shop" on every paint.
  //
  // The distinguishing fact: a fragment is a prefix that no code ever asks for
  // as a whole. So the key must ALSO appear in no other position — not as a
  // literal argument, not as a value in any module-level table. A table value
  // reaches t() as t(label), which no literal scan sees, so membership in a
  // table is what marks it alive.
  const isPrefixOfJoined = [...joined].some((j) => j.length > k.length && j.startsWith(k));
  const reachableSomehow = literal.has(k) || tableValues.has(k);
  if (isPrefixOfJoined && !reachableSomehow) dead.push(k);
  else legit.push(k);
}

const report = [
  '# Unreachable dictionary entries',
  '',
  `${dict.size} entries · ${dead.length} dead fragments · ${legit.length} reached through a variable or a table`,
  '',
  '## Dead fragments — safe to remove',
  '',
  'Each is the first fragment of a `t(\'a \' + \'b\')` whose joined key is now in the table.',
  'Keeping them changes nothing at runtime, but they make coverage() overstate itself and',
  'they are how the next translator convinces themselves a string is already handled.',
  '',
  ...dead.map((k) => `- ${JSON.stringify(k)}`),
  '',
  '## Reached through a variable or a table — keep',
  '',
  '`t(label)`, `t(SORT_MODES[i][1])`, `t(section.blurb)`. No literal at the call site, so a',
  'literal-only scanner cannot see them. Removing any of these would silently break it.',
  '',
  ...legit.map((k) => `- ${JSON.stringify(k)}`),
  '',
].join('\n');
writeFileSync(resolve(ROOT, 'tools/probe/orphans.md'), report, 'utf8');
console.log(`dead fragments : ${dead.length}`);
console.log(`legitimate     : ${legit.length}`);
console.log(`written to     : nx-probe/orphans.md`);
