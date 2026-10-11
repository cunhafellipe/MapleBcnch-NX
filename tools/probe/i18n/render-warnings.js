// Renders the real section list in zh-Hant and fails on ANY i18n warning.
//
// i18n.js logs "[i18n] no zh-Hant translation for: X" once per missing key. A
// warning during a normal paint is the cheapest possible detector for a missing
// translation: it fires from the real runtime, on the real strings, with no
// static analysis able to argue with it.
//
// This is the check that caught 'Cash Shop' and 'Map Editor' after the fragment
// sweep removed them — neither showed up in any static report, but both warned
// on every repaint of the section list.

import { readdirSync, copyFileSync, rmSync, mkdirSync } from 'node:fs';
import { join, resolve, dirname } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '../../..');
const WWW = resolve(ROOT, 'MapleBench/wwwroot');

const STAGE = 'C:/i18nrender';
rmSync(STAGE, { recursive: true, force: true });
mkdirSync(join(STAGE, 'js'), { recursive: true });
for (const f of readdirSync(join(WWW, 'js')).filter((x) => x.endsWith('.js'))) {
  copyFileSync(join(WWW, 'js', f), join(STAGE, 'js', f));
}

const mk = (tag) => {
  const n = {
    tagName: tag, children: [], dataset: {}, style: {}, attrs: {},
    classList: { add() {}, remove() {}, toggle() {}, contains: () => false },
    _t: '',
    get textContent() { return n._t; },
    set textContent(v) { n._t = String(v); },
    get firstChild() { return n.children[0] ?? null; },
    appendChild(c) { n.children.push(c); return c; },
    removeChild(c) { n.children = n.children.filter((x) => x !== c); return c; },
    append(...c) { n.children.push(...c); },
    prepend(c) { n.children.unshift(c); },
    setAttribute(k, v) { n.attrs[k] = String(v); },
    getAttribute(k) { return n.attrs[k] ?? null; },
    removeAttribute(k) { delete n.attrs[k]; },
    addEventListener() {}, removeEventListener() {}, focus() {}, blur() {},
    querySelector: () => null, querySelectorAll: () => [], closest: () => null,
    matches: () => false, contains: () => false, remove() {},
    cloneNode() { return mk(tag); },
    getBoundingClientRect: () => ({ top: 0, left: 0, width: 0, height: 0 }),
    insertAdjacentHTML() {}, offsetWidth: 0, offsetHeight: 0,
  };
  return n;
};
globalThis.document = {
  createElement: mk, createElementNS: (_n, t) => mk(t), createTextNode: (t) => ({ nodeValue: t }),
  documentElement: { dataset: {}, style: { setProperty() {} }, classList: { add() {}, remove() {}, toggle() {} } },
  body: mk('body'), addEventListener() {}, removeEventListener() {},
  querySelector: () => null, querySelectorAll: () => [], createDocumentFragment: () => mk('frag'),
};
globalThis.localStorage = { getItem: () => null, setItem() {}, removeItem() {} };
Object.defineProperty(globalThis, 'navigator', { value: { languages: ['zh-TW'], language: 'zh-TW' }, configurable: true });
globalThis.window = globalThis;
globalThis.matchMedia = () => ({ matches: false, addEventListener() {}, removeEventListener() {} });
globalThis.requestAnimationFrame = (f) => setTimeout(f, 0);
globalThis.getComputedStyle = () => ({ getPropertyValue: () => '' });
globalThis.CSS = { escape: (s) => s };

const warnings = [];
const realWarn = console.warn;
console.warn = (...a) => { if (String(a[0]).includes('[i18n]')) warnings.push(String(a[0])); else realWarn(...a); };

const i18n = await import(pathToFileURL(join(STAGE, 'js/i18n.js')).href);
i18n.initLocale();
const { Sections } = await import(pathToFileURL(join(STAGE, 'js/sections.js')).href);

const app = { files: [], stages: new Map(), openFilePicker() {}, copyText() {} };
const shell = new Sections({ host: mk('div'), app });
shell.paintList();

console.warn = realWarn;

const labels = [...shell.entries.entries()].map(([id, r]) => `  ${id.padEnd(12)} ${r.label.textContent}`);
console.log(`locale      : ${i18n.locale()}`);
console.log('section list as rendered:');
for (const l of labels) console.log(l);
console.log(`\ni18n warnings during render : ${warnings.length}`);
for (const w of warnings) console.log(`  ${w}`);

rmSync(STAGE, { recursive: true, force: true });
process.exit(warnings.length ? 1 : 0);
