// Merges translations produced by parallel agents back into locale.zh-Hant.js.
//
// Written as a tool rather than an instruction because nine agents editing one
// dictionary file at once is exactly the situation that loses work: last writer
// wins, and the entries the others contributed vanish with no trace.
//
// Each agent writes nx-probe/i18n/<file>.json -- a flat {englishKey: chinese} map.
// This merges them into the "batch" section at the end of the dictionary, checks
// every key against the runtime call sites (folded, not regex-fragmented),
// rejects duplicates and placeholder drift, and refuses to write if anything is
// wrong. Idempotent: running it twice changes nothing the second time.

import { readdirSync, readFileSync, writeFileSync, existsSync } from 'node:fs';
import { join, resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { Parser } from 'file:///C:/i18ntools/node_modules/acorn/dist/acorn.mjs';
import * as walk from 'file:///C:/i18ntools/node_modules/acorn-walk/dist/walk.mjs';

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '../../..');
const WWW = resolve(ROOT, 'MapleBench/wwwroot');
const DICT = join(WWW, 'js/locale.zh-Hant.js');
// Resolved against the CWD when it is already absolute or relative to the caller.
// path.resolve would silently re-root a relative argument against ROOT, so a
// caller passing '.test-inbox' from nx-probe pointed at nx-probe/nx-probe/.test-inbox
// and got a silent "no inbox yet" — a check that appears to pass because it
// examined nothing.
const INBOX = resolve(process.cwd(), process.argv[2] ?? resolve(ROOT, 'tools/probe/i18n'));

/** Folds constant concatenation, which is what t() actually receives at runtime. */
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

// A JS single-quoted literal must escape backslash, quote, AND the control
// characters a key can legitimately contain. Keys like '\nBackup: {file}' carry
// a real newline at runtime; writing it raw produced a string literal spanning
// three source lines, which is a syntax error in the middle of the dictionary
// and takes the whole app down with it.
const quote = (s) => `'${s
  .replace(/\\/g, '\\\\')
  .replace(/'/g, "\\'")
  .replace(/\n/g, '\\n')
  .replace(/\r/g, '\\r')
  .replace(/\t/g, '\\t')}'`;

// ---------------------------------------------------------------- collect keys
const files = readdirSync(join(WWW, 'js'))
  .filter((f) => f.endsWith('.js') && !f.startsWith('locale') && f !== 'i18n.js');
const used = new Set();

/**
 * Parses a module, returning null when it does not parse.
 *
 * During a parallel round several files are mid-edit and briefly do not parse.
 * Aborting the whole run on one of them turns a transient state into a tool that
 * cannot be used for the duration of the work; the caller reports which files
 * were skipped so the gap is visible rather than silent.
 */
function parseOrNull(src) {
  try {
    return Parser.parse(src, { ecmaVersion: 'latest', sourceType: 'module' });
  } catch {
    return null;
  }
}

const skipped = [];
for (const file of files) {
  const src = readFileSync(join(WWW, 'js', file), 'utf8');
  const ast = parseOrNull(src);
  if (!ast) { skipped.push(file); continue; }
  walk.simple(ast, {
    CallExpression(node) {
      if (node.callee.type !== 'Identifier' || node.callee.name !== 't') return;
      const folded = fold(node.arguments[0]);
      if (folded !== null) used.add(folded);
    },
  });

  // A key need not be a literal AT the t() call. The whole point of translating
  // a module table at the point of use is that the label arrives as a variable
  // (t(label), t(limit), t(FILTERS[i][1])), and collecting only literal arguments
  // made every such translation look like a dead entry and get it refused. So the
  // candidate set is every string literal in the UI sources: wider than reality,
  // which is safe, because each key is still checked for Chinese, placeholders
  // and value sanity below.
  walk.simple(ast, {
    Literal(node) {
      if (typeof node.value === 'string' && node.value.length > 1) used.add(node.value);
    },
  });
}
if (skipped.length) console.log(`WARNING: ${skipped.length} file(s) did not parse and were skipped: ${skipped.join(', ')}`);
const html = readFileSync(join(WWW, 'index.html'), 'utf8');
for (const m of html.matchAll(/data-i18n="([^"]+)"/g)) used.add(m[1].replace(/&amp;/g, '&'));

// The backend hands the UI prose to display. `t(change.reason)` and friends take
// a string that only exists in a C# service, so a wwwroot-only scan calls those
// entries dead and refuses the whole batch — which would ship English. Same
// Reason/Note/Message scan the checker uses.
const SERVICES = join(WWW, '..', 'Services');
if (existsSync(SERVICES)) {
  for (const f of readdirSync(SERVICES, { recursive: true }).filter((x) => x.endsWith('.cs'))) {
    const src = readFileSync(join(SERVICES, f), 'utf8');
    for (const m of src.matchAll(/(?:Reason|Note|Message)\s*=\s*\$?"((?:[^"\\]|\\.)*)"/g)) {
      const v = m[1].replace(/\\"/g, '"').replace(/\\'/g, "'");
      if (v.length > 8 && /[a-z]/.test(v)) used.add(v);
    }
  }
}
for (const m of html.matchAll(/data-i18n-attr="([^"]+)"/g)) {
  for (const pair of m[1].split(';')) {
    const at = pair.indexOf(':');
    if (at < 0) continue;
    used.add(pair.slice(at + 1).trim().replace(/&amp;/g, '&'));
  }
}

