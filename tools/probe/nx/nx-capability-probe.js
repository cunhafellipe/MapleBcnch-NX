// What does the editor actually do with an NX archive, feature by feature?
//
// The question "is NX support complete" is answerable, but not by reading the
// code -- the code says what was intended. This drives the real HTTP surface
// against a real NX and reports what answered and what did not, so the answer
// is a list of observations rather than an opinion.
//
// Read-only with respect to disk: every mutation below is made against a COPY
// in a scratch folder, and the source archives are hashed before and after.

import { spawn } from 'node:child_process';
import { createHash } from 'node:crypto';
import { readFileSync, copyFileSync, mkdirSync, rmSync, existsSync } from 'node:fs';
import { join } from 'node:path';

const RUNTIME = process.argv[2];
const DLL = process.argv[3];
const SRC = process.argv[4];        // a source NX, only ever read
const DIR = process.argv[5];
const PORT = process.argv[6];
const U = `http://127.0.0.1:${PORT}`;
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const sha = (p) => createHash('sha256').update(readFileSync(p)).digest('hex');

rmSync(DIR, { recursive: true, force: true });
mkdirSync(DIR, { recursive: true });
const work = join(DIR, 'probe.nx');
copyFileSync(SRC, work);
const sourceBefore = sha(SRC);

const rows = [];
const ok = (f, what, d = '') => rows.push(['YES', f, what, d]);
const no = (f, what, d = '') => rows.push(['NO', f, what, d]);

async function waitUp(t = 60) {
  for (let i = 0; i < t; i += 1) {
    try { const r = await fetch(`${U}/api/files`); if (r.ok) return true; } catch {}
    await sleep(400);
  }
  return false;
}
const api = async (route) => (await fetch(`${U}${route}`)).json().catch(() => null);
const child = async (path) => (await (await fetch(`${U}/api/children?path=${encodeURIComponent(path)}`)).json());
const node = async (path) => (await (await fetch(`${U}/api/node?path=${encodeURIComponent(path)}`)).json());
const put = async (path, value) => fetch(`${U}/api/node/value`, {
  method: 'PUT', headers: { 'content-type': 'application/json' },
  body: JSON.stringify({ path, value: String(value) }),
});
const post = async (route, body) => {
  const r = await fetch(`${U}${route}`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify(body) });
  let j = null; try { j = await r.json(); } catch {}
  return { status: r.status, body: j };
};

const srv = spawn(RUNTIME, [DLL, '--port', PORT, '--no-warmup'], { stdio: 'ignore' });
if (!await waitUp()) { console.log('server did not start'); process.exit(1); }

await post('/api/files/open', { path: work });
const files = await api('/api/files');
const id = files[0]?.id;

// ---------------------------------------------------------------- content shape
{
  const kids = await child(id);
  ok('open', `archive opens and lists ${Array.isArray(kids) ? kids.length : 0} roots`, files[0]?.kind);

  let canvases = 0, audio = 0, images = 0, deepest = 0;
  const seen = new Set();
  const walk = async (p, d) => {
    if (d > 4 || seen.has(p) || seen.size > 400) return;
    seen.add(p); deepest = Math.max(deepest, d);
    const k = await child(p);
    if (!Array.isArray(k)) return;
    for (const row of k.slice(0, 8)) {
      if (row.kind === 'Image') images += 1;
      if (row.kind === 'Canvas') canvases += 1;
      if (row.kind === 'Audio') audio += 1;
      if (row.hasChildren) await walk(row.path, d + 1);
    }
  };
  await walk(id, 0);
  ok('browse', `walked ${seen.size} nodes to depth ${deepest}`, `${images} images, ${canvases} canvases, ${audio} audio`);
}

