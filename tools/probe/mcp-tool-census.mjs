// Census of the MCP tool inventory: how many tools the server exposes, and
// which ones.  Measured TWO ways and printed side by side.
//
//   node tools/probe/mcp-tool-census.mjs
//   node tools/probe/mcp-tool-census.mjs --handshake <path-to-mcp-exe>
//   node tools/probe/mcp-tool-census.mjs --handshake <exe> --backend <base-url>
//   node tools/probe/mcp-tool-census.mjs --markdown
//   node tools/probe/mcp-tool-census.mjs --brief
//   node tools/probe/mcp-tool-census.mjs --out <file>
//
// EXIT CODES
// ----------
//   0  the census ANSWERED, and the two answers AGREE.  The static scan ran,
//      and either no --handshake was asked for, or the handshake returned a
//      tools/list answer that MATCHES the source scan.
//   1  the census could not give a TRUSTWORTHY answer about the running
//      server, because --handshake was asked for and one of two things
//      happened:
//        (a) the handshake produced NO answer -- the server binary is missing,
//            the probe failed, the probe answered something that is not a
//            tools/list.  This run then said NOTHING about the running server.
//        (b) the handshake answered, and the two answers DISAGREE.  The
//            running server and the tree are not the same thing, so one of
//            them is stale -- and a stale binary is not a measurement of the
//            tree (tools/verify/AGENTS.md rule 17).
//      Both used to exit 0, which made a broken handshake AND a stale binary
//      indistinguishable from a clean run -- the false green this exit code
//      now prevents.
//   2  the population could not be enumerated at all (see ">>> RED" below).
//
// The rule behind them: exit 0 means "the census answered, and the two
// answers agree".  Anything that stopped the census from answering, or that
// left two answers that do not match, is non-zero.
//
// WHY THIS FILE EXISTS
// --------------------
// The root AGENTS.md and .github/copilot-instructions.md used to say the MCP
// server exposes "34 tools" -- the number of tool NAMES those two documents
// happened to list, reported as the total.  The server exposes far more.  A
// number that counts what a document NAMES is not the total, and the two are
// indistinguishable once they are written down as one number.
//
// So the inventory is no longer hand-written.  It is produced by this file.
//
// THE TWO PATHS, AND WHY BOTH ARE RUN
// -----------------------------------
//   STATIC    read the method-level `[McpServerTool(Name = "...")]` attributes
//             out of the sources.  It measures the TREE.
//   HANDSHAKE speak JSON-RPC to a running server (tools/probe/mcp-probe.mjs)
//             and count what `tools/list` returns.  It measures the BINARY.
//
// A source scan and a running server can disagree, and when they do, one of
// them is stale: the binary may predate the sources, or the sources may not
// be what the server was built from (tools/verify/AGENTS.md rule 17 -- a
// judge pointed at a stale binary is not a measurement of the tree).  So the
// two numbers are printed next to each other, with the tree state they were
// measured against, and `AGREE` is a computed field, not a claim.
//
// THE POPULATION RULE, STATED (not implied)
// ------------------------------------------
// Printed at the top of every run.  A census with an unstated scope is not a
// census.  The rule is: every `.cs` file under <root>/MapleMcp/, recursively,
// minus any path with a `bin` or `obj` segment, minus the files this tool was
// told to exclude and minus its own output file.
//
// `truncated` is computed from a REAL fact: the number of files the rule
// matched on disk, against the number this tool actually READ.  A file that
// cannot be read is not a scan, and a scan that did not cover the population
// it set out to cover is a truncated census (tools/verify/AGENTS.md rule 18).
//
// SELF-EXCLUSION
// --------------
// A census that can be satisfied by the file it just wrote proves nothing
// (skill `census-must-exclude-the-file-you-just-wrote`).  This tool excludes,
// and PRINTS WITH A REASON, its own script, its `--out` file, and every
// `--exclude` path.  The exclusion is live: excluding a file really drops its
// tools from the total.
//
// NO PORT IS EVER WRITTEN HERE
// ----------------------------
// tools/verify/probe-instance-census.py scans every script under tools/probe/
// and reports a file that supplies a default instance as PICKED -- a defect.
// This file therefore contains no instance URL and no port literal at all: the
// backend address comes from `--backend` or from the environment, and is
// passed straight through to the child process.
import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { existsSync, readFileSync, readdirSync, statSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const SELF = fileURLToPath(import.meta.url);
const DEFAULT_ROOT = path.resolve(path.dirname(SELF), '..', '..');

const argv = process.argv.slice(2);
const flag = (name) => {
  const i = argv.indexOf(name);
  return i >= 0 ? argv[i + 1] : null;
};
const flagAll = (name) => {
  const out = [];
  argv.forEach((a, i) => {
    if (a === name && argv[i + 1] !== undefined) out.push(argv[i + 1]);
  });
  return out;
};

// --- the population rule, as constants that are PRINTED, not implied -------
const SCAN_DIR = 'MapleMcp';
const SCAN_EXT = '.cs';
const EXCLUDED_DIR_SEGMENTS = ['bin', 'obj'];
const PROBE_REL = path.join('tools', 'probe', 'mcp-probe.mjs');

// A method-level tool attribute.  The class-level marker is a DIFFERENT
// attribute ([McpServerToolType]), so it cannot be counted by accident.
// The pattern is anchored at the start of a line and allows only whitespace
// before it, so an attribute inside a comment (`// [McpServerTool(...)]`) does
// not match: the comment marker is not whitespace.
const TOOL_ATTR = /^[ \t]*\[McpServerTool\s*\(\s*Name\s*=\s*"([^"]+)"/gm;

const root = path.resolve(flag('--root') ?? DEFAULT_ROOT);
const handshakeExe = flag('--handshake');
const backend = flag('--backend');
const outFile = flag('--out') ? path.resolve(flag('--out')) : null;
const docFiles = flagAll('--doc');
const markdownOnly = argv.includes('--markdown');
const briefOnly = argv.includes('--brief');

/** Every `.cs` file under <root>/MapleMcp/, minus `bin/` and `obj/` segments. */
function enumeratePopulation() {
  const files = [];
  const skippedDirs = [];
  const walk = (dir, rel) => {
    for (const entry of readdirSync(dir, { withFileTypes: true })) {
      const childRel = rel ? `${rel}/${entry.name}` : entry.name;
      if (entry.isDirectory()) {
        if (EXCLUDED_DIR_SEGMENTS.includes(entry.name)) {
          skippedDirs.push(childRel);
          continue;
        }
        walk(path.join(dir, entry.name), childRel);
      } else if (entry.isFile() && entry.name.endsWith(SCAN_EXT)) {
        files.push(childRel);
      }
    }
  };
  const base = path.join(root, SCAN_DIR);
  if (!existsSync(base)) throw new Error(`no ${SCAN_DIR}/ directory under ${root}`);
  walk(base, '');
  files.sort();
  return { files, skippedDirs };
}

/** The family a tool name belongs to: the first two underscore segments. */
function familyOf(name) {
  const parts = name.split('_');
  if (parts[0] === 'wz' && parts.length > 2) return `${parts[0]}_${parts[1]}`;
  return name;
}

function sha256(buf) {
  return createHash('sha256').update(buf).digest('hex');
}

// --- the backend the server will use, and whether it answers --------------
//
// A handshake that produces no answer has THREE different causes, and from
// "initialize: no reply in 120s" a reader cannot tell them apart:
//
//   (a) the server binary is missing, or is not a server at all;
//   (b) the server started but could not reach its BACKEND;
//   (c) the server started, the backend answered, and the server still did
//       not reply to `initialize` -- that one IS a server defect.
//
// So the census measures the backend ITSELF, before it judges the handshake,
// and then says which of the three happened, with the evidence.
//
// The backend URL is NOT written into this file: the server reads WZ_SERVER
// and falls back to its own default, and that default is READ OUT of the
// server's source (MapleMcp/MapClient.cs, `DefaultServer`), so it cannot rot
// here.  When `--backend` is not given, the census SAYS which URL the server
// will use instead of letting the run die in silence.
const DEFAULT_SERVER_RE = /DefaultServer\s*=\s*"([^"]+)"/;

