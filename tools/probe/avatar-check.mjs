// Prove walkview.js draws the REAL avatar when the archive has it, that its
// frame clock follows the sprite's own delays and stops when idle, and that
// the placement uses the frame's WZ origin rather than a centred box.
//
//   node avatar-check.mjs <baseUrl> <mapPath> [cdpPort]
//
// WHY A BROWSER AND REAL PIXELS
// -----------------------------
// The claim being checked is about pixels: does the character stand with its
// FEET on the foothold line. Counting draw calls cannot see that, and neither
// can reading the placement numbers alone -- a placement can be arithmetically
// right and drawn at the wrong place. So the checks are read back out of a real
// 2D context, and the origin is asserted both as a number AND as ink.
//
// WHY SYNTHETIC FRAMES ARE ACCEPTED AND NAMED
// -------------------------------------------
// The archive under test (.simdata/Map.nx) is a MAP archive: its root holds
// Back, Effect.img, Map, MapHelper.img, Obj, Physics.img, Tile and WorldMap,
// and no Character image at all. So the real-art path cannot be exercised
// against real WZ data here. The frames below are SYNTHETIC -- canvases the
// harness builds and hands to the same code that would have received real ones
// -- and every assertion made about them is about the DRAWING and the CLOCK,
// which are the parts this file owns. The one thing synthetic frames cannot
// prove is that a real Character.wz lays out the way the reference describes;
// that is reported as unverified, not asserted.
//
// A second set of checks runs against the archive AS IT IS, to prove the
// fallback is honest: with no Character image open, the viewer must say so on
// screen and must still draw something.

import { spawn } from 'node:child_process';
import process from 'node:process';

