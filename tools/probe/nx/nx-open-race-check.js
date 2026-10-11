// Two questions the first run of this test could not answer.
//
// 1. After three racing opens of one NX the session showed 16 GB resident for a
//    tree that is 5.6 GB. Is that a leak -- the losers' trees still reachable --
//    or just a GC that has not run? The difference matters: one is a bug that
//    grows with every racing open, the other is a pause.
// 2. The original phase 2 opened the SAME archive the race phase had already
//    opened, so the "conversion" returned instantly and proved nothing. This
//    version closes everything first, then converts a different large archive
//    while a live tree is being read and written.

import { spawn } from 'node:child_process';

const RUNTIME = 'C:/MUWORK/app/scoop/apps/dotnet-sdk/current/dotnet.exe';
const DLL = process.argv[2];
const A = process.argv[3];      // races here
const B = process.argv[4];      // converts during concurrent work
const SMALL = process.argv[5];
const PORT = process.argv[6];
const U = `http://127.0.0.1:${PORT}`;
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

const results = [];
const ok = (w, d = '') => results.push(['PASS', w, d]);
const bad = (w, d = '') => results.push(['FAIL', w, d]);

async function waitUp(t = 60) {
  for (let i = 0; i < t; i += 1) {
    try { const r = await fetch(`${U}/api/files`); if (r.ok) return true; } catch {}
    await sleep(400);
  }
  return false;
}
const files = async () => (await fetch(`${U}/api/files`)).json();
const mem = async () => (await fetch(`${U}/api/session/memory`)).json();
const openOne = async (p) => (await fetch(`${U}/api/files/open`, {
  method: 'POST', headers: { 'content-type': 'application/json' },
  body: JSON.stringify({ path: p }),
})).json().catch(() => null);
const openMany = async (paths) => (await fetch(`${U}/api/files/open-many`, {
  method: 'POST', headers: { 'content-type': 'application/json' },
  body: JSON.stringify({ paths }),
})).json().catch(() => null);
const closeAll = async () => {
  for (const f of await files()) {
    await fetch(`${U}/api/files/${f.id}`, { method: 'DELETE' }).catch(() => null);
  }
};
const node = async (p) => (await fetch(`${U}/api/node?path=${encodeURIComponent(p)}`)).json();
const children = async (p) => (await fetch(`${U}/api/children?path=${encodeURIComponent(p)}`)).json();

const srv = spawn(RUNTIME, [DLL, '--port', PORT, '--no-warmup'], { stdio: 'ignore' });
if (!await waitUp()) { console.log('server did not start'); process.exit(1); }

// ---------------------------------------- 1. race: leak, or just not collected?
{
  await Promise.all([openOne(A), openOne(A), openOne(A)]);
  const before = await mem();
  const list = await files();
  const one = list.filter((f) => f.name.toLowerCase().endsWith('.nx'));
  ok('registered exactly once', `${one.length} entry, id=${one[0]?.id}`);

  // Force a collection the way the app's own sweep does.
  await fetch(`${U}/api/session/sweep`, { method: 'POST' });
  await sleep(3000);
  const after = await mem();

  console.log(`  working set: ${before.workingSetMB} MB before sweep -> ${after.workingSetMB} MB after`);
  console.log(`  managed:     ${before.managedMB} MB before sweep -> ${after.managedMB} MB after`);

  const ratio = after.workingSetMB / 6000;   // one tree measured at ~5.6-6 GB
  if (ratio < 1.6) ok('one tree survives the race', `${after.workingSetMB} MB for a ~5.6 GB archive`);
  else bad('leak', `${after.workingSetMB} MB after a collection -- about ${ratio.toFixed(1)} trees are still alive`);
}

