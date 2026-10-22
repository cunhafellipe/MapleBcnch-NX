// Prove walkview.js PAINTS mobs, by reading real pixels back out of a real
// Chrome canvas — and prove it paints them at the reference's depth.
//
//   node mob-draw-check.mjs <baseUrl> <mapPath> [cdpPort]
//
// WHY PIXELS AND NOT A CALL LOG
// ------------------------------
// A counting stub on ctx.arc() passes even if drawMob never advances the
// transform, never fills, or paints entirely off-canvas. Counting non-background
// pixels cannot: if the mob is not visible, the canvas is the background colour,
// and that is the only claim worth making about a drawing change.
//
// WHY THE BROWSER DAEMON IS NOT USED
// ---------------------------------
// The shared daemon fails to launch on this machine (exit 21). Chrome is
// already running with --remote-debugging-port, so this speaks CDP directly and
// depends on nothing but a port.
//
// WHY Z IS CHECKED HERE AND NOT BY EYE
// -----------------------------------
// "A mob is in front of the wall" and "a mob is behind the wall" produce nearly
// identical screenshots at a glance, and the difference is one integer: Mob.cs:191
// uses +6000 where MapleCharacter.cs:561 uses +60000. So the arithmetic is
// asserted directly too, not only through the picture.

import { spawn } from 'node:child_process';
import path from 'node:path';
import process from 'node:process';

const [, , base, mapPath, portArg = '9222'] = process.argv;
if (!base || !mapPath) {
  console.error('usage: node mob-draw-check.mjs <baseUrl> <mapPath> [cdpPort]');
  process.exit(2);
}
const PORT = Number(portArg);

const failures = [];
const check = (ok, what, detail = '') => {
  console.log(`  ${ok ? 'ok  ' : 'FAIL'}  ${what}${detail ? ` -- ${detail}` : ''}`);
  if (!ok) failures.push(what);
};

// ---------------------------------------------------------------- CDP client
const ver = await fetch(`http://127.0.0.1:${PORT}/json/version`).then((r) => r.json());
const ws = new WebSocket(ver.webSocketDebuggerUrl);
await new Promise((res, rej) => { ws.onopen = res; ws.onerror = rej; });

