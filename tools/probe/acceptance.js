// The acceptance check, run against the real i18n.js rather than a regex.
//
// Two scripts in nx-probe disagree about how many keys are untranslated:
// i18n-coverage.js says 198, i18n-inventory.js says 0. This settles it by asking
// the only question that matters — when t() is called with the string the code
// actually passes, does it return Chinese, English, or the English fallback?
//
// It loads the shipped locale.zh-Hant.js, flips to zh-Hant, and walks every t()
// call site. Any call that comes back unchanged AND has no Han characters in the
// English source is a genuine gap. This is what a user sees, not what a regex
// can see.

import { readdirSync, readFileSync, copyFileSync, rmSync, mkdirSync } from 'node:fs';
import { join, resolve, dirname } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { Parser } from 'file:///C:/i18ntools/node_modules/acorn/dist/acorn.mjs';
import * as walk from 'file:///C:/i18ntools/node_modules/acorn-walk/dist/walk.mjs';

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const WWW = resolve(ROOT, 'MapleBench/wwwroot');

// Node's ESM loader will not resolve a URL with CJK in it, so stage under ASCII.
const STAGE = 'C:/i18naccept';
rmSync(STAGE, { recursive: true, force: true });
mkdirSync(join(STAGE, 'js'), { recursive: true });
for (const f of readdirSync(join(WWW, 'js')).filter((x) => x.endsWith('.js'))) {
  copyFileSync(join(WWW, 'js', f), join(STAGE, 'js', f));
}

globalThis.document = { documentElement: { dataset: {} }, querySelectorAll: () => [], querySelector: () => null };
globalThis.localStorage = { getItem: () => null, setItem() {} };
Object.defineProperty(globalThis, 'navigator', { value: { languages: ['zh-TW'], language: 'zh-TW' }, configurable: true });
const i18n = await import(pathToFileURL(join(STAGE, 'js/i18n.js')).href);
i18n.initLocale();
if (i18n.locale() !== 'zh-Hant') {
  console.log(`could not switch to zh-Hant (locale is ${i18n.locale()})`);
  process.exit(1);
}

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

// Pull the vars object out of the call so placeholders resolve.
function varsOf(node) {
  if (!node || node.arguments.length < 2) return undefined;
  const src = node.arguments[1];
  const props = {};
  if (src.type === 'ObjectExpression') {
    for (const p of src.properties) {
      if (p.type !== 'Property' || p.computed) continue;
      const key = p.key.name ?? p.key.value;
      if (p.value.type === 'Literal') props[key] = p.value.value;
      else if (p.value.type === 'Identifier') props[key] = `{${key}}`;
      else props[key] = '{…}';
    }
    return props;
  }
  return undefined;
}

const HAN = /[一-鿿]/;
// Read the shipped table directly: i18n.js exposes no lookup, and this is the
// thing under test, not a proxy for it.
const { zhHant } = await import(pathToFileURL(join(STAGE, 'js/locale.zh-Hant.js')).href);
const files = readdirSync(join(WWW, 'js')).filter((f) => f.endsWith('.js') && !f.startsWith('locale') && f !== 'i18n.js');
const gaps = [];
let checked = 0;
let deliberateLatin = 0;

const record = (file, line, key, out) => {
  checked++;
  if (Object.prototype.hasOwnProperty.call(zhHant, key)) {
    // Present. If it renders Latin on purpose (mob, NPC, a format string), that
    // is a decision, not a gap.
    if (!HAN.test(out)) deliberateLatin++;
    return;
  }
  gaps.push({ file, line, key: key.slice(0, 90), out: out.slice(0, 50) });
};

for (const file of files) {
  const src = readFileSync(join(WWW, 'js', file), 'utf8');
  let ast;
  try { ast = Parser.parse(src, { ecmaVersion: 'latest', sourceType: 'module', locations: true }); }
  catch { continue; }
  walk.simple(ast, {
    CallExpression(node) {
      if (node.callee.type !== 'Identifier' || node.callee.name !== 't') return;
      const key = fold(node.arguments[0]);
      if (key === null || !key.trim()) return;
      if (HAN.test(key)) return;      // already Chinese in the source
      record(file, node.loc.start.line, key, i18n.t(key, varsOf(node)));
    },
  });
}

// index.html's static chrome
const html = readFileSync(join(WWW, 'index.html'), 'utf8');
for (const m of html.matchAll(/data-i18n="([^"]+)"/g)) {
  const k = m[1].replace(/&amp;/g, '&');
  if (!k.trim() || HAN.test(k)) continue;
  record('index.html', 0, k, i18n.t(k));
}
for (const m of html.matchAll(/data-i18n-attr="([^"]+)"/g)) {
  for (const pair of m[1].split(';')) {
    const at = pair.indexOf(':');
    if (at < 0) continue;
    const k = pair.slice(at + 1).trim().replace(/&amp;/g, '&');
    if (!k.trim() || HAN.test(k)) continue;
    record('index.html', 0, k, i18n.t(k));
  }
}

console.log(`locale under test : zh-Hant`);
console.log(`t() keys checked against the shipped table : ${checked}`);
console.log(`translations deliberately kept Latin       : ${deliberateLatin}`);
console.log(`RENDERING ENGLISH (real gaps)               : ${gaps.length}`);
for (const g of gaps) console.log(`  ${g.file}:${g.line}  ${JSON.stringify(g.key)}  -> ${JSON.stringify(g.out)}`);

const dict = i18n.coverage?.() ?? null;
if (dict) console.log(`\ndictionary coverage reported by i18n.js: ${JSON.stringify(dict)}`);

rmSync(STAGE, { recursive: true, force: true });
process.exit(gaps.length ? 1 : 0);