function serverDefaultBackend() {
  const dir = path.join(root, SCAN_DIR);
  let names = [];
  try { names = readdirSync(dir).filter((f) => f.endsWith(SCAN_EXT)); } catch { return null; }
  for (const name of names) {
    try {
      const text = readFileSync(path.join(dir, name), 'utf8');
      const m = text.match(DEFAULT_SERVER_RE);
      if (m) return { url: m[1], file: `${SCAN_DIR}/${name}` };
    } catch { /* an unreadable file: the next one may carry the constant */ }
  }
  return null;
}

/** The backend the server will use, and where that came from. */
function backendInUse() {
  if (backend) return { url: backend, source: 'the --backend flag' };
  const fromEnv = process.env.WZ_SERVER;
  if (fromEnv) return { url: fromEnv, source: 'the environment (WZ_SERVER)' };
  const dflt = serverDefaultBackend();
  if (dflt) return { url: dflt.url, source: `the server's own default (${dflt.file})` };
  return { url: null, source: 'UNKNOWN: the server default could not be read' };
}

/** Does that backend answer?  A short GET, and the evidence is kept. */
async function probeBackend(url) {
  if (!url) {
    return { url: null, answered: null, status: null, error: 'no backend URL to probe', ms: 0 };
  }
  const started = Date.now();
  const ac = new AbortController();
  const timer = setTimeout(() => ac.abort(), 4000);
  try {
    const res = await fetch(url, { signal: ac.signal, redirect: 'manual' });
    return { url, answered: true, status: res.status, error: null, ms: Date.now() - started };
  } catch (err) {
    const code = err?.cause?.code ?? err?.name ?? 'error';
    return { url, answered: false, status: null, error: String(code), ms: Date.now() - started };
  } finally {
    clearTimeout(timer);
  }
}

