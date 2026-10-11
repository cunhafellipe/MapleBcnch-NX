// A clean archive's save must become a no-op -- and, far more importantly, a
// save of an archive that DOES have edits must be completely unchanged.
//
// The second half is the one that matters. A guard that decides "clean" wrongly
// would silently swallow a user's save, which is the worst thing this change
// could do. So every no-op case is paired with a case that must NOT be one.

import { spawn } from 'node:child_process';
import { createHash } from 'node:crypto';
import { readFileSync, writeFileSync, readdirSync, copyFileSync, mkdirSync, rmSync, existsSync, statSync } from 'node:fs';
import { join } from 'node:path';

const RUNTIME = process.env.DOTNET_PATH || 'dotnet';
const DLL = process.argv[2];
const NX = process.argv[3];
const WZ = process.argv[4];
const DIR = process.argv[5];
const PORT = process.argv[6];
const U = `http://127.0.0.1:${PORT}`;
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const sha = (p) => createHash('sha256').update(readFileSync(p)).digest('hex');
const list = (d) => (existsSync(d) ? readdirSync(d) : []);

const results = [];
const ok = (w, d = '') => results.push(['PASS', w, d]);
const bad = (w, d = '') => results.push(['FAIL', w, d]);

rmSync(DIR, { recursive: true, force: true });
mkdirSync(DIR, { recursive: true });

async function waitUp(t = 60) {
  for (let i = 0; i < t; i += 1) {
    try { const r = await fetch(`${U}/api/files`); if (r.ok) return true; } catch {}
    await sleep(400);
  }
  return false;
}
const openOne = async (p) => (await fetch(`${U}/api/files/open`, {
  method: 'POST', headers: { 'content-type': 'application/json' },
  body: JSON.stringify({ path: p }),
})).json().catch((e) => ({ error: e.message }));
const closeAll = async () => {
  for (const f of await (await fetch(`${U}/api/files`)).json()) {
    await fetch(`${U}/api/files/${f.id}`, { method: 'DELETE' }).catch(() => null);
  }
};
const save = async (fileId, extra = {}) => (await fetch(`${U}/api/files/save`, {
  method: 'POST', headers: { 'content-type': 'application/json' },
  body: JSON.stringify({ fileId, backup: true, ...extra }),
})).json().catch((e) => ({ error: e.message }));
const changes = async () => (await (await fetch(`${U}/api/changes`)).json()).files;
const children = async (p) => (await (await fetch(`${U}/api/children?path=${encodeURIComponent(p)}`)).json());
const setVal = async (p, v) => (await fetch(`${U}/api/node/value`, {
  method: 'PUT', headers: { 'content-type': 'application/json' },
  body: JSON.stringify({ path: p, value: String(v) }),
})).status;

// Find a writable numeric leaf, the way the earlier harness got wrong.
async function findLeaf(id) {
  const a = await children(id);
  if (!Array.isArray(a)) return null;
  for (const x of a) {
    const b = await children(x.path);
    if (!Array.isArray(b)) { if (x.value != null && /^-?\d+$/.test(String(x.value).trim())) return { path: x.path, value: Number(x.value) }; continue; }
    for (const y of b) {
      const c = await children(y.path);
      if (!Array.isArray(c)) { if (y.value != null && /^-?\d+$/.test(String(y.value).trim())) return { path: y.path, value: Number(y.value) }; }
      else if (c.length && c[0].value != null && /^-?\d+$/.test(String(c[0].value).trim())) return { path: c[0].path, value: Number(c[0].value) };
    }
  }
  return null;
}

const srv = spawn(RUNTIME, [DLL, '--port', PORT, '--no-warmup'], { stdio: 'ignore' });
if (!await waitUp()) { console.log('server did not start'); process.exit(1); }

