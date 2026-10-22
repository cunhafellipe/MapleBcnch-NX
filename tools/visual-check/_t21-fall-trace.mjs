// Throwaway probe for TASK21: frame-by-frame trace of a body that walks off a ledge.
//
//   node _t21-fall-trace.mjs <port> <mapPath> [frames] [startX] [startY]
//
// Prints one line per sampled frame in the shape TASK21 asks for:
//     tick   x        y     onGround=...  action=...
const PORT = process.argv[2] ?? '5511';
const MAP = process.argv[3] ?? 'f1/Map/Map0/000030000.img';
const FRAMES = Number(process.argv[4] ?? 240);
const SX = process.argv[5] === undefined ? undefined : Number(process.argv[5]);
const SY = process.argv[6] === undefined ? undefined : Number(process.argv[6]);
const BASE = `http://127.0.0.1:${PORT}`;

async function post(p, b, t = 5000) {
  const r = await fetch(BASE + p, {
    method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(b),
    signal: AbortSignal.timeout(t),
  });
  if (!r.ok) throw new Error(`${p} -> ${r.status}`);
  return r.json();
}

const body = { path: MAP, input: {} };
if (SX !== undefined) body.startX = SX;
if (SY !== undefined) body.startY = SY;
const s0 = await post('/api/simulate/session', body);
const s = s0.sessionId;
const b = s0.bounds;
console.log(`${MAP}`);
console.log(`  bounds left=${b.left} top=${b.top} right=${b.right} bottom=${b.bottom}`);
console.log(`  spawn x=${s0.x} y=${s0.y} onGround=${s0.onGround} action=${s0.action}`);
console.log('');
console.log('  tick        x        y   onGround  action');

const f0 = s0;
const show = (tick, f) =>
  console.log(`${String(tick).padStart(6)} ${f.x.toFixed(1).padStart(8)} ${f.y.toFixed(1).padStart(8)}   `
    + `${String(f.onGround).padEnd(8)}  ${f.action}`);

show(0, f0);
const every = Number(process.env.T21_EVERY ?? 10);
let last = f0;
for (let i = 1; i <= FRAMES; i++) {
  const f = await post(`/api/simulate/session/${s}/frame`, { right: true, down: true });
  last = f;
  if (i % every === 0 || f.onGround || i <= 5) show(i, f);
}
await post(`/api/simulate/session/${s}/close`, {});
console.log('');
console.log(`  final x=${last.x.toFixed(1)} y=${last.y.toFixed(1)} onGround=${last.onGround} action=${last.action} foothold=${last.footholdId ?? ''}`);
if (b && last.y > b.bottom + 60) console.log(`  FELL OFF (y=${last.y.toFixed(1)} > bottom+60=${b.bottom + 60})`);
else console.log(`  stayed on the map (bottom+60=${b ? b.bottom + 60 : 'n/a'})`);