// --- the handshake: the repo's own probe, run as a child process ----------
/**
 * Run tools/probe/mcp-probe.mjs and read its tools/list answer.
 * Returns { count, names, blocked, detail }.  `blocked` names the LAYER that
 * stopped the handshake, because "it failed" is not a measurement -- and it
 * names WHICH of the three causes it was, because "the server is broken" and
 * "the server had no backend" are different facts with different owners.
 */
async function runHandshake(exe) {
  const probe = path.join(root, PROBE_REL);
  if (!existsSync(probe)) {
    return { count: null, names: [], blocked: 'probe missing', detail: probe };
  }
  if (!existsSync(exe)) {
    return { count: null, names: [], blocked: 'server binary missing', detail: exe };
  }
  const env = { ...process.env };
  // The address is passed straight through.  Nothing is defaulted here.
  if (backend) env.WZ_SERVER = backend;

  // WHICH backend will the server use, and does it answer?  Measured BEFORE
  // the handshake, because the handshake's own answer ("no reply in 120s")
  // cannot tell a server with no backend from a broken server.
  const target = backendInUse();
  const backendProbe = await probeBackend(target.url);

  // A server whose backend does not answer still answers the handshake: the
  // stdio transport is started with no startup gate (MapleMcp/Program.cs:
  // 154-156).  So the backend is NOT a reason to cut the handshake short --
  // cutting it would refuse a server that is merely slow.  The probe fails
  // IMMEDIATELY when the child is dead (mcp-probe.mjs rejects a pending
  // reply the moment the child exits), and that is where the wait goes.
  const res = spawnSync(process.execPath, [probe, exe], {
    env,
    encoding: 'utf8',
    maxBuffer: 32 * 1024 * 1024,
  });
  const text = `${res.stdout ?? ''}${res.stderr ?? ''}`;
  const countMatch = text.match(/^tool count:\s*(\d+)\s*$/m);
  const names = [];
  const lines = text.split(/\r?\n/);
  let inList = false;
  for (const line of lines) {
    if (line.startsWith('=== tools/list ===')) { inList = true; continue; }
    if (!inList) continue;
    if (line.startsWith('map-related tools:')) break;
    const m = line.match(/^ {2}- ([A-Za-z0-9_]+)\s*$/);
    if (m) names.push(m[1]);
  }
  const state = text.match(/^PROBE-STATE:\s*(\S+)/m)?.[1] ?? null;
  if (countMatch) {
    return {
      count: Number(countMatch[1]), names, blocked: null, detail: '',
      backend: target, backendProbe, probeState: state,
    };
  }
  // Which layer stopped it, and WHICH of the causes was it?
  //
  // MEASURED 2026-10-24, twice: the handshake does NOT depend on the
  // backend.  `initialize` and `tools/list` answer in about a second with
  // the backend REFUSING the connection, and in about a second with the
  // backend ACCEPTING and never answering -- because the stdio transport is
  // started with no startup gate (MapleMcp/Program.cs:154-156).  So a dead
  // backend is NOT a cause of a handshake stall, and this tool must not
  // claim that it is.  The backend is reported as CONTEXT.
  //
  // What the probe's own state DOES tell, and what the three causes are:
  //   the child never started  -> the binary could not start (the .NET 8
  //                               apphost trap AGENTS.md records is visible
  //                               in the child's stderr)
  //   the child exited         -> the server started and died
  //   the child alive, silent  -> the server started and said nothing; if
  //                               the backend answered as well, THAT is a
  //                               server defect
  const err = text.match(/^ERROR: ([^\n]+)$/m);
  const childStderr = (text.match(/--- server stderr ---\n([\s\S]*)/) ?? [])[1] ?? '';
  const stderrHead = childStderr.trim().split('\n').slice(0, 3).join(' | ');
  let blocked;
  let detail;
  if (state === 'child-never-started') {
    blocked = 'the server binary could not start';
    detail = (text.match(/the child never started: (.*)$/m)?.[1] ?? '').trim() || stderrHead;
  } else if (state === 'child-exited') {
    blocked = 'the server started and exited before answering';
    detail = (text.match(/the child exited: (.*)$/m)?.[1] ?? '').trim()
      + (stderrHead ? ` | stderr: ${stderrHead}` : '');
  } else if (state === 'child-alive-silent') {
    if (backendProbe.answered === true) {
      blocked = `the server started, the backend ${backendProbe.url} answered `
        + `(HTTP ${backendProbe.status}), and the server still did not reply to initialize`
        + ' -- that IS a server defect';
      detail = `the backend answered in ${backendProbe.ms} ms, so the refusal is the server's`;
    } else {
      blocked = 'the server started and said nothing';
      detail = `the backend ${backendProbe.url} did not answer either, but MEASURED: this `
        + 'handshake does NOT depend on the backend (initialize answers in ~1 s with the '
        + 'backend refusing the connection and with it hanging), so the dead backend is '
        + 'NOT the cause of the stall';
    }
  } else {
    blocked = 'no tools/list answer';
    detail = err ? err[1] : text.slice(0, 400);
  }
  return {
    count: null, names, blocked, detail,
    backend: target, backendProbe, probeState: state,
    childStderr: childStderr.trim().slice(0, 600),
    raw: text.slice(0, 600),
  };
}

