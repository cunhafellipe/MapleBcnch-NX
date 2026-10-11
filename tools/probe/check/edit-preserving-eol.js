// Replaces exact strings in a file while preserving every byte around them.
//
// The Edit tool normalises line endings on some files: applying five one-line
// changes to ui.js turned 5 changed lines into 212, because the file is `mixed`
// in git's index and the rewrite flattened it to CRLF. That is exactly the noise
// the task file forbids, so on a mixed file the edit has to be byte-level.
//
// Operates on the Buffer: a match is replaced only when it does not span a line
// terminator, and the terminator that followed the replaced text is re-asserted
// unchanged. CRLF lines stay CRLF, LF lines stay LF.
//
// A replacement may be given as the literal text `>>>` followed by more lines,
// which inserts those lines after the match, each terminated with whatever the
// matched line used. Editing one line at a time cannot express "add a comment
// and two statements here", and the Edit tool — which can — normalises the
// whole file instead.

import { readFileSync, writeFileSync } from 'node:fs';

const [, , file, ...pairs] = process.argv;
if (!file || pairs.length === 0 || pairs.length % 2 !== 0) {
  console.error('usage: node edit-preserving-eol.js <file> <old> <new> [<old> <new> …]');
  process.exit(2);
}

let text = readFileSync(file, 'utf8');

for (let i = 0; i < pairs.length; i += 2) {
  const from = pairs[i];
  const to = pairs[i + 1];

  const at = text.indexOf(from);
  if (at < 0) {
    console.error(`NOT FOUND in ${file}: ${JSON.stringify(from.slice(0, 70))}`);
    process.exit(1);
  }
  if (text.indexOf(from, at + 1) >= 0) {
    console.error(`AMBIGUOUS (${text.split(from).length - 1} matches) in ${file}: ${JSON.stringify(from.slice(0, 70))}`);
    process.exit(1);
  }

  // Whatever terminator followed the match is re-asserted after the replacement,
  // so a CRLF line stays CRLF and an LF line stays LF.
  const after = text[at + from.length];
  const terminator = after === '\r' ? '\r\n' : (after === '\n' ? '\n' : '');

  let inserted = to;
  const isInsert = to.startsWith('>>>');
  if (isInsert) {
    // Each supplied line gets the terminator of the line it follows. The matched
    // line is KEPT — an earlier version dropped it, which silently deleted a
    // declaration and left every later use of it referring to nothing.
    const lines = to.slice(3).split('\n').filter((l) => l.length > 0);
    inserted = from + terminator + lines.join(terminator);
  } else if (/[\r\n]/.test(to)) {
    console.error(`refusing a multi-line replacement in ${file}; use the ">>>" form to insert lines`);
    process.exit(1);
  }
  if (/[\r\n]/.test(from)) {
    console.error(`refusing a match containing a line break in ${file}; match a single line`);
    process.exit(1);
  }

  text = text.slice(0, at) + inserted + terminator + text.slice(at + from.length + terminator.length);
}

writeFileSync(file, Buffer.from(text, 'utf8'));
console.log(`edited ${file}: ${pairs.length / 2} replacement(s)`);
