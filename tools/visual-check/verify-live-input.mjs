// PART 1 verification: a character that responds to real input.
//
//   node verify-live-input.mjs <cdpPort> <serverPort> [outDir] [mapId]
//
// The numbers required by TASK 16, in order:
//   1. x at t=0, 1000, 2000, 3000ms with ArrowRight held, monotonically
//      increasing; then it stops on release; then x decreases on ArrowLeft.
//      FLAT numbers mean not done, so this reports them and fails.
//   2. The action at those same four points: stand1 -> walk1.
//   3. Screenshots mid-walk and airborne.
//   4. A traverse check that must not regress.
//
// Two defects in the earlier harness are fixed here rather than worked around:
//
//   - Key events were dispatched with key === code, so 'KeyS' and 'Space' never
//     matched LiveWalk.keyFor (which switches on e.key, like a real browser:
//     'ArrowRight', 's', ' '). The keydown arrived and the app ignored it, and
//     the scene printed a perfectly plausible action=walk. Dispatch now sends
//     the pair a browser really produces, and the handler's own key set is
//     read back so a scene cannot claim a key it did not take.
//
//   - A tab that is not visible gets no requestAnimationFrame. Every reading
//     then looks exactly like "the character does not respond to input" while
//     nothing at all is running. visibilityState is therefore part of every
//     sample, and a hidden tab is a HARD FAILURE rather than a flat number.
//
// Missing maps are a hard failure too. The old harness fell back to maps[0], so
// a scene could print "mushroom map=0" while claiming to test 106020010.

import { writeFileSync, mkdirSync } from 'node:fs';
import { join } from 'node:path';

const CDP = process.argv[2] ?? '9341';
const PORT = process.argv[3] ?? '5506';
const OUT = process.argv[4] ?? 'shots/live';
const MAP = process.argv[5] ?? '109050001';
mkdirSync(OUT, { recursive: true });

const pages = await (await fetch(`http://127.0.0.1:${CDP}/json/list`)).json();
const page = pages.find((t) => t.type === 'page' && t.url.includes(PORT));
if (!page) { console.error(`no page for :${PORT}`); process.exit(1); }

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
  if (r.result?.exceptionDetails) {
    return { error: r.result.exceptionDetails.exception?.description ?? r.result.exceptionDetails.text };
  }
  return r.result?.result?.value;
};
await cdp('Page.enable');
await cdp('Runtime.enable');

// A key, as a browser produces it: `key` is the printable value and `code` the
// physical key. LiveWalk.keyFor switches on `key`, so 'KeyS' as a key value is
// not a thing any keyboard sends -- and the old harness sent exactly that.
const KEYS = {
  ArrowRight: { key: 'ArrowRight', code: 'ArrowRight' },
  ArrowLeft: { key: 'ArrowLeft', code: 'ArrowLeft' },
  KeyS: { key: 's', code: 'KeyS' },
  Space: { key: ' ', code: 'Space' },
};

// Hold a key for `ms` and sample the live body on a fixed schedule. Sampling is
// done in-page against the SAME object the loop advances, so the number is the
// body's position and not a re-derivation.
const runHold = async (key, ms, samples) => {
  const k = KEYS[key];
  return ev(`(async () => {
    const live = window.app.simulator.live;
    if (!live || !live.sessionId) return { error: 'no live body' };
    const settle = (ms) => new Promise((r) => setTimeout(r, ms));
    const at = (t) => {
      const v = live.view;
      return {
        t,
        x: +(live.state?.x ?? NaN).toFixed(2),
        y: +(live.state?.y ?? NaN).toFixed(2),
        frame: live.state?.frame ?? null,
        // The server's own action word (walk/rise/fall/climb), and the WZ
        // stance name the avatar actually composes from it. Task 16 asks for
        // the action at four points; the stance is what is drawn.
        action: live.state?.action ?? null,
        speed: +(live.state?.speed ?? 0).toFixed(3),
        onGround: live.state?.onGround ?? null,
        stance: v?.avatarState ?? null,
        visibility: document.visibilityState,
        handler: { ...live.keys },
      };
    };
    // A plain object, not an array with properties hung off it. Runtime.evaluate
    // serialises with returnByValue, and a JSON array drops non-index keys --
    // so `out.released` and `out.handlerSaw` came back undefined and the script
    // read undefined.x. The shape is explicit so that cannot recur.
    const out = { points: [at(0)], handlerSaw: null, released: null };
    window.dispatchEvent(new KeyboardEvent('keydown',
      { key: ${JSON.stringify(k.key)}, code: ${JSON.stringify(k.code)}, bubbles: true }));
    // Confirm the handler actually took it before timing anything.
    await settle(60);
    out.handlerSaw = { ...live.keys };
    const step = ${ms} / ${samples};
    for (let i = 1; i <= ${samples}; i++) { await settle(step); out.points.push(at(i * step)); }
    window.dispatchEvent(new KeyboardEvent('keyup',
      { key: ${JSON.stringify(k.key)}, code: ${JSON.stringify(k.code)}, bubbles: true }));
    await settle(700);
    out.released = at(${ms} + 700);
    return out;
  })()`);
};

