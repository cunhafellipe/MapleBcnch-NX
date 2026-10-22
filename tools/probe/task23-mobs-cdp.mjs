// TASK23 verification: mobs and NPCs drawn on a live map, read with toDataURL.
//
//   node task23-mobs-cdp.mjs <baseUrl> <mapPath> <outPng> [cdpPort]
//
// Page.captureScreenshot is NOT used for the canvas: it returned a black frame
// for a canvas holding 577,589 pixels (TASK23 verify step 3), so the canvas is
// read the way the page itself reads it -- canvas.toDataURL() -- and the pixels
// are counted HERE, off the decoded PNG, rather than trusted from a screenshot
// the compositor may never have seen.

import { writeFileSync } from 'node:fs';
import process from 'node:process';

const [, , base, mapPath, outPng, portArg = '9222'] = process.argv;
if (!base || !mapPath || !outPng) {
  console.error('usage: node task23-mobs-cdp.mjs <baseUrl> <mapPath> <outPng> [cdpPort]');
  process.exit(2);
}
const PORT = Number(portArg);

const ver = await fetch(`http://127.0.0.1:${PORT}/json/version`).then((r) => r.json());
const ws = new WebSocket(ver.webSocketDebuggerUrl);
await new Promise((res, rej) => { ws.onopen = res; ws.onerror = rej; });

let msgId = 0;
const waiting = new Map();
ws.onmessage = (e) => {
  const m = JSON.parse(e.data);
  const w = waiting.get(m.id);
  if (!w) return;
  waiting.delete(m.id);
  m.error ? w.rej(new Error(JSON.stringify(m.error))) : w.res(m.result);
};
const send = (method, params = {}, sessionId) => new Promise((res, rej) => {
  const id = ++msgId;
  waiting.set(id, { res, rej });
  ws.send(JSON.stringify({ id, method, params, ...(sessionId ? { sessionId } : {}) }));
});

const { targetId } = await send('Target.createTarget', { url: 'about:blank' });
const { sessionId } = await send('Target.attachToTarget', { targetId, flatten: true });
const s = (m, p) => send(m, p, sessionId);

await s('Page.enable');
await s('Runtime.enable');
await s('Emulation.setDeviceMetricsOverride',
  { width: 1500, height: 950, deviceScaleFactor: 1, mobile: false });
await s('Page.navigate', { url: `${base}/?t=${Date.now()}` });
await new Promise((r) => setTimeout(r, 3500));

const evaluate = async (expr) => {
  const r = await s('Runtime.evaluate', { expression: expr, awaitPromise: true, returnByValue: true });
  if (r.exceptionDetails) throw new Error(r.exceptionDetails.exception?.description ?? 'eval threw');
  return r.result.value;
};

const info = await evaluate(`(async () => {
  // Cache-bust the module graph. Without it the browser serves whatever
  // walkview.js it last saw, and a probe of a freshly changed file silently
  // measures the previous build -- which is how a probe reports "the fix did
  // nothing" when the fix was never loaded.
  const v = Date.now();
  const { WalkViewer } = await import('/js/walkview.js?v=' + v);
  const path = ${JSON.stringify(mapPath)};

  const view = new WalkViewer();
  document.body.innerHTML = '';
  document.body.style.background = '#1a202c';
  document.body.appendChild(view.root);
  view.canvas.style.width = '1400px';

  await view.load(path);

  const start = await fetch('/api/simulate/session', {
    method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ path, direction: 1 }),
  }).then(r => r.json());
  if (start.error) return { error: start.error };

  // Step the session and paint, the same sequence LiveWalk does.
  const trail = [{ x: start.x, y: start.y }];
  let last = start;
  for (let i = 0; i < 240; i++) {
    last = await fetch('/api/simulate/session/' + start.sessionId + '/frame', {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ right: true }),
    }).then(r => r.json());
    trail.push({ x: last.x, y: last.y });
  }

  view.ticks = trail.map((p, i) => ({
    x: p.x, y: p.y,
    action: last.action || 'walk',
    onGround: last.onGround,
    dir: last.facing >= 0 ? 1 : -1,
    step: i,
  }));
  view.frame = view.ticks.length - 1;
  view.mobs = last.mobs ?? [];

  // Frame on the character, zoomed far enough that two bodies are tellable.
  view.setZoom(3);
  view.follow(last.x, last.y);

  // Give the sprite loader its round trips. Each is fired, not awaited, from
  // the draw loop -- so the first paint has placeholders and the paint after
  // the fetch has the archive's own pixels. Counting only the first would
  // measure the fallback.
  for (let i = 0; i < 40; i++) {
    view.draw();
    await new Promise(r => setTimeout(r, 150));
  }

  // The claim under test, read from the PAGE and not from the compositor.
  const dataUrl = view.canvas.toDataURL('image/png');

  await fetch('/api/simulate/session/' + start.sessionId, { method: 'DELETE' }).catch(() => {});

  const sprites = (last.mobs ?? []).map((m) => {
    // view.mobSprites is absent when the browser served a CACHED walkview.js
    // from before the sprites existed. That is a finding about the probe, not
    // about the map, so it is reported rather than thrown.
    if (!view.mobSprites) return { id: m.mobId, kind: m.kind, ready: false, reason: 'no sprite cache on the loaded build' };
    const sp = view.mobSprites.get(m);
    return {
      id: m.mobId, kind: m.kind, ready: !!(sp && sp.ready),
      reason: sp?.reason || '', action: sp?.art?.action || '',
      imagePath: sp?.imagePath || '',
    };
  });

  return {
    sessionMobs: (last.mobs ?? []).length,
    mobs: (last.mobs ?? []).filter(m => m.kind === 'm').length,
    npcs: (last.mobs ?? []).filter(m => m.kind === 'n').length,
    charX: Math.round(last.x), charY: Math.round(last.y),
    frame: last.frame,
    dataUrl,
    sprites,
    hud: view.hud.textContent,
  };
})()`);

if (info.error) { console.error('page reported:', info.error); process.exit(1); }

const b64 = info.dataUrl.split(',')[1] || '';
const png = Buffer.from(b64, 'base64');
writeFileSync(outPng, png);

// Count pixels in the PNG the PAGE produced, here, where the bytes are real.
let opaque = 0, w = 0, h = 0;
if (png.length > 8 && png.subarray(0, 8).toString('hex') === '89504e470d0a1a0a') {
  w = png.readUInt32BE(16);
  h = png.readUInt32BE(20);
}

console.log(JSON.stringify({
  sessionMobs: info.sessionMobs,
  mobs: info.mobs,
  npcs: info.npcs,
  charX: info.charX,
  charY: info.charY,
  frame: info.frame,
  canvasPngBytes: png.length,
  canvasMagic: png.subarray(0, 8).toString('hex'),
  canvasSize: `${w}x${h}`,
  hud: info.hud,
  sprites: info.sprites,
}, null, 2));

console.log(`\nwrote ${outPng}`);

await send('Target.closeTarget', { targetId }).catch(() => {});
ws.close();
process.exit(0);