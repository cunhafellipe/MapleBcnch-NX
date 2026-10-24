#!/usr/bin/env node
// crashshape-select.mjs -- choose a crash-detection regex against an owned corpus.
//
// Why this exists: the `node stack frame` shape in tools/verify/gate.ps1 has been
// proposed, measured, refuted and re-proposed four times, and every round the
// corpus was somebody's throwaway file.  This tool makes the corpus a repo
// asset and the measurement reproducible by anyone.
//
// Usage:
//   node tools/verify/crashshape-select.mjs --frames <file> --nonframes <file> \
//        --regex "<regex>" [--flags mi] [--label "<name>"] [--no-control]
//
// Exit codes:
//   0  ran; the positive control passed; numbers printed
//   2  INSTRUMENT BROKEN -- the positive control did not behave as a sane
//      instrument must. No conclusion is printed, because an instrument that
//      cannot detect an obvious fault cannot certify a subtle one.
//   3  usage / IO error
//
// Flags default to "mi" and that default is deliberate: PowerShell's -match is
// case-INSENSITIVE and ^/$ are line anchors. To reproduce what gate.ps1 actually
// does, the flags must be "mi". Comparing a JS regex against a PowerShell gate
// with different flags measures a different thing.

import { readFileSync } from 'node:fs';
import { createHash } from 'node:crypto';

const argv = process.argv.slice(2);
function arg(name, dflt = null) {
  const i = argv.indexOf(name);
  if (i === -1) return dflt;
  const v = argv[i + 1];
  if (v === undefined || v.startsWith('--')) {
    die(3, `${name} needs a value`);
  }
  return v;
}
const has = (n) => argv.includes(n);
function die(code, msg) {
  console.error(`ERROR: ${msg}`);
  process.exit(code);
}

const framesPath = arg('--frames');
const nonPath = arg('--nonframes');
const regexSrc = arg('--regex');
const flags = arg('--flags', 'mi');
const label = arg('--label', '(unnamed)');

if (!framesPath) die(3, '--frames is required');
if (!nonPath) die(3, '--nonframes is required');
if (regexSrc === null) die(3, '--regex is required');

function loadLines(path, what) {
  let text;
  try {
    text = readFileSync(path, 'utf8');
  } catch (e) {
    die(3, `cannot read ${what} file ${path}: ${e.message}`);
  }
  // Format: '#' comment lines, optionally a blank separator, then the body.
  // Line 1 is a provenance comment; every other comment and blank is skipped.
  // NOTE the `continue` on the first line: without it line 1 fell through and
  // was pushed into the body, which inflated every count by exactly one. The
  // positive control could not catch that, because `.*` matches a stray header
  // line as happily as a real frame -- a control that fires on everything
  // cannot see a corpus that wrongly gained a line.
  const body = [];
  let expected = null;
  let seenFirst = false;
  let cr = 0;
  for (const rawLine of text.split('\n')) {
    // A CRLF corpus leaves a trailing \r on every line. Left in place, the
    // blank separator becomes the ONE-CHARACTER string "\r", which is not
    // empty, so it is counted as a body line and every count is inflated by
    // one -- measured, on a CRLF copy of a corpus that is 22 lines long and
    // reported 23. It also leaves a \r at the end of every frame, which can
    // change whether `$`-anchored shapes match at all. Strip it, and report
    // whether the file was CRLF, because "the corpus was CRLF" is exactly the
    // kind of fact that must not be discovered by a mismatch later.
    let line = rawLine;
    if (line.endsWith('\r')) {
      line = line.slice(0, -1);
      cr++;
    }
    if (!seenFirst) {
      seenFirst = true;
      if (!line.startsWith('#')) {
        die(2, `INSTRUMENT BROKEN: ${path} does not start with a '#' provenance line`);
      }
      continue;
    }
    if (line.startsWith('#')) {
      const m = line.match(/EXPECT_LINES=(\d+)/);
      if (m) expected = parseInt(m[1], 10);
      continue;
    }
    if (line.length > 0) body.push(line);
  }
  if (cr > 0) {
    console.error(`NOTE: ${path} is CRLF (${cr} CR bytes stripped before matching).`);
  }
  // The corpus states its own size; a loader that disagrees with the file
  // refuses rather than reporting a well-formed wrong number.
  if (expected !== null && expected !== body.length) {
    console.error(`INSTRUMENT BROKEN`);
    console.error(`${path} declares EXPECT_LINES=${expected} but ${body.length} lines were loaded.`);
    console.error('The corpus or the loader is wrong; NO CONCLUSION IS PRINTED.');
    process.exit(2);
  }
  return { body, expected };
}

