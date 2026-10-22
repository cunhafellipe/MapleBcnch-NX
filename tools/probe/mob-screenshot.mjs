// Screenshot the live walker on a map with mobs, so the drawing can be seen
// rather than only counted.
//
//   node mob-screenshot.mjs <baseUrl> <mapPath> <outPng> [cdpPort]
//
// The pixel counts in mob-draw-check.mjs say "ink appeared". This says "the
// thing on the screen is a mob standing on the ground next to the character",
// which is a different claim and the only one a person can check.

import { writeFileSync } from 'node:fs';
import process from 'node:process';

const [, , base, mapPath, outPng, portArg = '9222'] = process.argv;
if (!base || !mapPath || !outPng) {
  console.error('usage: node mob-screenshot.mjs <baseUrl> <mapPath> <outPng> [cdpPort]');
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

// Drive the REAL live path: open a session, step it, hand the frame to the
// viewer exactly as LiveWalk.paint() does, then screenshot that canvas.
const info = await evaluate(`(async () => {
  const { WalkViewer } = await import('/js/walkview.js');
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

  // Frame the picture on the character so a mob is in shot, and zoom in far
  // enough that two bodies are distinguishable.
  view.setZoom(3);
  view.follow(last.x, last.y);
  view.draw();

  await fetch('/api/simulate/session/' + start.sessionId, { method: 'DELETE' }).catch(() => {});

  return {
    mobs: (last.mobs ?? []).length,
    moved: (last.mobs ?? []).filter(m => Math.abs(m.x) > 0).length,
    charX: last.x, charY: last.y,
    frame: last.frame,
    positions: (last.mobs ?? []).slice(0, 6).map(m =>
      ({ id: m.mobId, x: Math.round(m.x), y: Math.round(m.y), fh: m.foothold, dir: m.direction })),
  };
})()`);

console.log(JSON.stringify(info, null, 2));

const shot = await s('Page.captureScreenshot', { format: 'png' });
writeFileSync(outPng, Buffer.from(shot.data, 'base64'));
console.log(`\nwrote ${outPng}`);

await send('Target.closeTarget', { targetId }).catch(() => {});
ws.close();
process.exit(0);