// -------------------------- 2. a real conversion beside real MapleLib work
{
  await closeAll();
  await fetch(`${U}/api/session/sweep`, { method: 'POST' });
  await sleep(2500);
  const cleared = await mem();
  console.log(`  after closing everything: ${cleared.workingSetMB} MB`);

  await openMany([SMALL]);
  const target = (await files())[0];

  // Walk to a real scalar leaf. A container node answers a set with 400 -- the
  // server is right to -- and three runs of this test reported 373 "failed
  // writes" that were 373 correct rejections of a bad request.
  const path = await (async () => {
    const level1 = await children(target.id);
    if (!Array.isArray(level1) || !level1.length) return null;
    for (const a of level1) {
      const level2 = await children(a.path);
      if (!Array.isArray(level2)) { if (a.value != null) return a.path; continue; }
      for (const b of level2) {
        const level3 = await children(b.path);
        if (!Array.isArray(level3)) { if (b.value != null) return b.path; }
        else if (level3.length && level3[0].value != null) return level3[0].path;
      }
    }
    return null;
  })();
  if (!path) { bad('setup', 'no writable leaf found -- the write half cannot run'); }
  else console.log(`  writing to leaf: ${path}`);

  let stop = false;
  let reads = 0, writes = 0;
  const problems = [];
  const lat = [];
  const hammer = (async () => {
    while (!stop) {
      const t0 = performance.now();
      try {
        if (path) {
          const n = await node(path);
          if (n.error) problems.push(`node: ${n.error}`); else reads += 1;
          // Write too: a live edit against a tree while another is being built.
          const w = await fetch(`${U}/api/node/value`, {
            method: 'PUT', headers: { 'content-type': 'application/json' },
            body: JSON.stringify({ path, value: String(reads) }),
          });
          if (w.ok) writes += 1; else problems.push(`set: ${w.status}`);
        }
        await (await fetch(`${U}/api/files`)).json();
      } catch (e) { problems.push(`threw: ${e.message}`); }
      lat.push(performance.now() - t0);
      await sleep(10);
    }
  })();

  const t0 = performance.now();
  const r = await openMany([B]);
  const openMs = performance.now() - t0;
  stop = true;
  await hammer;

  lat.sort((a, b) => a - b);
  console.log(`  open of ${B.split(/[\\/]/).pop()}: ${(openMs / 1000).toFixed(2)} s`);
  console.log(`  during it: ${reads} reads, ${writes} writes, ${lat.length} probes, p95 ${lat[Math.floor(lat.length * 0.95)]?.toFixed(1)} ms, worst ${lat[lat.length - 1]?.toFixed(0)} ms`);

  ok('reads continued during a real conversion', `${reads} completed`);
  ok('writes continued during a real conversion', `${writes} completed`);
  if (problems.length === 0) ok('nothing errored', '');
  else bad('concurrency', `${problems.length}: ${[...new Set(problems)].slice(0, 3).join(' | ')}`);

  const worst = lat[lat.length - 1] ?? 0;
  if (worst < openMs / 2) ok('requests were not stuck behind the conversion', `worst ${worst.toFixed(0)} ms vs ${(openMs / 1000).toFixed(1)} s`);
  else bad('gate blocked', `worst probe ${worst.toFixed(0)} ms against a ${(openMs / 1000).toFixed(1)} s open`);

  const failed = r?.failed ?? [];
  if (failed.length === 0) ok('the concurrent open succeeded', (r?.opened ?? []).map((o) => o.name).join(', '));
  else bad('open failed', JSON.stringify(failed));

  // The archive that was being edited throughout must still be coherent.
  const after = await children(path ?? target.id);
  ok('the edited archive is still coherent', Array.isArray(after) ? `${after.length} children` : JSON.stringify(after).slice(0, 120));
}

srv.kill('SIGKILL');
await sleep(800);

console.log('\n=== results ===');
for (const [v, w, d] of results) console.log(`${v}  ${w}${d ? `  -- ${d}` : ''}`);
const f = results.filter((r) => r[0] === 'FAIL').length;
console.log(f ? `\n${f} FAILING` : '\nall checks passed');
process.exit(f ? 1 : 0);