const setup = await ev(`(async () => {
  const app = window.app;
  if (!app) return { error: 'no window.app' };
  await app.sections.open('simulator');
  await app.simulator.open();
  const want = '${MAP}';
  const m = (app.simulator.maps ?? []).find((x) => String(x.id) === want);
  if (!m) return { error: 'map-not-found:' + want,
                   have: (app.simulator.maps ?? []).slice(0, 12).map((x) => String(x.id)) };
  await app.simulator.walk(m);
  const live = app.simulator.live;
  return {
    active: app.sections.active,
    map: String(m.id), path: m.path,
    visibility: document.visibilityState,
    viewError: app.simulator.viewError ?? null,
    replayError: app.simulator.replayError ?? null,
    sessionId: live?.sessionId ?? null,
    avatarSource: window.app.simulator.viewer?.avatar?.source ?? null,
    avatarReason: window.app.simulator.viewer?.avatar?.reason || null,
  };
})()`);
console.log('setup:', JSON.stringify(setup));
if (setup.error || setup.active !== 'simulator') {
  console.error('!! no simulator section / map unavailable — see above');
  process.exit(1);
}
if (setup.visibility !== 'visible') {
  console.error(`!! document.visibilityState=${setup.visibility}; requestAnimationFrame does not run in a hidden tab, so every reading below would be a measurement artefact.`);
  process.exit(1);
}

const shot = async (name) => {
  const s = await cdp('Page.captureScreenshot', { format: 'png' });
  const file = join(OUT, `${name}.png`);
  writeFileSync(file, Buffer.from(s.result.data, 'base64'));
  return file;
};

const report = [];
const fail = [];
const check = (ok, msg) => { if (!ok) fail.push(msg); return ok; };

// ---------------------------------------------------------------- 1 + 2
console.log('\n=== hold ArrowRight for 3000ms, sampled at 0/1000/2000/3000 ===');
const right = await runHold('ArrowRight', 3000, 3);
if (right.error) { console.error('right hold failed:', right.error); process.exit(1); }
console.log('  handler saw:', JSON.stringify(right.handlerSaw));
check(right.handlerSaw?.right === true,
  `ArrowRight did not reach the handler (saw ${JSON.stringify(right.handlerSaw)})`);

const points = right.points;
for (const p of points) console.log('  ', JSON.stringify(p));

const xs = points.map((p) => p.x);
const monotonic = xs.every((v, i) => i === 0 || v > xs[i - 1]);
check(monotonic, `x not monotonic under ArrowRight: ${xs.join(' -> ')}`);
check(xs.every(Number.isFinite), 'x not finite under ArrowRight');
const hidden = points.some((p) => p.visibility !== 'visible');
check(!hidden, 'tab went hidden mid-measurement; rAF stops and the numbers are void');

const stopDelta = +(right.released.x - points[3].x).toFixed(2);
console.log('   released x=', right.released.x, 'delta after release=', stopDelta);
check(Math.abs(stopDelta) < Math.abs(xs[3] - xs[2]) + 2.0,
  `character kept moving after release (delta ${stopDelta})`);

const stances = points.map((p) => p.stance);
console.log('   stance sequence:', stances.join(' -> '));
check(stances[0] === 'stand1', `stance at t=0 is ${stances[0]}, expected stand1`);
check(stances[3] === 'walk1', `stance at t=3000 is ${stances[3]}, expected walk1`);
check(stances.includes('walk1'), 'stance never became walk1');

