// TASK 9B, step 2: the load chain is proven healthy (part composed, 14 bitmaps
// all decoded), yet no character appears. So stop asking the loader questions
// and hook the ONE call that would put pixels on the canvas: drawImage.
//
// Every drawImage is recorded with its destination rect, so this answers the
// question the spec calls out -- "what is avatarFrame at the moment of the draw
// call" -- and, crucially, whether drawAvatar is reached at all and whether the
// rect it computes lands ON the visible canvas or off in the void.
//
//   node drawimage-trace.mjs <baseUrl> <cdpPort>

import process from 'node:process';
import { writeFile } from 'node:fs/promises';

const [, , base, portArg = '9222'] = process.argv;
const PORT = Number(portArg);
const OUT = 'tools/probe/out/drawimage-trace.png';

const ver = await fetch(`http://127.0.0.1:${PORT}/json/version`).then((r) => r.json());
const ws = new WebSocket(ver.webSocketDebuggerUrl);
await new Promise((res, rej) => { ws.onopen = res; ws.onerror = rej; });

let msgId = 0;
const waiting = new Map();
ws.onmessage = (e) => {
  const m = JSON.parse(e.data);
  if (m.id && waiting.has(m.id)) {
    const { res, rej } = waiting.get(m.id);
    waiting.delete(m.id);
    m.error ? rej(new Error(JSON.stringify(m.error))) : res(m.result);
  }
};
const send = (method, params = {}, sessionId) => new Promise((res, rej) => {
  const id = ++msgId;
  waiting.set(id, { res, rej });
  ws.send(JSON.stringify({ id, method, params, sessionId }));
  setTimeout(() => {
    if (waiting.has(id)) { waiting.delete(id); rej(new Error(`${method} timed out`)); }
  }, 60_000);
});

const { targetId } = await send('Target.createTarget', { url: `${base}/?t=${Date.now()}` });
const { sessionId } = await send('Target.attachToTarget', { targetId, flatten: true });
const s = (m, p) => send(m, p, sessionId);
await s('Page.enable');
await s('Runtime.enable');

const evaluate = async (expr) => {
  const r = await s('Runtime.evaluate', { expression: expr, awaitPromise: true, returnByValue: true });
  if (r.exceptionDetails) throw new Error(r.exceptionDetails.exception?.description ?? 'eval threw');
  return r.result.value;
};

await s('Page.navigate', { url: base });
await new Promise((r) => setTimeout(r, 3500));

await evaluate(`(async () => {
  document.body.innerHTML = '';
  document.body.style.cssText = 'margin:0;background:#0b0e14';
  const { SimulatorSection } = await import('/js/simulator.js');
  const host = document.createElement('div');
  document.body.appendChild(host);
  const app = { locale: 'en', onLocaleChange: () => {}, navigate: () => {} };
  window.__sim = new SimulatorSection({ host, app });
  await window.__sim.open();
  return true;
})()`);

// Hook drawImage BEFORE walking, so every blit of the walked frame is captured.
await evaluate(`(() => {
  window.__calls = [];
  const orig = CanvasRenderingContext2D.prototype.drawImage;
  CanvasRenderingContext2D.prototype.drawImage = function (img, ...a) {
    try {
      const c = this.canvas;
      const r = { args: a.length, nw: img?.naturalWidth ?? null, src: (img?.src || '').split('/').slice(-2).join('/') };
      if (a.length >= 4) { r.dx = a[0]; r.dy = a[1]; r.dw = a[2]; r.dh = a[3]; }
      else if (a.length === 2) { r.dx = a[0]; r.dy = a[1]; r.dw = null; r.dh = null; }
      r.cw = c?.width; r.ch = c?.height;
      window.__calls.push(r);
    } catch (e) { window.__calls.push({ error: String(e) }); }
    return orig.call(this, img, ...a);
  };
  return true;
})()`);

await evaluate(`(async () => {
  const sim = window.__sim;
  const map = sim.maps.find(m => String(m.path).includes('109050001')) ?? sim.maps[0];
  await sim.walk(map);
  return map.path;
})()`).then(p => console.log(`walked ${p}`));

