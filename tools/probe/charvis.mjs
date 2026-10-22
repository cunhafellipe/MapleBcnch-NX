// TASK 9B -- "Map art renders, the character does not."
//
// The spec says: instrument, do not guess. This asks its six questions IN ORDER
// against the real page over CDP and prints the answers verbatim.
//
// It also does something the first pass got wrong: it screenshots the CANVAS,
// not the page. The first run screenshotted the editor panel while the
// simulator was mounted off-screen in a hidden host, so the picture proved
// nothing either way. Here the simulator is mounted fullscreen so what is
// captured is what is drawn.
//
//   node charvis.mjs <baseUrl> <cdpPort>

import process from 'node:process';
import { writeFile } from 'node:fs/promises';

const [, , base, portArg = '9222'] = process.argv;
const PORT = Number(portArg);
const OUT = 'tools/probe/out/charvis.png';

const ver = await fetch(`http://127.0.0.1:${PORT}/json/version`).then((r) => r.json());
const ws = new WebSocket(ver.webSocketDebuggerUrl);
await new Promise((res, rej) => { ws.onopen = res; ws.onerror = rej; });

let msgId = 0;
const waiting = new Map();
const network = [];
const sizes = new Map();

ws.onmessage = (e) => {
  const m = JSON.parse(e.data);
  if (m.method === 'Network.responseReceived') {
    network.push(m.params);
  } else if (m.method === 'Network.loadingFinished') {
    sizes.set(m.params.requestId, m.params.encodedDataLength);
  }
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
await s('Network.enable');

const evaluate = async (expr) => {
  const r = await s('Runtime.evaluate', { expression: expr, awaitPromise: true, returnByValue: true });
  if (r.exceptionDetails) throw new Error(r.exceptionDetails.exception?.description ?? 'eval threw');
  return r.result.value;
};

// Navigate to the app origin first, then replace the whole document with the
// simulator alone. Mounting into the app's own layout left the section below the
// fold, which is what made the first screenshot useless.
await s('Page.navigate', { url: base });
await new Promise((r) => setTimeout(r, 3500));

const mounted = await evaluate(`(async () => {
  document.body.innerHTML = '';
  document.body.style.cssText = 'margin:0;background:#0b0e14';
  const { SimulatorSection } = await import('/js/simulator.js');
  const host = document.createElement('div');
  host.id = 'probe-sim';
  document.body.appendChild(host);
  const app = { locale: 'en', onLocaleChange: () => {}, navigate: () => {} };
  const sec = new SimulatorSection({ host, app });
  window.__sim = sec;
  await sec.open();
  const canvas = sec.viewer.canvas;
  if (canvas) {
    canvas.style.cssText += ';display:block;margin:0 auto;border:1px solid #2a3140';
    canvas.scrollIntoView({ block: 'center' });
  }
  return { maps: sec.maps.length, hasCanvas: !!canvas, w: canvas?.width, h: canvas?.height };
})()`);
console.log(`simulator mounted fullscreen: ${mounted.maps} maps, canvas=${mounted.hasCanvas} ${mounted.w}x${mounted.h}`);

// Record every /api call the avatar path makes BEFORE walking: question 2 asks
// whether /api/animation was called at all, and with what path.
await evaluate(`(() => {
  window.__api = [];
  const orig = window.fetch;
  window.fetch = async (...a) => {
    const url = String(a[0]);
    if (/\\/api\\/(animation|canvas|children|files)/.test(url)) {
      window.__api.push({ url: url.split('?')[0], q: url.split('?')[1] || '' });
    }
    return orig(...a);
  };
  return true;
})()`);

// Walk the map the spec names. Falling back to maps[0] is what the first run
// did, and Map0 is an empty starting map -- it renders no art and proves nothing
// about the real case.
const walked = await evaluate(`(async () => {
  const sim = window.__sim;
  const want = '109050001';   // the map TASK 9B names
  const map = sim.maps.find(m => String(m.path).includes(want))
    ?? sim.maps.find(m => String(m.id) === want)
    ?? sim.maps.find(m => /910050001/.test(JSON.stringify(m)))
    ?? null;
  if (!map) return { found: false, sample: sim.maps.slice(0, 3).map(m => JSON.stringify(m)) };
  await sim.walk(map);
  return { found: true, map: map.path, name: map.name };
})()`);
if (!walked.found) {
  console.log(`map ${'109050001'} not in picker; samples:`, walked.sample);
} else {
  console.log(`walked ${walked.map}  (${walked.name})`);
}

// Let the live body run so the draw path actually executes.
await new Promise((r) => setTimeout(r, 2000));
await evaluate(`(async () => {
  const sim = window.__sim;
  sim.live.keys.right = true;
  await new Promise(r => setTimeout(r, 1500));
  sim.live.keys.right = false;
  await new Promise(r => setTimeout(r, 500));
  return true;
})()`);

// ---- The spec's six questions, answered in order. ----------------------
const probe = await evaluate(`(() => {
  const v = window.__sim.viewer;

  // Q1
  if (!v.avatar) return { q1: false };
  const part = v.avatar.part;
  const apiCalls = (window.__api || []).filter(c => c.url.includes('/api/animation'));

  // Q3
  const bitmaps = [];
  for (const [path, img] of v.avatar.bitmaps) {
    bitmaps.push({
      path,
      state: img === null ? 'NULL(decode failed)' : 'Image',
      nw: img ? img.naturalWidth : null,
      nh: img ? img.naturalHeight : null,
    });
  }

  // Q4: does the clock actually advance across ticks?
  const seen = [];
  const savedIdx = part?.index, savedTime = part?.time;
  for (let i = 0; i < 8; i++) {
    seen.push(v.avatar.step({ action: 'walk', speed: 100, onGround: true, x: 0, y: 0, dir: 1 }));
  }
  if (part) { part.index = savedIdx; part.time = savedTime; }

  // Q6
  const cur = (window.__sim.live && window.__sim.live.state) || v.ticks[v.ticks.length - 1] || null;
  const scale = v.scale ?? v.zoom ?? 1;
  let placement = null, pw = null, ph = null;
  if (v.avatar.part) {
    placement = v.avatar.placement(cur?.x ?? 0, cur?.y ?? 0, cur?.dir ?? 1);
    pw = placement.w * scale; ph = placement.h * scale;
  }
  const frame = part ? part.frames[part.index] : null;
  const layers = frame?.layers?.length ? frame.layers : (frame ? [frame] : []);

  return {
    q1: true,
    q2: {
      hasPart: !!part, state: part?.state ?? null, index: part?.index ?? null,
      frames: part?.frames?.length ?? 0, anchorX: part?.anchorX ?? null, anchorY: part?.anchorY ?? null,
      reason: v.avatar.reason || '', source: v.avatar.source || '',
      missingLayers: v.avatar.missingLayers ?? [],
      animationCalls: apiCalls.map(c => decodeURIComponent(c.q || '')),
      childrenCalls: (window.__api || []).filter(c => c.url.includes('/api/children')).map(c => decodeURIComponent(c.q || '')),
    },
    q3: { count: bitmaps.length, decoded: bitmaps.filter(b => b.nw > 0).length, entries: bitmaps },
    q4: { samples: seen, advancing: new Set(seen).size > 1 },
    q5: { avatarFrame: seen[seen.length - 1], layers: layers.length },
    q6: { scale, frameW: frame?.width ?? null, frameH: frame?.height ?? null,
          wPx: pw, hPx: ph, subPixel: pw !== null ? (pw < 0.5 || ph < 0.5) : null, placement },
    ready: !!v.avatar.ready,
    hud: v.hud?.textContent ?? '',
  };
})()`);

console.log('\n=== the six questions ===');
console.log(`Q1 viewer.avatar present ........ ${probe.q1}`);
if (probe.q2) {
  const q = probe.q2;
  console.log(`Q2 avatar.part present .......... ${q.hasPart}` +
    (q.hasPart ? `  state=${q.state} index=${q.index} frames=${q.frames} anchor=(${q.anchorX},${q.anchorY})`
               : `  reason="${q.reason}"`));
  console.log(`   /api/animation calls ......... ${q.animationCalls.length}`);
  for (const c of q.animationCalls) console.log(`      ${c}`);
  console.log(`   /api/children calls .......... ${q.childrenCalls.length}`);
  for (const c of q.childrenCalls.slice(0, 6)) console.log(`      ${c}`);
  console.log(`   source="${q.source}"  missingLayers=${q.missingLayers.length}`);
  console.log(`Q3 bitmaps .................... ${probe.q3.count} entries, ${probe.q3.decoded} decoded (naturalWidth>0)`);
  for (const e of probe.q3.entries.slice(0, 8)) console.log(`      ${e.state} ${e.nw}x${e.nh}  ${e.path}`);
  console.log(`Q4 stepAvatar advancing ....... ${probe.q4.advancing}  samples=${JSON.stringify(probe.q4.samples)}`);
  console.log(`Q5 avatarFrame ................. ${probe.q5.avatarFrame}  layers=${probe.q5.layers}`);
  console.log(`Q6 placement ................... w=${probe.q6.wPx}px h=${probe.q6.hPx}px subPixel=${probe.q6.subPixel} scale=${probe.q6.scale}`);
}
console.log(`\nready=${probe.ready}`);
console.log(`HUD: ${probe.hud}`);

// ---- What the BROWSER actually received for the avatar layers. ----------
const avatarReqs = network.filter(r => /00002000\.img\/(stand1|walk1)/.test(String(r.response.url)));
console.log(`\n=== avatar canvas requests the browser made: ${avatarReqs.length} ===`);
const perLayer = new Map();
for (const r of avatarReqs) {
  const p = decodeURIComponent(String(r.response.url).split('path=')[1] ?? '');
  perLayer.set(p, sizes.get(r.requestId) ?? r.response.encodedDataLength ?? 0);
}
for (const [p, n] of [...perLayer].slice(0, 12)) console.log(`  ${p}  ${n} bytes`);

// Opaque pixels, measured in the browser on the decoded bitmaps.
const opaque = await evaluate(`(() => {
  const out = [];
  for (const [path, img] of window.__sim.viewer.avatar.bitmaps) {
    if (!img || !img.naturalWidth) { out.push({ path, opaque: null }); continue; }
    const c = document.createElement('canvas');
    c.width = img.naturalWidth; c.height = img.naturalHeight;
    const g = c.getContext('2d');
    g.drawImage(img, 0, 0);
    const d = g.getImageData(0, 0, c.width, c.height).data;
    let n = 0;
    for (let i = 3; i < d.length; i += 4) if (d[i] > 0) n++;
    out.push({ path, w: img.naturalWidth, h: img.naturalHeight, opaque: n, total: c.width * c.height });
  }
  return out;
})()`);
console.log('\n=== opaque pixels per avatar layer ===');
for (const o of opaque.slice(0, 12)) {
  console.log(`  ${o.path}  ${o.w}x${o.h}  opaque=${o.opaque}/${o.total}`);
}

// ---- Feet vs foothold line, in WORLD units. ------------------------------
const feet = await evaluate(`(() => {
  const v = window.__sim.viewer;
  const cur = (window.__sim.live && window.__sim.live.state) || v.ticks[v.ticks.length - 1] || null;
  if (!cur || !v.avatar.part) return { error: 'no cur or no part' };
  const part = v.avatar.part;
  const frame = part.frames[part.index];
  const at = v.avatar.placement(cur.x, cur.y, cur.dir ?? 1);
  return {
    curX: cur.x, curY: cur.y, onGround: cur.onGround, action: cur.action,
    frameW: frame.width, frameH: frame.height, anchorY: part.anchorY,
    boxTop: at.y, boxBottom: at.y + at.h,
    footWorldY: at.y + at.h, deltaFromCurY: (at.y + at.h) - cur.y,
  };
})()`);
console.log('\n=== feet vs foothold line (world units; +Y is down) ===');
console.log(JSON.stringify(feet, null, 1));

// ---- What is ACTUALLY on the canvas, pixel by pixel. ---------------------
// The screenshot is the proof, but this says what the canvas itself holds,
// which is what separates "drew nothing" from "drew offscreen".
const pixels = await evaluate(`(() => {
  const v = window.__sim.viewer;
  const c = v.canvas;
  if (!c) return { error: 'no canvas' };
  const g = c.getContext('2d');
  const d = g.getImageData(0, 0, c.width, c.height).data;
  let nonBlank = 0;
  const seen = new Set();
  for (let i = 0; i < d.length; i += 4) {
    const a = d[i + 3];
    if (a > 0) { nonBlank++; seen.add((d[i] >> 4) + ',' + (d[i+1] >> 4) + ',' + (d[i+2] >> 4)); }
  }
  // The avatar's own footprint: count opaque pixels inside its placed rect.
  const cur = (window.__sim.live && window.__sim.live.state) || v.ticks[v.ticks.length - 1] || null;
  let inAvatar = null, rect = null;
  if (cur && v.avatar.part && v.scale) {
    const at = v.avatar.placement(cur.x, cur.y, cur.dir ?? 1);
    const s = v.scale;
    const x0 = Math.max(0, Math.round((at.x) * s)), y0 = Math.max(0, Math.round((at.y) * s));
    const w = Math.min(c.width - x0, Math.round(at.w * s)), h = Math.min(c.height - y0, Math.round(at.h * s));
    rect = { x: x0, y: y0, w, h };
    if (w > 0 && h > 0) {
      const sub = g.getImageData(x0, y0, w, h).data;
      inAvatar = 0;
      for (let i = 3; i < sub.length; i += 4) if (sub[i] > 0) inAvatar++;
    }
  }
  return {
    canvasW: c.width, canvasH: c.height,
    nonBlankPixels: nonBlank, distinctColors: seen.size,
    avatarRect: rect, opaqueInAvatarRect: inAvatar,
  };
})()`);
console.log('\n=== what is on the canvas ===');
console.log(JSON.stringify(pixels, null, 1));

const shot = await s('Page.captureScreenshot', { format: 'png' });
await writeFile(OUT, Buffer.from(shot.data, 'base64'));
console.log(`\nscreenshot -> ${OUT}`);

await send('Target.closeTarget', { targetId }).catch(() => {});
ws.close();