const shotWalk = await shot(`walk-mid-${MAP}`);
console.log('   shot ->', shotWalk);

// ---------------------------------------------------------------- left
console.log('\n=== hold ArrowLeft for 1500ms ===');
const left = await runHold('ArrowLeft', 1500, 3);
if (left.error) { console.error('left hold failed:', left.error); process.exit(1); }
console.log('  handler saw:', JSON.stringify(left.handlerSaw));
for (const p of left.points) console.log('  ', JSON.stringify(p));
check(left.handlerSaw?.left === true, 'ArrowLeft did not reach the handler');
const lxs = [left.points[0], left.points[left.points.length - 1]].map((p) => p.x);
check(lxs[1] < lxs[0], `x did not decrease under ArrowLeft: ${lxs[0]} -> ${lxs[1]}`);
check(left.points[left.points.length - 1].stance === 'walk1',
  `stance under ArrowLeft is ${left.points[left.points.length - 1].stance}, expected walk1`);

// ---------------------------------------------------------------- jump
console.log('\n=== jump (Space, edge-triggered like MapleCharacter.cs:775-777) ===');
const jump = await ev(`(async () => {
  const live = window.app.simulator.live;
  const settle = (ms) => new Promise((r) => setTimeout(r, ms));
  const K = { key: ' ', code: 'Space' };
  window.dispatchEvent(new KeyboardEvent('keydown', K));
  const out = [];
  for (let i = 0; i < 10; i++) {
    await settle(70);
    out.push({ t: (i + 1) * 70, y: +(live.state?.y ?? NaN).toFixed(2),
               vy: +(live.state?.velocityY ?? 0).toFixed(2),
               onGround: live.state?.onGround ?? null,
               action: live.state?.action ?? null,
               stance: live.view?.avatarState ?? null,
               visibility: document.visibilityState });
  }
  window.dispatchEvent(new KeyboardEvent('keyup', K));
  await settle(900);
  out.push({ mark: 'landed', y: +(live.state?.y ?? NaN).toFixed(2),
             onGround: live.state?.onGround ?? null,
             action: live.state?.action ?? null, stance: live.view?.avatarState ?? null });
  return out;
})()`);
for (const s of jump) console.log('  ', JSON.stringify(s));
const airborne = jump.filter((s) => s.onGround === false);
check(airborne.length > 0, 'jump never left the ground (every sample onGround=true)');
const minY = Math.min(...jump.filter((s) => Number.isFinite(s.y)).map((s) => s.y));
const groundY = jump[jump.length - 1].y;
check(minY < groundY, `jump did not rise: minY=${minY} groundY=${groundY}`);
const shotJump = await shot(`jump-${MAP}`);
console.log('   shot ->', shotJump);

// ---------------------------------------------------------------- 4
console.log('\n=== traverse check (must not regress) ===');
const traverse = await ev(`(async () => {
  const sim = window.app.simulator;
  const r = await fetch('/api/simulate/character', {
    method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ path: ${JSON.stringify(setup.path)}, direction: 1 }),
  });
  const j = await r.json();
  return { ok: r.ok, startX: j.startX, finalX: j.finalX, ticks: j.ticks?.length ?? null,
           fellOut: j.fellOut ?? null, moved: j.startX != null && j.finalX != null
                   && j.finalX !== j.startX };
})()`);
console.log('  ', JSON.stringify(traverse));
check(traverse.ok !== false, 'traverse endpoint failed');
check(traverse.moved === true, `traverse did not move: ${traverse.startX} -> ${traverse.finalX}`);

report.push({ right: points, released: right.released, left, jump, traverse,
              shotWalk, shotJump, stanceSequence: stances });

console.log('\n=== result ===');
if (fail.length) {
  console.log(`FAILED (${fail.length}):`);
  for (const f of fail) console.log('  -', f);
} else {
  console.log('PASS — all checks green');
}
console.log('shots ->', OUT);
writeFileSync(join(OUT, 'report.json'), JSON.stringify(report, null, 2));

ws.close();
process.exit(fail.length ? 1 : 0);