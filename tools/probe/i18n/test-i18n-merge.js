// Exercises the merge tool's refusals. A merge that silently drops an agent's work
// is worse than one that fails, so each guard is tested rather than assumed.
//
// The fixtures are derived at run time from the dictionary as it stands. An
// earlier version hard-coded a key that was a genuine gap when it was written;
// once it was translated, "that key has no entry" stopped being true and the test
// quietly began asserting something else. A check that rots into vacuity is worse
// than no check, because it still reports green.
//
// The dictionary is backed up before the first case and restored at the end, so a
// case that needs a key to be absent can remove it and the file still comes back
// exactly as it was found.

import { writeFileSync, readFileSync, rmSync, mkdirSync, readdirSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import { join, resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '../../..');
const DICT = resolve(ROOT, 'MapleBench/wwwroot/js/locale.zh-Hant.js');
const MERGE = resolve(ROOT, 'tools/probe/i18n-merge.js');

// A THROWAWAY inbox. The real nx-probe/i18n holds every parallel agent's
// deliverable — this test wipes its inbox between cases, and an earlier version
// pointed it at the shared directory, deleting another agent's output silently.
const INBOX = resolve(ROOT, 'tools/probe/.test-inbox');

const run = () => {
  try {
    return { code: 0, out: execFileSync(process.execPath, [MERGE, INBOX], { encoding: 'utf8' }) };
  } catch (e) {
    return { code: e.status, out: (e.stdout ?? '') + (e.stderr ?? '') };
  }
};

const ORIGINAL = readFileSync(DICT, 'utf8');
const results = [];
const check = (name, ok, detail = '') => {
  results.push(ok);
  console.log(`${ok ? 'pass' : 'FAIL'}  ${name}${detail && !ok ? `  -- ${detail}` : ''}`);
};

const clearInbox = () => { rmSync(INBOX, { recursive: true, force: true }); mkdirSync(INBOX, { recursive: true }); };
const put = (name, obj) => writeFileSync(join(INBOX, name), JSON.stringify(obj, null, 2) + '\n', 'utf8');

// Unescaped, because a dictionary key written '\n' in source IS a key holding a
// newline. Without this, a translated key looked absent and the test took the
// "temporarily remove it" path — which then made the second merge re-add it.
const dictKeys = (text) => new Set(
  [...text.matchAll(/^\s{2}'((?:[^'\\]|\\.)*)':/gm)]
    .map((m) => m[1].replace(/\\'/g, "'").replace(/\\n/g, '\n').replace(/\\t/g, '\t').replace(/\\\\/g, '\\')),
);

/** Keys taken out of the dictionary for a case, restored before the run ends. */
const temporarilyRemoved = [];

/**
 * Live t() keys in the sources, with placeholders.
 *
 * A key ending in a space or a comma is the FRAGMENT half of a t('a ' + 'b')
 * call: the runtime receives the joined sentence, never this. Such a key looks
 * live to a regex but is exactly the shape that must never reach the table, so
 * it is excluded here — as a fixture it would test the wrong thing twice over.
 */
const liveKeys = [];
// Every UI module, so the fixture is drawn from the same population the merge
// scans. Scanning a subset once produced a key the merge then refused as dead,
// which turned two cases into tests of the wrong guard.
const JS_DIR = resolve(ROOT, 'MapleBench/wwwroot/js');
for (const f of readdirSync(JS_DIR).filter((x) => x.endsWith('.js') && !x.startsWith('locale') && x !== 'i18n.js')) {
  const src = readFileSync(join(JS_DIR, f), 'utf8');
  for (const m of src.matchAll(/\bt\(\s*'((?:[^'\\]|\\.)*)'/g)) {
    // Unescape the way a JS literal is evaluated: a regex sees the two characters
    // \ and n where the runtime has a newline, and the merge compares the real
    // string. Feeding it the raw capture makes a live key look dead.
    const k = m[1].replace(/\\'/g, "'").replace(/\\n/g, '\n').replace(/\\t/g, '\t').replace(/\\\\/g, '\\');
    if (!k.trim() || !/\{\w+\}/.test(k) || !/[A-Za-z]{4}/.test(k)) continue;
    if (/[\s,]$/.test(k)) continue;                       // fragment, not a runtime key
    liveKeys.push(k);
  }
}

clearInbox();

// 1. A key no call site produces must be refused: it would be a dead entry.
put('x.json', { 'A string nobody calls': '沒有人呼叫的字串' });
let r = run();
check('refuses a key that is not a runtime key', r.code === 1 && /not a runtime key/.test(r.out), r.out.slice(0, 200));

// 2. A translation with no Chinese must be refused.
clearInbox();
put('x.json', { Size: 'Size' });
r = run();
check('refuses a translation with no Chinese in it', r.code === 1 && /no Chinese/.test(r.out), r.out.slice(0, 200));

// Cases 3 and 5 both need a live key that the dictionary does NOT already hold.
// If every live key is translated, remove one for the duration — the file is
// backed up and restored, so nothing is left changed. A synthetic key would not
// do: the merge rejects keys no call site produces, which is guard 1.
let driftKey = liveKeys.find((k) => !dictKeys(ORIGINAL).has(k));
if (!driftKey) {
  driftKey = liveKeys[0];
  const stripped = ORIGINAL.split('\n')
    .filter((l) => !l.startsWith("  '") || !l.includes(driftKey.replace(/'/g, "\\'")));
  writeFileSync(DICT, stripped.join('\n'), 'utf8');
  temporarilyRemoved.push(driftKey);
}
console.log(`fixture key: ${JSON.stringify(driftKey)}`);
const valueKeepingPlaceholders = '這個翻譯保留了 placeholder'
  + (driftKey.match(/\{\w+\}/g)?.join('') ?? '');

clearInbox();
put('x.json', { [driftKey]: '這個翻譯漏掉了 placeholder' });        // drift: placeholder dropped
r = run();
check('refuses placeholder drift', r.code === 1 && /missing \{/.test(r.out), r.out.slice(0, 200));

// 4. Two agents disagreeing on one key must be refused, not silently resolved.
clearInbox();
put('a.json', { Size: '大小' });
put('b.json', { Size: '尺寸' });
r = run();
check('refuses two conflicting translations', r.code === 1 && /two different translations/.test(r.out), r.out.slice(0, 200));

// 5. The same value from two agents is agreement, not conflict — and an agreed
//    value that is genuinely new does land. The key is derived for the same
//    reason as case 3: 'Size' happened to be untranslated when this was written,
//    and once translated there was nothing left to add.
const additiveKey = driftKey;
const additiveValue = valueKeepingPlaceholders;
clearInbox();
put('a.json', { [additiveKey]: additiveValue });
put('b.json', { [additiveKey]: additiveValue });
r = run();
const after = readFileSync(DICT, 'utf8');
check('accepts an agreed value from two agents', r.code === 0 && after.includes(additiveKey.slice(0, 20)), r.out.slice(0, 200));
check('the merge is additive only', after.length > ORIGINAL.length && after.startsWith(ORIGINAL.slice(0, 2000)));

// 6. Re-running must change nothing -- idempotence, or every later run is noise.
//    This is why the merge skips keys the dictionary already holds: a second pass
//    over the same inbox is the normal case, not an edge case.
// 6. Re-running must change nothing -- idempotence, or every later run is noise.
//    Measured over the SAME inbox as case 5, and read from disk after each run:
//    comparing a cached string against a cached string proves nothing about what
//    the tool wrote.
const afterFirstRun = readFileSync(DICT, 'utf8');
r = run();
const afterSecondRun = readFileSync(DICT, 'utf8');
check('a second run is a no-op', afterSecondRun === afterFirstRun, r.out.slice(0, 200));
check('and says so rather than re-adding', /nothing to add/.test(r.out), r.out.slice(0, 200));

// 7. Malformed JSON must be reported, not thrown past.
clearInbox();
writeFileSync(join(INBOX, 'c.json'), '{ not json');
r = run();
check('reports malformed JSON without crashing', r.code === 1 && /not valid JSON/.test(r.out), r.out.slice(0, 200));

// Restore: the dictionary must be exactly as we found it.
writeFileSync(DICT, ORIGINAL, 'utf8');
rmSync(INBOX, { recursive: true, force: true });
check('dictionary restored byte-for-byte', readFileSync(DICT, 'utf8') === ORIGINAL);

console.log(`\n${results.filter(Boolean).length}/${results.length} checks passed`);
process.exit(results.every(Boolean) ? 0 : 1);
