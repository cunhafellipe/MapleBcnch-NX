// Palette check: the tile/object/background grids must show pictures, and the
// strings must follow the language switch.
//
//   node palette.mjs <baseUrl> <outPrefix>

import { writeFileSync } from 'node:fs';

const [, , base, prefix] = process.argv;

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

ws.addEventListener('message', (ev) => {
  const m = JSON.parse(ev.data);
  if (m.id && pending.has(m.id)) {
    const { resolve, reject } = pending.get(m.id);
    pending.delete(m.id);
    m.error ? reject(new Error(JSON.stringify(m.error))) : resolve(m.result);
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
  return r.exceptionDetails ? 'THREW: ' + r.exceptionDetails.text : r.result.value;
};
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
const shoot = async (name) => {
  const s = await send('Page.captureScreenshot', { format: 'png' });
  writeFileSync(prefix + name, Buffer.from(s.data, 'base64'));
  console.log('saved', prefix + name);
};

await send('Page.navigate', { url: base + '/' });
await wait(13000);

// Language-agnostic: the rail items carry a stable hook, not a translated label.
console.log('section:', await evaluate(`(() => {
  const hit = document.querySelector('[data-i18n="Editors"]');
  if (!hit) return 'EDITORS TAB NOT FOUND';
  hit.click();
  return 'clicked Editors tab';
})()`));
await wait(6000);

// Open a map so the palette panel has a document to paint into.
await evaluate(`(async () => {
  const me = (window.app || globalThis.app)?.mapedit;
  if (!me) return 'no section';
  await me.openMap('f1/Map/Map0/000000001.img');
  return 'opened';
})()`);
await wait(9000);

// The palette is a drawer that slides over the canvas; open it explicitly.
console.log('palette:', await evaluate(`(() => {
  const me = (window.app || globalThis.app)?.mapedit;
  if (!me) return 'no section';
  me.togglePanel('pal', true);
  return 'ui.pal=' + me.ui.pal;
})()`));
await wait(12000);

// Ask the API directly what the palette contains, then judge the DOM separately.
// A 200 with empty grids is exactly the bug this project has shipped before.
console.log('api:', await evaluate(`(async () => {
  const out = {};
  for (const kind of ['Tile','Obj','Back']) {
    try {
      const r = await fetch('/api/mapedit/palette/sets?kind=' + kind);
      const j = await r.json();
      out[kind] = Array.isArray(j) ? 'sets=' + j.length : JSON.stringify(j).slice(0,120);
    } catch (e) { out[kind] = 'ERR ' + e.message; }
  }
  return JSON.stringify(out);
})()`));

// Count painted thumbnails rather than trusting element counts.
console.log('grids:', await evaluate(`(() => {
  const r = {};
  for (const sel of ['.me-pal-grid img', '.me-pal-item img', '.me-thumb img', 'aside img', 'img']) {
    const imgs = [...document.querySelectorAll(sel)];
    if (!imgs.length) continue;
    const ok = imgs.filter(i => i.complete && i.naturalWidth > 0).length;
    r[sel] = ok + '/' + imgs.length + ' loaded';
  }
  return JSON.stringify(r);
})()`));

await shoot('-palette.png');

// Language switch: the same grid must relabel.
console.log('lang:', await evaluate(`(() => {
  const el = [...document.querySelectorAll('*')]
    .find(e => /^(EN|繁中|中文|ENGLISH)$/.test((e.textContent || '').trim()) && e.children.length === 0);
  if (!el) return 'LANG BUTTON NOT FOUND';
  el.click(); return 'clicked ' + el.textContent.trim();
})()`));
await wait(2500);
console.log('after:', await evaluate(`document.body.innerText.replace(/\\s+/g,' ').slice(0,180)`));
await shoot('-lang-en.png');

ws.close();
process.exit(0);