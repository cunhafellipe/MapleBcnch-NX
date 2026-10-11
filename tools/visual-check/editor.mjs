// Visual check of the map editor: open it, pick a map, and screenshot the canvas.
//
//   node editor.mjs <baseUrl> <mapPath> <outPng>
//
// Driven through CDP without puppeteer. Every step reports what the DOM actually
// holds afterwards, because the failure this whole exercise exists to catch is
// a 200 response with nothing rendered on screen.

import { writeFileSync } from 'node:fs';

const [, , base, mapPath, out] = process.argv;
if (!base || !mapPath || !out) {
  console.error('usage: node editor.mjs <baseUrl> <mapPath> <outPng>');
  process.exit(2);
}

async function findPage() {
  for (let i = 0; i < 60; i++) {
    try {
      const list = await (await fetch('http://127.0.0.1:9222/json/list')).json();
      const p = list.find((t) => t.type === 'page' && t.webSocketDebuggerUrl);
      if (p) return p;
    } catch { /* not up yet */ }
    await new Promise((r) => setTimeout(r, 500));
  }
  throw new Error('no page on :9222');
}

const ws = new WebSocket((await findPage()).webSocketDebuggerUrl);
let nextId = 1;
const pending = new Map();
const logs = [];

ws.addEventListener('message', (ev) => {
  const m = JSON.parse(ev.data);
  if (m.id && pending.has(m.id)) {
    const { resolve, reject } = pending.get(m.id);
    pending.delete(m.id);
    m.error ? reject(new Error(JSON.stringify(m.error))) : resolve(m.result);
  } else if (m.method === 'Runtime.consoleAPICalled' && m.params.type === 'error') {
    logs.push('[console.error] ' + m.params.args.map((a) => a.value ?? a.description).join(' '));
  } else if (m.method === 'Runtime.exceptionThrown') {
    logs.push('[EXCEPTION] ' + m.params.exceptionDetails.text);
  }
});

const send = (method, params = {}) =>
  new Promise((resolve, reject) => {
    const id = nextId++;
    pending.set(id, { resolve, reject });
    ws.send(JSON.stringify({ id, method, params }));
  });

await new Promise((res, rej) => {
  ws.addEventListener('open', res, { once: true });
  ws.addEventListener('error', rej, { once: true });
});
await send('Page.enable');
await send('Runtime.enable');
await send('Emulation.setDeviceMetricsOverride', {
  width: 1680, height: 1050, deviceScaleFactor: 1, mobile: false,
});

const evaluate = async (expression) => {
  const r = await send('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true });
  if (r.exceptionDetails) return 'THREW: ' + r.exceptionDetails.text;
  return r.result.value;
};

const wait = (ms) => new Promise((r) => setTimeout(r, ms));

await send('Page.navigate', { url: base + '/' });
await wait(13000);

// Click the tab by its visible label rather than a brittle class name.
console.log('tab:', await evaluate(`(() => {
  const t = [...document.querySelectorAll('button, [role=tab], a')]
    .find(e => /編輯器|Editor/.test(e.textContent || ''));
  if (!t) return 'EDITOR TAB NOT FOUND';
  t.click(); return 'clicked: ' + t.textContent.trim();
})()`));
await wait(2500);

// The editor lands on its Mob section; the map editor is a sibling item.
console.log('section:', await evaluate(`(() => {
  // Pick the DEEPEST match: the rail item, not the app shell that contains it.
  const hits = [...document.querySelectorAll('*')].filter(e => {
    const t = (e.textContent || '').trim();
    return t === '地圖編輯器' || t === 'Map editor';
  });
  if (!hits.length) return 'MAP SECTION NOT FOUND';
  const el = hits[hits.length - 1];
  el.click();
  return 'clicked <' + el.tagName + '> depth=' + hits.length;
})()`));
await wait(4000);

// Go through the section's own openMap so the page adopts the document the
// same way a click on the picker row would; a raw fetch leaves the UI blank
// even when the API returned a full map.
console.log('openMap:', await evaluate(`(async () => {
  const app = window.app || window.App || globalThis.app;
  const me = app?.mapedit;
  if (!me) return 'NO MAPEDIT SECTION (keys: ' + Object.keys(window).filter(k => /app|section/i.test(k)) + ')';
  await me.openMap(${JSON.stringify(mapPath)});
  return 'doc=' + (me.doc ? 'loaded' : 'null') +
    ' dirty=' + me.unsavedEdits + ' layers=' + (me.doc?.layers?.length ?? 'n/a');
})()`));
await wait(9000);

console.log('probe:', await evaluate(`(() => {
  const cs = [...document.querySelectorAll('canvas')].map(c => ({
    w: c.width, h: c.height, vis: c.offsetWidth > 0,
    cls: c.className || c.id || '(no id)',
  }));
  const painted = [...document.querySelectorAll('canvas')].map(c => {
    try {
      const g = c.getContext('2d');
      const d = g.getImageData(0, 0, Math.min(c.width, 400), Math.min(c.height, 400)).data;
      let n = 0; for (let i = 3; i < d.length; i += 4) if (d[i] > 0) n++;
      return Math.round((n / (d.length / 4)) * 100);
    } catch (e) { return 'err:' + e.message; }
  });
  return JSON.stringify({ canvases: cs, paintedPct: painted,
    imgs: document.querySelectorAll('img').length,
    text: document.body.innerText.replace(/\\s+/g,' ').slice(0, 300) });
})()`));

const shot = await send('Page.captureScreenshot', { format: 'png' });
writeFileSync(out, Buffer.from(shot.data, 'base64'));
console.log('saved', out);
if (logs.length) console.log(logs.slice(-15).join('\n'));
ws.close();
process.exit(0);