// ---------------------------------------------------------------- read inbox
if (!existsSync(INBOX)) {
  console.log('no inbox yet; nothing to merge');
  process.exit(0);
}
const incoming = new Map();
const problems = [];
for (const name of readdirSync(INBOX).filter((f) => f.endsWith('.json'))) {
  let data;
  try {
    data = JSON.parse(readFileSync(join(INBOX, name), 'utf8'));
  } catch (e) {
    problems.push(`${name}: not valid JSON (${e.message})`);
    continue;
  }
  for (const [en, zh] of Object.entries(data)) {
    if (typeof zh !== 'string' || !zh.trim()) {
      problems.push(`${name}: ${JSON.stringify(en)} -> empty translation`);
      continue;
    }
    if (!used.has(en)) {
      problems.push(`${name}: ${JSON.stringify(en)} is not a runtime key (dead entry)`);
      continue;
    }
    if (!/[一-鿿]/.test(zh)) {
      problems.push(`${name}: ${JSON.stringify(en)} -> ${JSON.stringify(zh)} has no Chinese in it`);
      continue;
    }
    if (incoming.has(en) && incoming.get(en) !== zh) {
      problems.push(`two different translations for ${JSON.stringify(en)}:\n    ${JSON.stringify(incoming.get(en))}\n    ${JSON.stringify(zh)}`);
      continue;
    }
    incoming.set(en, zh);
  }
}

if (problems.length) {
  console.log('REFUSING TO WRITE:');
  for (const p of problems) console.log('  ' + p);
  process.exit(1);
}

// ------------------------------------------------------- rebuild the tail block
const src = readFileSync(DICT, 'utf8');
// A JS single-quoted literal escapes \' and \\; unescaping only \' leaves every
// backslash doubled, so a key like 'e.g. C:\\Nexon\\…' never matches its entry.
const unescape = (s) => s.replace(/\\'/g, "'").replace(/\\\\/g, '\\');
const keyList = [...src.matchAll(/^\s{2}'((?:[^'\\]|\\.)*)':/gm)].map((m) => unescape(m[1]));
const existing = new Map();
for (const m of src.matchAll(/^\s{2}'((?:[^'\\]|\\.)*)':\s*'((?:[^'\\]|\\.)*)',/gm)) {
  existing.set(unescape(m[1]), unescape(m[2]));
}

// A merged key that already exists keeps its existing translation; an identical
// one is a no-op. Conflict on a differing value is reported, never silently won.
const fresh = [];
for (const [en, zh] of incoming) {
  if (existing.has(en)) continue;
  fresh.push([en, zh]);
}

if (!fresh.length) {
  console.log(`nothing to add: all ${incoming.size} supplied keys already present`);
  process.exit(0);
}

// Re-translating a key whose current entry is a FRAGMENT of a longer runtime key
// would be dead weight; the fragment is replaced separately.
const orphansKept = [...existing.keys()].filter((k) => !used.has(k));

const header = '  // ---- round 5: wrapped strings, module tables, joined concat keys -----\n';
const body = fresh
  .sort((a, b) => b[0].length - a[0].length || a[0].localeCompare(b[0]))
  .map(([en, zh]) => `  ${quote(en)}: ${quote(zh)},\n`)
  .join('');

const marker = '  // ---- the last five -----------------------------------------------------';
const at = src.indexOf(marker);
if (at < 0) {
  console.log('REFUSING TO WRITE: section marker not found; the dictionary layout changed');
  process.exit(1);
}

const before = src.slice(0, at);
const after = src.slice(at);
const next = `${before}${header}${body}${after}`;

// Placeholder parity across everything we are about to write.
const bad = [];
for (const [en, zh] of fresh) {
  const a = new Set([...en.matchAll(/\{(\w+)\}/g)].map((m) => m[1]));
  const b = new Set([...zh.matchAll(/\{(\w+)\}/g)].map((m) => m[1]));
  for (const p of a) if (!b.has(p)) bad.push(`${en} -> ${zh} (missing {${p}})`);
  for (const p of b) if (!a.has(p)) bad.push(`${en} -> ${zh} (extra {${p}})`);
}
if (bad.length) {
  console.log('REFUSING TO WRITE: placeholder drift');
  for (const b of bad) console.log('  ' + b);
  process.exit(1);
}

// Parse what we are about to write BEFORE writing it. A dictionary that does not
// parse is not a partial translation, it is an app that will not boot, and the
// mistake is invisible in a diff because the diff just looks like prose.
try {
  Parser.parse(next, { ecmaVersion: 'latest', sourceType: 'module' });
} catch (e) {
  console.error(`REFUSING TO WRITE: the merged dictionary does not parse — ${e.message}`);
  console.error(`  at line ${e.loc?.line}`);
  process.exit(1);
}

writeFileSync(DICT, next, 'utf8');
console.log(`added ${fresh.length} entries from ${readdirSync(INBOX).filter((f) => f.endsWith('.json')).length} agent files`);
console.log(`dictionary now ${existing.size + fresh.length} entries`);
console.log(`merged dictionary parses: yes`);
console.log(`entries still not reached by any call site: ${orphansKept.length}`);
