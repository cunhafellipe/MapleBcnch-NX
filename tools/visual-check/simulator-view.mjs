// Open the simulator section on the running app and report what is on screen.
//
// The journal's last line was "I never saw it". This exists so that claim can
// never be true again: it opens the section, waits for the real map list to
// load, and prints DOM facts next to the screenshot.
//
//   CDP_PORT=9333 node simulator-view.mjs http://127.0.0.1:5480 shots/sim.png

import { writeFileSync } from 'node:fs';

const [, , url, out] = process.argv;
const PORT = process.env.CDP_PORT ?? '9222';
if (!url || !out) {
  console.error('usage: node simulator-view.mjs <url> <outPng>');
  process.exit(2);
}

async function findPage() {
  for (let i = 0; i < 60; i++) {
    try {
      const list = await (await fetch(`http://127.0.0.1:${PORT}/json/list`)).json();
      const page = list.find((t) => t.type === 'page' && t.webSocketDebuggerUrl);
      if (page) return page;
    } catch { /* not up yet */ }
    await new Promise((r) => setTimeout(r, 500));
  }
  throw new Error(`no debuggable page on :${PORT}`);
}

const page = await findPage();
const ws = new WebSocket(page.webSocketDebuggerUrl);
let nextId = 1;
const pending = new Map();
const consoleLines = [];

ws.addEventListener('message', (ev) => {
  const msg = JSON.parse(ev.data);
  if (msg.id && pending.has(msg.id)) {
    const { resolve, reject } = pending.get(msg.id);
    pending.delete(msg.id);
    msg.error ? reject(new Error(JSON.stringify(msg.error))) : resolve(msg.result);
  } else if (msg.method === 'Runtime.consoleAPICalled') {
    consoleLines.push(
      `[${msg.params.type}] ` +
        msg.params.args.map((a) => a.value ?? a.description ?? a.type).join(' '),
    );
  } else if (msg.method === 'Runtime.exceptionThrown') {
    const d = msg.params.exceptionDetails;
    consoleLines.push(`[EXCEPTION] ${d.text} ${d.exception?.description ?? ''}`);
  }
});

const send = (method, params = {}) =>
  new Promise((resolve, reject) => {
    const id = nextId++;
    pending.set(id, { resolve, reject });
    ws.send(JSON.stringify({ id, method, params }));
  });

