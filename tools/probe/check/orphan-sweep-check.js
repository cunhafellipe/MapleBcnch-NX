// Does a killed NX save leave something the sweeper can actually find?
//
// The defect: the NX writer used target + ".partial", while the sweeper only
// matches "mbsave-<pid>-<guid>.{wz,img}". So an NX save killed mid-write left
// a full-size copy of the user's archive that nothing would ever remove.

import { spawn } from 'node:child_process';
import { createHash } from 'node:crypto';
import { readFileSync, readdirSync, copyFileSync, mkdirSync, rmSync, existsSync } from 'node:fs';
import { join } from 'node:path';

const RUNTIME = 'C:/MUWORK/app/scoop/apps/dotnet-sdk/current/dotnet.exe';
const DLL = process.argv[2];
const SRC = process.argv[3];
const DIR = process.argv[4];
const PORT = process.argv[5];

const U = `http://127.0.0.1:${PORT}`;
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const sha = (p) => createHash('sha256').update(readFileSync(p)).digest('hex');
const list = (d) => (existsSync(d) ? readdirSync(d) : []);

rmSync(DIR, { recursive: true, force: true });
mkdirSync(DIR, { recursive: true });

const results = [];
const ok = (w, d = '') => results.push(['PASS', w, d]);
const bad = (w, d = '') => results.push(['FAIL', w, d]);

async function waitUp(tries = 40) {
  for (let i = 0; i < tries; i += 1) {
    try { const r = await fetch(`${U}/api/files`); if (r.ok) return true; } catch {}
    await sleep(400);
  }
  return false;
}

// --- 1. a normal save must still work, and still leave a backup -------------
{
  const target = join(DIR, 'Normal.nx');
  copyFileSync(SRC, target);
  const before = sha(target);

  const srv = spawn(RUNTIME, [DLL, '--port', PORT, '--no-warmup'], { stdio: 'ignore' });
  await waitUp();
  await fetch(`${U}/api/files/open-many`, {
    method: 'POST', headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ paths: [target] }),
  });
  const id = (await (await fetch(`${U}/api/files`)).json())[0].id;

  const save = await fetch(`${U}/api/files/save`, {
    method: 'POST', headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ fileId: id, backup: true }),
  });
  ok('normal save succeeded', `status=${save.status}`);
  ok('archive still valid NX', readFileSync(target).readUInt32LE(0) === 0x34474b50 ? 'magic PKG4' : 'BAD MAGIC');
  const baks = list(DIR).filter((f) => f.startsWith('Normal.nx.') && f.endsWith('.bak'));
  ok('backup of the original exists', `${baks.length} backup(s), intact=${baks.some((b) => sha(join(DIR, b)) === before)}`);
  // And no scratch is left behind by a save that completed.
  const stray = list(DIR).filter((f) => f.startsWith('mbsave-'));
  ok('completed save leaves no scratch', `stray=${stray.length}`);

  // --- 2. the sweeper must NOT take a live process's scratch ---------------
  const mine = list(DIR).filter((f) => f.startsWith('mbsave-'));
  srv.kill('SIGKILL');
  await sleep(1000);
  console.log(`\n  (scratch present at kill time: ${mine.length})`);
}

// --- 3. kill mid-save: does the orphan get swept on the next open? ---------
{
  const sub = join(DIR, 'kill');
  mkdirSync(sub, { recursive: true });
  const target = join(sub, 'K.nx');
  copyFileSync(SRC, target);
  const targetOriginal = sha(target);

  const srv = spawn(RUNTIME, [DLL, '--port', PORT, '--no-warmup'], { stdio: 'ignore' });
  await waitUp();
  await fetch(`${U}/api/files/open-many`, {
    method: 'POST', headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ paths: [target] }),
  });
  const id = (await (await fetch(`${U}/api/files`)).json())[0].id;

  const saving = fetch(`${U}/api/files/save`, {
    method: 'POST', headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ fileId: id, backup: true }),
  }).catch(() => null);

  // Wait until a scratch file appears, so the kill lands inside the write.
  let sawScratch = null;
  for (let t = 0; t < 300; t += 1) {
    const s = list(sub).filter((f) => f.startsWith('mbsave-'));
    if (s.length) { sawScratch = s[0]; break; }
    await sleep(10);
  }
  srv.kill('SIGKILL');
  await saving;
  await sleep(1200);

  const afterKill = list(sub);
  const orphan = afterKill.filter((f) => f.startsWith('mbsave-'));
  const oldStyle = afterKill.filter((f) => f.endsWith('.partial'));

  ok('a killed save leaves a scratch', `name=${sawScratch ?? '(none seen)'}`);
  if (oldStyle.length) bad('naming', `${oldStyle.length} file(s) still use the unsweepable .partial name`);
  if (orphan.length) ok('orphan uses the sweeper name', orphan[0]);
  else bad('naming', 'no recognisable scratch left behind -- kill may have landed after the swap');

  // Data safety first: the original must still be recoverable.
  const stillNx = readFileSync(target).readUInt32LE(0) === 0x34474b50;
  ok('archive still a valid NX after the kill', stillNx ? 'magic PKG4' : 'CORRUPT');
  const baks = list(sub).filter((f) => f.endsWith('.bak'));
  const recovered = sha(target) === targetOriginal || baks.some((b) => sha(join(sub, b)) === targetOriginal);
  ok('original still recoverable', recovered ? 'target or backup' : 'UNRECOVERABLE');
  if (!recovered) bad('data', 'the original is not recoverable after a killed save');

  // Now: does a fresh open sweep it away?
  if (orphan.length) {
    const srv2 = spawn(RUNTIME, [DLL, '--port', PORT, '--no-warmup'], { stdio: 'ignore' });
    await waitUp();
    await fetch(`${U}/api/files/open-many`, {
      method: 'POST', headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ paths: [target] }),
    });
    await sleep(2500);
    srv2.kill('SIGKILL');
    await sleep(800);
    const afterSweep = list(sub).filter((f) => f.startsWith('mbsave-'));
    if (afterSweep.length === 0) ok('orphan swept on the next open', 'gone');
    else bad('sweep', `${afterSweep.length} orphan(s) survived: ${afterSweep.join(', ')}`);
  }
}

console.log('\n=== results ===');
for (const [v, w, d] of results) console.log(`${v}  ${w}${d ? `  -- ${d}` : ''}`);
const failed = results.filter((r) => r[0] === 'FAIL').length;
console.log(failed ? `\n${failed} FAILING` : '\nall checks passed');
process.exit(failed ? 1 : 0);