const [, , base, mapPath, portArg = '9222'] = process.argv;
if (!base || !mapPath) {
  console.error('usage: node avatar-check.mjs <baseUrl> <mapPath> [cdpPort]');
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

const avatarUrl = `${base}/js/avatar.js?probe=${Date.now()}`;
const viewerUrl = `${base}/js/walkview.js?probe=${Date.now()}`;

const probe = await evaluate(`(async () => {
  const { WalkViewer } = await import(${JSON.stringify(viewerUrl)});
  const { Avatar } = await import(${JSON.stringify(avatarUrl)});

  const geo = await fetch('/api/simulate/geometry?path=' + encodeURIComponent(${JSON.stringify(mapPath)}))
    .then(r => r.json());

  const mkViewer = () => {
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
    v.mobs = [];
    v.scene = v.buildDrawList();
    return v;
  };

  const fh = geo.footholds[0];
  const ground = fh.y1;
  const x0 = fh.x1 + 4;

  // ------------------------------------------------------------------
  // SYNTHETIC AVATAR FRAMES. Stated as synthetic in the report.
  //
  // Origins are deliberately NOT the centre of the canvas: the sprite is
  // 60x80 with origin (30, 78) -- near the bottom middle, like a real Maple
  // body. If the drawing centred the box instead of using the origin, the
  // measured foot position would be off by 78 - 40 = 38 world px.
  const FRAMES = [
    { name: '0', width: 60, height: 80, originX: 30, originY: 78, delay: 40,  path: 'synthetic/stand1/0' },
    { name: '1', width: 60, height: 80, originX: 30, originY: 78, delay: 120, path: 'synthetic/stand1/1' },
    { name: '2', width: 60, height: 80, originX: 30, originY: 78, delay: 40,  path: 'synthetic/stand1/2' },
    { name: '3', width: 60, height: 80, originX: 30, originY: 78, delay: 120, path: 'synthetic/stand1/3' },
  ];

  // A visibly different picture per frame, so "the frame changed" is a pixel
  // fact rather than a claim about an index.
  const paintFrame = (index) => {
    const c = document.createElement('canvas');
    c.width = FRAMES[index].width; c.height = FRAMES[index].height;
    const g = c.getContext('2d');
    g.fillStyle = index % 2 === 0 ? '#ff3355' : '#33ff88';
    g.fillRect(0, 0, c.width, c.height);
    g.fillStyle = '#ffffff';
    // A bar at a frame-specific height, so the ink pattern differs per frame.
    g.fillRect(0, index * 7, c.width, 6);
    // A bright block hard against the LEFT edge. A solid rectangle is its own
    // mirror image, so without an asymmetric mark a flip is pixel-identical and
    // "is it facing the right way" cannot be answered by ink at all.
    g.fillStyle = '#ffff00';
    g.fillRect(0, 20, 12, 12);
    return c;
  };

  const avatar = new Avatar();
  FRAMES.forEach((f, i) => avatar.bitmaps.set(f.path, paintFrame(i)));
  avatar.part = { state: 'stand1', frames: FRAMES, index: 0, time: 0, flip: 1 };

  // ---- CHECK A: the origin is used, as a number -------------------
  // x0 is the character's world X and ground its world Y. placement() adds the
  // origin straight onto both, so the assertions below are on the result and
  // not on a re-derivation of the rule.
  const atRight = avatar.placement(x0, ground, 1);
  const atLeft  = avatar.placement(x0, ground, -1);

  // ---- CHECK B: the origin is used, AS INK -------------------------
  // Draw ONLY the avatar: no foothold lines, no bounds, no fallback figure, so
  // every non-background pixel measured belongs to the sprite. Counting the
  // whole scene instead would measure the map, and would "pass" for any
  // placement whatsoever.
  const v = mkViewer();
  const c = document.createElement('canvas');
  c.width = 1200; c.height = 800;
  v.canvas = c;
  v.ticks = [{ x: x0, y: ground, action: 'walk', onGround: false, dir: 1, foothold: fh.id }];
  v.frame = 0;
  v.avatar = avatar;
  v.avatarState = 'stand1';

  const ctx = c.getContext('2d');
  // A controlled scale of 1 with the character centred, rather than layout()'s
  // fit-to-map scale. The map is ~1000px wide, so the fitted scale puts the
  // foothold several thousand rows down an 800px canvas and the sprite is drawn
  // correctly OFF-SCREEN -- which measures as "no ink" and proves nothing.
  const s2 = 1;
  const px = (x) => x + 600;
  const py = (y) => y + 400;
  const groundRow = py(ground);

  // The foothold line, drawn where the geometry says it is, so "the character
  // stands ON the line" is a statement about two pieces of ink on a canvas.
  ctx.fillStyle = '#1a202c';
  ctx.fillRect(0, 0, c.width, c.height);
  ctx.fillStyle = '#ffffff';
  const lineY = Math.round(groundRow);
  ctx.fillRect(0, lineY, c.width, 1);

  v.drawWalk(ctx, px, py, s2, [], v.ticks[0], 0, 0);

  const d = ctx.getImageData(0, 0, c.width, c.height).data;
  let botRow = -1, leftCol = 1e9, rightCol = -1, ink = 0;
  for (let yy = 0; yy < c.height; yy++) {
    for (let xx = 0; xx < c.width; xx++) {
      const i = (yy * c.width + xx) * 4;
      if (d[i] === 26 && d[i+1] === 32 && d[i+2] === 44) continue;   // background
      if (d[i] === 255 && d[i+1] === 255 && d[i+2] === 255
          && xx < c.width - 1 && d[i+4] === 255) continue;            // foothold line
      ink++;
      if (yy > botRow) botRow = yy;
      if (xx < leftCol) leftCol = xx;
      if (xx > rightCol) rightCol = xx;
    }
  }

  // The sprite's own last row: the origin is 78 of 80 rows down, so the drawn
  // bottom sits 2 sprite-rows BELOW the origin -- and therefore below the
  // foothold line, by exactly that overhang.
  const overhangRows = 80 - 78;
  const expectBot = lineY + overhangRows;
  // Where the sprite would have bottomed out if it had been drawn as a 55x79
  // box CENTRED on the anchor instead: the anchor is the box's bottom.
  const centreWouldBe = Math.round(py(ground - 79 / 2 + 79));

  // ---- CHECK C: the frame clock advances while walking ------------
  const clock = new Avatar();
  clock.part = { state: 'walk1', frames: FRAMES, index: 0, time: 0, flip: 1 };
  const walkTick = { action: 'walk', onGround: true, dir: 1 };
  const standTick = { action: 'stand1', onGround: true, dir: 1 };

  const walkSeq = [];
  for (let i = 0; i < 12; i++) walkSeq.push(clock.step(walkTick));
  const distinctWalk = [...new Set(walkSeq)];

  // ---- CHECK D: the clock STOPS while idle ------------------------
  // A fresh clock so the walk sequence above cannot be mistaken for the idle
  // one, then 60 steps with the character standing still.
  const idle = new Avatar();
  idle.part = { state: 'stand1', frames: FRAMES, index: 1, time: 0, flip: 1 };
  const idleSeq = [];
  for (let i = 0; i < 60; i++) idleSeq.push(idle.step(standTick));
  const distinctIdle = [...new Set(idleSeq)];

  // ---- CHECK E: delays are per-frame, not one global rate ---------
  // frame 0 has delay 40, frame 1 has delay 120. Walk from 0 and count how
  // many 17ms steps each frame is actually held for. A frame is "held" for the
  // number of consecutive steps that returned its index.
  const holds = [];
  const holdClock = new Avatar();
  holdClock.part = { state: 'walk1', frames: FRAMES, index: 0, time: 0, flip: 1 };
  let cur0 = holdClock.step(walkTick);
  let run = 0;
  for (let i = 0; i < 600; i++) {
    const now = holdClock.step(walkTick);
    if (now === cur0) { run++; continue; }
    holds.push({ frame: cur0, steps: run });
    cur0 = now;
    run = 1;
    if (holds.length >= 8) break;
  }

  // ---- CHECK F: the honest fallback when the archive has no art ------
  // A SEPARATE canvas and a SEPARATE viewer: v2.draw() resizes whatever canvas
  // it is given to the fitted map size, so sharing one canvas with the
  // measurements above silently changes their scale partway through the probe.
  //
  // The absence of art is ARRANGED here, not assumed. This check used to rely on
  // the real archive having no character art, which stopped being true the moment
  // Character.nx was open -- and a check whose precondition silently flipped
  // reports a FAIL that reads like a rendering bug while the renderer is in fact
  // correct. What is under test is the honest-reporting path, so the condition is
  // created: the probe is pointed at a node that cannot exist, which is the same
  // state as an archive missing the art, and the placeholder is required to be
  // both drawn and NAMED.
  const c2 = document.createElement('canvas');
  c2.width = 1200; c2.height = 800;
  const v2 = mkViewer();
  v2.canvas = c2;
  v2.ticks = [{ x: x0, y: ground, action: 'walk', onGround: true, dir: 1, foothold: fh.id }];
  v2.frame = 0;
  v2.avatarState = '';
  // A state name the archive cannot have, so no candidate path can resolve.
  v2.avatar.candidates = () => ['f0/__no_such_image__/stand1'];
  let reason = null, threw = null;
  try {
    await v2.avatar.load('stand1');
    reason = v2.avatar.reason;
    v2.draw();
  } catch (e) { threw = String((e && e.message) || e); }

  const hudText = v2.hud.textContent;

  // ---- CHECK G: Z ordering unchanged ------------------------------
  const zChar = v2.characterZ(v2.ticks[0]);
  const layer = String(fh.layer ?? '0');
  const expectedChar = (parseInt(layer, 10) || 0) * 100000 + 60000;

  // Flip: mirrored placement must keep the origin on the character.
  const spanFor = (dir) => {
    ctx.fillStyle = '#1a202c';
    ctx.fillRect(0, 0, c.width, c.height);
    ctx.fillStyle = '#ffffff';
    // The foothold line goes down first, so anything drawn over it is avatar
    // ink and the line can be masked out of the count below.
    ctx.fillRect(0, lineY, c.width, 1);
    v.ticks = [{ x: x0, y: ground, action: 'walk', onGround: false, dir, foothold: fh.id }];
    v.drawWalk(ctx, px, py, s2, [], v.ticks[0], 0, 0);
    const dd = ctx.getImageData(0, 0, c.width, c.height).data;
    // The YELLOW block is the facing marker, so its centroid is measured
    // rather than the whole span: a symmetric sprite has one span either way.
    let sy = 0, syw = 0;
    for (let yy = 0; yy < c.height; yy++) for (let xx = 0; xx < c.width; xx++) {
      const i = (yy * c.width + xx) * 4;
      if (dd[i] === 255 && dd[i+1] === 255 && dd[i+2] === 0) { sy += xx; syw++; }
    }
    return { l: syw ? sy / syw : -1, r: 0, cx: syw ? sy / syw : -1, pixels: syw };
  };
  const spanRight = spanFor(1);
  const spanLeft = spanFor(-1);

  return {
    ink, botRow, groundRow, expectBot, centreWouldBe, lineY, atRight, atLeft,
    overhangRows, x0, ground,
    walkSeq, distinctWalk, distinctIdle,
    holds: holds.slice(0, 6),
    reason, threw, hudText, zChar, expectedChar, layer,
    spanRight, spanLeft,
    frameDeltas: FRAMES.map(f => f.delay),
  };
})()`);

console.log('\n--- A. origin (numbers) ---');
check(probe.atRight.x === probe.x0 - 30,
  'unflipped: placement puts the frame origin on the character X (originX 30)',
  `x=${probe.atRight.x} expected=${probe.x0 - 30}`);
check(probe.atRight.y === probe.ground - 78,
  'unflipped: placement puts the frame origin on the character Y (originY 78)',
  `y=${probe.atRight.y} expected=${probe.ground - 78}`);
check(probe.atRight.w === 60 && probe.atRight.h === 80,
  'the canvas is the frame\'s own size, not the 55x79 fallback box',
  `${probe.atRight.w}x${probe.atRight.h}`);
// placement() returns the TOP-LEFT of the destination rect. Unflipped the
// origin is -originX, so the left edge is characterX - originX. Flipped the
// origin is (originX - width), so the left edge is characterX + originX -
// width. With a sprite whose origin is centred that lands on characterX - 30,
// the same as unflipped -- which is CORRECT: a mirror must not move the
// character's own column.
check(probe.atLeft.x === probe.x0 + 30 - 60,
  'flipped: dest left = characterX + originX - width (MapleCharacter.cs:1348)',
  `x=${probe.atLeft.x} expected=${probe.x0 + 30 - 60}`);
check(probe.atRight.x === probe.x0 - 30,
  'unflipped: dest left = characterX - originX',
  `x=${probe.atRight.x} expected=${probe.x0 - 30}`);
check(probe.atLeft.y === probe.ground - 78,
  'flipping does not touch Y: origin.Y = -wzOrigin.Y either way',
  `y=${probe.atLeft.y}`);

console.log('\n--- B. origin (pixels) ---');
check(probe.ink > 1000, 'the avatar alone paints visible pixels', `${probe.ink} non-background px`);
console.log(`     foothold line row = ${probe.lineY}`);
console.log(`     painted bottom row = ${probe.botRow}`);
console.log(`     origin overhang = ${probe.overhangRows} sprite rows -> expected bottom = ${probe.expectBot}`);
check(Math.abs(probe.botRow - probe.expectBot) <= 1,
  'the painted bottom row is the ORIGIN row plus its overhang, not the box centre',
  `painted=${probe.botRow} expected=${probe.expectBot}`);
console.log(`     a box drawn centred on the anchor would have bottomed out at ${probe.centreWouldBe},`);
console.log(`     which is ${Math.abs(probe.botRow - probe.centreWouldBe).toFixed(0)}px from what was actually painted.`);

console.log('\n--- C/D. frame clock ---');
console.log(`     walking: ${probe.walkSeq.join(',')}`);
check(probe.distinctWalk.length > 1,
  'the frame advances while walking', `${probe.distinctWalk.length} distinct frames`);
console.log(`     idle:    ${probe.distinctIdle.join(',')} (${probe.distinctIdle.length} distinct over 60 steps)`);
check(probe.distinctIdle.length === 1,
  'the frame does NOT advance while idle',
  `60 idle steps stayed on frame ${probe.distinctIdle[0]}`);

console.log('\n--- E. per-frame delays ---');
console.log(`     delays: ${probe.frameDeltas.join(',')} ms`);
console.log(`     holds (17ms steps per frame): ${JSON.stringify(probe.holds)}`);
const hold0 = probe.holds.find(h => h.frame === 0)?.steps;
const hold1 = probe.holds.find(h => h.frame === 1)?.steps;
check(hold0 !== undefined && hold1 !== undefined && hold0 !== hold1,
  'frames are held for their OWN delay, not one global rate',
  `frame0=${hold0} steps frame1=${hold1} steps`);
// The first sample is partial (the clock starts at time=0 and the loop counts
// from the frame after the initial step), so frame 0's real hold is the first
// REPEAT of frame 0, not its first appearance. Later frames are complete.
const hold2 = probe.holds.filter(h => h.frame === 0)[1]?.steps;
console.log(`     frame 0 appears twice: ${hold0} then ${hold2} (first is partial)`);
check(hold2 >= 2 && hold2 <= 4,
  'a 40ms frame is held 2-3 ticks of 17ms (partial first sample excluded)',
  `${hold2} steps`);
check(hold1 >= 6 && hold1 <= 9,
  'a 120ms frame is held ~7 ticks of 17ms',
  `${hold1} steps`);

console.log('\n--- F. honest fallback when the archive has no art ---');
console.log(`     reason: ${probe.reason}`);
check(probe.threw === null, 'no throw when the archive has no art', probe.threw ?? '');
check(!!probe.reason, 'a reason is recorded, so the fallback is not silent', probe.reason ?? '');
check(/placeholder/i.test(probe.hudText), 'the HUD names the placeholder figure',
  probe.hudText.slice(0, 200));

// ---- CHECK F2: the REAL archive produces a REAL avatar ------------
// The other half of the same surface. F proves the fallback is honest; this
// proves it is not what happens by default when Character.nx is open. Without
// it, a build that always fell back would pass F and look like a success.
//
// The archive is discovered the way the app discovers it (avatar.characterFile),
// not named, so this stays honest if the archive's session id is not f2.
const v3 = mkViewer();
v3.canvas = c2;
v3.ticks = [{ x: x0, y: ground, action: 'walk', onGround: true, dir: 1, foothold: fh.id }];
v3.frame = 0;
v3.avatarState = '';
let realThrew = null, realOk = false;
try {
  realOk = await v3.avatar.load('stand1');
  v3.draw();
} catch (e) { realThrew = String((e && e.message) || e); }
console.log('\n--- F2. the real archive composes a real avatar ---');
console.log(`     source: ${v3.avatar.source}  layers: ${v3.avatar.bitmaps.size}`);
check(realThrew === null, 'no throw with the real character archive', realThrew ?? '');
check(realOk && v3.avatar.ready, 'a body composed from the archive', v3.avatar.reason || '');
// Every layer path the server named must have produced pixels. A path that 404s
// is the exact defect this check exists to catch, and it is invisible unless the
// count is compared against the paths that were asked for.
const wanted = new Set(v3.avatar.part.frames.flatMap(f => (f.layers?.length ? f.layers : [f]).map(l => l.path)));
const got = [...v3.avatar.bitmaps.values()].filter(Boolean).length;
check(got === wanted.size && v3.avatar.missingLayers.length === 0,
  'every layer the server named decoded to real pixels',
  `${got}/${wanted.size} decoded, ${v3.avatar.missingLayers.length} missing`);
check(/real avatar/i.test(v3.hud.textContent), 'the HUD names the real avatar',
  v3.hud.textContent.slice(0, 200));

console.log('\n--- G. Z order unchanged ---');
check(probe.zChar === probe.expectedChar,
  'character Z is still layer*100000 + 60000 (MapleCharacter.cs:561)',
  `layer=${probe.layer} z=${probe.zChar} expected=${probe.expectedChar}`);

console.log('\n--- H. facing ---');
console.log(`     facing right: marker centroid x = ${probe.spanRight.cx.toFixed(1)} (${probe.spanRight.pixels} px)`);
console.log(`     facing left:  marker centroid x = ${probe.spanLeft.cx.toFixed(1)} (${probe.spanLeft.pixels} px)`);
check(probe.spanRight.pixels > 0 && probe.spanLeft.pixels > 0,
  'the facing marker is painted in both directions',
  `right=${probe.spanRight.pixels} left=${probe.spanLeft.pixels}`);
check(probe.spanLeft.cx > probe.spanRight.cx,
  'facing left moves the marker to the RIGHT (the sprite is mirrored)',
  `right=${probe.spanRight.cx.toFixed(1)} left=${probe.spanLeft.cx.toFixed(1)}`);
check(Math.abs((probe.spanRight.cx + probe.spanLeft.cx) / 2 - (probe.x0 + 600)) <= 3,
  'the two mirrored positions are symmetric about the character column',
  `mid=${((probe.spanRight.cx + probe.spanLeft.cx) / 2).toFixed(1)} characterX=${probe.x0 + 600}`);

await send('Target.closeTarget', { targetId }).catch(() => {});
ws.close();
console.log(`\n=== ${failures.length ? 'FAILURES: ' + failures.length : 'all checks passed'} ===`);
process.exit(failures.length ? 1 : 0);