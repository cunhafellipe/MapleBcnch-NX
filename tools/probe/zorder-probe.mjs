// TASK 9B, step 3: drawAvatar IS reached and drawImage IS called with correct,
// on-canvas rectangles -- yet nothing is visible. So the pixels must be drawn and
// then painted over. That is a Z-order question, and the reference's own comment
// in draw() says the figure is spliced into the scene at charZ and everything
// with z > charZ is drawn AFTER it.
//
// This records the paint order of one frame and asks, in pixels:
//   1. what is drawn after the avatar, and does any of it overlap its rect?
//   2. what are the avatar rect's pixels immediately after the avatar blit?
//   3. what are the SAME pixels at the end of the frame?
// If (2) has the character and (3) does not, the sprite is being covered.
//
// A/B test: the same frame is drawn with the avatar forced LAST. If the
// character appears, the break is its Z, not its pixels.
//
//   node zorder-probe.mjs <baseUrl> <cdpPort>

import process from 'node:process';
import { writeFile } from 'node:fs/promises';

const [, , base, portArg = '9222'] = process.argv;
const PORT = Number(portArg);
const OUT = 'tools/probe/out/zorder-probe.png';

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

// Record EVERY blit in paint order, and snapshot the avatar rect right after
// the avatar's own blits and again at the end of the frame.
await evaluate(`(() => {
  window.__frame = null;
  window.__hook = () => {
    const orig = CanvasRenderingContext2D.prototype.drawImage;
    CanvasRenderingContext2D.prototype.drawImage = function (img, ...a) {
      const f = window.__frame;
      if (f) {
        const isAvatar = /00002000\\.img/.test(img?.src || '');
        f.calls.push({
          isAvatar,
          src: (img?.src || '').split('path=')[1]?.split('&')[0] || '',
          dx: a[0], dy: a[1], dw: a[2], dh: a[3],
          cw: this.canvas?.width, ch: this.canvas?.height,
        });
        if (isAvatar && !f.snapAfterAvatar) f.snapAfterAvatar = this._snap();
      }
      return orig.call(this, img, ...a);
    };
    CanvasRenderingContext2D.prototype._snap = function () {
      const c = this.canvas, g = this;
      const rect = window.__avatarRect;
      if (!rect) return null;
      const x = Math.max(0, Math.round(rect.x)), y = Math.max(0, Math.round(rect.y));
      const w = Math.min(c.width - x, Math.round(rect.w)), h = Math.min(c.height - y, Math.round(rect.h));
      if (w <= 0 || h <= 0) return { x, y, w, h, opaque: 0, total: w * h };
      const d = g.getImageData(x, y, w, h).data;
      let n = 0;
      for (let i = 3; i < d.length; i += 4) if (d[i] > 0) n++;
      return { x, y, w, h, opaque: n, total: w * h };
    };
    return true;
  };
  window.__hook();
  return true;
})()`);

