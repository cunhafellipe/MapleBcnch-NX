// Independent visual verification: does the character actually appear?
//   node verify-char.mjs <cdpPort>
// Drives the real page, forces the simulator onto a real map with real art,
// and reads canvas pixels directly instead of trusting a screenshot.

// Node 22+ ships a global WebSocket; the repo has no `ws` dependency and
// this script must not add one just to drive a check.
if (typeof WebSocket === 'undefined') throw new Error('needs node 22+ global WebSocket');

const PORT = process.argv[2] ?? '9340';
const list = await (await fetch(`http://127.0.0.1:${PORT}/json/list`)).json();
const page = list.find((t) => t.type === 'page' && t.url.includes('5509'))
          ?? list.find((t) => t.type === 'page');
console.log('page:', page.url);

const ws = new WebSocket(page.webSocketDebuggerUrl);
// The global WebSocket is the DOM one: addEventListener, not .on() like `ws`.
await new Promise((r) => ws.addEventListener('open', r, { once: true }));
let id = 0;
const pending = new Map();
ws.addEventListener('message', (ev) => {
  const d = JSON.parse(ev.data);
  if (d.id && pending.has(d.id)) { pending.get(d.id)(d); pending.delete(d.id); }
});
const cdp = (method, params = {}) => new Promise((res) => {
  const i = ++id; pending.set(i, res);
  ws.send(JSON.stringify({ id: i, method, params }));
});
const ev = async (expr) => {
  const r = await cdp('Runtime.evaluate', {
    expression: expr, awaitPromise: true, returnByValue: true,
  });
  if (r.result?.exceptionDetails) return { error: r.result.exceptionDetails.text };
  return r.result?.result?.value;
};

await cdp('Page.enable');
await cdp('Runtime.enable');

// Open the simulator section and load the real map with real art.
console.log(await ev(`(async () => {
  const app = window.app;
  if (!app) return 'no window.app';
  // sections.js:450 opens the simulator through app.simulator; the viewer is
  // a property on it, not a global.
  // sections.open() is what actually makes the simulator the visible stage.
  // Calling sim.open() directly loads data while the mobs section is still on
  // screen -- the map is then simulated off-screen and every screenshot of it
  // is evidence of nothing.
  await app.sections.open('simulator');
  const sim = app.simulator;
  await sim.open();
  const map = sim.maps?.find((m) => String(m.id) === '109050001') ?? sim.maps?.[0];
  if (!map) return 'no maps; caps=' + JSON.stringify(sim.caps)?.slice(0, 200);
  await sim.walk(map);
  return 'walking ' + (map.id ?? map.path) + ' section=' + app.sections.active;
})()`));

await new Promise((r) => setTimeout(r, 1500));

const info = await ev(`(() => {
  const v = window.app?.simulator?.viewer;
  if (!v) return { error: 'no viewer', keys: Object.keys(window).filter(k=>/view|walk/i.test(k)) };
  return {
    hasAvatar: !!v.avatar,
    hasPart: !!v.avatar?.part,
    frames: v.avatar?.part?.frames?.length ?? 0,
    bitmaps: v.avatar?.bitmaps?.size ?? 0,
    mapPath: v.path ?? null,
    canvasSize: [v.canvas?.width, v.canvas?.height],
  };
})()`);
console.log('viewer:', JSON.stringify(info));

// Read the pixels at the character's own position.
const px = await ev(`(() => {
  const v = window.app?.simulator;
  const c = v?.canvas;
  if (!c) return { error: 'no canvas' };
  const g = c.getContext('2d');
  const d = g.getImageData(0, 0, c.width, c.height).data;
  let nonBlank = 0, opaque = 0;
  for (let i = 0; i < d.length; i += 4) {
    if (d[i] || d[i+1] || d[i+2]) nonBlank++;
    if (d[i+3] > 0) opaque++;
  }
  return { w: c.width, h: c.height, nonBlank, opaque, total: d.length / 4 };
})()`);
console.log('canvas pixels:', JSON.stringify(px));

// Hold right for a while so the character walks into open ground, then shoot.
await ev(`(async () => {
  const fire = (type, code) => window.dispatchEvent(
    new KeyboardEvent(type, { code, key: code === 'ArrowRight' ? 'ArrowRight' : code, bubbles: true }));
  fire('keydown', 'ArrowRight');
  await new Promise(r => setTimeout(r, 2600));
  fire('keyup', 'ArrowRight');
  return 'held';
})()`);
await new Promise((r) => setTimeout(r, 700));

const after = await ev(`(() => {
  const v = window.app?.simulator;
  const cur = v?.ticks?.[v.ticks.length - 1] ?? v?.current ?? null;
  return {
    x: cur?.x, y: cur?.y, action: cur?.action,
    frame: cur?.frame, onGround: cur?.onGround,
    frameIndex: v?.avatarFrame ?? null,
    bitmaps: v?.avatar?.bitmaps?.size ?? 0,
    hud: document.querySelector('.hud')?.textContent?.slice(0, 200) ?? null,
  };
})()`);
console.log('after walk:', JSON.stringify(after));

// Screenshot only after the simulator is actually the visible section AND the
// character has been on screen: a shot of the file browser proves nothing.
const ready = await ev(`(() => {
  const app = window.app, sim = app?.simulator;
  const stage = document.querySelector('.sim-stage, #sim-canvas, canvas');
  const visible = [...document.querySelectorAll('canvas')]
    .filter(c => c.offsetParent !== null).map(c => c.id || c.className || 'anon');
  return {
    activeSection: app?.sections?.active,
    visibleCanvases: visible,
    hasSimStage: !!stage,
    tickCount: sim?.result?.ticks?.length ?? null,
  };
})()`);
console.log('ready:', JSON.stringify(ready));
if (ready.activeSection !== 'simulator') {
  console.log('!! 沒切到 simulator，截圖會是無效證據');
}

const shot = await cdp('Page.captureScreenshot', { format: 'png' });
const { writeFileSync } = await import('node:fs');
const out = process.argv[3] ?? 'shots/VERIFY-CHAR.png';
writeFileSync(out, Buffer.from(shot.result.data, 'base64'));
console.log('screenshot ->', out);

ws.close();