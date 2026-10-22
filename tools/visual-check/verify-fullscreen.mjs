// Full-screen simulator check: does it take over the window, is the exit
// button wired, and is the map picker an overlay rather than a column?
//
//   node verify-fullscreen.mjs <cdpPort> <serverPort> [outDir]

import { writeFileSync, mkdirSync } from 'node:fs';
import { join } from 'node:path';

const CDP = process.argv[2] ?? '9341';
const PORT = process.argv[3] ?? '5511';
const OUT = process.argv[4] ?? 'shots/fullscreen';
mkdirSync(OUT, { recursive: true });

const pages = await (await fetch(`http://127.0.0.1:${CDP}/json/list`)).json();
const page = pages.find((t) => t.type === 'page' && t.url.includes(PORT)) ?? pages.find((t) => t.type === 'page');
if (!page) { console.error(`no page for :${PORT}`); process.exit(1); }

const ws = new WebSocket(page.webSocketDebuggerUrl);
await new Promise((r) => ws.addEventListener('open', r, { once: true }));
let id = 0;
const pending = new Map();
ws.addEventListener('message', (e) => {
  const d = JSON.parse(e.data);
  if (d.id && pending.has(d.id)) { pending.get(d.id)(d); pending.delete(d.id); }
});
const cdp = (m, p = {}) => new Promise((res) => { const i = ++id; pending.set(i, res); ws.send(JSON.stringify({ id: i, method: m, params: p })); });
const ev = async (expression) => {
  const r = await cdp('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true });
  if (r.result?.exceptionDetails) return { error: r.result.exceptionDetails.text };
  return r.result?.result?.value;
};
const shot = async (name) => {
  const s = await cdp('Page.captureScreenshot', { format: 'png' });
  const f = join(OUT, `${name}.png`);
  writeFileSync(f, Buffer.from(s.result.data, 'base64'));
  return f;
};

await cdp('Page.enable');
await cdp('Runtime.enable');
await new Promise((r) => setTimeout(r, 2500));

// Editor chrome that must be GONE in full-screen mode.
const CHROME = [
  '.file-tree, #file-tree, [class*="file-tree"]',
  '.nav, nav, aside, [class*="sidebar"], [class*="left-rail"]',
  '[class*="inspector"], [class*="detail-panel"]',
].join(', ');

const before = await ev(`(() => ({
  active: window.app?.sections?.active,
  visibleCanvas: [...document.querySelectorAll('canvas')].filter(c => c.offsetParent !== null)
    .map(c => ({ id: c.id || c.className, w: c.width, h: c.height })),
}))()`);
console.log('before:', JSON.stringify(before));

// Open the simulator the way a user does.
const opened = await ev(`(async () => {
  const app = window.app;
  await app.sections.open('simulator');
  const sim = app.simulator;
  await sim.open();
  const map = sim.maps?.find(m => String(m.id) === '109050001') ?? sim.maps?.[0];
  if (!map) return { error: 'no maps' };
  await sim.walk(map);
  return { active: app.sections.active, canvasRect: (() => {
    const c = document.querySelector('.walkview-canvas');
    if (!c) return null;
    const r = c.getBoundingClientRect();
    return { x: Math.round(r.x), y: Math.round(r.y), w: Math.round(r.width), h: Math.round(r.height) };
  })() };
})()`);
console.log('opened:', JSON.stringify(opened));
await new Promise((r) => setTimeout(r, 1500));

const fs1 = await ev(`(() => {
  const vis = (sel) => [...document.querySelectorAll(sel)]
    .filter(e => e.offsetParent !== null && e.getBoundingClientRect().width > 0)
    .map(e => e.className || e.tagName);
  const c = document.querySelector('.walkview-canvas');
  const r = c?.getBoundingClientRect();
  return {
    editorChromeVisible: vis('${CHROME}'),
    canvas: r ? { x: Math.round(r.x), y: Math.round(r.y), w: Math.round(r.width), h: Math.round(r.height) } : null,
    viewport: { w: innerWidth, h: innerHeight },
    canvasCoversViewport: r ? (r.width >= innerWidth * 0.9 && r.height >= innerHeight * 0.75) : false,
    exitButtons: [...document.querySelectorAll('button')]
      .filter(b => /exit|close|leave|back|離開|退出/i.test(b.textContent + ' ' + (b.className || '') + ' ' + (b.id || '')))
      .map(b => ({ text: b.textContent.trim().slice(0, 24), id: b.id, cls: b.className })),
  };
})()`);
console.log('fullscreen:', JSON.stringify(fs1, null, 1));
console.log('shot:', await shot('1-fullscreen'));

// Map picker as an overlay, not a permanent column.
const picker = await ev(`(() => {
  const host = document.querySelector('.walkview-canvas')?.closest('[data-sim], .sim-stage, section, .section') ?? document;
  const col = host.querySelector('.map-list, [class*="maplist"], [class*="map-list"], .picker, [class*="picker"]');
  if (!col) return { error: 'no picker element found' };
  const r = col.getBoundingClientRect();
  const c = document.querySelector('.walkview-canvas').getBoundingClientRect();
  const overlap = !(r.right < c.left || r.left > c.right || r.bottom < c.top || r.top > c.bottom);
  return { picker: { x: Math.round(r.x), w: Math.round(r.width) }, canvas: { x: Math.round(c.x), w: Math.round(c.width) },
           overlapsCanvas: overlap, hiddenByDefault: getComputedStyle(col).display === 'none' };
})()`);
console.log('picker:', JSON.stringify(picker));

// Exit must actually return to the editor.
const exited = await ev(`(async () => {
  const btn = [...document.querySelectorAll('button')]
    .find(b => /exit|leave|離開|退出/i.test(b.textContent + ' ' + (b.className || '') + ' ' + (b.id || '')));
  if (!btn) return { error: 'no exit button' };
  btn.click();
  await new Promise(r => setTimeout(r, 1200));
  return { active: window.app?.sections?.active,
           visibleCanvas: [...document.querySelectorAll('canvas')].filter(c => c.offsetParent !== null).length };
})()`);
console.log('after exit:', JSON.stringify(exited));
console.log('shot:', await shot('2-after-exit'));

ws.close();