// Compute the avatar rect and wrap draw() to snapshot before/after.
await evaluate(`(() => {
  const v = window.__sim.viewer;
  const origDraw = v.draw.bind(v);
  v.draw = function () {
    const cur = (window.__sim.live && window.__sim.live.state) || v.ticks[v.ticks.length - 1] || null;
    let rect = null;
    if (cur && v.avatar.part) {
      const part = v.avatar.part, frame = part.frames[part.index];
      const at = v.avatar.placement(cur.x, cur.y, cur.dir ?? 1);
      rect = { x: at.x, y: at.y, w: at.w, h: at.h };
    }
    window.__avatarRect = rect;
    const f = { calls: [], snapAfterAvatar: null, snapBefore: null, snapEnd: null, rect };
    window.__frame = f;
    origDraw();
    const g = v.canvas.getContext('2d');
    f.snapEnd = g._snap();
    window.__frame = null;
    window.__lastFrame = f;
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

const r1 = await evaluate(`(() => {
  const f = window.__lastFrame;
  if (!f) return { error: 'no frame captured' };
  const rect = f.rect;
  const afterIdx = f.calls.findIndex(c => c.isAvatar);
  const after = f.calls.slice(afterIdx).filter(c => !c.isAvatar);
  const overlap = after.filter(c => rect && c.dx != null
    && c.dx < rect.x + rect.w && c.dx + c.dw > rect.x
    && c.dy < rect.y + rect.h && c.dy + c.dh > rect.y);
  return {
    rect,
    totalCalls: f.calls.length,
    avatarAt: afterIdx,
    drawnAfterAvatar: after.length,
    overlappingAfterAvatar: overlap.length,
    overlapSample: overlap.slice(0, 8).map(c => ({ src: decodeURIComponent(c.src), dx: c.dx, dy: c.dy, dw: c.dw, dh: c.dh })),
    orderSample: f.calls.slice(Math.max(0, afterIdx - 3), afterIdx + 6)
      .map(c => (c.isAvatar ? 'AVATAR ' : 'scene  ') + decodeURIComponent(c.src).slice(0, 60)
        + ' @' + Math.round(c.dx) + ',' + Math.round(c.dy)
        + ' ' + Math.round(c.dw) + 'x' + Math.round(c.dh)),
    snapAfterAvatar: f.snapAfterAvatar,
    snapEnd: f.snapEnd,
  };
})()`);

console.log('\n=== paint order around the avatar ===');
console.log(JSON.stringify(r1, null, 1));

// ---- A/B: force the avatar to draw LAST and look again. ------------------
// Purely a diagnostic: it does not touch the source, it wraps drawAvatar so it
// is called once more at the end of the frame. If the sprite appears, the
// pixels were always fine and the Z splice is what buried them.
const r2 = await evaluate(`(() => {
  const v = window.__sim.viewer;
  const origAvatar = v.drawAvatar.bind(v);
  v.drawAvatar = function () {};           // suppress the in-order draw
  const origDraw = v.draw.bind(v);
  v.draw = function () {
    origDraw();
    const cur = (window.__sim.live && window.__sim.live.state) || v.ticks[v.ticks.length - 1] || null;
    if (!cur || !v.avatar.part) return;
    const ctx = v.canvas.getContext('2d');
    const part = v.avatar.part, frame = part.frames[part.index];
    const at = v.avatar.placement(cur.x, cur.y, cur.dir ?? 1);
    const ax = part.anchorX ?? frame.originX, ay = part.anchorY ?? frame.originY;
    const layers = frame.layers?.length ? frame.layers : [frame];
    for (const layer of layers) {
      const img = v.avatar.bitmaps.get(layer.path);
      if (!img) continue;
      ctx.drawImage(img, at.x + (ax - layer.originX), at.y + (ay - layer.originY), layer.width, layer.height);
    }
  };
  v.draw();
  const cur = (window.__sim.live && window.__sim.live.state) || v.ticks[v.ticks.length - 1];
  const part = v.avatar.part, frame = part.frames[part.index];
  const at = v.avatar.placement(cur.x, cur.y, cur.dir ?? 1);
  const g = v.canvas.getContext('2d');
  const d = g.getImageData(Math.max(0, Math.round(at.x)), Math.max(0, Math.round(at.y)),
                           Math.round(at.w), Math.round(at.h)).data;
  let n = 0;
  for (let i = 3; i < d.length; i += 4) if (d[i] > 0) n++;
  return { forcedLastOpaque: n, rect: { x: at.x, y: at.y, w: at.w, h: at.h } };
})()`);
console.log('\n=== A/B: avatar forced to draw LAST ===');
console.log(JSON.stringify(r2, null, 1));

const shot = await s('Page.captureScreenshot', { format: 'png' });
await writeFile(OUT, Buffer.from(shot.data, 'base64'));
console.log(`\nscreenshot -> ${OUT}`);

await send('Target.closeTarget', { targetId }).catch(() => {});
ws.close();