await new Promise((r) => setTimeout(r, 2500));
await evaluate(`(async () => {
  const sim = window.__sim;
  sim.live.keys.right = true;
  await new Promise(r => setTimeout(r, 1500));
  sim.live.keys.right = false;
  await new Promise(r => setTimeout(r, 600));
  return true;
})()`);

const report = await evaluate(`(() => {
  const v = window.__sim.viewer;
  const calls = window.__calls || [];

  // Is the viewer's own canvas the one being drawn to? A draw going to a
  // DETACHED or zero-sized canvas is invisible while reporting "drew".
  const c = v.canvas;
  const attached = !!c && document.body.contains(c);
  const cs = c ? { w: c.width, h: c.height } : null;

  const cur = (window.__sim.live && window.__sim.live.state) || v.ticks[v.ticks.length - 1] || null;

  // Reproduce drawAvatar's own arithmetic here, so the numbers are visible even
  // if the function never ran.
  let expect = null;
  if (cur && v.avatar.part) {
    const part = v.avatar.part;
    const idx = part.index;
    const frame = part.frames[idx];
    const at = v.avatar.placement(cur.x, cur.y, cur.dir ?? 1);
    const ax = part.anchorX ?? frame.originX;
    const ay = part.anchorY ?? frame.originY;
    const view = v.view || null;
    const s = view?.scale ?? 1;
    const ox = view?.minX ?? 0, oy = view?.minY ?? 0;
    const px = (x) => (x - ox) * s, py = (y) => (y - oy) * s;
    expect = {
      index: idx, frameName: frame.name, frameW: frame.width, frameH: frame.height,
      anchorX: ax, anchorY: ay,
      scale: s, minX: ox, minY: oy,
      atX: at.x, atY: at.y, atW: at.w, atH: at.h,
      layerRects: (frame.layers?.length ? frame.layers : [frame]).map(l => ({
        path: l.path, w: l.width, h: l.height, ox: l.originX, oy: l.originY,
        dx: px(at.x + (ax - l.originX)), dy: py(at.y + (ay - l.originY)),
        dw: l.width * s, dh: l.height * s,
      })),
    };
  }

  const avatarBlits = calls.filter(x => x.src && /00002000|body|arm|stand1|walk1/.test(x.src));

  return {
    totalCalls: calls.length,
    avatarBlits: avatarBlits.length,
    sampleAvatarBlits: avatarBlits.slice(0, 6),
    sampleAnyBlits: calls.slice(0, 6),
    canvas: cs, attached,
    viewKeys: v.view ? Object.keys(v.view) : null,
    scale: v.view?.scale ?? null,
    dpr: window.devicePixelRatio,
    hasViewProp: !!v.view,
    cur: cur ? { x: cur.x, y: cur.y, action: cur.action, dir: cur.dir } : null,
    expect,
  };
})()`);

console.log('\n=== drawImage trace ===');
console.log(`total drawImage calls .......... ${report.totalCalls}`);
console.log(`avatar blits ................... ${report.avatarBlits}`);
console.log(`canvas ........................ ${report.canvas?.w}x${report.canvas?.h} attached=${report.attached}`);
console.log(`view.scale .................... ${report.scale}   dpr=${report.dpr}  hasViewProp=${report.hasViewProp}`);
console.log(`cur ........................... ${JSON.stringify(report.cur)}`);
console.log(`view keys ..................... ${JSON.stringify(report.viewKeys)}`);
console.log('\nany blits recorded:');
for (const b of report.sampleAnyBlits) console.log('  ', JSON.stringify(b));
console.log('\navatar blits recorded:');
for (const b of report.sampleAvatarBlits) console.log('  ', JSON.stringify(b));
console.log('\nexpected rects if drawAvatar ran:');
console.log(JSON.stringify(report.expect, null, 1));

const shot = await s('Page.captureScreenshot', { format: 'png' });
await writeFile(OUT, Buffer.from(shot.data, 'base64'));
console.log(`\nscreenshot -> ${OUT}`);

await send('Target.closeTarget', { targetId }).catch(() => {});
ws.close();