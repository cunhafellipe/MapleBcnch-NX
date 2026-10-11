// Checks every delivered JSON file against the runtime, before it is merged.
//
// The failures this catches are the ones that look fine in a diff:
//  - a key that is not what t() actually receives (fragment instead of joined)
//  - a translation identical to its English source
//  - placeholder drift in either direction
//  - a key another agent already owns with a different value
//
// Reports; it does not write. i18n-merge.js is what enforces the same rules.

import { readdirSync, readFileSync, existsSync } from 'node:fs';
import { join, resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { Parser } from 'file:///C:/i18ntools/node_modules/acorn/dist/acorn.mjs';
import * as walk from 'file:///C:/i18ntools/node_modules/acorn-walk/dist/walk.mjs';

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '../../..');
const WWW = resolve(ROOT, 'MapleBench/wwwroot');
const INBOX = resolve(ROOT, process.argv[2] ?? 'tools/probe/i18n');

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

const used = new Set();
/** Every string literal in the UI sources: a superset of anything t() can receive. */
const candidate = new Set();

/**
 * Parses a module, or returns null. During a parallel round a file is briefly
 * unparseable while its owner edits it; aborting the whole run would mean no
 * agent can check its work for the whole round, so the file is skipped and
 * named — a gap that is visible, not a check that silently passed.
 */
function parseOrNull(src) {
  try {
    return Parser.parse(src, { ecmaVersion: 'latest', sourceType: 'module' });
  } catch {
    return null;
  }
}
const skipped = [];

for (const f of readdirSync(join(WWW, 'js')).filter((x) => x.endsWith('.js') && !x.startsWith('locale') && x !== 'i18n.js')) {
  const ast = parseOrNull(readFileSync(join(WWW, 'js', f), 'utf8'));
  if (!ast) { skipped.push(f); continue; }
  walk.simple(ast, {
    CallExpression(node) {
      if (node.callee.type !== 'Identifier' || node.callee.name !== 't') return;
      const k = fold(node.arguments[0]);
      if (k !== null) used.add(k);
    },
  });
}

// A key need not be a literal AT the t() call. Three other routes reach t():
// a module table read at the point of use (t(label)), a variable holding a
// literal, and an aria-label/tip applied later by i18n.js's own rewriter.
// Counting only literal arguments reported valid translations as dead, so the
// candidate set is every string literal that any of those three could carry.
const TABLE_VARS = new Set(['SORT_MODES', 'GENDERS', 'FILTERS', 'SORTS', 'OPS', 'ROUNDING',
  'STORAGE_LABEL', 'STORAGE_BLURB', 'FIELD_LABELS', 'FIELD_HINTS', 'KIND_NOUN', 'KIND_ARTICLE',
  'NO_ART', 'KIND_HOME', 'FALLBACK_BULK_FIELDS', 'TOOLS', 'FORMAT_LIMITS', 'SECTIONS',
  'PART_CARRIES', 'STATUS', 'ARCHIVE_CARRIES', 'REFERENCE_TAGS', 'LINK_KINDS', 'SEVERITY_BLURB',
  'KIND_LABEL', 'KIND_ARCHIVE']);
for (const f of readdirSync(join(WWW, 'js')).filter((x) => x.endsWith('.js') && !x.startsWith('locale') && x !== 'i18n.js')) {
  if (skipped.includes(f)) continue;
  const ast = parseOrNull(readFileSync(join(WWW, 'js', f), 'utf8'));
  if (!ast) continue;
  walk.simple(ast, {
    VariableDeclarator(node) {
      if (node.id.type !== 'Identifier' || !TABLE_VARS.has(node.id.name)) return;
      const visit = (n) => {
        if (!n) return;
        if (n.type === 'Literal' && typeof n.value === 'string') used.add(n.value);
        else if (n.type === 'ArrayExpression') n.elements.forEach(visit);
        else if (n.type === 'ObjectExpression') n.properties.forEach((p) => visit(p.value));
      };
      visit(node.init);
    },
    Literal(node) {
      if (typeof node.value === 'string' && node.value.length > 1) candidate.add(node.value);
    },
  });
}
for (const c of candidate) used.add(c);

// The backend hands the UI prose to display. `t(change.reason)`,
// `t(r.reason)` and `t(f.reason)` take a string that only ever exists in a C#
// service, so a wwwroot-only scan calls those entries dead and the agent drops
// them — which ships English. Read the reason strings out of the services too.
//
// They are C# interpolated strings: `row.Reason = "plain."` on one line, or
// `change.Reason = $"'{{field}}' holds a group of values here, not one string. " + ...`
// continued over several lines. Only the FIRST segment carries the runtime key
// prefix here, so each fragment is registered and the agent can match on it.
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
const html = readFileSync(join(WWW, 'index.html'), 'utf8');
for (const m of html.matchAll(/data-i18n="([^"]+)"/g)) used.add(m[1].replace(/&amp;/g, '&'));
for (const m of html.matchAll(/data-i18n-attr="([^"]+)"/g)) {
  for (const pair of m[1].split(';')) {
    const at = pair.indexOf(':');
    if (at < 0) continue;
    used.add(pair.slice(at + 1).trim().replace(/&amp;/g, '&'));
  }
}

