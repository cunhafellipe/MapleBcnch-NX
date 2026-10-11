// Finds string literals that reach the user but never pass through t().
//
// The trap this exists for: grepping object properties (text:, label:, title:)
// misses positional arguments -- emptyState('layers', 'No Mob.wz open', ...),
// toast('...'), el('p', {}, 'literal'). Those are among the strings users read
// most often, and they were the ones previous passes dropped.
//
// Uses a real parser rather than a regex, so a literal in a comment, an import
// specifier, an object key or a CSS declaration is not reported. A literal counts
// as handled when it sits anywhere inside a t(...) call.

import { readdirSync, readFileSync, writeFileSync } from 'node:fs';
import { join, resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { Parser } from 'file:///C:/i18ntools/node_modules/acorn/dist/acorn.mjs';
import * as walk from 'file:///C:/i18ntools/node_modules/acorn-walk/dist/walk.mjs';

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '../../..');
const WWW = resolve(ROOT, 'MapleBench/wwwroot');
const OUT = resolve(ROOT, 'tools/probe/bare-strings.txt');

/** Object keys whose value the user reads. */
const VISIBLE_KEYS = new Set([
  'text', 'label', 'title', 'message', 'placeholder', 'tip', 'note', 'subtitle',
  'tooltip', 'aria-label', 'caption', 'summary', 'hint', 'description', 'value',
]);

/** Object keys that are never user-visible prose. */
const INERT_KEYS = new Set([
  'class', 'className', 'id', 'type', 'style', 'src', 'href', 'name', 'role',
  'data-tip', 'data-key', 'dataset', 'rel', 'target', 'method', 'action',
  'icon', 'kind', 'tone', 'placement', 'align', 'variant', 'shape', 'field',
  'column', 'width', 'height', 'rows', 'key', 'value-key', 'tag', 'lang', 'dir',
]);

/** Functions taking user-visible text as a positional argument. */
const POSITIONAL = {
  emptyState: [1, 2], toast: [0], toastError: [0, 1], statChip: [0], tip: [0],
  busyButton: [1], busyPanel: [0, 1], confirmDialog: [], promptForText: [], modal: [],
};

/** Values that are identifiers or protocol, never prose. */
const NOT_PROSE = new Set([
  'Image', 'Canvas', 'Property', 'Directory', 'File', 'String', 'Node', 'Folder',
  'folderOpen', 'search', 'save', 'info', 'star', 'grid4', 'layers', 'check',
  'list', 'close', 'cancel', 'ok', 'add', 'edit', 'copy', 'trash', 'warn', 'error',
  'success', 'loading', 'image', 'grid', 'file', 'folder', 'arrowUp', 'arrowDown',
  'text', 'label', 'title', 'message', 'default', 'none', 'all', 'name', 'type',
]);