// --- the scan -------------------------------------------------------------
function runScan() {
  const { files, skippedDirs } = enumeratePopulation();

  // The exclusions, each with its reason, each PRINTED.
  const exclusions = [];
  const excludedAbs = new Set();
  const exclude = (abs, reason) => {
    exclusions.push({ abs, reason });
    excludedAbs.add(path.resolve(abs).toLowerCase());
  };
  exclude(SELF, 'this census script');
  for (const p of flagAll('--exclude')) exclude(path.resolve(p), '--exclude argument');
  if (outFile) exclude(outFile, 'this census tool\'s own output file');

  const matched = files.map((rel) => path.join(root, SCAN_DIR, rel));
  const inScope = matched.filter((abs) => !excludedAbs.has(path.resolve(abs).toLowerCase()));

  const read = [];
  const unreadable = [];
  const names = [];
  const perFile = [];
  for (const abs of inScope) {
    const rel = path.relative(root, abs).split(path.sep).join('/');
    let buf;
    try {
      buf = readFileSync(abs);
    } catch (err) {
      unreadable.push({ rel, why: String(err.message ?? err) });
      continue;
    }
    const text = buf.toString('utf8');
    const found = [];
    TOOL_ATTR.lastIndex = 0;
    let m;
    while ((m = TOOL_ATTR.exec(text)) !== null) found.push(m[1]);
    read.push({ rel, bytes: buf.length, sha256: sha256(buf).slice(0, 16), tools: found.length });
    perFile.push({ rel, tools: found });
    for (const n of found) names.push(n);
  }

  return {
    skippedDirs,
    exclusions: exclusions.map((e) => ({
      path: path.relative(root, path.resolve(e.abs)).split(path.sep).join('/'),
      reason: e.reason,
      // Whether this exclusion actually removed a member of the population, or
      // named a file that was never in it (this script, an --out file outside
      // the tree).  A document that says "minus the excluded files" has to say
      // WHICH files, or the count it prints cannot be reproduced.
      inPopulation: matched.some(
        (m) => path.resolve(m).toLowerCase() === path.resolve(e.abs).toLowerCase()),
    })),
    matchedFiles: matched.length,
    populationFiles: inScope.length,
    scanned: read.length,
    unreadable,
    read,
    perFile,
    names,
  };
}

