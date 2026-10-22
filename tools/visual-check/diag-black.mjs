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
  const m=a.simulator.maps?.find(x=>String(x.id)==='109050001')??a.simulator.maps?.[0];await a.simulator.walk(m);})()`);
await new Promise(r=>setTimeout(r,2000));
console.log(JSON.stringify(await ev("(() => {\n  const app = window.app, sim = app?.simulator;\n  const cands = [...document.querySelectorAll('canvas')].map(c => ({\n    id: c.id, cls: c.className, w: c.width, h: c.height,\n    visible: c.offsetParent !== null,\n    rect: (() => { const r = c.getBoundingClientRect(); return {x:Math.round(r.x),y:Math.round(r.y),w:Math.round(r.width),h:Math.round(r.height)}; })(),\n  }));\n  const v = sim?.viewer;\n  let pix = null;\n  if (v?.canvas) {\n    const g = v.canvas.getContext('2d');\n    const d = g.getImageData(0,0,v.canvas.width,v.canvas.height).data;\n    let n=0; for (let i=0;i<d.length;i+=4) if (d[i]||d[i+1]||d[i+2]) n++;\n    pix = { w:v.canvas.width, h:v.canvas.height, drawn:n };\n  }\n  return {\n    activeSection: app?.sections?.active,\n    simKeys: sim ? Object.keys(sim).filter(k=>/view|live|canvas|host|root/.test(k)) : null,\n    viewerHasCanvas: !!v?.canvas,\n    viewerPath: v?.path ?? null,\n    avatar: v ? { hasPart: !!v.avatar?.part, bitmaps: v.avatar?.bitmaps?.size ?? 0 } : null,\n    allCanvases: cands,\n    pixels: pix,\n  };\n})()"),null,1));
ws.close();
