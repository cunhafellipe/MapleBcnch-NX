// Does a synthetic KeyboardEvent on window actually reach the app's handler?
//
//   node probe-keys.mjs <cdpPort> <serverPort>
//
// The scene harness dispatches KeyboardEvent on window and then reads the
// character state. When the state does not change, there are exactly two
// possible causes -- the app ignores the key, or the key never arrived -- and
// the difference decides where the bug is. This instrumented window and counts
// real arrivals before anything else is tried.

const CDP = process.argv[2] ?? '9341';
const PORT = process.argv[3] ?? '5506';

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

// Instrument FIRST, before loading a map, so a keydown that arrives during
// setup is counted too.
await ev(`(() => {
  window.__keyLog = [];
  if (!window.__keyProbeInstalled) {
    window.__keyProbeInstalled = true;
    // Capture phase on window: this sees the event BEFORE the app's bubble
    // handler, so a count of zero here is conclusive -- nothing dispatched
    // at all, rather than the app dropping it.
    window.addEventListener('keydown', (e) => {
      window.__keyLog.push({ type: e.type, key: e.key, code: e.code,
                             trusted: e.isTrusted, target: e.target?.tagName ?? null });
    }, true);
  }
  return true;
})()`);

const setup = await ev(`(async () => {
  const app = window.app;
  if (!app) return { error: 'no window.app' };
  await app.sections.open('simulator');
  await app.simulator.open();
  await app.simulator.refresh();
  const maps = app.simulator.maps ?? [];
  const m = maps.find((x) => String(x.id) === '109050001');
  if (!m) return { error: 'map 109050001 not in list', count: maps.length };
  await app.simulator.walk(m);
  return { active: app.sections.active, id: String(m.id),
           session: app.simulator.live?.sessionId ?? null,
           x: app.simulator.live?.state?.x ?? null };
})()`);
console.log('setup:', JSON.stringify(setup));
if (setup.error) process.exit(1);

// Four ways a key can arrive. Report all four; the diagnosis depends on which
// one the app's own handler accepts.
const probe = await ev(`(async () => {
  const sim = window.app.simulator;
  const live = sim.live;
  const before = live.state ? { x: live.state.x, y: live.state.y } : null;
  window.__keyLog = [];

  const results = {};
  const settle = (ms) => new Promise((r) => setTimeout(r, ms));

  const trial = async (label, make, ms) => {
    window.__keyLog = [];
    const t0 = live.state ? { x: live.state.x } : null;
    const down = make('keydown');
    await settle(ms);
    const mid = live.state ? { x: live.state.x, keys: { ...live.keys } } : null;
    make('keyup');
    await settle(250);
    const after = live.state ? { x: live.state.x } : null;
    results[label] = {
      arrived: window.__keyLog.length,
      arrivedKeys: window.__keyLog.map((k) => k.key),
      handlerSaw: mid?.keys ?? null,
      dxHeld: t0 && mid ? +(mid.x - t0.x).toFixed(2) : null,
      dxAfterRelease: t0 && after ? +(after.x - t0.x).toFixed(2) : null,
    };
    await settle(300);
  };

  // 1. The harness's shape today: key === code, so 'KeyS'/'Space'.
  await trial('harness-shape', (type) => window.dispatchEvent(new KeyboardEvent(type,
    { code: 'KeyD', key: 'KeyD', bubbles: true })), 1200);

  // 2. The shape a real browser produces: key is 'd', code is 'KeyD'.
  await trial('real-shape', (type) => window.dispatchEvent(new KeyboardEvent(type,
    { code: 'KeyD', key: 'd', bubbles: true })), 1200);

  // 3. A real arrow, exactly as the task asks for.
  await trial('arrow-right', (type) => window.dispatchEvent(new KeyboardEvent(type,
    { code: 'ArrowRight', key: 'ArrowRight', bubbles: true })), 1200);

  // 4. CDP-level trusted input, which no JS constructor can imitate. If this
  // moves the body and the synthetic ones do not, the app is fine and the
  // probe is wrong.
  return { results, before, handlerKeys: { ...live.keys } };
})()`);
console.log('synthetic:', JSON.stringify(probe.results, null, 2));

// The trusted-input test has to happen outside the page.
const trusted = async (key, code, type, ms) => cdp('Input.dispatchKeyEvent', {
  type, key, code, windowsVirtualKeyCode: key === 'ArrowRight' ? 39 : 68,
  nativeVirtualKeyCode: key === 'ArrowRight' ? 39 : 68,
});
const x0 = await ev(`window.app.simulator.live.state?.x ?? null`);
await trusted('ArrowRight', 'ArrowRight', 'keyDown');
await new Promise((r) => setTimeout(r, 1500));
const x1 = await ev(`window.app.simulator.live.state?.x ?? null`);
await trusted('ArrowRight', 'ArrowRight', 'keyUp');
await new Promise((r) => setTimeout(r, 400));
const x2 = await ev(`window.app.simulator.live.state?.x ?? null`);
console.log('trusted ArrowRight: x0=%s x1=%s x2=%s dx=%s',
  x0, x1, x2, (x0 != null && x1 != null ? (x1 - x0).toFixed(2) : '?'));

ws.close();