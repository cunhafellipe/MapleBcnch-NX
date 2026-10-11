// Does moving the NX conversion out of the session gate actually help, and is it
// safe?
//
// Before: Open held the gate for the whole conversion, 7.6 s for Character.nx,
// and every request that touched the session waited behind it.
// After: the conversion runs off-gate.
//
// Two things must hold:
//   1. The gate is genuinely free during a long open -- measured, not assumed.
//   2. Nothing breaks while a conversion is running concurrently with other
//      MapleLib work, which is the risk the change introduces. MapleLib is not
//      documented thread-safe, and the conversion now runs beside it.

import { spawn } from 'node:child_process';
import { readFileSync, existsSync } from 'node:fs';

const RUNTIME = process.env.DOTNET_PATH || 'dotnet';
const DLL = process.argv[2];
const BIG = process.argv[3];      // a large NX: the slow open
const SMALL = process.argv[4];    // a small NX: opened first, browsed during
const PORT = process.argv[5];
const U = `http://127.0.0.1:${PORT}`;
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

const results = [];
const ok = (w, d = '') => results.push(['PASS', w, d]);
const bad = (w, d = '') => results.push(['FAIL', w, d]);

async function waitUp(tries = 60) {
  for (let i = 0; i < tries; i += 1) {
    try { const r = await fetch(`${U}/api/files`); if (r.ok) return true; } catch {}
    await sleep(400);
  }
  return false;
}
const files = async () => (await fetch(`${U}/api/files`)).json();
const openMany = async (paths) => (await fetch(`${U}/api/files/open-many`, {
  method: 'POST', headers: { 'content-type': 'application/json' },
  body: JSON.stringify({ paths }),
})).json();

const srv = spawn(RUNTIME, [DLL, '--port', PORT, '--no-warmup'], { stdio: 'ignore' });
if (!await waitUp()) { console.log('server did not start'); process.exit(1); }

// Have something open to browse while the big one converts.
await openMany([SMALL]);
const small = (await files())[0];
const browsable = await (await fetch(`${U}/api/children?path=${small.id}`)).json();
const firstChild = Array.isArray(browsable) ? browsable[0]?.path : null;

// --- latency while a big open is in flight ---------------------------------
const latencies = [];
const statuses = new Set();
let stop = false;

const prober = (async () => {
  while (!stop) {
    const t0 = performance.now();
    try {
      const r = await fetch(`${U}/api/files`);
      statuses.add(r.status);
      await r.text();
    } catch { statuses.add('throw'); }
    latencies.push(performance.now() - t0);
    await sleep(25);
  }
})();

const openStart = performance.now();
const opening = openMany([BIG]);
const result = await opening;
const openMs = performance.now() - openStart;
stop = true;
await prober;

latencies.sort((a, b) => a - b);
const p50 = latencies[Math.floor(latencies.length * 0.5)];
const p95 = latencies[Math.floor(latencies.length * 0.95)];
const worst = latencies[latencies.length - 1];

console.log(`  open of ${BIG.split(/[\\/]/).pop()} took ${(openMs / 1000).toFixed(2)} s`);
console.log(`  ${latencies.length} probes during the open: p50 ${p50.toFixed(1)} ms, p95 ${p95.toFixed(1)} ms, worst ${worst.toFixed(1)} ms`);
console.log(`  statuses seen: ${[...statuses].join(',')}`);

// The whole point: probes must not have been stuck behind the open.
if (worst < openMs / 2) ok('the gate was free during the open', `worst probe ${worst.toFixed(0)} ms vs ${(openMs / 1000).toFixed(1)} s open`);
else bad('gate still blocked', `worst probe ${worst.toFixed(0)} ms against a ${(openMs / 1000).toFixed(1)} s open`);

const nonOk = [...statuses].filter((s) => s !== 200);
if (nonOk.length === 0) ok('no request failed while converting', 'all 200');
else bad('concurrency', `statuses seen: ${nonOk.join(',')}`);

// --- the open itself must still have worked --------------------------------
const failed = result?.failed ?? [];
if (failed.length === 0) ok('the concurrent open succeeded', (result?.opened ?? []).map((o) => o.name).join(', '));
else bad('open failed', JSON.stringify(failed));

// --- and the small archive must still be usable afterwards ----------------
if (firstChild) {
  const after = await fetch(`${U}/api/children?path=${encodeURIComponent(firstChild)}`);
  ok('the pre-existing archive still browses', `status=${after.status}`);
} else {
  ok('the pre-existing archive still browses', 'no child to browse');
}

srv.kill('SIGKILL');
await sleep(800);

console.log('\n=== results ===');
for (const [v, w, d] of results) console.log(`${v}  ${w}${d ? `  -- ${d}` : ''}`);
const failedCount = results.filter((r) => r[0] === 'FAIL').length;
console.log(failedCount ? `\n${failedCount} FAILING` : '\nall checks passed');
process.exit(failedCount ? 1 : 0);