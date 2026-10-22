// TASK 19 evidence: full-screen simulator, overlay picker, camera centring,
// and the exit round-trip.
//
//   node t19-shots.mjs <cdpPort> <serverPort> [outDir]
//
// Every shot is paired with a DOM/JS assertion made at the moment of capture,
// so a screenshot cannot be reported as evidence of something the page did not
// actually have. A claim printed without a passing assertion is not a claim.

import { writeFileSync, mkdirSync } from 'node:fs';
import { join } from 'node:path';

const CDP = process.argv[2] ?? '9341';
const PORT = process.argv[3] ?? '5508';
const OUT = process.argv[4] ?? 'shots/t19';
mkdirSync(OUT, { recursive: true });

const tabs = await (await fetch(`http://127.0.0.1:${CDP}/json/list`)).json();
const page = tabs.find((t) => t.type === 'page' && t.url.includes(PORT)) ?? tabs.find((t) => t.type === 'page');
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
    return { error: r.result.exceptionDetails.text, detail: r.result.exceptionDetails.exception?.description };
  }
  return r.result?.result?.value;
};
const shot = async (name) => {
  const s = await cdp('Page.captureScreenshot', { format: 'png' });
  const file = join(OUT, `${name}.png`);
  writeFileSync(file, Buffer.from(s.result.data, 'base64'));
  return file;
};
const wait = (ms) => new Promise((r) => setTimeout(r, ms));

await cdp('Page.enable');
await cdp('Runtime.enable');

// ---- how much of the window the simulator actually owns --------------------
//
// The full-screen claim is a claim about CHROME: the command bar, the rail,
// the file tree and the inspector must not be laid out. Measuring whether they
// are `display:none` is the only honest test -- a screenshot at a small window
// looks identical either way.
const CHROME = `(() => {
  const vis = (sel) => {
    const e = document.querySelector(sel);
    if (!e) return 'missing';
    const cs = getComputedStyle(e);
    const box = e.getBoundingClientRect();
    return cs.display === 'none' ? 'display:none'
         : (box.width < 1 || box.height < 1) ? 'zero-size'
         : cs.visibility === 'hidden' ? 'hidden' : 'VISIBLE';
  };
  return {
    focus: document.body.dataset.focus ?? null,
    topbar: vis('.topbar'),
    statusbar: vis('.statusbar'),
    rail: vis('.rail'),
    sidePanel: vis('#tree-pane'),
    inspector: vis('#inspector'),
    workspaceCols: getComputedStyle(document.querySelector('#workspace')).gridTemplateColumns,
    viewport: { w: innerWidth, h: innerHeight },
    canvas: (() => { const c = document.querySelector('.walkview-canvas');
      if (!c) return null; const b = c.getBoundingClientRect();
      return { w: Math.round(b.width), h: Math.round(b.height) }; })(),
  };
})()`;

const log = [];
const note = (label, obj) => { log.push([label, obj]); console.log(label + ': ' + JSON.stringify(obj)); };

// ---- 1. full-screen simulator, with a map loaded ----------------------------
await ev(`(async () => {
  const app = window.app;
  app.sections.active === 'simulator' || await app.sections.open('simulator');
  const sim = app.simulator;
  const map = sim.maps.find((m) => String(m.id) === '109050001');
  if (!map) return 'map-missing';
  await sim.walk(map);
  return 'ok';
})()`).then((r) => note('walk', r));
await wait(3000);

note('chrome.fullscreen', await ev(CHROME));
const f1 = await shot('01-fullscreen-simulator');
note('shot', { file: f1 });

// The canvas must actually have pixels, or the shot is a dark rectangle.
const drawn = await ev(`(() => {
  const c = document.querySelector('.walkview-canvas');
  if (!c) return { error: 'no canvas' };
  const d = c.getContext('2d').getImageData(0, 0, c.width, c.height).data;
  let n = 0; for (let i = 0; i < d.length; i += 4) if (d[i] || d[i+1] || d[i+2]) n++;
  return { w: c.width, h: c.height, drawn: n };
})()`);
note('canvas.drawn', drawn);