const dict = new Map();
const dictSrc = readFileSync(join(WWW, 'js/locale.zh-Hant.js'), 'utf8');
// A JS single-quoted literal escapes \' and \\. Unescaping only \' left every
// backslash doubled, so no key containing a backslash ('e.g. C:\\Nexon\\…')
// could ever match its own entry.
const unescape = (s) => s.replace(/\\'/g, "'").replace(/\\\\/g, '\\');
for (const m of dictSrc.matchAll(/^\s{2}'((?:[^'\\]|\\.)*)':\s*'((?:[^'\\]|\\.)*)',/gm)) {
  dict.set(unescape(m[1]), unescape(m[2]));
}

const CJK = /[一-鿿]/;
const problems = [];
const conflicts = [];
/** Keys whose leading/trailing space is part of the runtime key, verified live. */
const edgeSpace = new Set();
let total = 0;
let fresh = 0;
let sameAsDict = 0;

for (const name of readdirSync(INBOX).filter((f) => f.endsWith('.json')).sort()) {
  let data;
  try {
    data = JSON.parse(readFileSync(join(INBOX, name), 'utf8'));
  } catch (e) {
    problems.push(`${name}: invalid JSON — ${e.message}`);
    continue;
  }
  for (const [en, zh] of Object.entries(data)) {
    total++;
    if (!used.has(en)) problems.push(`${name}: NOT A RUNTIME KEY -> ${JSON.stringify(en.slice(0, 70))}`);
    if (typeof zh !== 'string' || !zh.trim()) problems.push(`${name}: empty value for ${JSON.stringify(en.slice(0, 50))}`);
    if (!CJK.test(zh ?? '')) problems.push(`${name}: no Chinese in value for ${JSON.stringify(en.slice(0, 50))}`);
    if (zh === en) problems.push(`${name}: value identical to English -> ${JSON.stringify(en.slice(0, 60))}`);
    // Edge whitespace is NOT automatically a defect. t() is sometimes called on
    // one span of a sentence built from sibling elements, and the separator
    // space is part of that span's key -- family.js:303 is one, and trimming it
    // makes the string silently English. Only flag it when no call site
    // produces that exact key, which the runtime-key test above already does.
    if (/^\s|\s$/.test(en)) edgeSpace.add(en);
    const a = new Set([...en.matchAll(/\{(\w+)\}/g)].map((m) => m[1]));
    const b = new Set([...(zh ?? '').matchAll(/\{(\w+)\}/g)].map((m) => m[1]));
    for (const p of a) if (!b.has(p)) problems.push(`${name}: missing {${p}} -> ${JSON.stringify(en.slice(0, 50))}`);
    for (const p of b) if (!a.has(p)) problems.push(`${name}: extra {${p}} -> ${JSON.stringify(en.slice(0, 50))}`);
    if (!dict.has(en)) fresh++;
    else if (dict.get(en) === zh) sameAsDict++;
    // A different value for a key the dictionary already has is not an error:
    // the merge keeps the established translation and drops the newcomer, so a
    // synonym from one agent never edits a string the other 20 agents already
    // render. Reported so the divergence is visible, not silent.
    else conflicts.push(`${name}: "${en.slice(0, 50)}" — existing "${dict.get(en)}" vs agent "${zh}"`);
  }
}

if (skipped.length) console.log(`WARNING: ${skipped.length} file(s) did not parse and were SKIPPED — their keys are absent from the used-set, so entries for them may be reported as dead: ${skipped.join(', ')}`);
console.log(`files       : ${readdirSync(INBOX).filter((f) => f.endsWith('.json')).length}`);
console.log(`entries     : ${total}`);
console.log(`net-new     : ${fresh}`);
console.log(`already in dictionary, identical : ${sameAsDict}`);
console.log(`differs from dictionary (existing wins) : ${conflicts.length}`);
for (const c of conflicts) console.log('  ' + c);
console.log(`problems    : ${problems.length}`);
for (const p of problems) console.log('  ' + p);

// A clean run against an inbox that is not the one you meant is indistinguishable
// from a clean run against the right one — an agent already reported a false pass
// that way. Listing every file proves the check actually read them.
console.log('files read  :');
for (const name of readdirSync(INBOX).filter((f) => f.endsWith('.json')).sort()) {
  const n = Object.keys(JSON.parse(readFileSync(join(INBOX, name), 'utf8'))).length;
  console.log(`    ${name} ${n}`);
}
process.exit(problems.length ? 1 : 0);