const frames = loadLines(framesPath, 'frames').body;
const nonFrames = loadLines(nonPath, 'nonframes').body;
if (frames.length === 0) die(3, `${framesPath} yielded 0 frame lines`);
if (nonFrames.length === 0) die(3, `${nonPath} yielded 0 non-frame lines`);

// A repeated frame line is one string, not several. Both denominators are
// reported because a corpus of N lines can hold far fewer than N distinct
// strings (node repeats its internal frames across crashes), and quoting only
// one of them hides which claim is being made.
const distinct = [...new Set(frames)];

function sha256(s) {
  return createHash('sha256').update(s, 'utf8').digest('hex').toUpperCase();
}

// JavaScript REFUSES a leading inline modifier group: `new RegExp('(?m)^a')`
// throws "Invalid group" under every flag combination, including ''. Measured.
// Every shape in gate.ps1 starts with `(?m)` because .NET needs it there, so
// none of them can be pasted into JavaScript unmodified -- and the first thing
// a fourth-round proposer would hit. The inline group is moved into the flags
// argument here, and BOTH the original and the effective form are printed, so
// the translation is never invisible. Silently dropping a flag would change
// what is measured, which is rule 21's whole subject.
function normalise(src, fl) {
  const m = src.match(/^\(\?([a-zA-Z]+)(?:-([a-zA-Z]+))?\)/);
  if (!m) return { src, flags: fl, moved: null };
  const add = m[1];
  const remove = m[2] || '';
  let f = fl;
  for (const ch of add) if (!f.includes(ch)) f += ch;
  for (const ch of remove) f = f.replace(ch, '');
  const stripped = src.slice(m[0].length);
  return { src: stripped, flags: f, moved: { inline: m[0], intoFlags: add, outOfFlags: remove } };
}

function compile(rawSrc, rawFlags) {
  const n = normalise(rawSrc, rawFlags);
  let re;
  try {
    re = new RegExp(n.src, n.flags);
  } catch (e) {
    die(3, `not a valid regex: ${e.message}`);
  }
  return { re, n };
}

function run(src, fl) {
  const { re, n } = compile(src, fl);
  const fHit = frames.map((l) => re.test(l));
  const nHit = nonFrames.map((l) => re.test(l));
  const dHit = distinct.map((l) => re.test(l));
  return {
    re,
    n,
    fHit,
    nHit,
    dHit,
    recallLines: fHit.filter(Boolean).length,
    recallDistinct: dHit.filter(Boolean).length,
    falseHits: nHit.filter(Boolean).length,
  };
}

function show(m) {
  console.log(`  lines      ${String(m.recallLines).padStart(3)} / ${frames.length}`);
  console.log(`  distinct   ${String(m.recallDistinct).padStart(3)} / ${distinct.length}`);
  console.log(`  false      ${String(m.falseHits).padStart(3)} / ${nonFrames.length}`);
}

const shape = (s, n) => (n === 1 ? 'line' : 'lines');