// --- the markdown block the documents carry -------------------------------
function markdownBlock(scan, hs) {
  const fams = new Map();
  for (const n of scan.names) fams.set(familyOf(n), (fams.get(familyOf(n)) ?? 0) + 1);
  const excluded = scan.exclusions.filter((e) => e.inPopulation);
  const excludedText = excluded.length
    ? excluded.map((e) => `\`${e.path}\` (${e.reason})`).join(', ')
    : 'none';
  const lines = [];
  lines.push(`**MCP tools: ${scan.names.length}**, in ${fams.size} families.  Counted by`);
  lines.push('`node tools/probe/mcp-tool-census.mjs --markdown`');
  lines.push(`(population: a FILESYSTEM WALK of \`${SCAN_DIR}/**/*${SCAN_EXT}\` -- census mode (c),`);
  lines.push(` not a git tree -- minus \`${EXCLUDED_DIR_SEGMENTS.join('/')}/\`, minus the files this`);
  lines.push(`tool was told to exclude (${excludedText}), and minus its own output file;`);
  lines.push(`${scan.scanned} file(s) read, truncated=${scan.scanned === scan.populationFiles ? 'False' : 'True'}).  To ask a`);
  lines.push('running server the same question, add `--handshake <path-to-the-mcp-exe>`:');
  lines.push('the census then prints the handshake answer next to this one and says whether');
  lines.push('they agree.');
  lines.push('');
  lines.push('| Family | Tools | Names |');
  lines.push('|---|---|---|');
  const sorted = [...fams.entries()].sort((a, b) => (a[0] < b[0] ? -1 : 1));
  for (const [fam, n] of sorted) {
    const names = scan.names.filter((x) => familyOf(x) === fam).sort();
    lines.push(`| \`${fam}_*\` | ${n} | ${names.map((x) => '`' + x + '`').join(', ')} |`);
  }
  lines.push('');
  lines.push(`Total: **${scan.names.length}** tools in ${fams.size} families.`);
  return lines.join('\n');
}

/**
 * The compact one-line form the quick-facts documents carry.  It is printed so
 * that the documents can PASTE it instead of typing the numbers: the same
 * rule the INVENTORY comment in AGENTS.md states for the table above.
 *
 * The family order is fixed here, not in the documents: the families an agent
 * meets first, then the rest, alphabetically.  A document that reorders the
 * list by hand would be retyping the census.
 */
function briefBlock(scan) {
  const fams = new Map();
  for (const n of scan.names) fams.set(familyOf(n), (fams.get(familyOf(n)) ?? 0) + 1);
  const ORDER = ['wz_map', 'wz_asset', 'wz_sim', 'wz_archive', 'wz_data',
                 'wz_job', 'wz_render', 'wz_diag', 'wz_save'];
  const keys = [...fams.keys()].sort((a, b) => {
    const ia = ORDER.indexOf(a), ib = ORDER.indexOf(b);
    if (ia !== -1 && ib !== -1) return ia - ib;
    if (ia !== -1) return -1;
    if (ib !== -1) return 1;
    return a < b ? -1 : 1;
  });
  const parts = keys.map((k) => `\`${k}_*\` (${fams.get(k)})`);
  const lines = [`**${scan.names.length} tools** are auto-discovered once connected,`
    + ` in ${fams.size} families:`];
  let cur = '';
  for (let i = 0; i < parts.length; i++) {
    const tok = parts[i] + (i === parts.length - 1 ? '.' : ',');
    if (cur.length + tok.length + 2 > 74 && cur.length > 0) {
      lines.push('  ' + cur.trimEnd());
      cur = '';
    }
    cur += tok + ' ';
  }
  if (cur.length) lines.push('  ' + cur.trimEnd());
  return lines.join('\n');
}

// --- main -----------------------------------------------------------------
let scan;
try {
  scan = runScan();
} catch (err) {
  console.log(`>>> RED: the census could not enumerate its population: ${err.message}`);
  process.exit(2);
}

const hs = handshakeExe
  ? await runHandshake(handshakeExe)
  : { count: null, names: [], blocked: 'not requested (--handshake was not given)', detail: '' };

