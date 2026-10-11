// Runs the real closeDiscardMessage out of app.js -- extracted from the source,
// not retyped -- against the shapes closeFiles actually builds.
//
// The batch close is the one place where the wording is the safety property:
// "1 unsaved change has not been written to disk" against a real archive that
// holds two edits is a smaller lie than it looks, but "3 archives with unsaved
// changes" when only one of them has any is the kind of thing that teaches
// people to click through dialogs.

import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '../../..');
const APP = resolve(ROOT, 'MapleBench/wwwroot/js/app.js');

const src = readFileSync(APP, 'utf8');
const start = src.indexOf('function closeDiscardMessage');
if (start < 0) throw new Error('closeDiscardMessage not found in app.js');
const end = src.indexOf('\n}\n', start);
if (end < 0) throw new Error('end of closeDiscardMessage not found');
const closeDiscardMessage = eval(`(${src.slice(start, end + 3)})`); // eslint-disable-line no-eval

const f = (id, name) => ({ id, name, dirty: true });

const cases = [
  {
    // Caught by driving the real dialog in a browser, not by reading it: the
    // batch branch was carrying the single-archive "discards it" into a sentence
    // whose subject is five archives.
    what: 'plural batch -- "them", never the single-archive "it"',
    dirty: [f('f1', 'Character.nx'), f('f2', 'String.nx')],
    counted: new Map([['f1', 2], ['f2', 5]]),
    total: 7,
    mustContain: ['Closing discards them'],
    mustNotContain: ['discards it'],
  },
  {
    what: 'one dirty archive, counted -- must match the pre-batch wording exactly',
    dirty: [f('f1', 'Character.nx')],
    counted: new Map([['f1', 3]]),
    total: 3,
    mustContain: ['3 unsaved changes have not been written to disk',
      'Closing discards it', 'The file on disk is not changed'],
  },
  {
    what: 'one dirty archive, counted 1 -- the singular has to be singular',
    dirty: [f('f1', 'Character.nx')],
    counted: new Map([['f1', 1]]),
    total: 1,
    mustContain: ['1 unsaved change has not been written to disk'],
    mustNotContain: ['1 unsaved changes have'],
  },
  {
    what: 'one dirty archive, uncountable (a deletion) -- work announced, not "0 changes"',
    dirty: [f('f1', 'Character.nx')],
    counted: new Map(),
    total: 0,
    mustContain: ['holds unsaved work', 'Closing discards it'],
    mustNotContain: ['0 unsaved change'],
  },
  {
    what: 'two dirty archives, both counted -- names both, total summed',
    dirty: [f('f1', 'Character.nx'), f('f2', 'String.nx')],
    counted: new Map([['f1', 2], ['f2', 5]]),
    total: 7,
    mustContain: ['Character.nx, String.nx', '7 unsaved changes have',
      'Nothing on disk is changed'],
  },
  {
    what: '16 dirty archives -- the dialog must not be sixteen names tall',
    dirty: Array.from({ length: 16 }, (_, i) => f(`f${i + 1}`, `A${i + 1}.nx`)),
    counted: new Map(Array.from({ length: 16 }, (_, i) => [`f${i + 1}`, 1])),
    total: 16,
    mustContain: ['A5.nx', 'and 11 more'],
    mustNotContain: ['A6.nx', 'A16.nx'],
  },
  {
    // The single-dirty branch deliberately does not name the archive: it is
    // the only branch where dirty.length === 1, which is also exactly when
    // runCloseFiles puts the name in the dialog *title*. Asserting the name is
    // absent from the message is asserting the title is not duplicated in it.
    what: 'one clean + one dirty -- the lone dirty archive is named in the title, not twice',
    dirty: [f('f2', 'String.nx')],
    counted: new Map([['f2', 1]]),
    total: 1,
    mustContain: ['1 unsaved change has not been written to disk'],
    mustNotContain: ['String.nx', 'Character.nx'],
  },
  {
    // The case that actually bites. runCloseFiles hands this function only the
    // archives that are dirty, so the naming set is the discard set by
    // construction. Here one of the two has no countable changes -- a deletion,
    // or an edit to a node no image owns -- and it must still be named, because
    // "Character.nx hold unsaved work" that quietly omits Morph.nx is how a
    // batch close loses work without ever saying it would.
    what: 'two dirty, one uncountable -- both named, total counts only what it can',
    dirty: [f('f1', 'Character.nx'), f('f2', 'Morph.nx')],
    counted: new Map([['f1', 4]]),
    total: 4,
    mustContain: ['Character.nx, Morph.nx', '4 unsaved changes have'],
    mustNotContain: ['5 unsaved changes'],
  },
];

let failed = 0;
for (const c of cases) {
  const msg = closeDiscardMessage(c.dirty, c.counted, c.total);
  const missing = (c.mustContain ?? []).filter((s) => !msg.includes(s));
  const leaked = (c.mustNotContain ?? []).filter((s) => msg.includes(s));
  const ok = !missing.length && !leaked.length;
  if (!ok) failed += 1;
  console.log(`${ok ? 'PASS' : 'FAIL'}  ${c.what}`);
  console.log(`      "${msg}"`);
  if (missing.length) console.log(`      missing: ${JSON.stringify(missing)}`);
  if (leaked.length) console.log(`      leaked:  ${JSON.stringify(leaked)}`);
}

console.log(failed ? `\n${failed} case(s) failed` : `\nall ${cases.length} cases passed`);
process.exit(failed ? 1 : 0);