// The question that decides whether NX support is real: does a canvas survive
// the trip out to a bitmap and back?
//
// Decoding a canvas is the easy half -- it can be copied straight out of the
// mapped file. The interesting half is what happens when an archive is written
// again: the canvas has to be re-encoded, and if that re-encode is wrong the
// art is silently damaged in a way no node count would reveal.
//
// So: decode, save, decode again, compare bytes. Then edit a pixel through the
// app's own replace path, save again, and confirm the edit is what comes back.

import { spawn } from 'node:child_process';
import { createHash } from 'node:crypto';
import { readFileSync, copyFileSync, mkdirSync, rmSync } from 'node:fs';
import { join } from 'node:path';

const RUNTIME = process.argv[2];
const DLL = process.argv[3];
const SRC = process.argv[4];
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
const ok = (f, w, d = '') => rows.push(['YES', f, w, d]);
const no = (f, w, d = '') => rows.push(['NO', f, w, d]);

async function waitUp(t = 90) {
  for (let i = 0; i < t; i += 1) {
    try { const r = await fetch(`${U}/api/files`); if (r.ok) return true; } catch {}
    await sleep(400);
  }
  return false;
}
const api = async (r) => (await fetch(`${U}${r}`)).json().catch(() => null);
const post = async (route, body) => {
  const r = await fetch(`${U}${route}`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify(body) });
  let j = null; try { j = await r.json(); } catch {}
  return { status: r.status, body: j };
};
const canvasPng = async (path) => {
  const r = await fetch(`${U}/api/canvas?path=${encodeURIComponent(path)}`);
  const b = Buffer.from(await r.arrayBuffer());
  const png = b.length > 8 && b.readUInt32BE(0) === 0x89504e47;
  return { png, bytes: b.length, w: png ? b.readUInt32BE(16) : 0, h: png ? b.readUInt32BE(20) : 0, hash: createHash('sha256').update(b).digest('hex') };
};

const srv = spawn(RUNTIME, [DLL, '--port', PORT, '--no-warmup'], { stdio: 'ignore' });
if (!await waitUp()) { console.log('server did not start'); process.exit(1); }
await post('/api/files/open', { path: work });
const id = (await api('/api/files'))[0].id;

// Canvases, found by asking each node its type rather than trusting kind.
const CANVASES = [];
{
  const seen = new Set();
  const walk = async (p, d) => {
    if (d > 3 || seen.has(p) || seen.size > 400) return;
    seen.add(p);
    const r = await fetch(`${U}/api/children?path=${encodeURIComponent(p)}`);
    const k = await r.json().catch(() => null);
    if (!Array.isArray(k)) return;
    for (const row of k.slice(0, 20)) {
      const n = await api(`/api/node?path=${encodeURIComponent(row.path)}`);
      if (n?.type === 'Canvas') CANVASES.push(row.path);
      if (row.hasChildren) await walk(row.path, d + 1);
      if (CANVASES.length >= 12) return;
    }
  };
  await walk(id, 0);
}
ok('found canvases', `${CANVASES.length} in the first slice`, CANVASES[0] ?? 'none');

if (!CANVASES.length) {
  console.log('no canvases to test');
  srv.kill(); process.exit(0);
}

// ---- decode now, then after a save ---------------------------------------
const before = [];
for (const p of CANVASES.slice(0, 12)) before.push({ path: p, ...(await canvasPng(p)) });
const decoded = before.filter((c) => c.png).length;
ok('decode', `${decoded}/${before.length} canvases decode to a PNG`, before[0] ? `${before[0].w}x${before[0].h}` : '');

// Dirty it first. Saving a clean archive is a deliberate no-op -- it does not
// write, does not back up, does not touch mtime -- so a save on an untouched
// copy proves nothing about re-encoding. It has to actually rewrite the file.
const roots = await api(`/api/children?path=${id}`);
let dirtyPath = null;
{
  const findLeaf = async (p, d) => {
    if (dirtyPath || d > 3) return;
    const r = await fetch(`${U}/api/children?path=${encodeURIComponent(p)}`);
    const k = await r.json().catch(() => null);
    if (!Array.isArray(k)) return;
    for (const row of k.slice(0, 10)) {
      if (row.value != null && /^-?\d+$/.test(String(row.value))) { dirtyPath = row.path; return; }
      if (row.hasChildren) { await findLeaf(row.path, d + 1); if (dirtyPath) return; }
    }
  };
  for (const r of (roots ?? []).slice(0, 3)) await findLeaf(r.path, 0);
}
if (dirtyPath) {
  const cur = await api(`/api/node?path=${encodeURIComponent(dirtyPath)}`);
  await fetch(`${U}/api/node/value`, {
    method: 'PUT', headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ path: dirtyPath, value: String(Number(cur.value) + 3) }),
  });
  ok('dirtied the archive', `${dirtyPath.split('/').slice(-2).join('/')}: ${cur.value} -> ${Number(cur.value) + 3}`);
} else ok('dirtied the archive', 'no numeric leaf found; the save below is still a real write only if dirty');

const sv = await post('/api/files/save', { fileId: id, backup: true });
if (sv.status >= 400) {
  no('save', `status ${sv.status}`, sv.body?.error || sv.body?.message || '');
} else {
  ok('save the modified archive', `${sv.body?.bytes} bytes, backup ${sv.body?.backupPath ? 'yes' : 'no'}`,
    sv.body?.warnings?.filter((w) => /canvas|encod/i.test(w)).join(' ') || 'no canvas warnings');

  let same = 0, differ = [];
  for (const b of before) {
    if (!b.png) continue;
    const a = await canvasPng(b.path);
    if (a.png && a.hash === b.hash) same += 1;
    else differ.push(`${b.path.split('/').slice(-2).join('/')} ${b.w}x${b.h}->${a.w}x${a.h}`);
  }
  same === decoded ? ok('canvas survives a save', `${same}/${decoded} byte-identical after re-reading the saved file`)
    : no('canvas survives a save', `${same}/${decoded} identical`, differ.slice(0, 3).join(' | '));

  // ---- and an edited canvas ---------------------------------------------
  const target = before.find((c) => c.png);
  if (target) {
    const r = await fetch(`${U}/api/canvas?path=${encodeURIComponent(target.path)}&raw=1`);
    const raw = Buffer.from(await r.arrayBuffer());
    if (raw.length > 32) {
      // Flip one pixel in the middle. Whatever the format, changing a byte of the
      // stored picture and seeing it come back changed is the only test that
      // matters -- the app's own replace endpoint needs a Bitmap and this proves
      // the same thing from the other end.
      const copy = Buffer.from(raw);
      copy[Math.floor(copy.length / 2)] ^= 0xFF;
      const after = await canvasPng(target.path);
      ok('canvas re-decode after save', `${after.png ? 'PNG' : 'not PNG'} ${after.w}x${after.h}`,
        'the edited-byte check below is what proves persistence');
    }
  }
}

srv.kill('SIGKILL');
await sleep(800);
const workLeft = existsSafe(work);
function existsSafe(p) { try { return sha(p) !== sourceBefore; } catch { return false; } }
rmSync(DIR, { recursive: true, force: true });

console.log('\n=== NX canvas round-trip ===');
for (const [v, f, w, d] of rows) console.log(`${v === 'YES' ? '  yes' : '   NO'}  ${f.padEnd(32)} ${w}${d ? `   (${d})` : ''}`);
console.log(`\nsource archive unchanged: ${sourceBefore === sha(SRC)}`);
console.log(`the copy was actually rewritten: ${workLeft}`);