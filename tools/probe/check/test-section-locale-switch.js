// Proves the section list re-translates when the language changes at runtime.
//
// The defect: paintList() built each section's name inside `if (!this.listBuilt)`
// and never reset the flag, so after switching English -> 繁中 the seven section
// names stayed English while their tooltips and archive notes followed along.
// A test that only inspects the source would not catch it -- the strings ARE in
// the dictionary -- so this drives the real classes.

import { readFileSync, mkdtempSync, rmSync, mkdirSync, copyFileSync, readdirSync } from 'node:fs';
import { join, resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { pathToFileURL } from 'node:url';

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '../../..');
const WWW = resolve(ROOT, 'MapleBench/wwwroot');

// Node's ESM loader will not resolve a URL with CJK in it on Windows, so the
// modules are staged under an ASCII path first. That is a loader limitation,
// not a property of the app.
const STAGE = 'C:/i18nsecttest';
rmSync(STAGE, { recursive: true, force: true });
mkdirSync(join(STAGE, 'js'), { recursive: true });
// Stage the whole module graph: sections.js imports api.js, and a partial copy
// fails on a missing specifier rather than on the thing under test.
for (const f of readdirSync(join(WWW, 'js')).filter((x) => x.endsWith('.js'))) {
  copyFileSync(join(WWW, 'js', f), join(STAGE, 'js', f));
}

// A DOM just rich enough for these modules to construct their elements.
const nodes = [];
function makeNode(tag) {
  const node = {
    tagName: tag, children: [], dataset: {}, style: {}, attrs: {}, classList: {
      add() {}, remove() {}, toggle() {}, contains: () => false,
    },
    _text: '',
    get textContent() { return node._text; },
    set textContent(v) { node._text = String(v); },
    get firstChild() { return node.children[0] ?? null; },
    appendChild(c) { node.children.push(c); return c; },
    removeChild(c) { node.children = node.children.filter((x) => x !== c); return c; },
    append(...c) { node.children.push(...c); },
    prepend(c) { node.children.unshift(c); },
    setAttribute(k, v) { node.attrs[k] = String(v); },
    getAttribute(k) { return node.attrs[k] ?? null; },
    removeAttribute(k) { delete node.attrs[k]; },
    addEventListener() {}, removeEventListener() {}, focus() {}, blur() {},
    querySelector: () => null, querySelectorAll: () => [], closest: () => null,
    matches: () => false, contains: () => false, remove() {}, cloneNode() { return makeNode(tag); },
    getBoundingClientRect: () => ({ top: 0, left: 0, width: 0, height: 0 }),
  };
  nodes.push(node);
  return node;
}
globalThis.document = {
  createElement: makeNode,
  createElementNS: (_ns, tag) => makeNode(tag),
  createTextNode: (t) => ({ nodeValue: t }),
  documentElement: { dataset: {}, style: { setProperty() {} }, classList: { add() {}, remove() {}, toggle() {} } },
  body: makeNode('body'),
  addEventListener() {}, removeEventListener() {},
  querySelector: () => null, querySelectorAll: () => [],
  createDocumentFragment: () => makeNode('fragment'),
};
globalThis.localStorage = { getItem: () => null, setItem() {}, removeItem() {} };
// Bun's global navigator is getter-only, so define rather than assign.
Object.defineProperty(globalThis, 'navigator', {
  value: { languages: ['en-US'], language: 'en-US' },
  configurable: true,
  writable: true,
});
globalThis.window = globalThis;
globalThis.matchMedia = () => ({ matches: false, addEventListener() {}, removeEventListener() {} });
globalThis.requestAnimationFrame = (fn) => setTimeout(fn, 0);
globalThis.getComputedStyle = () => ({ getPropertyValue: () => '' });
globalThis.CSS = { escape: (s) => s };

let sections;
try {
  sections = await import(pathToFileURL(join(STAGE, 'js/sections.js')).href);
} catch (e) {
  console.log('could not import sections.js:', e.message);
  process.exit(1);
}
const i18n = await import(pathToFileURL(join(STAGE, 'js/i18n.js')).href);
i18n.initLocale();

const app = {
  files: [], stages: new Map(),
  openFilePicker() {}, copyText() {},
};
const host = makeNode('div');
const shell = new sections.Sections({ host, app });

shell.paintList();
const first = shell.entries.get('mobs') ?? [...shell.entries.values()][0];
const beforeLabel = first.label?.textContent ?? first.entry.textContent;
const beforeAria = first.entry.getAttribute('aria-label');
console.log(`locale            : ${i18n.locale()}`);
console.log(`section name      : ${JSON.stringify(beforeLabel)}`);
console.log(`aria-label        : ${JSON.stringify(beforeAria)}`);

i18n.setLocale('zh-Hant');
shell.paintList();
const afterLabel = first.label?.textContent ?? first.entry.textContent;
const afterAria = first.entry.getAttribute('aria-label');
console.log(`\nafter switch      : ${JSON.stringify(afterLabel)}`);
console.log(`aria-label        : ${JSON.stringify(afterAria)}`);

// What matters is that the value CHANGED and now equals what the dictionary
// holds for that key -- not that it contains Han characters. "Mobs" translates to
// "Mob" and "NPCs" to "NPC", because mob and NPC are domain terms kept Latin on
// purpose. Asserting Chinese here would fail on a correct fix.
const dict = (await import(pathToFileURL(join(STAGE, 'js/locale.zh-Hant.js')).href)).zhHant;
// Find the SECTIONS row whose label is what we rendered, so the expectation
// comes from the table rather than from a hard-coded guess.
const src = readFileSync(join(WWW, 'js/sections.js'), 'utf8');
const SECTIONS_BY_LABEL = new Map();
for (const m of src.matchAll(/label:\s*'((?:[^'\\]|\\.)*)'/g)) {
  SECTIONS_BY_LABEL.set(m[1].replace(/\\'/g, "'"), m[1].replace(/\\'/g, "'"));
}
const section = SECTIONS_BY_LABEL.get(beforeLabel) ?? beforeLabel;
const expected = dict[section] ?? null;
console.log(`\nexpected for ${JSON.stringify(section)} : ${JSON.stringify(expected)}`);

const changed = afterLabel !== beforeLabel && afterAria !== beforeAria;
const matchesDict = expected === null || (afterLabel === expected && afterAria === expected);
const ok = changed && matchesDict;
console.log(`\n${ok ? 'pass' : 'FAIL'}  section name and aria-label re-translate on a live language switch`
  + (changed ? '' : ' (value did not change)')
  + (matchesDict ? '' : ' (did not match the dictionary)'));
rmSync(STAGE, { recursive: true, force: true });
process.exit(ok ? 0 : 1);
