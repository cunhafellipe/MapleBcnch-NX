// Independent visual verification: does the character actually appear?
//   node verify-char.mjs <cdpPort>
// Drives the real page, forces the simulator onto a real map with real art,
// and reads canvas pixels directly instead of trusting a screenshot.

// Node 22+ ships a global WebSocket; the repo has no `ws` dependency and
// this script must not add one just to drive a check.
if (typeof WebSocket === 'undefined') throw new Error('needs node 22+ global WebSocket');

const PORT = process.argv[2] ?? '9340';
const list = await (await fetch(`http://127.0.0.1:${PORT}/json/list`)).json();
const page = list.find((t) => t.type === 'page' && t.url.includes('5505'))
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


await cdp('Page.enable'); await cdp('Runtime.enable');
await ev(`(async()=>{const s=window.app.simulator;await s.open();
  const m=s.maps?.find(x=>String(x.id)==='109050001')??s.maps?.[0];await s.walk(m);})()`);
await new Promise(r=>setTimeout(r,1200));
const res = await ev("(async () => {\n  const sim = window.app.simulator, v = sim.viewer;\n  const c = v.canvas;\n  if (!c) return { error: 'still no canvas' };\n  const g = c.getContext('2d');\n  const d = g.getImageData(0,0,c.width,c.height).data;\n  let nz=0; for (let i=0;i<d.length;i+=4) if (d[i]||d[i+1]||d[i+2]) nz++;\n  const cur = v.ticks?.[v.ticks.length-1] ?? null;\n  const s = v.scale ?? v.s ?? 1;\n  const sx = cur ? (cur.x - (v.camX??0)) * s : null;\n  const sy = cur ? (cur.y - (v.camY??0)) * s : null;\n  // \u62bd\u89d2\u8272\u5468\u570d 40x60 \u7684\u50cf\u7d20\u7d71\u8a08\n  let patch = null;\n  if (sx!=null && sy!=null) {\n    const x0=Math.max(0,Math.round(sx)-20), y0=Math.max(0,Math.round(sy)-60);\n    const w=40,h=60;\n    let pnz=0; const reg = g.getImageData(x0,y0,w,h).data;\n    for (let i=0;i<reg.length;i+=4) if (reg[i]||reg[i+1]||reg[i+2]) pnz++;\n    patch = { x0,y0,w,h, nonBlank: pnz };\n  }\n  return {\n    canvas: [c.width,c.height],\n    nonBlankTotal: nz,\n    curTick: cur && { x: cur.x, y: cur.y, action: cur.action, frame: cur.frame },\n    scale: s, cam: [v.camX, v.camY], screenPos: [sx,sy],\n    patch,\n    avatar: { hasPart: !!v.avatar?.part, frames: v.avatar?.part?.frames?.length,\n              bitmaps: v.avatar?.bitmaps?.size,\n              natural: [...(v.avatar?.bitmaps?.values()??[])].map(i=>i.naturalWidth+'x'+i.naturalHeight).slice(0,4) },\n    avatarFrame: v.avatarFrame,\n    placement: v.avatar?.placement?.(v.ticks?.[v.ticks.length-1]) ?? null,\n  };\n})()");
console.log(JSON.stringify(res,null,1));
ws.close();
