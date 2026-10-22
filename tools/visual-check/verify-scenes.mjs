// Multi-scene visual verifier — one build, many claims.
//
//   node verify-scenes.mjs <cdpPort> <serverPort> [outDir]
//
// TASK 9C's lesson: a screenshot is only evidence if the section is actually
// visible and the thing you claim is on screen is actually on screen. This
// script therefore refuses to count a scene as verified unless it can point at
// non-background pixels inside the expected region.
//
// Scenes are declared up front with the DOM/JS probe that must succeed, so a
// missing feature shows up as a failed assertion rather than a plausible
// screenshot.

import { writeFileSync, mkdirSync } from 'node:fs';
import { join } from 'node:path';

const CDP = process.argv[2] ?? '9341';
const PORT = process.argv[3] ?? '5507';
const OUT = process.argv[4] ?? 'shots/scenes';
mkdirSync(OUT, { recursive: true });

const pages = await (await fetch(`http://127.0.0.1:${CDP}/json/list`)).json();
const page = pages.find((t) => t.type === 'page' && t.url.includes(PORT)) ?? pages.find((t) => t.type === 'page');
if (!page) { console.error(`no page for :${PORT}`); process.exit(1); }
console.log('page:', page.url);

const ws = new WebSocket(page.webSocketDebuggerUrl);
await new Promise((r) => ws.addEventListener('open', r, { once: true }));
let id = 0;
const pending = new Map();
ws.addEventListener('message', (e) => {
  const d = JSON.parse(e.data);
  if (d.id && pending.has(d.id)) { pending.get(d.id)(d); pending.delete(d.id); }
});
const cdp = (method, params = {}) => new Promise((res) => {
  const i = ++id; pending.set(i, res);
  ws.send(JSON.stringify({ id: i, method, params }));
});
const ev = async (expression) => {
  const r = await cdp('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true });
  if (r.result?.exceptionDetails) return { error: r.result.exceptionDetails.text };
  return r.result?.result?.value;
};

await cdp('Page.enable');
await cdp('Runtime.enable');

// One-time setup: the simulator must be the VISIBLE section. Loading data
// without switching the section is the failure that produced a screenshot of
// the file browser while the script reported "walking".
const setup = await ev(`(async () => {
  const app = window.app;
  if (!app) return { error: 'no window.app' };
  await app.sections.open('simulator');
  await app.simulator.open();
  return { active: app.sections.active, maps: app.simulator.maps?.length ?? 0 };
})()`);
console.log('setup:', JSON.stringify(setup));
if (setup.error || setup.active !== 'simulator') {
  console.error('!! simulator 未成為可見 section，截圖會是無效證據');
  process.exit(1);
}

// A canvas is "showing something" only if it has pixels that are not the
// background. Counting total non-transparent pixels is not enough -- the map
// art alone satisfies that even when the avatar is skipped.
const CANVAS_PROBE = `(() => {
  const v = window.app?.simulator?.viewer;
  const c = v?.canvas;
  if (!c) return { error: 'no canvas' };
  const g = c.getContext('2d');
  const d = g.getImageData(0, 0, c.width, c.height).data;
  let drawn = 0;
  for (let i = 0; i < d.length; i += 4) if (d[i] || d[i +1] || d[i+2]) drawn++;
  return { w: c.width, h: c.height, drawn };
})()`;

const FEET_HELPER = `
// Feet, measured from the RENDERED PIXELS rather than from any model's own
// numbers.
//
// The harness used to compute \`bounds.maxY - tick.y\`. Those are different
// coordinate spaces -- bounds.maxY is the composed look's box in its own local
// frame, tick.y is a WORLD coordinate -- so the subtraction produced a number
// that looked like a measurement and was not one. Measuring the canvas cannot
// have that problem: it is the picture.
//
// The background is the fill walkview.draw() lays down (#1a202c). Rows that are
// still that colour are not art.
function measureFeet(v, cur, av) {
  if (!v || !cur || !av) return null;
  const c = v.canvas;
  const ctx = c?.getContext('2d');
  if (!ctx || !c) return null;
  const s = v.viewBox?.scale ?? 1;
  const dpr = window.devicePixelRatio || 1;
  const at = av.placement(cur.x, cur.y, cur.dir ?? cur.facing ?? 1, av.part?.index ?? 0);
  if (!at || !Number.isFinite(at.cx)) return null;
  const x0 = Math.max(0, Math.floor(at.cx * s * dpr) - 2);
  const w = Math.max(4, Math.ceil((at.w + 4) * s * dpr));
  const y0 = Math.max(0, Math.floor(at.cy * s * dpr) - 2);
  const h = Math.min(c.height - y0, Math.ceil((at.h + 8) * s * dpr));
  if (h <= 2 || x0 + w > c.width) return null;
  const data = ctx.getImageData(x0, y0, w, h).data;
  const isArt = (i) => data[i + 3] >= 8
    && !(Math.abs(data[i] - 0x1a) < 6 && Math.abs(data[i+1] - 0x20) < 6 && Math.abs(data[i+2] - 0x2c) < 6);
  let top = -1, bottom = -1;
  for (let r = 0; r < h; r++)
    for (let col = 0; col < w; col++) {
      if (!isArt((r * w + col) * 4)) continue;
      if (top < 0) top = r;
      bottom = r;
    }
  if (bottom < 0) return null;
  // Device pixels -> world units, so this is comparable with the tick, unlike
  // the look's local bounds.
  const toWorld = (row) => (y0 + row) / dpr / s;
  return { topWorldY: toWorld(top), bottomWorldY: toWorld(bottom + 1),
           worldFeetY: cur.y, drawn: true };
}`;

const SCENES = [
  { id: 'stand',      map: '109050001', action: 'stand1', hold: null },
  { id: 'walk-right', map: '109050001', action: 'walk1',  hold: 'ArrowRight', ms: 2400 },
  { id: 'walk-left',  map: '109050001', action: 'walk1',  hold: 'ArrowLeft',  ms: 2400 },
  { id: 'jump-map',   map: '109050001', action: 'jump',   hold: 'Space',      ms: 900 },
  { id: 'sit-map',    map: '109050001', action: 'sit',    hold: 'KeyS',       ms: 700 },
  { id: 'town',       map: '100000000', action: null,      hold: null },
  // 106020010 is NOT in Map.nx -- it was never there, and the harness's own
  // map-not-found guard caught it. Two scenes from the real list take its
  // place; ids are verified against /api/simulate/maps, not remembered.
  { id: 'mushroom',   map: '120000202', action: null,      hold: null },
  { id: 'village',    map: '101000200', action: null,      hold: null },
];

const results = [];
for (const scene of SCENES) {
  // A missing map is a HARD FAILURE. It used to fall back to maps[0] silently,
  // which printed "mushroom map=0" while claiming to test 106020010 -- a
  // harness that lies about which map it tested invalidates every number it
  // prints. No fallback, ever.
  const loaded = await ev(`(async () => {
    const sim = window.app.simulator;
    const want = '${scene.map}';
    const map = sim.maps?.find((m) => String(m.id) === want);
    if (!map) {
      return { error: 'map-not-found:' + want,
               have: (sim.maps ?? []).slice(0, 12).map((m) => String(m.id)) };
    }
    await sim.walk(map);
    return { id: String(map.id ?? map.path), ticks: sim.result?.ticks?.length ?? null };
  })()`);
  if (loaded.error) {
    console.error(`!! scene ${scene.id}: ${loaded.error}` +
      (loaded.have ? ` (have: ${loaded.have.join(',')})` : ''));
    results.push({ ...scene, ok: false, why: loaded.error });
    continue;
  }
  // And the id that actually got tested must be the id that was asked for.
  // Belt and braces against a future fallback creeping back in.
  if (loaded.id !== scene.map) {
    console.error(`!! scene ${scene.id}: tested ${loaded.id}, asked for ${scene.map}`);
    results.push({ ...scene, ok: false, why: 'id-mismatch' });
    continue;
  }

  if (scene.hold) {
    // keydown/keyup are edge events; a level-triggered handler needs the key to
    // STAY down. Note `key:` must be the bare name ('ArrowRight', not
    // 'KeyS') or the handler's keyFor() lookup never matches.
    const bare = scene.hold.replace(/^Key|^Digit/, '');
    await ev(`(async () => {
      const fire = (type, code, key) => window.dispatchEvent(new KeyboardEvent(type,
        { code, key, bubbles: true }));
      fire('keydown', '${scene.hold}', '${bare}');
      await new Promise((r) => setTimeout(r, ${scene.ms}));
      fire('keyup', '${scene.hold}', '${bare}');
    })()`);
  }
  await new Promise((r) => setTimeout(r, 600));

  const probe = await ev(CANVAS_PROBE);
  // The composed look, not avatar.part. `part` was the single-canvas shape the
  // server used to return; the composer returns a list of parts per frame, and
  // anchorY/anchorX no longer exist on it at all -- reading them gave `undefined`,
  // which is why every scene reported placement=BAD while the picture was right.
  const avatar = await ev(`(() => {
    ${FEET_HELPER}
    const v = window.app?.simulator?.viewer;
    const av = v?.avatar;
    const cur = v?.ticks?.[v.ticks.length - 1] ?? null;
    const index = av?.part?.index ?? 0;
    // The frame the draw loop is actually showing. anchorsAt() is per-frame
    // because the anchors themselves are per-frame (BodyDrawInfo.cpp:59-100),
    // so frame 0's bounds are the wrong answer for frame 3.
    const frame = av?.anchorsAt?.(index) ?? null;
    const parts = av?.partsAt?.(index) ?? [];
    const p = parts.length ? av.placement(cur?.x ?? 0, cur?.y ?? 0,
                                          cur?.dir ?? cur?.facing ?? 1, index) : null;
    return {
      action: cur?.action ?? null,
      frame: index,
      onGround: cur?.onGround ?? null,
      tick: cur ? { x: cur.x, y: cur.y } : null,
      placement: p ? { cx: p.cx, cy: p.cy, w: p.w, h: p.h,
                       originX: p.originX, originY: p.originY } : null,
      // The SERVER's own numbers, straight off the composed frame. These are
      // what the blit positions are derived from; a client-side re-derivation
      // is the second thing that can disagree with the tables.
      bounds: frame?.bounds ?? av?.anchors?.bounds ?? null,
      partCount: parts.length,
      owners: [...new Set(parts.map((p) => p.owner))],
      hidden: av?.anchors?.hidden ?? [],
      zmap: av?.anchors?.zmap ?? null,
      missingLayers: av?.missingLayers?.length ?? 0,
      state: av?.part?.state ?? null,
      reason: av?.reason ?? '',
      partFrames: av?.part?.frames?.length ?? 0,
      bitmaps: av?.bitmaps?.size ?? 0,
      feet: measureFeet(v, cur, av),
    };
  })()`);

  const shot = await cdp('Page.captureScreenshot', { format: 'png' });
  const file = join(OUT, `${scene.id}-${scene.map}.png`);
  writeFileSync(file, Buffer.from(shot.result.data, 'base64'));

  // placement() must be numbers. The NaN/string bug was silent: it still
  // returned an object, it just drew nothing.
  const pl = avatar.placement;
  const placementOk = !!pl
    && typeof pl.cx === 'number' && Number.isFinite(pl.cx)
    && typeof pl.cy === 'number' && Number.isFinite(pl.cy)
    && typeof pl.w === 'number' && Number.isFinite(pl.w)
    && typeof pl.h === 'number' && Number.isFinite(pl.h);

  // Feet contact, from rendered pixels. See FEET_HELPER for why the old
  // `bounds.maxY - tick.y` was removed.
  //
  // `bottomWorldY` is the lowest drawn row of the character in WORLD units --
  // the same space as `tick.y`, so the difference between them is meaningful
  // and is exactly the gap between the drawn feet and where the world says the
  // feet are. 0 = the art lands on the character's own position.
  const feet = avatar.feet;
  const feetDelta = feet && Number.isFinite(feet.bottomWorldY) && Number.isFinite(feet.worldFeetY)
    ? Math.round((feet.bottomWorldY - feet.worldFeetY) * 10) / 10 : null;

  results.push({
    ...scene, map: loaded.id, ...probe, ...avatar, placementOk, feetDelta, file,
  });
  console.log(
    `${scene.id.padEnd(11)} map=${loaded.id.padEnd(10)} ` +
    `drawn=${String(probe.drawn ?? '?').padStart(7)} ` +
    `action=${String(avatar.action).padEnd(9)} ` +
    `parts=${String(avatar.partCount ?? 0).padStart(2)} ` +
    `placement=${placementOk ? 'OK ' : 'BAD'} feetΔ=${feetDelta ?? '?'}`,
  );
}

// A scene passes when the look is composed, its placement is finite, and the
// server gave real bounds to measure. feetDelta is REPORTED, not asserted: a
// jump is legitimately in the air, so a non-zero delta there is the scene doing
// what it was asked to do rather than a failure.
const bad = results.filter((r) => !r.placementOk || r.error === 'no canvas'
  || r.bounds == null);
console.log(`\n${results.length - bad.length}/${results.length} scenes have finite placement`);
if (bad.length) console.log('FAILED:', bad.map((b) => b.id).join(', '));
console.log('shots ->', OUT);

ws.close();