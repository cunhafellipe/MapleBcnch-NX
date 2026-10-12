// Drives the real page through the Chrome DevTools Protocol and reports what
// the browser actually did.
//
// Why this exists: every other check in this project has been a syntax check or
// a curl against the API. A syntax check cannot see a ReferenceError -- the bug
// that blanked the simulator passed `node --check` cleanly, and every backend
// test passed with the section dead. Only running the page can see it.
//
// `--dump-dom` returns nothing on this machine, so this talks CDP over a
// websocket instead: it captures console messages and exceptions, evaluates
// expressions in the page, and screenshots.
//
// Usage: node tools/probe/page-check.mjs <baseUrl> [outPng]

import { spawn } from 'node:child_process';
import { mkdtempSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

const base = process.argv[2] ?? 'http://127.0.0.1:5460';
const outPng = process.argv[3] ?? '';
const EDGE = 'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe';
const PORT = 9222 + (process.pid % 500);

const profile = mkdtempSync(join(tmpdir(), 'mb-cdp-'));
const edge = spawn(EDGE, [
  '--headless=new', '--disable-gpu', '--no-sandbox', '--no-first-run',
  '--disable-extensions', '--mute-audio',
  `--remote-debugging-port=${PORT}`,
  `--user-data-dir=${profile}`,
  '--window-size=1600,1000',
  base,
], { windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] });

let edgeErr = '';
edge.stderr.on('data', (d) => { edgeErr += d.toString(); });

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

// Wait for the debugging endpoint to answer.
let targets = null;
for (let i = 0; i < 40 && !targets; i++) {
  await sleep(500);
  try {
    const res = await fetch(`http://127.0.0.1:${PORT}/json/list`);
    targets = await res.json();
  } catch { /* not up yet */ }
}
if (!targets) {
  console.error('  could not reach the DevTools endpoint');
  console.error(edgeErr.split('\n').slice(0, 6).join('\n'));
  edge.kill();
  process.exit(2);
}

const page = targets.find((t) => t.type === 'page') ?? targets[0];
const ws = new WebSocket(page.webSocketDebuggerUrl);

let nextId = 1;
const pending = new Map();
const console_ = [];
const exceptions = [];

ws.addEventListener('message', (ev) => {
  const msg = JSON.parse(ev.data);
  if (msg.id && pending.has(msg.id)) {
    pending.get(msg.id)(msg.result ?? msg.error);
    pending.delete(msg.id);
    return;
  }
  if (msg.method === 'Runtime.consoleAPICalled') {
    const text = (msg.params.args ?? [])
      .map((a) => a.value ?? a.description ?? a.unserializableValue ?? '')
      .join(' ');
    console_.push({ level: msg.params.type, text });
  }
  if (msg.method === 'Runtime.exceptionThrown') {
    const d = msg.params.exceptionDetails;
    exceptions.push(d.exception?.description ?? d.text ?? 'unknown');
  }
  if (msg.method === 'Log.entryAdded') {
    const e = msg.params.entry;
    if (e.level === 'error') exceptions.push(`[${e.source}] ${e.text}`);
  }
});

const send = (method, params = {}) => new Promise((resolve) => {
  const id = nextId++;
  pending.set(id, resolve);
  ws.send(JSON.stringify({ id, method, params }));
});

await new Promise((r) => ws.addEventListener('open', r, { once: true }));
await send('Runtime.enable');
await send('Log.enable');
await send('Page.enable');
await send('Page.navigate', { url: base });

// Let the page load the archive, list 5,366 maps and render.
await sleep(9000);

const evaluate = async (expression) => {
  const r = await send('Runtime.evaluate', {
    expression, returnByValue: true, awaitPromise: true,
  });
  if (r?.exceptionDetails) return { __error: r.exceptionDetails.text ?? 'error' };
  return r?.result?.value;
};

const probe = await evaluate(`(() => {
  const q = (s) => document.querySelector(s);
  const selects = [...document.querySelectorAll('select')];
  const canvas = q('canvas.walkview-canvas');
  const root = q('#app') || document.body;
  const text = (root?.innerText ?? '').slice(0, 400);
  return {
    title: document.title,
    bodyLength: document.body.innerHTML.length,
    textHead: text,
    selectCount: selects.length,
    firstOptions: selects.map(s => [...s.options].slice(0, 3).map(o => o.textContent.trim())),
    canvas: canvas ? { w: canvas.width, h: canvas.height, cssH: canvas.style.height } : null,
    simRoot: !!q('.sim'),
    simChildren: q('.sim')?.children.length ?? 0,
    errors: [...document.querySelectorAll('.sim-error, .err, .toast-error')].map(e => e.textContent.trim()).slice(0, 5),
  };
})()`);

if (outPng) {
  const shot = await send('Page.captureScreenshot', { format: 'png' });
  if (shot?.data) {
    writeFileSync(outPng, Buffer.from(shot.data, 'base64'));
    console.log(`  screenshot -> ${outPng}`);
  }
}

const checks = [];
checks.push(['page loaded (body not empty)', (probe?.bodyLength ?? 0) > 500, `${probe?.bodyLength ?? 0} chars`]);
checks.push(['no uncaught exception', exceptions.length === 0, exceptions[0] ?? '']);
checks.push(['no console error', !console_.some((c) => c.level === 'error'), console_.find((c) => c.level === 'error')?.text ?? '']);
checks.push(['simulator section present', probe?.simRoot === true, '']);
checks.push(['simulator rendered children', (probe?.simChildren ?? 0) > 0, `${probe?.simChildren} children`]);
checks.push(['map select rendered', (probe?.selectCount ?? 0) > 0, `${probe?.selectCount} select(s)`]);
checks.push(['select has options', (probe?.firstOptions?.[0]?.length ?? 0) > 0,
              (probe?.firstOptions?.[0] ?? []).slice(0, 2).join(' | ')]);
checks.push(['no error banner', (probe?.errors?.length ?? 0) === 0, (probe?.errors ?? []).join(' / ')]);

let failed = 0;
for (const [name, pass, detail = ''] of checks) {
  if (!pass) failed++;
  console.log(`  ${pass ? 'PASS' : 'FAIL'}  ${name}${detail ? `  — ${detail}` : ''}`);
}

if (failed) {
  console.log('\n  --- page text (first 400 chars) ---');
  console.log('  ' + String(probe?.textHead ?? '').split('\n').slice(0, 8).join('\n  '));
  if (exceptions.length) {
    console.log('\n  --- exceptions ---');
    for (const e of exceptions.slice(0, 6)) console.log('  ' + String(e).split('\n')[0]);
  }
}

ws.close();
edge.kill();
console.log(failed ? `\n${failed} check(s) failed` : '\npage behaves as expected');
process.exit(failed ? 1 : 0);