// ------------------------------------------------------------------ control
// Two controls, because one is not enough.
//   A) `.*` must match every frame AND at least one non-frame. Without the
//      second half, a corpus of only frames would still "pass" control A.
//   B) a string that occurs nowhere must match NOTHING. Control A alone cannot
//      see a loader that wrongly ADDED a line, because `.*` matches a stray
//      header line as happily as a real frame. Measured: this tool shipped one
//      such loader bug and control A stayed green.
// A `.*`-only control certifies that the instrument is alive. It does not
// certify that its input is what it says it is.
const CONTROL_A = '.*';
const CONTROL_B = 'zzz-no-such-text-anywhere-zzz';
let control = null;
if (!has('--no-control')) {
  console.log('=== CORPUS ===');
  console.log(`  frames    : ${frames.length} lines, ${distinct.length} distinct`);
  console.log(`  non-frames: ${nonFrames.length} lines`);
  console.log('');
  console.log('=== CONTROLS (must behave exactly as specified) ===');
  const a = run(CONTROL_A, 's');
  const b = run(CONTROL_B, 's');
  const aOk = a.recallLines === frames.length && a.falseHits > 0;
  const bOk = b.recallLines === 0 && b.falseHits === 0;
  console.log(`  A  ${CONTROL_A}   recall ${a.recallLines}/${frames.length}  false ${a.falseHits}/${nonFrames.length}   ok=${aOk}`);
  console.log(`  B  ${CONTROL_B}   recall ${b.recallLines}/${frames.length}  false ${b.falseHits}/${nonFrames.length}   ok=${bOk}`);
  if (!aOk || !bOk) {
    console.log('');
    console.log('INSTRUMENT BROKEN');
    if (!aOk) {
      console.log(`Control A failed: \`.*\` must match every frame (${a.recallLines}/${frames.length})`);
      console.log(`and at least one non-frame (${a.falseHits}/${nonFrames.length}).`);
    }
    if (!bOk) {
      console.log(`Control B failed: a string present nowhere matched something.`);
      console.log(`The corpus or the loader is admitting lines it should not.`);
    }
    console.log('NO CONCLUSION IS PRINTED.');
    process.exit(2);
  }
  control = a;
  console.log('  both controls ok');
  console.log('');
}

console.log('=== REGEX UNDER TEST ===');
// rule 25: a digest is only quotable together with the rule that produced it.
const m = run(regexSrc, flags);
console.log(`  label        : ${label}`);
console.log(`  source (as given)  : ${regexSrc}`);
console.log(`  source len         : ${regexSrc.length} chars`);
console.log(`  source sha256      : ${sha256(regexSrc)}`);
console.log(`  flags arg          : ${flags}`);
if (m.n.moved) {
  console.log(`  inline group moved : ${m.n.moved.inline}  -> added '${m.n.moved.intoFlags}' to flags`);
  console.log(`  effective source   : ${m.n.src}`);
  console.log(`  effective len      : ${m.n.src.length} chars`);
  console.log(`  effective sha256   : ${sha256(m.n.src)}`);
  console.log(`  (JavaScript rejects a leading (?m) outright; .NET does not.`);
  console.log(`   Both forms are printed so the translation is auditable.)`);
} else {
  console.log(`  inline group moved : (none)`);
}
console.log(`  flags used  : ${m.re.flags}`);
console.log(`  compiled    : ${m.re.toString()}`);
console.log('');

console.log('=== RESULT ===');
console.log(`  recall (frame lines)   : ${m.recallLines} / ${frames.length}`);
console.log(`  recall (distinct)      : ${m.recallDistinct} / ${distinct.length}`);
console.log(`  false hits (non-frames): ${m.falseHits} / ${nonFrames.length}`);
console.log('');

console.log(`=== PER-LINE, FRAMES (${frames.length} ${shape(frames.length, frames.length)}) ===`);
frames.forEach((l, i) => {
  console.log(`  ${m.fHit[i] ? 'HIT ' : ' .  '} ${l}`);
});
const missed = frames.filter((_, i) => !m.fHit[i]);
if (missed.length) {
  console.log('');
  console.log(`  MISSED (${missed.length}):`);
  for (const l of missed) console.log(`    - ${l}`);
}
console.log('');
console.log(`=== PER-LINE, NON-FRAMES (${nonFrames.length} ${shape(nonFrames.length, nonFrames.length)}) ===`);
nonFrames.forEach((l, i) => {
  console.log(`  ${m.nHit[i] ? 'HIT ' : ' .  '} ${l}`);
});
const fp = nonFrames.filter((_, i) => m.nHit[i]);
if (fp.length) {
  console.log('');
  console.log(`  FALSE POSITIVES (${fp.length}):`);
  for (const l of fp) console.log(`    - ${l}`);
}

console.log('');
console.log('=== TARGETS (acceptance criteria, not results) ===');
console.log('  recall == all frames AND false == 0 is the acceptance bar.');
console.log('  report what you measured; do not widen the bar to fit a regex.');

if (control) show(control);
process.exit(0);