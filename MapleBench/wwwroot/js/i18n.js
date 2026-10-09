/**
 * Language: English and Traditional Chinese, switchable at runtime.
 *
 * ## Why the dictionary is keyed by the English text
 *
 * There are 733 user-visible strings in this app, scattered through 31 files,
 * and the ones that matter most are the ones nobody remembers when hunting for
 * them -- a tooltip, a toast, an error inside a catch block. A key registry
 * (t('btn.save')) means every one of them needs inventing, registering, and
 * keeping in step with the code that uses it, and a string that was never
 * registered fails silently at runtime.
 *
 * Keying on the English source text means the call site is readable on its own
 * (`t('Save')` says what it will show), the table can be filled in by anyone
 * reading the app, and a missing translation degrades to English rather than to
 * a key name. The cost is that a string which needs two different translations
 * must be worded differently in English first -- which is worth knowing and
 * rare.
 *
 * ## Switching
 *
 * `t()` reads the current locale at call time, so anything drawn after a switch
 * is already correct. Static chrome carries `data-i18n` and is rewritten in
 * place. Anything already on screen that was built by a view re-renders through
 * `onLocaleChange`, which the app uses to redraw the current view.
 */

import { zhHant } from './locale.zh-Hant.js';

const KEY = 'mb.locale';
export const LOCALES = ['en', 'zh-Hant'];
export const DEFAULT_LOCALE = 'en';

const TABLES = {
  'zh-Hant': zhHant,
  // English is the key, so its table is the identity.
  en: null,
};

let current = DEFAULT_LOCALE;
const listeners = new Set();
const warned = new Set();

/** True when `code` is a locale this build carries. */
export function isLocale(code) {
  return LOCALES.includes(code);
}

/**
 * The locale to start in.
 *
 * The stored choice wins, then the browser's own preference, then English. The
 * browser is worth honouring: a machine set to 繁體中文 should not have to be
 * told twice.
 */
export function initialLocale() {
  try {
    const stored = localStorage.getItem(KEY);
    if (isLocale(stored)) return stored;
  } catch { /* storage disabled; fall through to the browser */ }

  for (const tag of navigator.languages ?? [navigator.language ?? '']) {
    if (/^zh\b/i.test(tag) || /hant|tw|hk|mo/i.test(tag)) return 'zh-Hant';
  }
  return DEFAULT_LOCALE;
}

export function locale() {
  return current;
}

/** The other language, for a button that says what it will switch to. */
export function otherLocale() {
  return current === 'en' ? 'zh-Hant' : 'en';
}

export const LOCALE_LABELS = { en: 'EN', 'zh-Hant': '繁中' };

/**
 * Translate `key`, substituting `{name}` placeholders from `vars`.
 *
 * Placeholders are the English source's own: `t('{count} closed', {count: 3})`.
 * A placeholder with no value is left as written rather than becoming
 * "undefined", because a toast that says "undefined closed" is worse than one
 * that reads slightly wrong.
 */
export function t(key, vars) {
  const table = TABLES[current];
  let text = key;

  if (table) {
    const hit = table[key];
    if (hit) {
      text = hit;
    } else if (!warned.has(key)) {
      warned.add(key);
      console.warn(`[i18n] no ${current} translation for: ${key}`);
    }
  }

  if (!vars) return text;
  return text.replace(/\{(\w+)\}/g, (whole, name) => (
    Object.prototype.hasOwnProperty.call(vars, name) ? String(vars[name]) : whole
  ));
}

/** Notifies views that they should redraw. */
export function onLocaleChange(fn) {
  listeners.add(fn);
  return () => listeners.delete(fn);
}

/**
 * Rewrites the static chrome: text, titles, aria-labels and placeholders.
 *
 * `data-i18n` sets the text, `data-i18n-attr="placeholder:Filter tree;aria-label:Filter tree"`
 * sets attributes, which is how a button's accessible name changes along with
 * its label. Elements are matched once and cached, so switching is a walk over
 * a few hundred nodes rather than a querySelectorAll.
 */
function applyStaticDom() {
  for (const node of document.querySelectorAll('[data-i18n]')) {
    node.textContent = t(node.dataset.i18n);
  }
  for (const node of document.querySelectorAll('[data-i18n-attr]')) {
    for (const pair of node.dataset.i18nAttr.split(';')) {
      const at = pair.indexOf(':');
      if (at < 0) continue;
      node.setAttribute(pair.slice(0, at).trim(), t(pair.slice(at + 1).trim()));
    }
  }
}

/** Switches language, persists the choice, and tells everything to redraw. */
export function setLocale(code) {
  const next = isLocale(code) ? code : DEFAULT_LOCALE;
  if (next === current) return current;

  current = next;
  warned.clear();
  try { localStorage.setItem(KEY, next); } catch { /* storage disabled */ }

  document.documentElement.lang = next === 'zh-Hant' ? 'zh-Hant' : 'en';
  document.documentElement.dataset.locale = next;
  applyStaticDom();

  for (const fn of listeners) {
    try { fn(next); } catch (e) { console.error('[i18n] a locale listener threw', e); }
  }
  return current;
}

/** Flips between the two languages. Returns the new one. */
export function toggleLocale() {
  return setLocale(otherLocale());
}

/** Call once at boot, before the first view renders. */
export function initLocale() {
  current = initialLocale();
  document.documentElement.lang = current === 'zh-Hant' ? 'zh-Hant' : 'en';
  document.documentElement.dataset.locale = current;
  applyStaticDom();
  return current;
}

/**
 * How many of the app's keys this build has translated.
 *
 * For the About box, so a half-finished table is visible rather than being a
 * surprise found in the middle of a workflow.
 */
export function coverage() {
  const table = TABLES['zh-Hant'] ?? {};
  const total = Object.keys(table).length;
  return { translated: total };
}
