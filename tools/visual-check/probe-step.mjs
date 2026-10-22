// Does the SERVER move the body when right is held? Or is it the client loop?
//
//   node probe-step.mjs <serverPort>
//
// The key probe proved the events reach the handler (keys.right became true) and
// that x never moved -- including under trusted CDP input. That leaves exactly
// two suspects: the /frame endpoint not stepping, or the client never asking.
// This asks directly, with no browser involved.

const PORT = process.argv[2] ?? '5506';
const B = `http://127.0.0.1:${PORT}`;

const post = async (p, b) => {
  const r = await fetch(B + p, {
    method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(b ?? {}),
  });
  return { status: r.status, body: await r.json() };
};
const del = async (p) => fetch(B + p, { method: 'DELETE' });

// Resolved, not guessed. A hard-coded path that is wrong yields "session
// refused" and no numbers at all, and a probe that reports nothing looks like
// a probe that passed.
const list = await (await fetch(`${B}/api/simulate/maps?q=${process.argv[3] ?? '109050001'}`)).json();
const want = (list.maps ?? [])[0];
if (!want) { console.error('no map', list); process.exit(1); }
console.log('map:', want.id, want.path);

const s0 = await post('/api/simulate/session', {
  path: want.path, startX: null, startY: null, direction: 1,
});
if (s0.status !== 200) {
  console.error('session refused:', s0.status, JSON.stringify(s0.body).slice(0, 300));
  process.exit(1);
}
const id = s0.body.sessionId;
console.log(`session ${id} start x=${s0.body.x} y=${s0.body.y} ` +
            `action=${s0.body.action} onGround=${s0.body.onGround}`);

// --- A: 180 explicit /frame POSTs with right held. No clock, no browser.
const trace = [];
for (let i = 0; i < 180; i++) {
  const f = await post(`/api/simulate/session/${id}/frame`, {
    left: false, right: true, up: false, down: false, jump: false,
  });
  if (f.status !== 200) { console.error('frame', i, f.status, JSON.stringify(f.body).slice(0, 200)); break; }
  if (i % 30 === 0 || i === 179) {
    trace.push({ i, x: +f.body.x.toFixed(2), y: +f.body.y.toFixed(2),
                 speed: +f.body.speed.toFixed(3), action: f.body.action,
                 onGround: f.body.onGround, facing: f.body.facing });
  }
}
console.log('\nA: right held, 180 explicit frame POSTs');
for (const t of trace) console.log('  ', JSON.stringify(t));

// --- B: the level-triggered route instead: one Hold, then poll.
const b0 = (await (await fetch(`${B}/api/simulate/session/${id}`)).json());
await post(`/api/simulate/session/${id}/input`, {
  left: false, right: true, up: false, down: false, jump: false,
});
const samples = [];
for (let i = 0; i < 5; i++) {
  await new Promise((r) => setTimeout(r, 400));
  const f = await (await fetch(`${B}/api/simulate/session/${id}`)).json();
  samples.push({ t: (i + 1) * 400, x: +f.x.toFixed(2), y: +f.y.toFixed(2),
                 speed: +f.speed.toFixed(3), action: f.action, frame: f.frame });
}
console.log('\nB: right HELD via /input, polled every 400ms  (x0=' + b0.x + ')');
for (const s of samples) console.log('  ', JSON.stringify(s));

// --- C: release and confirm it stops.
await post(`/api/simulate/session/${id}/input`, {
  left: false, right: false, up: false, down: false, jump: false,
});
await new Promise((r) => setTimeout(r, 900));
const c = await (await fetch(`${B}/api/simulate/session/${id}`)).json();
console.log('\nC: after release x=' + c.x.toFixed(2) + ' speed=' + c.speed.toFixed(3));

// --- D: left.
await post(`/api/simulate/session/${id}/input`, {
  left: true, right: false, up: false, down: false, jump: false,
});
await new Promise((r) => setTimeout(r, 1200));
const d = await (await fetch(`${B}/api/simulate/session/${id}`)).json();
console.log('D: left held 1.2s x=' + d.x.toFixed(2) + ' facing=' + d.facing);

await del(`/api/simulate/session/${id}`);