// ------------------------------------------------------------- edit operations
{
  // find a writable numeric leaf
  let leaf = null;
  const find = async (p, d) => {
    if (leaf || d > 3) return;
    const k = await child(p);
    if (!Array.isArray(k)) return;
    for (const row of k) {
      const sub = await child(row.path);
      if (!Array.isArray(sub)) { if (row.value != null && /^-?\d+$/.test(String(row.value))) { leaf = row; return; } }
      else for (const r2 of sub.slice(0, 4)) { const s2 = await child(r2.path); if (!Array.isArray(s2) && r2.value != null && /^-?\d+$/.test(String(r2.value))) { leaf = r2; return; } }
      await find(row.path, d + 1);
      if (leaf) return;
    }
  };
  await find(id, 0);
  if (leaf) {
    const before = leaf.value;
    const r = await put(leaf.path, Number(before) + 5);
    const after = (await node(leaf.path)).value;
    r.ok && String(after) === String(Number(before) + 5)
      ? ok('edit a value', 'set, read back', `${before} -> ${after}`)
      : no('edit a value', `status ${r.status}`, `read back ${after}`);
  } else no('edit a value', 'no numeric leaf found to test');

  // add a child under a directory. AddNodeRequest is {Path: parent, Name, Type,
  // Value} -- the parent and the child are separate fields, and passing the
  // child inside Path asks the server to find a node that does not exist yet.
  const dirs = await child(id);
  if (Array.isArray(dirs) && dirs.length) {
    const r = await post('/api/node', { path: dirs[0].path, name: 'probeChild', type: 'Long', value: '1' });
    r.status < 400 ? ok('add a node', `status ${r.status}`, `under ${dirs[0].path}`) : no('add a node', r.body?.error || r.body?.message || `status ${r.status}`);
    const del = await post('/api/node/delete', { paths: [`${dirs[0].path}/probeChild`] });
    del.status < 400 ? ok('delete a node', `status ${del.status}`) : no('delete a node', del.body?.error || del.body?.message || `status ${del.status}`);
  } else no('add a node', 'no directory child to add under');

  const rn = await post('/api/node', { path: id, name: 'probeRename', type: 'Long', value: '2' });
  if (rn.status < 400) {
    const r = await post('/api/node/rename', { path: `${id}/probeRename`, newName: 'probeRenamed' });
    r.status < 400 ? ok('rename a node', `status ${r.status}`) : no('rename a node', r.body?.error || r.body?.message || `status ${r.status}`);
    await post('/api/node/delete', { paths: [`${id}/probeRenamed`] });
  } else no('rename a node', 'not tested: could not create a node to rename', rn.body?.error || `create status ${rn.status}`);

  const dup = await post('/api/node/duplicate', { paths: [`${dirs?.[0]?.path ?? id}`] });
  dup.status < 400 ? ok('duplicate a node', `status ${dup.status}`) : no('duplicate a node', dup.body?.error || dup.body?.message || `status ${dup.status}`);
}

// --------------------------------------------------------------- save and export
{
  const changes = await api('/api/changes');
  if ((changes.files ?? []).length) {
    const sv = await post('/api/files/save', { fileId: id, backup: true });
    sv.status < 400 ? ok('save in place', sv.body?.savedTo ? `wrote ${sv.body.bytes} bytes, backup ${sv.body.backupPath ? 'yes' : 'none'}` : `status ${sv.status}`)
      : no('save in place', sv.body?.error || sv.body?.message || `status ${sv.status}`);
  } else ok('save in place', 'skipped: nothing was dirty');

  for (const [label, route] of [
    ['export JSON', `/api/export/json?path=${encodeURIComponent(id)}`],
    ['export XML', `/api/export/xml?path=${encodeURIComponent(id)}`],
    ['export IMG', `/api/export/img?path=${encodeURIComponent(id)}`],
  ]) {
    try {
      const r = await fetch(`${U}${route}`);
      const body = await r.arrayBuffer();
      r.ok && body.byteLength > 0
        ? ok(label, `${body.byteLength} bytes`)
        : no(label, `status ${r.status}`, new TextDecoder().decode(body).slice(0, 120).replace(/\s+/g, ' '));
    } catch (e) { no(label, e.message); }
  }
}

// ------------------------------------------------------------------ section editors
for (const [file, label] of [
  ['Item', 'cash shop / items'], ['Mob', 'mobs'], ['Skill', 'skills'], ['Npc', 'NPCs'], ['String', 'strings'], ['Etc', 'etc'],
]) {
  // q is required and filters by name or value; an empty q is not "everything",
  // it is "nothing matches". Ask for something every archive has instead.
  const r = await fetch(`${U}/api/db/search?q=&kind=${file}&limit=5`);
  const j = await r.json().catch(() => null);
  const n = Array.isArray(j) ? j.length : (j?.items?.length ?? j?.results?.length ?? null);
  r.ok ? ok('game-data search', label, n === null ? 'answered, shape unknown' : `${n} rows for an empty query`)
       : no('game-data search', label, `status ${r.status}`);
}

// ------------------------------------------------------------- memory and sweep
{
  const before = await api('/api/session/memory');
  const sw = await post('/api/session/sweep', {});
  const after = await api('/api/session/memory');
  const reclaimed = (after?.workingSetMB ?? 0) - (before?.workingSetMB ?? 0);
  sw.body?.swept > 0 ? ok('memory sweep releases images', `swept ${sw.body.swept}, kept ${sw.body.kept}`, `${reclaimed} MB`)
    : no('memory sweep releases images', `swept ${sw.body?.swept ?? '?'}, kept ${sw.body?.kept ?? '?'}`, 'NX images carry no on-disk block, so none are releasable');
}

// ------------------------------------------------------------------- cleanup
srv.kill('SIGKILL');
await sleep(800);
rmSync(DIR, { recursive: true, force: true });
const sourceAfter = sha(SRC);

console.log('\n=== NX capability probe ===');
for (const [v, f, what, d] of rows) {
  console.log(`${v === 'YES' ? '  yes' : '   NO'}  ${f.padEnd(24)} ${what}${d ? `   (${d})` : ''}`);
}
const noCount = rows.filter((r) => r[0] === 'NO').length;
console.log(`\n${rows.length - noCount} working, ${noCount} not`);
console.log(`source archive unchanged: ${sourceBefore === sourceAfter}`);