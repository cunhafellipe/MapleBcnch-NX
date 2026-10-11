// Restores per-line endings on ONE file against its HEAD version, matching by
// line content. git's `text=auto eol=lf` does not help here: HEAD stores those
// three lines with a literal CRLF, so leaving the working file all-CRLF makes
// the diff show them as changed when their content is identical.
//
// restore-eol.py already does this, but it looks the file up by its current
// path, and this one was renamed -- so there is no HEAD entry under the new
// name to compare against. Hence the old name is passed in.

import { readFileSync, writeFileSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import { resolve } from 'node:path';

const [repo, currentRel, headRel] = process.argv.slice(2);

const head = execFileSync('git', ['-C', repo, 'show', `HEAD:${headRel}`], {
  maxBuffer: 64 * 1024 * 1024,
});
// git show emits bytes; decode without a BOM being swallowed or invented.
const headText = head.toString('utf8').replace(/^﻿/, '');

const currentPath = resolve(repo, currentRel);
const current = readFileSync(currentPath, 'utf8');

// Ends in HEAD: a line's content -> whether HEAD terminated it with CRLF.
const headCrlf = new Set();
for (const line of headText.split('\n')) {
  if (line.endsWith('\r')) headCrlf.add(line.slice(0, -1));
}

// Rebuild with LF separators, then put the CRLF back on the lines that had it,
// and only on those. A line whose content is new to this file gets LF.
const body = current.replace(/\r\n/g, '\n');
const out = body
  .split('\n')
  .map((line, i, all) => {
    const isLast = i === all.length - 1;
    if (isLast && line === '') return line;
    return headCrlf.has(line) ? `${line}\r` : line;
  })
  .join('\n');

if (out === current) {
  console.log('no change needed');
} else {
  writeFileSync(currentPath, out, 'utf8');
  console.log(`restored endings on ${headCrlf.size} HEAD-CRLF line(s)`);
}