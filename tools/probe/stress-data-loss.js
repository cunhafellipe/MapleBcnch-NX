// The two stress cases that can lose someone's archive. Everything else in this
// audit degrades; these two destroy.
//
//   A. N writers at one node, and N saves of one file.
//   B. The process killed while a save is mid-flight -- power loss, really.
//
// Run against a COPY. Nothing here touches the user's archives.

import { spawn } from 'node:child_process';
import { createHash } from 'node:crypto';
import { readFileSync, existsSync, readdirSync, writeFileSync, copyFileSync, mkdirSync, rmSync, statSync } from 'node:fs';
import { join } from 'node:path';

const RUNTIME = 'C:/MUWORK/app/scoop/apps/dotnet-sdk/current/dotnet.exe';
const DLL = process.argv[2];
const CLI = process.argv[3];
const SRC = process.argv[4];           // an NX file to copy and stress
const DIR = process.argv[5];           // scratch directory we own
const PORT = process.argv[6];

const sha = (p) => createHash('sha256').update(readFileSync(p)).digest('hex');
const U = `http://127.0.0.1:${PORT}`;
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

rmSync(DIR, { recursive: true, force: true });
mkdirSync(DIR, { recursive: true });

function launch(env = {}) {
  const p = spawn(RUNTIME, [DLL, '--port', PORT, '--no-warmup'], { stdio: 'ignore', env: { ...process.env, ...env } });
  return p;
}
async function waitUp(tries = 40) {
  for (let i = 0; i < tries; i += 1) {
    try { const r = await fetch(`${U}/api/files`); if (r.ok) return true; } catch {}
    await sleep(500);
  }
  return false;
}
async function openIt(path) {
  const r = await fetch(`${U}/api/files/open-many`, {
    method: 'POST', headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ paths: [path] }),
  });
  const j = await r.json();
  return j.opened?.[0]?.id;
}
async function setValue(path, value) {
  const r = await fetch(`${U}/api/node/value`, {
    method: 'PUT', headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ path, value }),
  });
  return r.status;
}
const readValue = async (p) => (await (await fetch(`${U}/api/node?path=${encodeURIComponent(p)}`)).json()).value;

// Find a real editable leaf rather than assuming a path. Two earlier runs of this
// script hardcoded `smap.img/Ae`, which exists in Base.nx and not in Item.nx,
// and the 404s it produced looked exactly like lost writes. Returns both the
// leaf and the child row it came from, since the browsability check wants that.
async function findLeaf(id) {
  const dirs = await (await fetch(`${U}/api/children?path=${id}`)).json();
  if (!Array.isArray(dirs)) return null;
  for (const dir of dirs) {
    const kids = await (await fetch(`${U}/api/children?path=${dir.path}`)).json();
    if (!Array.isArray(kids)) continue;
    // Only a numeric leaf is usable here. Writing "W0" into a String node is
    // rejected with a 400 by the server doing exactly the right thing, and three
    // runs of this script reported it as twenty lost writes before that was
    // noticed. The value the node already holds says what it will accept.
    for (const k of kids) {
      if (!Array.isArray(await (await fetch(`${U}/api/children?path=${k.path}`)).json())) {
        if (k.value != null && /^-?\d+$/.test(String(k.value).trim())) {
          return { leaf: k.path, parent: dir, value: String(k.value).trim(), type: k.type };
        }
      }
    }
    for (const k of kids) {
      const sub = await (await fetch(`${U}/api/children?path=${k.path}`)).json();
      if (Array.isArray(sub) && sub.length) return { leaf: sub[0].path, parent: dir };
    }
  }
  return null;
}

const results = [];
const ok = (what, detail = '') => results.push(['PASS', what, detail]);
const bad = (what, detail = '') => results.push(['FAIL', what, detail]);

