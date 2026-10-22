// Smoke the REAL simulator page: load a map, let the live body walk, screenshot
// what is actually on screen and read the HUD line that says which figure is
// being drawn.
//
//   node avatar-live.mjs <baseUrl> <cdpPort>

import process from 'node:process';

const [, , base, portArg = '9222'] = process.argv;
const PORT = Number(portArg);
const OUT = 'tools/probe/out/avatar-live.png';

const ver = await fetch(`http://127.0.0.1:${PORT}/json/version`).then((r) => r.json());
const ws = new WebSocket(ver.webSocketDebuggerUrl);
await new Promise((res, rej) => { ws.onopen = res; ws.onerror = rej; });

let msgId = 0;
const waiting = new Map();
ws.onmessage = (e) => {
  const m = JSON.parse(e.data);
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
await s('Page.navigate', { url: base });
await new Promise((r) => setTimeout(r, 4000));

const evaluate = async (expr) => {
  const r = await s('Runtime.evaluate', { expression: expr, awaitPromise: true, returnByValue: true });
  if (r.exceptionDetails) throw new Error(r.exceptionDetails.exception?.description ?? 'eval threw');
  return r.result.value;
};

// The archive must be open before the page can walk a map.
await evaluate(`fetch('/api/files/open', {
  method: 'POST', headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({ path: 'C:/MUWORK/GAME/MAPLESOTRY/wz工具/MapleBcnch-NX-audit/.simdata/Map.nx' })
}).then(r => r.ok)`);

// Mount the simulator section the way the app does, then pick a map and walk.
const mounted = await evaluate(`(async () => {
  const { SimulatorSection } = await import('/js/simulator.js');
  const host = document.createElement('div');
  host.id = 'probe-sim';
  document.body.appendChild(host);
  const app = { locale: 'en', onLocaleChange: () => {}, navigate: () => {} };
  const sec = new SimulatorSection({ host, app });
  window.__sim = sec;
  await sec.open();
  return { maps: sec.maps.length, total: sec.mapsTotal ?? null };
})()`);
console.log(`  section opened: ${mounted.maps} maps available`);

const walked = await evaluate(`(async () => {
  const sim = window.__sim;
  const map = sim.maps.find(m => String(m.path).includes('910020202')) ?? sim.maps[0];
  await sim.walk(map);
  return {
    map: map.path,
    viewError: sim.viewError || '',
    replayError: sim.replayError || '',
    ticks: sim.viewer.ticks.length,
    avatarReady: !!sim.viewer.avatar?.ready,
    avatarReason: sim.viewer.avatar?.reason || '',
    hud: sim.viewer.hud.textContent,
  };
})()`);
console.log(`  walked ${walked.map}`);
console.log(`  ticks=${walked.ticks}  viewError="${walked.viewError}"  replayError="${walked.replayError}"`);
console.log(`  avatarReady=${walked.avatarReady}  reason="${walked.avatarReason}"`);

// Let the live body run, then read the HUD again.
await new Promise((r) => setTimeout(r, 2500));
const live = await evaluate(`(async () => {
  const sim = window.__sim;
  sim.live.keys.right = true;
  await new Promise(r => setTimeout(r, 1200));
  sim.live.keys.right = false;
  await new Promise(r => setTimeout(r, 400));
  return {
    running: sim.live.running,
    session: sim.live.sessionId || null,
    status: sim.live.status(),
    x: sim.live.state?.x, action: sim.live.state?.action,
    hud: sim.viewer.hud.textContent,
    avatarState: sim.viewer.avatarState,
  };
})()`);
console.log(`  live: running=${live.running} x=${live.x} action=${live.action} avatarState=${live.avatarState}`);
console.log(`  live status: ${live.status}`);
console.log(`  HUD: ${live.hud}`);

const shot = await s('Page.captureScreenshot', { format: 'png' });
const { writeFile } = await import('node:fs/promises');
await writeFile(OUT, Buffer.from(shot.data, 'base64'));
console.log(`  screenshot -> ${OUT}`);

await send('Target.closeTarget', { targetId }).catch(() => {});
ws.close();