const out = [];
const put = (s = '') => out.push(s);

put('=== POPULATION RULE ===');
put(`  root             = ${root}`);
put(`  include          = ${SCAN_DIR}/**/*${SCAN_EXT}  (recursive; every ${SCAN_EXT} file under ${SCAN_DIR}/)`);
put(`  exclude segments = ${EXCLUDED_DIR_SEGMENTS.join(', ')}`);
put(`  skipped dirs     = ${scan.skippedDirs.length ? scan.skippedDirs.join(', ') : '(none present)'}`);
put('  exclude files    =');
for (const e of scan.exclusions) put(`      ${e.path}   <- ${e.reason}`);
put('');

put('=== FILES READ ===');
put(`  population_files_matched=${scan.matchedFiles}  excluded=${scan.matchedFiles - scan.populationFiles}  population_files=${scan.populationFiles}  scanned=${scan.scanned}  unreadable=${scan.unreadable.length}`);
put('  truncated=' + (scan.scanned === scan.populationFiles ? 'False' : 'True') +
    '   (truncated = scanned != population_files.  population_files is the population');
put('   IN FORCE after the exclusions above; scanned is the files this tool actually READ.');
put('   An excluded file is a rule, not a truncation; a file that cannot be read IS one.)');
for (const f of scan.read) {
  put(`  ${f.rel}  bytes=${f.bytes}  sha256[0:16]=${f.sha256}  tools=${f.tools}`);
}
for (const u of scan.unreadable) put(`  >>> RED: ${u.rel} could not be read: ${u.why}`);
put('');

const distinct = new Set(scan.names);
const dups = [...distinct].filter((n) => scan.names.filter((x) => x === n).length > 1);
put('=== TOOL NAMES (source scan) ===');
put(`  total=${scan.names.length}  distinct=${distinct.size}  duplicated_names=${dups.length ? dups.join(',') : '(none)'}`);
const fams = new Map();
for (const n of scan.names) fams.set(familyOf(n), (fams.get(familyOf(n)) ?? 0) + 1);
put('  families:');
for (const [fam, n] of [...fams.entries()].sort((a, b) => (a[0] < b[0] ? -1 : 1))) {
  put(`    ${fam}_* = ${n}`);
}
put('');

put('=== HANDSHAKE (tools/list, via tools/probe/mcp-probe.mjs) ===');
if (hs.backend) {
  put(`  the backend the server will use: ${hs.backend.url ?? '(none)'}  <- ${hs.backend.source}`);
  if (!backend) {
    put('    (no --backend was given, so the server falls back to its own default;');
    put('     pass --backend <url> to point it somewhere else)');
  }
  if (hs.backendProbe) {
    const bp = hs.backendProbe;
    if (bp.answered === true) {
      put(`  the backend answered: HTTP ${bp.status} in ${bp.ms} ms`);
    } else if (bp.answered === false) {
      put(`  the backend DID NOT answer: ${bp.error} after ${bp.ms} ms`);
    } else {
      put(`  the backend could not be probed: ${bp.error}`);
    }
    put('  MEASURED: the handshake does NOT depend on the backend -- initialize');
    put('  answers in ~1 s with the backend refusing the connection, and in ~1 s');
    put('  with the backend accepting and never answering.  A dead backend is');
    put('  therefore NOT a cause of a handshake stall, and it is reported here');
    put('  as context, not as the verdict.');
  }
}
if (hs.count === null) {
  put(`  count=(no answer)  blocked at: ${hs.blocked}`);
  if (hs.detail) put(`  detail: ${hs.detail.split(/\r?\n/)[0]}`);
  // WHY it was refused, with the evidence.  "the handshake produced no
  // answer" is a symptom; the causes have different owners.
  if (hs.probeState) put(`  the probe's own state: ${hs.probeState}`);
  if (hs.childStderr) {
    put('  the server stderr:');
    for (const line of hs.childStderr.split(/\r?\n/).slice(0, 6)) put(`    ${line}`);
  }
  put('  => ' + hs.blocked);
} else {
  put(`  count=${hs.count}  names=${hs.names.length}`);
  const hsFams = new Map();
  for (const n of hs.names) hsFams.set(familyOf(n), (hsFams.get(familyOf(n)) ?? 0) + 1);
  for (const [fam, n] of [...hsFams.entries()].sort((a, b) => (a[0] < b[0] ? -1 : 1))) {
    put(`    ${fam}_* = ${n}`);
  }
}
put('');