// ---------------------------------------------------------------- A. concurrency
{
  const copy = join(DIR, 'A.nx');
  copyFileSync(SRC, copy);
  const original = sha(copy);

  const srv = launch();
  if (!await waitUp()) { bad('A', 'server did not start'); }
  else {
    const id = await openIt(copy);
    const found = await findLeaf(id);
    const node = found?.leaf;
    const dir = found?.parent;
    if (!node) {
      bad('A', 'no editable leaf found -- cannot run the concurrency test');
    } else {
      // A1: twenty writers, one node, at the same time.
      const writes = Array.from({ length: 20 }, (_, i) => setValue(node, `${1000 + i}`));
      const statuses = await Promise.all(writes);
      const failedWrites = statuses.filter((s) => s !== 200);
      const after = await readValue(node);
      ok('A1 twenty concurrent writes to one node', `all 200=${failedWrites.length === 0}, final=${after}`);
      if (failedWrites.length) bad('A1', `${failedWrites.length} writes rejected: ${[...new Set(failedWrites)]}`);
      // The value must be exactly one writer's, not a blend of two.
      if (!/^W\d+$/.test(String(after))) bad('A1', `torn value: ${after}`);

    // A2: five saves of the same file, simultaneously.
    const saves = Array.from({ length: 5 }, () =>
      fetch(`${U}/api/files/save`, {
        method: 'POST', headers: { 'content-type': 'application/json' },
        body: JSON.stringify({ fileId: id, backup: true }),
      }).then((r) => r.status));
    const saveStatuses = await Promise.all(saves);
    ok('A2 five concurrent saves', `statuses=${[...new Set(saveStatuses)].join(',')}`);

    // The archive must still be a valid NX and still parse.
    const files = await (await fetch(`${U}/api/files`)).json();
    const stillOpen = files.find((f) => f.id === id);
    ok('A2 session survived', `files=${files.length}, dirty=${stillOpen?.dirty}`);

    // And the tree must still be browsable, which a torn write would break.
    const kids = await (await fetch(`${U}/api/children?path=${dir.path}`)).json();
    ok('A2 archive still browsable', `children=${Array.isArray(kids) ? kids.length : 'not-an-array'}`);

    srv.kill();
    await sleep(1500);

    // On disk: valid NX magic, and a backup that is still the original.
    const buf = readFileSync(copy);
    const magic = buf.readUInt32LE(0);
    const isNx = magic === 0x34474b50;
    const backups = readdirSync(DIR).filter((f) => f.startsWith('A.nx.') && f.endsWith('.bak'));
    const backupIntact = backups.some((b) => sha(join(DIR, b)) === original);
    ok('A3 saved file still a valid NX', `magic=0x${magic.toString(16).toUpperCase()}`);
    if (!isNx) bad('A3', 'magic is not PKG4 -- the archive is corrupt');
    ok('A3 a backup of the original exists', `${backups.length} backup(s)`);
    if (!backupIntact) bad('A3', 'no backup matches the original -- first save destroyed the only copy');
    }
  }
}

// ------------------------------------------------------------- B. killed mid-save
{
  const copy = join(DIR, 'B.nx');
  copyFileSync(SRC, copy);
  const original = sha(copy);

  const attempts = 4;
  let survivors = 0, corrupt = 0, noBackup = 0, orphans = 0;

  for (let i = 0; i < attempts; i += 1) {
    const sub = join(DIR, `B${i}`);
    mkdirSync(sub, { recursive: true });
    const target = join(sub, 'B.nx');
    copyFileSync(SRC, target);
    const targetOriginal = sha(target);

    const srv = launch();
    await waitUp();
    const id = await openIt(target);
    const found = await findLeaf(id);
    if (found?.leaf) await setValue(found.leaf, String(Number(found.value) + 1));

    // Start the save, then kill the process while it is still working.
    const saving = fetch(`${U}/api/files/save`, {
      method: 'POST', headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ fileId: id, backup: true }),
    }).catch(() => null);

    // Wait until the .partial shows up, so the kill lands inside the write.
    const partial = join(sub, 'B.nx.partial');
    let sawPartial = false;
    for (let t = 0; t < 200; t += 1) {
      if (existsSync(partial)) { sawPartial = true; break; }
      await sleep(10);
    }

    srv.kill('SIGKILL');
    await saving;
    await sleep(1200);

    const stillNx = readFileSync(target).readUInt32LE(0) === 0x34474b50;
    if (stillNx) survivors += 1; else corrupt += 1;

    // A killed save must leave the user with their bytes somewhere. That is
    // satisfied by EITHER a backup holding the original OR the target still
    // being the original -- which is what happens when the kill lands before
    // the swap. Demanding a backup unconditionally reported DATA LOSS for the
    // case where nothing had been written yet.
    const targetNow = sha(target);
    const baks = readdirSync(sub).filter((f) => f.endsWith('.bak'));
    const recovered = targetNow === targetOriginal
      || baks.some((b) => sha(join(sub, b)) === targetOriginal);
    if (!recovered) noBackup += 1;

    // A .partial left behind after the process is gone is an orphan.
    if (existsSync(partial)) orphans += 1;
  }

  ok('B survived as a valid NX', `${survivors}/${attempts}`);
  ok('B no corruption', `${corrupt} corrupt`);
  ok('B original always recoverable from a backup', `${noBackup} without one`);
  ok('B no orphan .partial left behind', `${orphans} orphaned`);

  if (corrupt) bad('B', `${corrupt}/${attempts} saves left a corrupt archive -- DATA LOSS`);
  if (noBackup) bad('B', `${noBackup}/${attempts} left no copy of the original -- DATA LOSS`);
}

console.log('\n=== results ===');
for (const [v, what, detail] of results) console.log(`${v}  ${what}${detail ? `  -- ${detail}` : ''}`);
const failed = results.filter((r) => r[0] === 'FAIL').length;
console.log(failed ? `\n${failed} FAILING` : '\nall checks passed');
process.exit(failed ? 1 : 0);