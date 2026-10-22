// Full-screen simulator check: does it take over the window, is the exit
// button wired, and is the map picker an overlay rather than a column?
//
//   node verify-fullscreen.mjs <cdpPort> <serverPort> [outDir]

import { writeFileSync, mkdirSync } from 'node:fs';
import { join } from 'node:path';

const CDP = process.argv[2] ?? '9341';
const PORT = process.argv[3] ?? '5510';
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


await cdp('Page.enable'); await cdp('Runtime.enable');
await ev(`(async()=>{const a=window.app;await a.sections.open('simulator');await a.simulator.open();
  const m=a.simulator.maps?.find(x=>String(x.id)==='109050001');await a.simulator.walk(m);})()`);
await new Promise(r=>setTimeout(r,2000));
const url = await ev(`window.app.simulator.viewer.canvas.toDataURL('image/png')`);
writeFileSync('shots/fs2/canvas-direct.png', Buffer.from(url.split(',')[1],'base64'));
console.log('canvas PNG bytes:', Buffer.from(url.split(',')[1],'base64').length);
ws.close();