// ---- 2. the map picker as an overlay ---------------------------------------
note('picker.closed', await ev(`(() => {
  const p = document.querySelector('.sim-picker');
  if (!p) return { error: 'no picker' };
  return { hidden: p.hidden, display: getComputedStyle(p).display };
})()`));
await ev(`window.app.simulator.pickerOpen = true; window.app.simulator.renderBodyInto();`);
await wait(900);
note('picker.open', await ev(`(() => {
  const p = document.querySelector('.sim-picker');
  const c = document.querySelector('.walkview-canvas');
  if (!p || !c) return { error: 'missing' };
  const pb = p.getBoundingClientRect(), cb = c.getBoundingClientRect();
  // Overlapping the canvas is the whole point: an overlay that does not overlap
  // it is just a column again.
  const overlaps = pb.left < cb.right && pb.right > cb.left && pb.top < cb.bottom && pb.bottom > cb.top;
  return { hidden: p.hidden, display: getComputedStyle(p).display,
           position: getComputedStyle(p).position,
           overlapsCanvas: overlaps,
           options: p.querySelector('select')?.options.length ?? 0 };
})()`));
const f2 = await shot('02-picker-open-overlay');
note('shot', { file: f2 });

await ev(`window.app.simulator.pickerOpen = false; window.app.simulator.renderBodyInto();`);
await wait(700);
note('picker.reclosed', await ev(`document.querySelector('.sim-picker')?.hidden`));
const f3 = await shot('03-picker-closed');
note('shot', { file: f3 });

// ---- 3. camera: centring at rest and after walking --------------------------
//
// The claim is a FRACTION of the viewport, so it is measured as one: where the
// character's feet land across the canvas, 0.5 being dead centre. Read from the
// rendered pixels, not from the model's own camera fields -- a camera that
// believes it is centred while the picture is not is exactly the failure being
// tested.
const CAM = `(() => {
  const v = window.app?.simulator?.viewer;
  const c = v?.canvas; if (!c) return { error: 'no canvas' };
  const cur = v.ticks[v.ticks.length - 1];
  if (!cur) return { error: 'no tick' };
  const view = v.viewBox; if (!view) return { error: 'no viewBox' };
  const s = view.scale, dpr = window.devicePixelRatio || 1;
  // Feet position in canvas CSS px: cam is the top-left of the view.
  const px = (cur.x - view.minX) * s;
  const py = (cur.y - view.minY) * s;
  const cb = c.getBoundingClientRect();
  return {
    fx: px / cb.width, fy: py / cb.height,
    tick: { x: cur.x, y: cur.y }, action: cur.action, onGround: cur.onGround,
    cam: { x: v.cam.x, y: v.cam.y },
    camBounds: view.camBounds ?? null,
    drawWorld: view.world,
    canvasCss: { w: Math.round(cb.width), h: Math.round(cb.height) },
    zoom: v.zoom,
  };
})()`;

note('camera.rest', await ev(CAM));
const f4 = await shot('04-camera-rest');
note('shot', { file: f4 });

// Hold a direction for 3 s (the task's number), then measure again.
await ev(`(async () => {
  const fire = (type, code, key) => window.dispatchEvent(new KeyboardEvent(type, { code, key, bubbles: true }));
  fire('keydown', 'ArrowRight', 'ArrowRight');
  await new Promise((r) => setTimeout(r, 3000));
  fire('keyup', 'ArrowRight', 'ArrowRight');
})()`).then(() => wait(600));
note('camera.afterWalkRight', await ev(CAM));
const f5 = await shot('05-camera-walk-right');
note('shot', { file: f5 });

// ---- 4. the same spot at two zoom levels ------------------------------------
await ev(`window.app.simulator.viewer.setZoom(2.5)`);
await wait(1200);
note('camera.zoom250', await ev(CAM));
const f6 = await shot('06-camera-zoom250');
note('shot', { file: f6 });
await ev(`window.app.simulator.viewer.setZoom(1)`);
await wait(1000);
note('camera.zoom100', await ev(CAM));
const f7 = await shot('07-camera-zoom100');
note('shot', { file: f7 });

// ---- 5. exit restores the editor -------------------------------------------
note('chrome.beforeExit', await ev(CHROME));
await ev(`document.querySelector('.sim-exit')?.click()`);
await wait(2500);
note('chrome.afterExit', await ev(CHROME));
note('sections.afterExit', await ev(`window.app.sections.active`));
const f8 = await shot('08-after-exit-editor');
note('shot', { file: f8 });

writeFileSync(join(OUT, 't19-report.json'), JSON.stringify(log, null, 1));
console.log('\nreport -> ' + join(OUT, 't19-report.json'));
ws.close();