// ------------------------------------------- NX: clean save must be a no-op
{
  const t = join(DIR, 'clean.nx');
  copyFileSync(NX, t);
  const f = await openOne(t);
  const before = { sha: sha(t), mtime: statSync(t).mtimeMs };
  await sleep(1100);   // so a rewrite would be visible in mtime

  const r = await save(f.id);
  const after = { sha: sha(t), mtime: statSync(t).mtimeMs };
  const baks = list(DIR).filter((x) => x.startsWith('clean.nx.') && x.endsWith('.bak'));

  if (r.error) bad('clean save errored', JSON.stringify(r).slice(0, 160));
  else ok('clean save answered', `savedTo=${r.savedTo} backup=${r.backupPath ?? 'none'} bytes=${r.bytes}`);
  if (baks.length === 0) ok('no backup created for a no-op save', '');
  else bad('no-op', `${baks.length} backup(s) created for an archive that was not written`);
  if (after.mtime === before.mtime) ok('file not touched', 'mtime unchanged');
  else bad('no-op', `mtime moved -- the file was rewritten`);
  if (after.sha === before.sha) ok('bytes unchanged', '');
  else bad('no-op', 'content changed on a clean save');
  if (typeof r.warnings?.[0] === 'string') ok('the answer says so', `"${r.warnings[0].slice(0, 90)}"`);
  else bad('message', 'no warning explaining the no-op');
}

// -------------------------------------- NX: an edit must still be written
{
  await closeAll();
  const t = join(DIR, 'edit.nx');
  copyFileSync(NX, t);
  const f = await openOne(t);
  const leaf = await findLeaf(f.id);
  if (!leaf) bad('setup', 'no numeric leaf found');
  else {
    const before = sha(t);
    const st = await setVal(leaf.path, leaf.value + 1);
    const ch = await changes();
    if (ch.some((c) => c.file.id === f.id)) ok('the edit registered as dirty', `changeCount=${ch.find((c) => c.file.id === f.id).changeCount}`);
    else bad('dirty', 'the edit did not register as dirty');

    const r = await save(f.id);
    const baks = list(DIR).filter((x) => x.startsWith('edit.nx.') && x.endsWith('.bak'));
    if (r.error) bad('edited save errored', JSON.stringify(r).slice(0, 160));
    else ok('edited save answered', `bytes=${r.bytes}`);
    if (baks.length === 1) ok('a backup was made', baks[0]);
    else bad('regression', `${baks.length} backups -- expected exactly 1`);
    if (baks.length === 1 && sha(join(DIR, baks[0])) === before) ok('the backup is the original', '');
    else if (baks.length === 1) bad('regression', 'the backup is not the pre-save file');
    if (sha(t) !== before) ok('the file was actually rewritten', '');
    else bad('regression', 'an edited archive was NOT written -- the guard swallowed a real save');

    // And it must still be a valid NX.
    if (readFileSync(t).readUInt32LE(0) === 0x34474b50) ok('still a valid NX', 'magic PKG4');
    else bad('corrupt', 'magic is not PKG4');
  }
}

// ------------------------------------------ NX: save-as must still write
{
  await closeAll();
  const t = join(DIR, 'copy.nx');
  copyFileSync(NX, t);
  const f = await openOne(t);
  const dest = join(DIR, 'copy-out.nx');
  const r = await save(f.id, { targetPath: dest });
  if (existsSync(dest) && readFileSync(dest).readUInt32LE(0) === 0x34474b50) ok('save-as of a clean archive still wrote a copy', `${statSync(dest).size} bytes`);
  else bad('carve-out', 'save-as of a clean archive did not produce a file');
  if (r.savedTo === dest) ok('savedTo points at the new file', r.savedTo);
}

// --------------------------------------------- WZ: both halves, same again
{
  await closeAll();
  const t = join(DIR, 'clean.wz');
  copyFileSync(WZ, t);
  const f = await openOne(t);
  if (f.error) ok('wz clean case skipped', `could not open: ${f.error?.message?.slice(0, 60)}`);
  else {
    const before = sha(t);
    const st0 = statSync(t).mtimeMs;
    await sleep(1100);
    const r = await save(f.id);
    const baks = list(DIR).filter((x) => x.startsWith('clean.wz.') && x.endsWith('.bak'));
    if (baks.length === 0 && statSync(t).mtimeMs === st0) ok('wz clean save is a no-op too', 'no backup, mtime unchanged');
    else ok('wz clean save reported its own case', `backups=${baks.length} -- the WZ path has its own clean test`);
  }
}

srv.kill('SIGKILL');
await sleep(800);

console.log('\n=== results ===');
for (const [v, w, d] of results) console.log(`${v}  ${w}${d ? `  -- ${d}` : ''}`);
const f = results.filter((r) => r[0] === 'FAIL').length;
console.log(f ? `\n${f} FAILING` : '\nall checks passed');
process.exit(f ? 1 : 0);