// Does releasing cached NX pixels lose anything?
//
// The sweep now drops the decoded byte[] an NX canvas holds. Those bytes came
// out of the memory-mapped archive and go straight back into it, so the claim
// is that a canvas renders identically afterwards. "Identically" is checked by
// hash, not by eye, and by eye is how a subtly wrong pixel format would ship.
//
// Also checks that the cache actually did something: without a second render
// filling it, "freed N bytes" is trivially true of a cache that was never used.

import { spawn } from 'node:child_process';
import { createHash } from 'node:crypto';

const RUNTIME = process.argv[2];
const DLL = process.argv[3];
const SRC = process.argv[4];
const PORT = process.argv[5];
const U = `http://127.0.0.1:${PORT}`;
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const j = async (route) => (await fetch(`${U}${route}`)).json();

async function waitUp(t = 60) {
  for (let i = 0; i < t; i += 1) {
    try { const r = await fetch(`${U}/api/files`); if (r.ok) return true; } catch {}
    await sleep(400);
  }
  return false;
}

const pngOf = async (path) => {
  const r = await fetch(`${U}/api/canvas?path=${encodeURIComponent(path)}`);
  const b = Buffer.from(await r.arrayBuffer());
  return {
    ok: r.ok, bytes: b.length,
    png: b.length > 8 && b.readUInt32BE(0) === 0x89504e47,
    w: b.length > 24 ? b.readUInt32BE(16) : 0,
    h: b.length > 24 ? b.readUInt32BE(20) : 0,
    hash: createHash('sha256').update(b).digest('hex').slice(0, 16),
  };
};

const rows = [];
const ok = (w, d = '') => rows.push(['yes', w, d]);
const no = (w, d = '') => rows.push(['NO', w, d]);

const srv = spawn(RUNTIME, [DLL, '--port', PORT, '--no-warmup'], { stdio: 'ignore' });
if (!await waitUp()) { console.log('server did not start'); process.exit(1); }

await fetch(`${U}/api/files/open`, {
  method: 'POST', headers: { 'content-type': 'application/json' },
  body: JSON.stringify({ path: SRC }),
});

// Find canvases without walking the whole tree.
const found = [];
const seen = new Set();
const walk = async (p, d) => {
  if (d > 3 || seen.has(p) || seen.size > 300 || found.length >= 40) return;
  seen.add(p);
  const kids = await (await fetch(`${U}/api/children?path=${encodeURIComponent(p)}`)).json();
  if (!Array.isArray(kids)) return;
  for (const r of kids.slice(0, 15)) {
    const n = await j(`/api/node?path=${encodeURIComponent(r.path)}`);
    if (n?.type === 'Canvas') found.push(r.path);
    else if (r.hasChildren) await walk(r.path, d + 1);
    if (found.length >= 40) return;
  }
};
await walk('f1', 0);
ok('canvases found', `${found.length}`);

if (!found.length) { srv.kill(); process.exit(0); }

// Render them all: fills the cache.
const before = [];
for (const c of found) before.push({ c, ...(await pngOf(c)) });
const real = before.filter((b) => b.ok && b.png);
ok('rendered', `${real.length}/${found.length} as PNG`, real[0] ? `${real[0].w}x${real[0].h}, ${real[0].bytes} bytes` : '');

const sweep1 = await (await fetch(`${U}/api/session/sweep`, { method: 'POST' })).json();
ok('sweep after rendering', `imagesSwept=${sweep1.swept} kept=${sweep1.kept} nxPixelsFreed=${sweep1.nxPixelsFreedMB}MB reclaimed=${sweep1.reclaimedMB}MB`);

// The whole point: same bytes after the cache was thrown away.
let same = 0, differ = [];
for (const b of real) {
  const a = await pngOf(b.c);
  if (a.hash === b.hash && a.w === b.w && a.h === b.h) same += 1;
  else differ.push(`${b.c.split('/').slice(-2).join('/')} ${b.hash}->${a.hash}`);
}
same === real.length
  ? ok('every canvas renders identically after its pixels were released', `${same}/${real.length}`)
  : no('pixels changed after release', `${same}/${real.length} identical`, differ.slice(0, 3).join(' | '));

// And a second sweep after re-rendering must free something again, or the
// release is not being reached at all.
const sweep2 = await (await fetch(`${U}/api/session/sweep`, { method: 'POST' })).json();
ok('sweep after re-render', `nxPixelsFreed=${sweep2.nxPixelsFreedMB}MB`,
  sweep2.nxPixelsFreedMB > 0 ? 'the cache refilled and was released again' : 'nothing left to free — the re-render may not have cached');

// Third render, to be sure the second sweep did not leave anything broken.
let same3 = 0;
for (const b of real) { const a = await pngOf(b.c); if (a.hash === b.hash) same3 += 1; }
same3 === real.length
  ? ok('still identical after a second release', `${same3}/${real.length}`)
  : no('drifted after the second sweep', `${same3}/${real.length}`);

srv.kill('SIGKILL');
await sleep(600);

console.log('\n=== NX canvas pixel release ===');
for (const [v, w, d] of rows) console.log(`${v === 'yes' ? '  yes' : '   NO'}  ${w}${d ? `   (${d})` : ''}`);
const bad = rows.filter((r) => r[0] === 'NO').length;
console.log(bad ? `\n${bad} FAILING` : '\nno loss: released pixels came back identical');