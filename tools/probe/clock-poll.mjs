// Prove the session has its own clock: hold a key ONCE, then poll GET and
// watch the body move with no POST /frame in the entire run.
//
//   node clock-poll.mjs <Map.nx path> [mapId]
//
// The point is what is NOT here: no step() helper, no frame endpoint, no
// accumulator on this side. If the body moves between two GETs, the server
// moved it -- and the only thing on the server that could have done so is the
// clock.

const BASE = process.env.WZ_SERVER ?? 'http://127.0.0.1:5100';
const [, , mapId = '910020202'] = process.argv;

const api = async (path, init) => {
  const r = await fetch(BASE + path, init);
  const text = await r.text();
  if (!r.ok) throw new Error(`${r.status} ${path} ${text.slice(0, 200)}`);
  return JSON.parse(text);
};

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

const list = await api(`/api/simulate/maps?q=${mapId}&limit=20`);
const target = (list.maps ?? []).map((m) => m.path).find((p) => p.includes(mapId));
if (!target) throw new Error(`no map matching ${mapId}`);

const open = await api('/api/simulate/session', {
  method: 'POST',
  headers: { 'content-type': 'application/json' },
  body: JSON.stringify({ path: target, direction: 1 }),
});
const sid = open.sessionId;
console.log(`session ${sid} on ${target}`);
console.log(`opened at x=${open.x.toFixed(2)} y=${open.y.toFixed(2)} frame=${open.frame}\n`);

// Standing still first: an untouched body must NOT drift. If it did, the
// clock would be inventing movement rather than simulating held input.
console.log('--- no input held, polling (body must NOT move) ---');
let idleFrom = performance.now();
let idleFrames0 = open.frame;
for (let i = 0; i < 4; i++) {
  await sleep(500);
  const f = await api(`/api/simulate/session/${sid}`);
  const dt = performance.now() - idleFrom;
  console.log(`  idle  frame=${String(f.frame).padStart(4)}  x=${f.x.toFixed(2).padStart(9)}  y=${f.y.toFixed(2).padStart(9)}  speed=${f.speed.toFixed(3).padStart(7)}  (${((f.frame - idleFrames0) / (dt / 1000)).toFixed(1)} fps)`);
}

// ONE call. Not 30 of them. After this there are no more writes in this script.
const held = await api(`/api/simulate/session/${sid}/input`, {
  method: 'POST',
  headers: { 'content-type': 'application/json' },
  body: JSON.stringify({ right: true }),
});
console.log(`\n--- held right ONCE (one POST /input); now only GETs, no frames ---`);
console.log(`  held  frame=${String(held.frame).padStart(4)}  x=${held.x.toFixed(2).padStart(9)}  y=${held.y.toFixed(2).padStart(9)}  speed=${held.speed.toFixed(3).padStart(7)}`);

const walkFrom = performance.now();
const walkFrames0 = held.frame;
const seen = [];
for (let i = 0; i < 6; i++) {
  await sleep(500);
  const f = await api(`/api/simulate/session/${sid}`);
  seen.push(f);
  const dt = performance.now() - walkFrom;
  console.log(`  poll${i} frame=${String(f.frame).padStart(4)}  x=${f.x.toFixed(2).padStart(9)}  y=${f.y.toFixed(2).padStart(9)}  speed=${f.speed.toFixed(3).padStart(7)}  facing=${f.facing}  fh=${f.foothold}  (${((f.frame - walkFrames0) / (dt / 1000)).toFixed(1)} fps)`);
}

const xs = seen.map((f) => f.x);
const frames = seen.map((f) => f.frame);
const moved = xs[xs.length - 1] - xs[0];
const framesAdvanced = frames[frames.length - 1] - frames[0];
const secs = (performance.now() - walkFrom) / 1000;

console.log('\n=== verdict ===');
console.log(`  x moved ${moved.toFixed(2)}px over ${framesAdvanced} frames with no frame pushed`);
console.log(`  clock rate ${(framesAdvanced / secs).toFixed(1)} fps over ${secs.toFixed(2)}s`);
// Release, so the body stops, then show it did.
await api(`/api/simulate/session/${sid}/input`, {
  method: 'POST',
  headers: { 'content-type': 'application/json' },
  body: JSON.stringify({ right: false }),
});
const released = await api(`/api/simulate/session/${sid}`);
await sleep(1200);
const coasted = await api(`/api/simulate/session/${sid}`);
console.log(`  released: x ${released.x.toFixed(2)} -> ${coasted.x.toFixed(2)} (frame ${released.frame} -> ${coasted.frame})`);

await api(`/api/simulate/session/${sid}`, { method: 'DELETE' });
process.exit(moved > 5 ? 0 : 1);