put('=== THE TWO PATHS ===');
let notTrustworthy = false;
if (hs.count === null) {
  put(`  static=${scan.names.length}  handshake=(no answer)  AGREE=unknown  (${hs.blocked})`);
  if (handshakeExe) {
    // A REFUSAL is not a measurement.  The handshake was asked for and this
    // run learned NOTHING about the running server, so it must not exit 0.
    notTrustworthy = true;
    put('  >>> RED: the handshake was asked for and produced no answer, so this run');
    put('     measured NOTHING about the running server.  A refusal is not a');
    put('     measurement, and it exits non-zero.');
  }
} else {
  const sameSet = hs.names.length === scan.names.length &&
    [...distinct].every((n) => hs.names.includes(n));
  put(`  static=${scan.names.length}  handshake=${hs.count}  AGREE=${sameSet ? 'True' : 'False'}`);
  if (!sameSet) {
    const onlyStatic = [...distinct].filter((n) => !hs.names.includes(n));
    const onlyHandshake = hs.names.filter((n) => !distinct.has(n));
    put(`  only in the source scan: ${onlyStatic.length ? onlyStatic.join(',') : '(none)'}`);
    put(`  only in the handshake : ${onlyHandshake.length ? onlyHandshake.join(',') : '(none)'}`);
    put('  >>> RED: the source scan and the running server disagree, so one of them');
    put('     is stale (a stale binary is not a measurement of the tree).');
    if (handshakeExe) {
      // The handshake WAS asked for, and it answered -- but the two answers do
      // not match, so this run cannot say that the running server is this
      // tree.  Exiting 0 would report a clean run over an unanswered
      // question, which is the same false green the refusal case was.
      notTrustworthy = true;
      put('  >>> RED: the handshake was asked for and the two answers DISAGREE, so');
      put('     this run cannot vouch for the running server.  It exits non-zero.');
    }
  }
}
put('');

if (docFiles.length) {
  put('=== DOCUMENT COVERAGE (which of the tool names each document names) ===');
  for (const doc of docFiles) {
    const abs = path.resolve(doc);
    if (!existsSync(abs)) {
      put(`  ${doc}: >>> RED: no such file`);
      continue;
    }
    // The census's own output file is excluded from the doc scan too: a census
    // that can be satisfied by the file it just wrote proves nothing.
    const selfExcluded = outFile && path.resolve(outFile) === abs;
    if (selfExcluded) {
      put(`  ${doc}: EXCLUDED (this is the census's own --out file)`);
      continue;
    }
    const text = readFileSync(abs, 'utf8');
    const present = [];
    const missing = [];
    for (const n of [...distinct].sort()) {
      if (new RegExp(`\\b${n}\\b`).test(text)) present.push(n);
      else missing.push(n);
    }
    put(`  ${path.relative(root, abs).split(path.sep).join('/')}`);
    put(`    names_present=${present.length}  names_missing=${missing.length}  of ${distinct.size}`);
    put(`    missing: ${missing.length ? missing.join(', ') : '(none)'}`);
  }
  put('');
}

const report = out.join('\n');
if (briefOnly) {
  console.log(briefBlock(scan));
  console.log(`  (source scan of \`${SCAN_DIR}/**/*${SCAN_EXT}\`: ${scan.scanned} file(s) read,`
    + ` truncated=${scan.scanned === scan.populationFiles ? 'False' : 'True'})`);
  if (notTrustworthy) {
    // the brief form carries no refusal text; it goes to stderr so the pasted
    // stdout stays a clean fragment.
    const why = hs.count === null
      ? `the handshake was asked for and produced no answer (${hs.blocked}); this run measured nothing about the running server.`
      : 'the handshake was asked for and the two answers disagree; this run cannot vouch for the running server.';
    process.stderr.write(`>>> RED: ${why}\n`);
  }
} else if (markdownOnly) {
  console.log(markdownBlock(scan, hs));
} else {
  console.log(report);
}
if (outFile) {
  writeFileSync(outFile, report + '\n', 'utf8');
  console.log(`\nWROTE ${outFile}  (${statSync(outFile).size} bytes)`);
}
if (notTrustworthy) process.exitCode = 1;
