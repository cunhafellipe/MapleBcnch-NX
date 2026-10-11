// Wraps the emptyState() titles that were left as bare strings.
//
// These are the messages a user sees most often when a section has nothing to
// show -- "No Mob.wz open", "Could not read the mob list" -- and they were
// skipped by the wrapping passes because they are positional arguments rather
// than an object property, so the text:/label: search that drove the work never
// saw them. That is the whole reason the app looked half-translated: the parts
// nearest the top of each section said English.
//
// Mechanical by design: it only touches the argument immediately after the icon
// name, and only when it is a plain string literal that is not already wrapped.

import { readFileSync, writeFileSync } from 'node:fs';
import { join, resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const JS = resolve(ROOT, 'MapleBench/wwwroot/js');

// The titles, with their translations. Kept here rather than guessed in place so
// the wording is decided once and reviewable.
const TITLES = {
  'Point it at a client folder': '請指向一個客戶端資料夾',
  'No cash shop data here': '這裡沒有商城資料',
  'Open the Map archives first': '請先開啟地圖封存',
  'Could not read the mob list': '無法讀取怪物清單',
  'No Mob.wz open': '尚未開啟 Mob.wz',
  'No mobs in this archive': '這個封存裡沒有怪物',
  'Could not read the NPC list': '無法讀取 NPC 清單',
  'No Npc.wz open': '尚未開啟 Npc.wz',
  'No NPCs in this archive': '這個封存裡沒有 NPC',
  'Could not read the skill list': '無法讀取技能清單',
  'No Skill.wz open': '尚未開啟 Skill.wz',
  'This skill has no levels': '這個技能沒有等級',
  'Could not reach the name editor': '無法開啟名稱編輯器',
  'No String.wz open': '尚未開啟 String.wz',
  'That search did not finish': '該搜尋未完成',
};

const files = ['audit.js', 'cashshop.js', 'mapedit.js', 'mob.js', 'npc.js', 'skill.js', 'strings.js'];
let changed = 0;
const touched = [];

for (const file of files) {
  const path = join(JS, file);
  let src = readFileSync(path, 'utf8');
  const before = src;

  for (const [en, zh] of Object.entries(TITLES)) {
    // emptyState(<icon>, 'Title', ...) -- the icon is the first argument and the
    // title the second. Anchoring on the quote means we only ever wrap a
    // literal that exists exactly as written.
    const needle = `, '${en}',`;
    if (!src.includes(needle)) continue;
    src = src.split(needle).join(`, t('${en}'),`);
    changed += 1;
  }

  if (src !== before) {
    writeFileSync(path, src, 'utf8');
    touched.push(file);
  }
}

console.log(`wrapped ${changed} emptyState title(s) across: ${touched.join(', ')}`);
console.log(`translations prepared for: ${Object.keys(TITLES).length}`);

// Emit the dictionary lines so they can be appended deliberately.
const lines = Object.entries(TITLES)
  .map(([en, zh]) => `  '${en.replace(/'/g, "\\'")}': '${zh.replace(/'/g, "\\'")}',`)
  .join('\n');
console.log('--- dictionary lines ---');
console.log(lines);