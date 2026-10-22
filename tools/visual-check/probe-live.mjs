// Why does the browser body not move when the server moves on request?
//
//   node probe-live.mjs <cdpPort> <serverPort> [mapId]
//
// The server was proved to step on explicit /frame POSTs (probe-step.mjs test A)
// and to move under a held level (test B, until it hit a wall). The keys reach
// the handler (probe-keys.mjs: keys.right became true). So whatever is wrong is
// in the loop that connects them, and this reads that loop's own fields.

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
  if (r.result?.exceptionDetails) return { error: r.result.exceptionDetails.text };
  return r.result?.result?.value;
};
await cdp('Runtime.enable');

const setup = await ev(`(async () => {
  const app = window.app;
  if (!app) return { error: 'no window.app' };
  await app.sections.open('simulator');
  await app.simulator.open();
  const maps = app.simulator.maps ?? [];
  const m = maps.find((x) => String(x.id) === '${MAP}') ?? null;
  if (!m) return { error: 'map not in list', count: maps.length };
  await app.simulator.walk(m);
  const live = app.simulator.live;
  return {
    active: app.sections.active,
    map: String(m.id), path: m.path,
    viewError: app.simulator.viewError ?? null,
    replayError: app.simulator.replayError ?? null,
    live: live ? {
      running: live.running, busy: live.busy, error: live.error,
      sessionId: live.sessionId, hasRaf: !!live.raf,
      keys: { ...live.keys }, accumulator: live.accumulator, last: live.last,
      trailLen: live.trail.length,
      state: live.state ? { x: live.state.x, y: live.state.y, frame: live.state.frame,
                            action: live.state.action, speed: live.state.speed } : null,
    } : null,
  };
})()`);
console.log('setup:', JSON.stringify(setup, null, 1));

// Hold right through the handler, exactly as a person would, and watch the
// loop's own fields over 2.5s. Every number here is read off the object that
// does the stepping -- not off the rendered canvas.
const watch = await ev(`(async () => {
  const live = window.app.simulator.live;
  const settle = (ms) => new Promise((r) => setTimeout(r, ms));
  const snap = () => ({
    t: performance.now() | 0,
    running: live.running, busy: live.busy, accumulator: +live.accumulator.toFixed(2),
    keys: { ...live.keys }, trailLen: live.trail.length,
    frame: live.state?.frame ?? null, x: live.state ? +live.state.x.toFixed(2) : null,
    action: live.state?.action ?? null, error: live.error || null,
  });
  const out = [];
  window.dispatchEvent(new KeyboardEvent('keydown', { code: 'ArrowRight', key: 'ArrowRight', bubbles: true }));
  out.push({ mark: 'down', ...snap() });
  for (let i = 1; i <= 5; i++) { await settle(500); out.push({ mark: i * 500, ...snap() }); }
  window.dispatchEvent(new KeyboardEvent('keyup', { code: 'ArrowRight', key: 'ArrowRight', bubbles: true }));
  await settle(600);
  out.push({ mark: 'up+600', ...snap() });
  return out;
})()`);
for (const w of watch) console.log(' ', JSON.stringify(w));

// Does a direct POST from the page move it? That separates "the loop is not
// posting" from "the loop is posting but the result is discarded".
const direct = await ev(`(async () => {
  const live = window.app.simulator.live;
  if (!live.sessionId) return { error: 'no session' };
  const x0 = live.state?.x;
  const r = await fetch('/api/simulate/session/' + live.sessionId + '/frame', {
    method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ left: false, right: true, up: false, down: false, jump: false }),
  });
  const f = await r.json();
  return { x0, status: r.status, x1: f.x, frame: f.frame, action: f.action,
           liveStillAt: live.state?.x ?? null };
})()`);
console.log('direct POST from page:', JSON.stringify(direct));

// And can the page reach the server at all from inside an animation frame?
const rafReach = await ev(`(async () => {
  const live = window.app.simulator.live;
  const before = live.state?.frame ?? null;
  let ran = false;
  await new Promise((r) => requestAnimationFrame(() => { ran = true; r(); }));
  await new Promise((r) => setTimeout(r, 300));
  return { rafRan: ran, frameBefore: before, frameAfter: live.state?.frame ?? null,
           hasRaf: !!live.raf, running: live.running };
})()`);
console.log('rAF reach:', JSON.stringify(rafReach));

ws.close();