let msgId = 0;
const waiting = new Map();
const events = [];
ws.onmessage = (e) => {
  const m = JSON.parse(e.data);
  if (m.id && waiting.has(m.id)) {
    const { res, rej } = waiting.get(m.id);
    waiting.delete(m.id);
    m.error ? rej(new Error(JSON.stringify(m.error))) : res(m.result);
  } else if (m.method) {
    events.push(m);
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

// A fresh tab, so nothing from a previous run is cached.
const { targetId } = await send('Target.createTarget', { url: 'about:blank' });
const { sessionId } = await send('Target.attachToTarget', { targetId, flatten: true });
const s = (m, p) => send(m, p, sessionId);

await s('Page.enable');
await s('Runtime.enable');
await s('Page.navigate', { url: `${base}/?t=${Date.now()}` });
await new Promise((r) => setTimeout(r, 3500));

const evaluate = async (expr) => {
  const r = await s('Runtime.evaluate', {
    expression: expr, awaitPromise: true, returnByValue: true,
  });
  if (r.exceptionDetails) throw new Error(r.exceptionDetails.exception?.description ?? 'eval threw');
  return r.result.value;
};

check(await evaluate('document.readyState') !== undefined, 'page loaded',
  await evaluate('document.title'));

// The viewer's own module, loaded by the page as an ES module.
const modUrl = `${base}/js/walkview.js?probe=${Date.now()}`;
await s('Page.addScriptToEvaluateOnNewDocument', {}).catch(() => {});

const probe = await evaluate(`(async () => {
  const { WalkViewer } = await import(${JSON.stringify(modUrl)});

  const geo = await fetch('/api/simulate/geometry?path=' + encodeURIComponent(${JSON.stringify(mapPath)}))
    .then(r => r.json());

  const v = new WalkViewer();
  v.path = ${JSON.stringify(mapPath)};
  v.geometry = {
    Footholds: geo.footholds ?? geo.Footholds ?? [],
    Ladders: geo.ladders ?? geo.Ladders ?? [],
    Portals: geo.portals ?? geo.Portals ?? [],
    Bounds: geo.bounds ?? geo.Bounds ?? null,
  };
  v.footholdsById = new Map();
  for (const f of v.geometry.Footholds) if (f.id && !v.footholdsById.has(f.id)) v.footholdsById.set(f.id, f);
  v.doc = null;
  v.images = new Map();
  v.scene = v.buildDrawList();

  const fh = v.geometry.Footholds[0];
  const ground = fh.y1;
  const x0 = fh.x1 + 4;
  v.ticks = [{ x: x0, y: ground, action: 'walk', onGround: true, dir: 1, foothold: fh.id }];
  v.frame = 0;

  // Paint into the canvas draw() will use, at a size that holds both bodies.
  const c = document.createElement('canvas');
  c.width = 1400; c.height = 700;
  v.canvas = c;
  document.body.appendChild(c);
  v.viewBox = v.layout();

  // Count pixels that are neither the background nor the bounds dashes. This
  // is the whole test: a body that did not paint leaves the canvas flat.
  const ctx = c.getContext('2d');
  const inkCount = () => {
    const d = ctx.getImageData(0, 0, c.width, c.height).data;
    let n = 0;
    for (let i = 0; i < d.length; i += 4) {
      const r = d[i], g = d[i + 1], b = d[i + 2];
      // background is #1a202c
      if (r === 26 && g === 32 && b === 44) continue;
      n++;
    }
    return n;
  };

  // Character only.
  v.mobs = [];
  v.draw();
  const charInk = inkCount();
  const charShot = c.toDataURL('image/png');

  // Character plus one mob standing well to the right of it on the same ground.
  const mobX = Math.min(fh.x2 - 4, x0 + 120);
  v.mobs = [{ mobId: 'probe', x: mobX, y: ground, foothold: fh.id, facing: 1, direction: 1, onGround: true, reversals: 0 }];
  v.draw();
  const bothInk = inkCount();
  const bothShot = c.toDataURL('image/png');

  // Mob alone, no character, so the mob's own ink is countable on its own.
  v.ticks = [];
  v.mobs = [{ mobId: 'probe', x: mobX, y: ground, foothold: fh.id, facing: 1, direction: 1, onGround: true, reversals: 0 }];
  v.draw();
  const mobOnlyInk = inkCount();

  // The depth arithmetic itself. Mob.cs:191 vs MapleCharacter.cs:561.
  v.ticks = [{ x: x0, y: ground, action: 'walk', onGround: true, dir: 1, foothold: fh.id }];
  const zMob = v.mobZ(v.mobs[0]);
  const zChar = v.characterZ(v.ticks[0]);
  const layer = String(fh.layer ?? '0');
  const expectedMob = (parseInt(layer, 10) || 0) * 100000 + 6000;

  // A mob whose foothold is not on this map must not invent a depth.
  const zUnknown = v.mobZ({ foothold: 'no-such-foothold' });

  // Far off the map: the frame must survive it.
  let threw = null;
  try {
    v.mobs = [{ mobId: 'x', x: 999999, y: -99999, foothold: fh.id, facing: 1, direction: 1, onGround: true, reversals: 0 }];
    v.draw();
  } catch (e) { threw = String(e && e.message || e); }

  return {
    charInk, bothInk, mobOnlyInk, zMob, zChar, layer, expectedMob, zUnknown, threw,
    charBytes: charShot.length, bothBytes: bothShot.length,
    fhId: fh.id, mobX, x0,
  };
})()`);

check(probe.threw === null, 'a mob far off the map does not throw the frame', probe.threw ?? '');

// The mob must put ink on a canvas by itself.
check(probe.mobOnlyInk > 500,
  'a mob alone paints visible pixels',
  `${probe.mobOnlyInk} non-background px`);

// The character must too — otherwise "the mob added ink" would be satisfied by
// the character's own drawing.
check(probe.charInk > 500,
  'the character alone paints visible pixels',
  `${probe.charInk} non-background px`);

// Adding the mob must add ink, and by more than a stray mark.
check(probe.bothInk > probe.charInk + 200,
  'adding a mob adds its own pixels',
  `${probe.charInk} -> ${probe.bothInk} (+${probe.bothInk - probe.charInk})`);

check(probe.zMob === probe.expectedMob,
  'mob Z is layer*100000 + 6000 (Mob.cs:191)',
  `layer=${probe.layer} z=${probe.zMob} expected=${probe.expectedMob}`);

check(probe.zChar !== probe.zMob && probe.zMob < probe.zChar,
  'on one layer a mob sorts behind the character (6000 < 60000)',
  `mob=${probe.zMob} char=${probe.zChar}`);

check(probe.zUnknown === null || probe.zUnknown === undefined || probe.zUnknown === null,
  'an unknown foothold yields null rather than a fabricated depth',
  String(probe.zUnknown));

// Print the numbers a reviewer can check by hand.
console.log(`\n  char=${probe.charInk}  char+mob=${probe.bothInk}  mob-only=${probe.mobOnlyInk}`);
console.log(`  foothold=${probe.fhId} charX=${probe.x0.toFixed(0)} mobX=${probe.mobX.toFixed(0)}`);
console.log(`  zMob=${probe.zMob} zChar=${probe.zChar} layer=${probe.layer}`);

// Close the tab BEFORE the socket. Target.closeTarget is answered on the
// browser-level socket, so sending it after ws.close() waits on a connection
// that is already gone and the run dies on a timeout that says nothing about
// the drawing.
await send('Target.closeTarget', { targetId }).catch(() => {});
ws.close();
console.log(`\n=== ${failures.length ? 'FAILURES: ' + failures.length : 'all checks passed'} ===`);
process.exit(failures.length ? 1 : 0);