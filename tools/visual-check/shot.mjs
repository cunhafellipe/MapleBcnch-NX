// Screenshot the running editor through Chrome's DevTools Protocol.
// No puppeteer: talk to the CDP WebSocket directly over the built-in fetch.
//
//   node shot.mjs <url> <outPng> [waitMs] [clickSelector]
//
// The point of this script is the visual check the journal kept asking for:
// an API returning 200 is not the same as a tile being visible on screen.

import { writeFileSync } from 'node:fs';

const [, , url, out, waitMsArg, clickSel] = process.argv;
const waitMs = Number(waitMsArg ?? 12000);
// CDP_PORT lets several checks run against browsers on different ports without
// editing the script. Defaults to 9222, which is what the README documents.
const PORT = process.env.CDP_PORT ?? '9222';
if (!url || !out) {
  console.error('usage: node shot.mjs <url> <outPng> [waitMs] [clickSelector]');
  process.exit(2);
}

// Chrome must already be listening; this only discovers the page target.
async function findPage() {
  for (let i = 0; i < 60; i++) {
    try {
      const list = await (await fetch(`http://127.0.0.1:${PORT}/json/list`)).json();
      const page = list.find((t) => t.type === 'page' && t.webSocketDebuggerUrl);
      if (page) return page;
    } catch { /* browser not up yet */ }
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
await new Promise((r) => setTimeout(r, waitMs));

if (clickSel) {
  // Clicking is done in page script rather than Input.dispatchMouseEvent so the
  // selector does not have to be resolved to viewport coordinates by hand.
  const r = await send('Runtime.evaluate', {
    expression: `(() => { const el = document.querySelector(${JSON.stringify(clickSel)});
      if (!el) return 'NOT FOUND: ' + ${JSON.stringify(clickSel)};
      el.click(); return 'clicked ' + el.tagName + '.' + el.className; })()`,
    returnByValue: true,
  });
  console.log('click:', r.result.value);
  await new Promise((r2) => setTimeout(r2, 9000));
}

// A blank canvas and a broken palette both render as "no error". Ask the page
// what it actually drew, so the screenshot is checked against the DOM too.
const probe = await send('Runtime.evaluate', {
  expression: `(() => {
    const canvases = [...document.querySelectorAll('canvas')]
      .map(c => ({ w: c.width, h: c.height, cls: c.className || c.id }));
    const imgs = [...document.querySelectorAll('img')].length;
    const body = document.body.innerText.replace(/\\s+/g, ' ').trim().slice(0, 400);
    return JSON.stringify({ canvases, imgs, text: body });
  })()`,
  returnByValue: true,
});
console.log('probe:', probe.result.value);

const shot = await send('Page.captureScreenshot', { format: 'png' });
writeFileSync(out, Buffer.from(shot.data, 'base64'));
console.log('saved', out);

if (consoleLines.length) {
  console.log('--- page console ---');
  consoleLines.slice(-25).forEach((l) => console.log(l));
}

ws.close();
process.exit(0);