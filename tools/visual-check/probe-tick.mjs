// Does LiveWalk.tick actually run, and does it accumulate?
//
//   node probe-tick.mjs <cdpPort> <serverPort> [mapId]
//
// Everything upstream is now proved: the handler receives the key
// (keys.right === true), the server steps on an explicit /frame POST
// (probe-step.mjs test A), and a POST issued from inside this very page moves
// the body. What has never been observed is whether the rAF loop body executes
// at all. `live.last` is the field that answers it: tick() rewrites it on every
// entry, so a frozen `last` is a loop that is never entered -- a different
// defect from a loop that runs and computes owed === 0.

const CDP = process.argv[2] ?? '9341';
const PORT = process.argv[3] ?? '5506';
const MAP = process.argv[4] ?? '109050001';

const pages = await (await fetch(`http://127.0.0.1:${CDP}/json/list`)).json();
const page = pages.find((t) => t.type === 'page' && t.url.includes(PORT));
if (!page) { console.error(`no page for :${PORT}`); process.exit(1); }
const ws = new WebSocket(page.webSocketDebuggerUrl);
await new Promise((r) => ws.addEventListener('open', r, { once: true }));
let id = 0;
const pending = new Map();
ws.addEventListener('message', (e) => {
  const d = JSON.parse(e.data);
  if (d.id && pending.has(d.id)) { pending.get(d.id)(d); pending.delete(d.id); }
});
const cdp = (method, params = {}) => new Promise((res) => {
  const i = ++id; pending.set(i, res);
  ws.send(JSON.stringify({ id: i, method, params }));
});
const ev = async (expression) => {
  const r = await cdp('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true });
  if (r.result?.exceptionDetails) {
    return { error: r.result.exceptionDetails.exception?.description ?? r.result.exceptionDetails.text };
  }
  return r.result?.result?.value;
};
await cdp('Runtime.enable');

const setup = await ev(`(async () => {
  const app = window.app;
  if (!app) return { error: 'no window.app' };
  await app.sections.open('simulator');
  await app.simulator.open();
  const m = (app.simulator.maps ?? []).find((x) => String(x.id) === '${MAP}');
  if (!m) return { error: 'map ${MAP} not listed' };
  await app.simulator.walk(m);
  const live = app.simulator.live;
  return {
    active: app.sections.active,
    viewError: app.simulator.viewError ?? null,
    replayError: app.simulator.replayError ?? null,
    live: live && {
      running: live.running, busy: live.busy, error: live.error || null,
      sessionId: live.sessionId, hasRaf: !!live.raf,
      last: live.last, accumulator: live.accumulator, trailLen: live.trail.length,
      stateFrame: live.state?.frame ?? null,
      // Are the listeners actually attached to the window?
      // stop()/start() come in pairs and a double-stop would silently detach.
    },
  };
})()`);
console.log('setup:', JSON.stringify(setup, null, 1));

const tickWatch = await ev(`(async () => {
  const live = window.app.simulator.live;
  const settle = (ms) => new Promise((r) => setTimeout(r, ms));
  // Independent rAF counter, so "rAF is broken" is separable from "tick is broken".
  let rafCount = 0;
  let rafStop = false;
  const spin = () => { rafCount++; if (!rafStop) requestAnimationFrame(spin); };
  requestAnimationFrame(spin);

  const out = [];
  const snap = (mark) => ({
    mark,
    perfNow: performance.now(),
    liveLast: live.last,
    lastMoved: +(performance.now() - live.last).toFixed(1),
    accumulator: +live.accumulator.toFixed(2),
    running: live.running, busy: live.busy, error: live.error || null,
    stateFrame: live.state?.frame ?? null, trailLen: live.trail.length,
    rafCount,
  });
  out.push(snap('t0'));
  for (let i = 1; i <= 4; i++) { await settle(400); out.push(snap('+' + i * 400)); }
  rafStop = true;
  return out;
})()`);
for (const w of tickWatch) console.log(' ', JSON.stringify(w));

// Call tick by hand with a real timestamp and see what it does. If the loop is
// merely unscheduled, this moves the body; if tick itself throws, the error is
// the answer.
const manual = await ev(`(async () => {
  const live = window.app.simulator.live;
  const before = { x: live.state?.x ?? null, frame: live.state?.frame ?? null,
                   last: live.last, accumulator: live.accumulator };
  let threw = null;
  try {
    await live.tick(performance.now() + 200);
  } catch (e) { threw = String(e && e.stack || e); }
  await new Promise((r) => setTimeout(r, 300));
  return {
    before,
    threw,
    after: { x: live.state?.x ?? null, frame: live.state?.frame ?? null,
             last: live.last, accumulator: live.accumulator,
             trailLen: live.trail.length, error: live.error || null,
             running: live.running },
  };
})()`);
console.log('manual tick:', JSON.stringify(manual, null, 1));

ws.close();