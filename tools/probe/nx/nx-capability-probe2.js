// Second pass: the capabilities that decide whether NX support is complete.
//
// The first probe's failures were mostly the server rightly refusing a malformed
// request -- an archive root is not an image, so "export as .img" correctly
// says so. What is left to establish is the part that would actually matter to
// somebody editing a game: can you change a canvas, does it survive a save,
// does an image export, and does a deep value edit round-trip.
//
// Uses an archive that actually contains canvases. Item.nx has none, which is
// why the first pass found zero.

import { spawn } from 'node:child_process';
import { createHash } from 'node:crypto';
import { readFileSync, copyFileSync, mkdirSync, rmSync, writeFileSync } from 'node:fs';
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
const png = join(DIR, 'canvas.png');

const rows = [];
const ok = (f, what, d = '') => rows.push(['YES', f, what, d]);
const no = (f, what, d = '') => rows.push(['NO', f, what, d]);

async function waitUp(t = 90) {
  for (let i = 0; i < t; i += 1) {
    try { const r = await fetch(`${U}/api/files`); if (r.ok) return true; } catch {}
    await sleep(400);
  }
  return false;
}
const api = async (r) => (await fetch(`${U}${r}`)).json().catch(() => null);
const child = async (p) => (await (await fetch(`${U}/api/children?path=${encodeURIComponent(p)}`)).json());
const node = async (p) => (await (await fetch(`${U}/api/node?path=${encodeURIComponent(p)}`)).json());
const post = async (route, body) => {
  const r = await fetch(`${U}${route}`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify(body) });
  let j = null; try { j = await r.json(); } catch {}
  return { status: r.status, body: j };
};

const srv = spawn(RUNTIME, [DLL, '--port', PORT, '--no-warmup'], { stdio: 'ignore' });
if (!await waitUp()) { console.log('server did not start (the archive may be large)'); process.exit(1); }

await post('/api/files/open', { path: work });
const files = await api('/api/files');
const id = files[0]?.id;

// Find one canvas and one numeric leaf, walking properly this time.
let canvas = null, leaf = null, image = null;
const seen = new Set();
const walk = async (p, d) => {
  if (d > 6 || seen.has(p) || seen.size > 20000) return;
  seen.add(p);
  const k = await child(p);
  if (!Array.isArray(k)) return;
  for (const row of k) {
    if (row.kind === 'Canvas' && !canvas) canvas = row.path;
    if (row.kind === 'Image' && !image) image = row.path;
    if (row.value != null && /^-?\d+$/.test(String(row.value)) && !leaf) leaf = { path: row.path, value: row.value };
    if (row.hasChildren) await walk(row.path, d + 1);
    // A canvas is the thing this pass exists to find, so the walk only stops
    // early once it has one. Stopping as soon as there was an image and a leaf
    // made Morph.nx -- 1,824 bitmaps -- report no canvas at all.
    if (canvas) return;
  }
};
await walk(id, 0);
ok('walked', `${seen.size} nodes`, `${image ? 'has an image' : 'no image'}, ${canvas ? 'has a canvas' : 'NO CANVAS'}, ${leaf ? 'has a numeric leaf' : 'no numeric leaf'}`);

// --- deep value edit round-trip ------------------------------------------
if (leaf) {
  const want = Number(leaf.value) + 7;
  const r = await fetch(`${U}/api/node/value`, {
    method: 'PUT', headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ path: leaf.path, value: String(want) }),
  });
  const readBack = (await node(leaf.path)).value;
  r.ok && String(readBack) === String(want)
    ? ok('edit a deep value', `${leaf.value} -> ${readBack}`, leaf.path)
    : no('edit a deep value', `status ${r.status}`, `read ${readBack}`);
}

// --- canvas decode --------------------------------------------------------
if (canvas) {
  const meta = await node(canvas);
  const r = await fetch(`${U}/api/canvas?path=${encodeURIComponent(canvas)}`);
  const buf = Buffer.from(await r.arrayBuffer());
  const isPng = buf.length > 8 && buf.readUInt32BE(0) === 0x89504e47;
  const w = isPng ? buf.readUInt32BE(16) : 0;
  const h = isPng ? buf.readUInt32BE(20) : 0;
  ok('decode a canvas', isPng ? `${w}x${h} PNG, ${buf.length} bytes` : `not a PNG (${buf.length} bytes)`,
    meta?.width ? `declared ${meta.width}x${meta.height} ${meta.format ?? ''}` : '');
  writeFileSync(png, buf);
}

// --- export a real image --------------------------------------------------
if (image) {
  for (const [label, route] of [
    ['export .img', `/api/export/img?path=${encodeURIComponent(image)}`],
    ['export JSON', `/api/export/json?path=${encodeURIComponent(image)}`],
    ['export XML', `/api/export/xml?path=${encodeURIComponent(image)}`],
  ]) {
    const r = await fetch(`${U}${route}`);
    const body = Buffer.from(await r.arrayBuffer());
    r.ok && body.length
      ? ok(label, `${body.length} bytes`)
      : no(label, `status ${r.status}`, body.toString('utf8').slice(0, 110).replace(/\s+/g, ' '));
  }
} else no('export .img', 'no image node found to export');

// --- the canvas survives a save -------------------------------------------
if (canvas) {
  const before = sha(work);
  const sv = await post('/api/files/save', { fileId: id, backup: true });
  if (sv.status < 400) {
    const after = await fetch(`${U}/api/canvas?path=${encodeURIComponent(canvas)}`);
    const buf = Buffer.from(await after.arrayBuffer());
    const isPng = buf.length > 8 && buf.readUInt32BE(0) === 0x89504e47;
    ok('canvas survives a save', isPng ? `re-decodes, ${buf.length} bytes` : 'DID NOT DECODE AFTER SAVE',
      `${before === sha(work) ? 'file unchanged' : 'file rewritten'} (${sv.body?.bytes ?? '?'} bytes)`);
    const report = await post('/api/files/preflight', { fileId: id });
    ok('preflight after save', `status ${report.status}`,
      report.body?.dirtyImages ?? report.body?.imagesRewritten ?? JSON.stringify(report.body ?? {}).slice(0, 90));
  } else no('canvas survives a save', `save failed: ${sv.body?.error || sv.body?.message || sv.status}`);
}

srv.kill('SIGKILL');
await sleep(800);
rmSync(DIR, { recursive: true, force: true });

console.log('\n=== NX capability probe, second pass ===');
for (const [v, f, what, d] of rows) {
  console.log(`${v === 'YES' ? '  yes' : '   NO'}  ${f.padEnd(26)} ${what}${d ? `   (${d})` : ''}`);
}
const n = rows.filter((r) => r[0] === 'NO').length;
console.log(`\n${rows.length - n} working, ${n} not`);
console.log(`source archive unchanged: ${sourceBefore === sha(SRC)}`);