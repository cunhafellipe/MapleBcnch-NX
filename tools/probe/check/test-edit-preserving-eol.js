// Exercises the byte-level editor on a synthetic mixed file. The property that
// matters: the edit must not change a single line terminator. A tool that
// normalises turns 5 real changes into a 200-line diff, and that is invisible
// in the diff itself — only the numstat pair reveals it.

import { writeFileSync, readFileSync, mkdtempSync, rmSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import { tmpdir } from 'node:os';
import { join, resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '../../..');
const TOOL = resolve(ROOT, 'tools/probe/edit-preserving-eol.js');
const dir = mkdtempSync(join(tmpdir(), 'eol-'));
const file = join(dir, 'sample.js');

const results = [];
const check = (name, ok, detail = '') => {
  results.push(ok);
  console.log(`${ok ? 'pass' : 'FAIL'}  ${name}${!ok && detail ? `  -- ${detail}` : ''}`);
};

const census = (path) => {
  const b = readFileSync(path);
  let crlf = 0;
  let lf = 0;
  for (let i = 0; i < b.length; i++) if (b[i] === 10) (i > 0 && b[i - 1] === 13 ? crlf++ : lf++);
  return { crlf, lf, bytes: b.length };
};

const write = (lines) => writeFileSync(file, lines.map((l) => l.text + l.eol).join(''), 'utf8');
const run = (args) => {
  try { execFileSync(process.execPath, [TOOL, file, ...args], { encoding: 'utf8' }); return 0; }
  catch (e) { return e.status; }
};

// A mixed fixture: alternating terminators, a CJK line, a line the tool will
// target, and a trailing line with no terminator at all.
const ORIGINAL = [
  { text: '// 追風WZ編輯器 test fixture', eol: '\r\n' },
  { text: 'const a = 1;', eol: '\n' },
  { text: "const target = 'x';", eol: '\r\n' },
  { text: 'const b = 2;', eol: '\n' },
  { text: "const second = 'y';", eol: '\r\n' },
  { text: 'const c = 3;', eol: '' },
];
write(ORIGINAL);
const before = census(file);

run(["const target = 'x';", "const target = t('x');"]);
let after = census(file);
check('single replacement keeps CRLF count', after.crlf === before.crlf, `${before.crlf} -> ${after.crlf}`);
check('single replacement keeps LF count', after.lf === before.lf, `${before.lf} -> ${after.lf}`);
check('replacement applied', readFileSync(file, 'utf8').includes("t('x')"));

// An insertion: the ">>>" form must give the inserted lines the terminator of
// the line they follow, not the file's dominant one — and must KEEP the line it
// matched. An earlier version dropped it, deleting a declaration and leaving
// every later reference to it dangling, which reads as a syntax error far from
// the cause.
const beforeIns = census(file);
run(["const b = 2;", '>>>\n// inserted note\nconst inserted = true;']);
after = census(file);
const insCrlf = after.crlf - beforeIns.crlf;
const insLf = after.lf - beforeIns.lf;
check('insertion after an LF line adds LF only', insLf === 2 && insCrlf === 0, `+${insCrlf} CRLF +${insLf} LF`);
check('inserted lines present', readFileSync(file, 'utf8').includes('const inserted = true;'));
check('the matched line survives an insertion', readFileSync(file, 'utf8').includes('const b = 2;'));
check('no blank line left where the anchor was', !/\n[ \t]*\n[ \t]*\n/.test(readFileSync(file, 'utf8')));

const beforeCrlfIns = census(file);
run(["const second = 'y';", '>>>\n// after a CRLF line']);
after = census(file);
check('insertion after a CRLF line adds one CRLF', after.crlf - beforeCrlfIns.crlf === 1 && after.lf === beforeCrlfIns.lf,
  `+${after.crlf - beforeCrlfIns.crlf} CRLF`);

// Guards must refuse and leave the file byte-identical.
const snapshot = readFileSync(file);
check('refuses a NOT FOUND match', run(["'does not exist';", 'x;']) === 1);
check('file untouched after NOT FOUND', readFileSync(file).equals(snapshot));
check('refuses an ambiguous match', run(['const ', 'x;']) === 1);
check('file untouched after guards', readFileSync(file).equals(snapshot));

const final = census(file);
check('CJK content survived', readFileSync(file, 'utf8').includes('追風WZ編輯器'));
check('no BOM introduced', readFileSync(file)[0] !== 0xEF);

rmSync(dir, { recursive: true, force: true });
console.log(`\n${results.filter(Boolean).length}/${results.length} checks passed`);
process.exit(results.every(Boolean) ? 0 : 1);