const CSSISH = /[;{}]|^[a-z-]+\s*:|^[.#]|^@|^\d|^[\w-]+\s+\S+$/;

const isProse = (v) => {
  if (!v || v.length < 2) return false;
  if (NOT_PROSE.has(v)) return false;
  if (/^[a-z0-9_.-]+$/i.test(v)) return false;                    // one token
  if (/^[./#?&]/.test(v)) return false;                           // path, selector, query
  if (/^\/?(api|img|js|css|fonts)\//.test(v)) return false;
  if (/^[A-Z][a-zA-Z]*(?:[A-Z][a-zA-Z]*)+$/.test(v)) return false; // CamelCase kind
  if (/^[a-z]+(?:\.[a-z]+)+$/.test(v)) return false;              // mob.wz
  if (/^\d/.test(v)) return false;
  if (!/[A-Za-z]/.test(v)) return false;
  if (CSSISH.test(v)) return false;
  if (!/\s/.test(v) && v.length < 4) return false;
  return true;
};

const files = readdirSync(join(WWW, 'js'))
  .filter((f) => f.endsWith('.js') && !f.startsWith('locale') && f !== 'i18n.js');

const rows = [];

for (const file of files) {
  const src = readFileSync(join(WWW, 'js', file), 'utf8');
  const lines = src.split('\n');
  const ast = Parser.parse(src, { ecmaVersion: 'latest', sourceType: 'module', locations: true });

  const handled = new Set();
  const parentOf = new Map();
  walk.fullAncestor(ast, (node, ancestors) => {
    const parent = ancestors[ancestors.length - 2];
    if (parent) parentOf.set(node, parent);
    for (const a of ancestors) {
      if (a.type === 'CallExpression' && a.callee.type === 'Identifier' && a.callee.name === 't') {
        handled.add(node);
      }
    }
    const p = parent;
    if (p && p.type === 'Property' && p.key === node && !p.computed) handled.add(node);
    if (p && p.type === 'ImportDeclaration') handled.add(node);
    if (p && (p.type === 'ExportNamedDeclaration' || p.type === 'ExportAllDeclaration') && p.source) handled.add(node);
    if (p && p.type === 'MemberExpression' && p.property === node && p.computed) handled.add(node);
    if (p && p.type === 'MethodDefinition' && p.key === node && !p.computed) handled.add(node);
    if (p && p.type === 'LabeledStatement' && p.label === node) handled.add(node);
    if (p && p.type === 'BreakStatement' && p.label === node) handled.add(node);
    if (p && p.type === 'ContinueStatement' && p.label === node) handled.add(node);
    if (p && p.type === 'SwitchCase' && p.test === node) handled.add(node);
  });

  const report = (node, value) => {
    if (!isProse(value)) return;
    const parent = parentOf.get(node);

    // A literal in a constant table is translated where the table is read.
    let table = null;
    for (let a = parent; a; a = parentOf.get(a)) {
      if (a.type === 'VariableDeclarator' && a.init
          && (a.init.type === 'ArrayExpression' || a.init.type === 'ObjectExpression')) {
        table = a.id.type === 'Identifier' ? a.id.name : null;
        break;
      }
      if (a.type === 'FunctionDeclaration' || a.type === 'ClassDeclaration' || a.type === 'FunctionDeclaration') break;
    }
    if (table) {
      rows.push({ file, line: node.loc.start.line, kind: 'TABLE', reason: table, value, table });
      return;
    }

    let reason = null;
    if (parent && parent.type === 'Property' && parent.value === node && !parent.computed) {
      const key = parent.key.name ?? parent.key.value;
      if (VISIBLE_KEYS.has(key)) reason = `key:${key}`;
      else if (INERT_KEYS.has(key)) return;
    }
    if (!reason && parent && parent.type === 'CallExpression') {
      const callee = parent.callee.type === 'Identifier' ? parent.callee.name : null;
      const idx = parent.arguments.indexOf(node);
      if (callee && POSITIONAL[callee] && POSITIONAL[callee].includes(idx)) reason = `arg${idx}:${callee}`;
      else if (callee === 'el' && idx >= 2) reason = `arg${idx}:el`;
    }
    if (!reason && parent && parent.type === 'ConditionalExpression') reason = 'cond';
    if (!reason && parent && parent.type === 'LogicalExpression' && parent.left === node) reason = 'logical';
    if (!reason && parent && parent.type === 'ArrayExpression') reason = 'array';
    if (!reason) reason = 'other';

    rows.push({ file, line: node.loc.start.line, kind: 'CODE', reason, value, table: null,
      snippet: (lines[node.loc.start.line - 1] || '').trim().slice(0, 160) });
  };

  walk.full(ast, (node) => {
    if (handled.has(node)) return;
    if (node.type === 'Literal' && typeof node.value === 'string') report(node, node.value);
    else if (node.type === 'TemplateLiteral') {
      // Only when there is no substitution: a template with ${} usually builds
      // markup or a query and is handled where it is finally rendered.
      if (node.expressions.length === 0) report(node, node.quasis[0].value.cooked ?? '');
    }
  });
}

rows.sort((a, b) => a.file.localeCompare(b.file) || a.line - b.line || a.reason.localeCompare(b.reason));
writeFileSync(OUT, rows.map((r) => [r.file, r.line, r.kind, r.reason, r.value].join('\t')).join('\n') + '\n', 'utf8');

const code = rows.filter((r) => r.kind === 'CODE');
const table = rows.filter((r) => r.kind === 'TABLE');
const byFile = {};
for (const r of code) byFile[r.file] = (byFile[r.file] || 0) + 1;
console.log(`code candidates : ${code.length}`);
for (const [f, n] of Object.entries(byFile).sort((a, b) => b[1] - a[1])) console.log(`  ${f} ${n}`);
const byTable = {};
for (const r of table) byTable[`${r.file}:${r.reason}`] = (byTable[`${r.file}:${r.reason}`] || 0) + 1;
console.log(`table strings : ${table.length}`);
for (const [f, n] of Object.entries(byTable).sort((a, b) => b[1] - a[1])) console.log(`  ${f} ${n}`);
console.log(`written to : ${OUT}`);