const evaluate = async (expression) => {
  const r = await send('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true });
  if (r.exceptionDetails) {
    throw new Error(r.exceptionDetails.exception?.description ?? r.exceptionDetails.text);
  }
  return r.result.value;
};

await new Promise((resolve, reject) => {
  ws.addEventListener('open', resolve, { once: true });
  ws.addEventListener('error', reject, { once: true });
});

await send('Page.enable');
await send('Runtime.enable');
await send('Emulation.setDeviceMetricsOverride', {
  width: 1680, height: 1050, deviceScaleFactor: 1, mobile: false,
});

await send('Page.navigate', { url });
await new Promise((r) => setTimeout(r, 10000));

// Two clicks, in the order a user makes them. #mode-sections is the Editors
// toggle in the toolbar; only once the section nav exists can Simulator be
// picked from it. Both ids are asserted rather than assumed.
const opened = await evaluate(`(() => {
  const modeBtn = document.querySelector('#mode-sections');
  if (!modeBtn) return 'NO #mode-sections';
  modeBtn.click();
  return 'clicked #mode-sections';
})()`);
console.log('mode:', opened);
await new Promise((r) => setTimeout(r, 4000));

const sections = await evaluate(`JSON.stringify(
  [...document.querySelectorAll('.sec-list button')].map(b => b.textContent.trim()))`);
console.log('sections:', sections);

const picked = await evaluate(`(() => {
  // The UI is zh-Hant by default, so the nav reads 模擬器; the English label is
  // the fallback. Match either, otherwise this silently screenshots the wrong
  // section -- which is exactly the failure the journal complains about.
  const btn = [...document.querySelectorAll('.sec-list button')]
    .find(b => /simulator|模擬器/i.test(b.textContent));
  if (!btn) return 'NO SIMULATOR BUTTON';
  btn.click();
  return 'clicked: ' + btn.textContent.trim();
})()`);
console.log('open:', picked);
await new Promise((r) => setTimeout(r, 18000));

// Pick a map and walk it. There is no "Walk" button: selecting a map calls
// walk() directly, so changing the select IS the action. Stopping at the empty
// state would prove the picker loads but not that a character is drawn, which
// is the thing the journal never saw.
// The picker caps at 400 options and says "search to narrow" -- but it only
// searches the first 2,000 maps it fetched, so the search box is the only way
// to reach the other 3,366. Type into it exactly as a user would.
// Which map to walk. Overridable so the same script can prove the fix on more
// than one map -- a renderer that works on one map proves nothing.
const MAP_ID = process.env.SIM_MAP ?? '109050001';

const searched = await evaluate(`(() => {
  const box = document.querySelector('.sim-search');
  if (!box) return 'NO SEARCH BOX';
  box.value = ${JSON.stringify(MAP_ID)};
  box.dispatchEvent(new Event('input', { bubbles: true }));
  return 'typed ' + ${JSON.stringify(MAP_ID)};
})()`);
console.log('search:', searched);
await new Promise((r) => setTimeout(r, 2500));

const picked2 = await evaluate(`(() => {
  // After searching there is exactly one option, so a length threshold picks
  // nothing. Identify the map select by holding real map paths instead.
  const s = [...document.querySelectorAll('select')]
    .find(x => [...x.options].some(o => /\\/Map\\/Map\\d+\\/\\d+\\.img$/.test(o.value)));
  if (!s) return 'NO MAP SELECT';
  const opt = [...s.options].find(o => o.textContent.trim().startsWith(${JSON.stringify(MAP_ID)}));
  if (!opt) return 'NOT FOUND after search; options=' +
    JSON.stringify([...s.options].map(o => o.textContent.trim()).slice(0, 6));
  s.value = opt.value;
  s.dispatchEvent(new Event('change', { bubbles: true }));
  return 'selected: ' + opt.textContent.trim() + ' (value=' + opt.value + ')';
})()`);
console.log('map:', picked2);
await new Promise((r) => setTimeout(r, 30000));

// Canvas pixels, not just presence. But paintedPct alone lies: the view fills
// the background first, so a map with zero art scores 100%. What separates
// "art drawn" from "empty background" is how many DISTINCT colours are on it --
// a blank canvas is one or two, real map art is hundreds. Both are reported so
// a regression cannot hide behind a full-looking percentage.
const painted = await evaluate(`(() => {
  const c = [...document.querySelectorAll('canvas')].map(cv => {
    try {
      const ctx = cv.getContext('2d');
      const d = ctx.getImageData(0, 0, cv.width, cv.height).data;
      let opaque = 0, total = d.length / 4;
      const colours = new Set();
      for (let i = 0; i < d.length; i += 4) {
        if (d[i+3] > 0) {
          opaque++;
          colours.add((d[i]>>3<<16) | (d[i+1]>>3<<8) | (d[i+2]>>3));
        }
      }
      return {
        w: cv.width, h: cv.height,
        paintedPct: +(100*opaque/Math.max(total,1)).toFixed(2),
        distinctColours: colours.size,
        visible: !!(cv.offsetWidth || cv.offsetHeight),
      };
    } catch (e) { return { w: cv.width, h: cv.height, err: String(e).slice(0,80) }; }
  });
  return JSON.stringify(c);
})()`);
console.log('canvas:', painted);

const state2 = await evaluate(`(() => {
  const root = document.querySelector('.sim-root') || document.body;
  return JSON.stringify({
    text: root.innerText.replace(/\\s+/g,' ').trim().slice(0, 900),
  });
})()`);
console.log('after-walk:', state2);

const shot = await send('Page.captureScreenshot', { format: 'png' });
writeFileSync(out, Buffer.from(shot.data, 'base64'));
console.log('saved', out);

if (consoleLines.length) {
  console.log('--- page console ---');
  consoleLines.slice(-30).forEach((l) => console.log(l));
}

ws.close();
process.exit(0);