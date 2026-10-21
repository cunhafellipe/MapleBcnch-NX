// The browser-side judge. ONE dependency: playwright-core, already on this box
// (DSH's own profile ships it, and the Playwright MCP uses the same build).
//
// WHY THIS EXISTS
// The other suites are HTTP, and HTTP is blind to a canvas. Twice the answer
// came from looking at the page: once for the alias poses (115 of 158 were
// aliases), once for the effect art never decoding on a FIRST cast. A person can
// see "the skill does nothing" and no HTTP assertion will ever notice.
//
// WHAT IT DOES NOT DO
// It does not hand-drive the frame loop. An earlier version of this file called
// `await live.tick()` by hand while headless Chrome ALSO ran the page's own
// requestAnimationFrame, so two drivers shared one LiveWalk: the loser found
// `this.busy` already true and returned having done nothing, and the cast
// silently never happened. It was green on one run and red on the next against
// an unchanged server. Playwright gives real key events and real waiting, so
// neither problem has a place to live here.
//
// RUN
//   node MapleBench/tools/browser-verify.mjs                 core only
//   node MapleBench/tools/browser-verify.mjs --shapes        + B1 shape sweep
//   node MapleBench/tools/browser-verify.mjs --all           every section
//   BASE=http://127.0.0.1:5591  node .../browser-verify.mjs
//
// SECTIONS
//   core    the original single-scenario gate (1121008). Unchanged.
//   shapes  B1 -- one representative per DISTINCT effect shape, both facings.
//   fx5     B2 -- the five-node skills: do all five reach the canvas, in order.
//   actions B3 -- one-action and two-action skills: which pose actually plays.
//   tiles   B4 -- the no-foothold-in-the-box fallback (SkillUseEffect.cpp:263).
//   hits    B5 -- several hit effects on one mob: N places, or one pile?
//
// THE SHAPE LIST IS NOT HAND-COPIED. `--shapes` reads /api/skill-cast/castable
// and every /plan behind it and derives the representatives itself, so adding a
// skill with a frame count nobody has tested before widens the sweep instead of
// silently going untested.

import { createRequire } from 'node:module';
import { writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

const require_ = createRequire(import.meta.url);
const { chromium } = (() => {
  const candidates = [
    'playwright-core',
    ...(process.env.DSH_PROFILE_DIR
      ? [join(process.env.DSH_PROFILE_DIR, 'node_modules', 'playwright-core')]
      : []),
  ];
  for (const c of candidates) {
    try { return require_(c); } catch { /* try the next one */ }
  }
  console.log('>>> RED: browser-verify -- playwright-core is not installed');
  console.log('    it ships with DSH; set DSH_PROFILE_DIR or run with NODE_PATH set');
  process.exit(1);
})();

const BASE = process.env.BASE ?? 'http://127.0.0.1:5591';
const MAP = /100000004/;          // Henesys: a map with mobs on it
const SKILL = '1121008';          // Slash Blast: has a damage node and effect art
const KEY = 'k';
const MOB = '9400101';            // a mob with stand art, so it can be seen and hit

const argv = process.argv.slice(2);
const has = (f) => argv.includes(f);
const ALL = has('--all');
const wantShapes = ALL || has('--shapes');
const wantFx5 = ALL || has('--fx5');
const wantActions = ALL || has('--actions');
const wantTiles = ALL || has('--tiles');
const wantHits = ALL || has('--hits');
// The sweep is long; `--limit N` keeps a re-run short without changing WHICH
// shapes are chosen, so a short run and a full run still agree on the list.
const limitArg = argv.find(a => a.startsWith('--limit='));
const LIMIT = limitArg ? Number(limitArg.slice(8)) : Infinity;

let asserts = 0, fails = 0;
const say = (ok, name, detail = '') => {
  asserts++;
  if (!ok) {
    fails++;
    console.log(`  FAIL  ${name}${detail ? ' -- ' + detail : ''}`);
    console.log(`>>> RED: browser-verify -- ${name}${detail ? ' (' + detail + ')' : ''}`);
  } else console.log(`  ok    ${name}${detail ? '  ' + detail : ''}`);
};
const heading = (s) => console.log(`\n== ${s} ==`);

const api = async (path) => (await fetch(BASE + path)).json();

/** One call in the page: open the simulator, walk to the map, arm the hook. */
async function setUp(page) {
  return page.evaluate(async ({ mapRe, skill, key, mob }) => {
    const app = window.app;
    await app.sections.open('simulator');
    const sim = await app.ensureSimulator();
    const re = new RegExp(mapRe);
    // ASK THE BACKEND FOR THE MAP, do not read it out of the app's list.
    //
    // `sim.visibleMaps()` is one PAGE of /api/simulate/maps, and that endpoint
    // returns 200 by default out of 5,366 maps -- so whether the target is present
    // depends on where it falls in the archive's order. MEASURED 2026-10-17: the
    // same harness passed four times and then failed three in a row with "no map
    // matching 100000004 appeared within 20s", on an unchanged server, because
    // 100000004 had fallen outside the page the app had loaded. Asking with `q`
    // is the same call the picker makes when a person types an id.
    const until = Date.now() + 20000;
    let target = null;
    while (Date.now() < until && !target) {
      try {
        const r = await (await fetch(`/api/simulate/maps?q=${encodeURIComponent(mapRe)}&limit=20`)).json();
        target = (r.maps ?? r).find?.(m => re.test(m.path)) ?? null;
      } catch { /* not up yet */ }
      if (!target) await new Promise(r => setTimeout(r, 200));
    }
    if (!target) throw new Error('no map matching ' + mapRe + ' answered within 20s');
    await sim.walk(target);
    const live = sim.live;

    // Every drawImage the viewer makes, tagged with whether it fell inside the
    // measured window. Nothing else can see what reached the canvas.
    //
    // Three publications are read here, because between them they are the only
    // statement the renderer makes about WHAT it drew:
    //
    //   lastEffectRect    walkview.js drawSkillEffectPass  -- a cast sprite or
    //                      a ground decal. Carries `pass`/`order`/`node`, which
    //                      is how five effects in one cast stay five things.
    //   lastMobHitRect     walkview.js drawMobHitEffectPass -- a struck mob's
    //                      own hit effect, anchored on the MOB.
    //   lastAvatarPose     walkview.js drawAvatar           -- which pose, which
    //                      frame of it, which way round.
    //
    // The blob URL the viewer builds for its own fallback art is skipped: it has
    // no archive path, and an `img.src` of `blob:` is the one case where the
    // "does this blit's source equal the rectangle that is published" test
    // cannot be made at all.
    const rects = [];
    const poses = [];
    const proto = CanvasRenderingContext2D.prototype, orig = proto.drawImage;
    let measuring = false;
    const seenPose = new Set();
    // A character-part path. The composed body, its equipment and its hair are all
// under `f1/` -- MEASURED: `f1/00002000.img/stand1/0/body`, `f1/00012000.img/
// stand1/0/head`, `f1/Hair/00030000.img/stand1/0/hair`, `f1/Coat/01040000.img/
// walk1/0/mail` -- while mobs are `f5/<id>.img/...`, map art is `f4/...` and
// every skill sprite is `f8/...`. The archive folder is the discriminator, and it
// is exact: nothing a map or a skill draws lives under `f1`.
const BODY_PART = /^f1\//;
    // What the hook SAW, as opposed to what it kept. A count of zero in the
    // column a reader expects is otherwise indistinguishable from a hook that
    // stopped firing, and "the effect never reached the canvas" is the exact
    // wording a broken hook would also produce.
    const stats = { all: 0, skill: 0, mobHit: 0, body: 0, noPath: 0, noRect: 0, otherPath: 0 };
    proto.drawImage = function (img, ...a) {
      if (measuring && img?.src) {
        stats.all++;
        // `rect`, not the raw drawImage arguments.
        //
        // `drawSkillEffectPass` publishes the WORLD rectangle it is about to fill
        // (`walkview.js` `lastEffectRect`, set before the blit, so it is current
        // here). Reading that instead of `a[0]/a[1]` is what makes the measurement
        // branch-aware: the two blits report differently -- unmirrored
        // `drawImage(img, px(left), py(top), …)`, mirrored
        // `translate(px(right),0); scale(-1,1); drawImage(img, 0, py(top), …)`
        // whose x is literally 0 -- and a judge reading arguments could measure
        // only one of them. Choosing a facing that made the other measurable moved
        // the body out of the skill's range and the cast stopped connecting.
        const R = live.view?.lastEffectRect || null;
        const H = live.view?.lastMobHitRect || null;
        let p = null;
        try { p = new URL(img.src, location.href).searchParams.get('path'); } catch { /* blob: */ }
        // The rectangle is only attributed to this blit when the blit's own
        // source IS the rectangle's own `imagePath`.
        //
        // A single-valued `lastEffectRect` stays on the view after the frame that
        // set it, so the next frame's map tile reads it too and answers as if a
        // skill effect had been drawn. Matching the two paths cannot have that
        // failure: the field is overwritten immediately before the blit it
        // describes, and the path is the same string the `Image` was built from.
        // The CONTAINER, not the frame.
        //
        // `lastEffectRect.imagePath` is the sprite's FIRST frame
        // (`.../effect/0/0`), but the viewer blits `.../effect/0/<i>` for every
        // frame of the animation. Matching the two exactly attributes ONLY frame
        // 0 to the rectangle, and the assertion that the animation advances then
        // counts one distinct position and reports the effect as frozen.
        // MEASURED 2026-10-18: the client advanced through frames 0,1,2,4,5,6,7,8
        // with `left` moving 1680 -> 1661 -> 1658, and the judge said
        // "1 distinct position(s)".
        //
        // The container is what identifies the ANIMATION; the frame index is
        // carried separately in the rectangle and checked against the archive's
        // own origin list. A single-valued `lastEffectRect` still stays on the
        // view after the frame that set it, so the container is also what keeps
        // the next frame's map tile from answering as if a skill effect had been
        // drawn.
        const containerOf = (path) => (path ? String(path).replace(/\/[^/]+$/, '') : null);
        const R1 = (R && containerOf(p) !== null && containerOf(p) === containerOf(R.imagePath)) ? R : null;
        const H1 = (H && containerOf(p) !== null && containerOf(p) === containerOf(H.imagePath)) ? H : null;
        // Whether the blit was issued mirrored, and where it landed in the frame.
        //
        // These two are read here rather than from a publication because they
        // survive a build that has none: `drawSkillEffectPass` blits an unmirrored
        // effect at `px(left)` and a mirrored one under `translate(px(right),0);
        // scale(-1,1)` at x = 0, so `a[0] === 0` IS the mirrored branch's
        // signature, and the index in this list is the frame's paint order. A
        // build with the rectangle publications answers the same questions more
        // directly; a build without them still answers them.
        const mirrored = a[0] === 0 && a[2] > 0;
        const A = live.view?.lastAvatarPose || null;
        if (A) {
          const k = `${A.state}|${A.frame}|${A.flipped}|${A.x}|${A.y}|${A.parts}`;
          if (!seenPose.has(k)) { seenPose.add(k); poses.push({ ...A }); }
        }
        if (R1 || H1) {
          stats[H1 ? 'mobHit' : 'skill']++;
          rects.push({
            src: String(img.src),
            kind: H1 ? 'mobHit' : 'skill',
            order: rects.length,
            mirrored,
            a: a.map(x => (typeof x === 'number' ? Math.round(x * 10) / 10 : null)),
            rect: H1 ? H1 : R1,
            // The CONTAINER of the blit's own source, for the same reason the
            // other branch records one: a blit that reached this branch through
            // the published rectangle has no path, and the comparison against the
            // wire's effects is by path.
            // MEASURED 2026-10-18: "undefined is not one of the wire's effects"
            // for every blit that the published rectangle matched.
            path: containerOf(p),
          });
        } else if (p && BODY_PART.test(p)) {
          // The character's own blits, kept ONLY as paint-order markers.
          //
          // They are the boundary W-076 splits on: `drawSkillEffectPass(.., true)`
          // runs before the avatar and `(.., false)` after it, so "drawn before
          // the body" and "drawn after the body" are the whole of "which side of
          // the character", with nothing published and nothing inferred about the
          // effect itself. The index in `rects` is the frame's true paint order
          // because the hook appends in call order.
          stats.body++;
          rects.push({ src: String(img.src), kind: 'body', order: rects.length,
                       mirrored, a: a.slice(0, 4) });
        } else if (p && window.__bv.mobFxPaths.has(containerOf(p))) {
          // A struck mob's hit effect, identified by its own archive path.
          //
          // The CONTAINER, not the frame: the wire carries the hit sprite's FIRST
          // frame (`.../hit/0/0`) and the renderer blits `.../hit/0/<i>`, so an
          // exact-path test names frame 0 and nothing else. MEASURED 2026-10-18:
          // the blits came back as "undefined is not one of the wire's effects"
          // because the hook could not name 90 of 90 of them.
          stats.mobHit++;
          rects.push({
            src: String(img.src), kind: 'mobHit', path: containerOf(p), order: rects.length, mirrored,
            a: a.map(x => (typeof x === 'number' ? Math.round(x * 10) / 10 : null)),
            // Where the body was when this blit was issued, so a world offset can
            // be recovered from a screen coordinate without a second sample of
            // the (moving) mob.
            at: (live.view?.mobs ?? []).map(m => ({ mobId: m.mobId, x: m.x, y: m.y })),
            rect: null,
          });
        } else if (!p) stats.noPath++;
        else if (!R && !H) stats.noRect++;
        else stats.otherPath++;
      }
      return orig.apply(this, [img, ...a]);
    };

    const post = (u, b) => fetch(u, {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(b ?? {}),
    });
    const sid = live.sessionId;
    await post(`/api/simulate/session/${sid}/invulnerable`, { value: true });
    await post(`/api/simulate/session/${sid}/revive`);
    await live.setSkills([{ skillId: skill, key }]);
    await post(`/api/simulate/session/${sid}/summon`, { mobId: mob, count: 1 });

    // and every skillId the client puts on the wire
    const sent = [];
    const of = window.fetch;
    window.fetch = function (u, o) {
      if (String(u).includes('/frames') && o?.body) {
        try { sent.push(JSON.parse(o.body).first?.skillId ?? null); } catch { /* not our shape */ }
      }
      return of.apply(this, arguments);
    };

    window.__bv = {
      live, sim, rects, sent, poses, stats,
      artReady: { bad: [] },
      // The paths a struck mob's hit effects live at, registered from the wire
      // before the measurement window opens.
      //
      // A build that publishes `lastMobHitRect` names its own blits. One that does
      // not still blits them, and the source path is on the wire: `hitEffects[i]
      // .sprite.imagePath` is the exact string the renderer's `<img>` was built
      // from, so a blit carrying it IS that effect and nothing else. Registering
      // the paths is what lets the finding survive a build that has no new code in
      // it, which is the situation this was measured in.
      mobFxPaths: new Set(),
      registerMobFx: (paths) => {
        // Containers, for the reason the hook's test gives: the wire carries one
        // frame's path and the renderer walks the whole animation.
        for (const p of paths ?? []) {
          if (!p) continue;
          window.__bv.mobFxPaths.add(String(p).replace(/\/[^/]+$/, ''));
        }
        return window.__bv.mobFxPaths.size;
      },
      // A hit effect is published for ONE frame and the server clears it on the next
      // poll, so a recorder that samples from Node at 40 ms misses it entirely --
      // measured: 6 casts of 1121008, each of which demonstrably connects
      // (1000 -> 900), and zero observations. The recorder therefore runs INSIDE
      // the page on requestAnimationFrame, which is the only clock fast enough,
      // and Node reads the result afterwards.
      armHitRecorder: (maxSamples = 400) => {
        const out = [];
        let raf = 0;
        const tick = () => {
          const mobs = live.view?.mobs ?? [];
          const hit = mobs.filter(m => (m.hitEffects ?? []).length > 0);
          if (hit.length && out.length < maxSamples) {
            for (const m of hit) {
              out.push({
                mobId: m.mobId, x: m.x, y: m.y,
                effects: (m.hitEffects ?? []).map(e => ({
                  path: e.sprite?.imagePath ?? null,
                  frames: e.sprite?.frames ?? 0,
                  origins: e.sprite?.origins ?? [],
                  z: e.z ?? 0, flipped: e.flipped === true,
                  shiftX: e.shiftX ?? 0, shiftY: e.shiftY ?? 0,
                })),
              });
            }
          }
          if (out.length < maxSamples) raf = requestAnimationFrame(tick);
        };
        raf = requestAnimationFrame(tick);
        window.__bv.hitSamples = () => { cancelAnimationFrame(raf); return out; };
        return true;
      },
      mobsAt: () => (live.view?.mobs ?? []).map(m => ({ mobId: m.mobId, x: m.x, y: m.y,
                                                      n: (m.hitEffects ?? []).length })),
      measure: (v) => { measuring = v; },
      reset: () => { rects.length = 0; poses.length = 0; seenPose.clear(); sent.length = 0;
                     for (const k of Object.keys(stats)) stats[k] = 0; },
      // Decode a skill's art BEFORE it is measured.
      //
      // `drawSkillEffectPass` will not draw an image that has not decoded, and
      // on a map that is still pulling its own background the skill's frames sit
      // behind it: MEASURED 2026-10-18, the nine 1121008 frame images were still
      // `complete === false` for the whole pose and the one that finally arrived
      // was the LAST frame. That is a real property of a cold cache and it is
      // reported (see `artReady`), but it is not what the per-frame offset rule
      // is about, so the cache is warmed explicitly and the two are separated.
      // A path that comes back 404 is remembered, because "this frame has no art
      // in the archive" and "the renderer ignored it" are different findings.
      prewarm: async (paths, timeoutMs = 8000) => {
        const bad = [], stuck = [];
        const one = (p) => new Promise((res) => {
          let settled = false;
          const done = (why) => { if (settled) return; settled = true; res(why); };
          const im = new Image();
          im.onload = () => done(im.naturalWidth ? 'ok' : 'empty');
          im.onerror = () => done('error');
          // A TIMEOUT, because `Promise.all` over images that neither load nor
          // error never settles and the whole judge stops -- which is what
          // happened: a 33-frame skill left the sweep with no output at all for
          // ten minutes, and the run looked identical to one that was merely
          // slow. A path that is still unanswered after the timeout is reported
          // as `stuck` rather than silently treated as warm.
          setTimeout(() => done('stuck'), timeoutMs);
          im.src = `/api/canvas?path=${encodeURIComponent(p)}`;
        });
        // In small batches, so a 30-frame effect does not queue 30 requests
        // behind its own siblings on a page that is already drawing a map.
        for (let i = 0; i < paths.length; i += 8) {
          const why = await Promise.all(paths.slice(i, i + 8).map(one));
          why.forEach((w, k) => {
            if (w !== 'ok') (w === 'stuck' ? stuck : bad).push(paths[i + k]);
          });
        }
        window.__bv.artReady.bad.push(...bad, ...stuck.map(p => `${p} (never answered)`));
        return { bad, stuck };
      },
      // `?? []` plus the raw body kept on the side: this endpoint answers 404
      // `{error}` for a session id the server no longer holds, and an
      // unguarded `.find` on that turned a dead session into
      // "Cannot read properties of undefined (reading 'find')" -- which names a
      // JavaScript bug and hides the actual cause.
      mobs: async () => {
        const r = await fetch(`/api/simulate/session/${sid}`);
        const j = await r.json();
        if (!Array.isArray(j.mobs)) { window.__bv.mobBody = { status: r.status, body: j }; return []; }
        return j.mobs;
      },
      pos: () => { const t = live.view?.ticks?.at(-1) || {}; return { x: t.x ?? 0, y: t.y ?? 0, dir: t.dir ?? 1 }; },
    };
    return { sessionId: sid, keys: Object.fromEntries(live.skillKeys) };
  }, { mapRe: MAP.source, skill: SKILL, key: KEY.toUpperCase(), mob: MOB });
}

/**
 * Walk into range: the box test is the part of the cast that is about the skill,
 * and a mob across the map is a miss that is correct behaviour.
 *
 * The loop lives HERE, in Node, because `page.keyboard` only exists there. Each
 * step asks the page for two numbers. Holding the key open and polling from one
 * side is what a person does, and it keeps the whole walk in real frames.
 */
async function walkIntoRange(page) {
  const gap = () => page.evaluate(async (mobId) => {
    const { mobs, pos } = window.__bv;
    const m = (await mobs()).find(x => x.mobId === mobId);
    const me = pos();
    return { gap: m ? Math.abs(m.x - me.x) : 0, meX: me.x, mobX: m ? m.x : null, dir: me.dir };
  }, MOB);

  const start = await gap();
  if (start.mobX === null) return { walkKey: '', mobs: [] };
  const walkKey = start.mobX > start.meX ? 'ArrowRight' : 'ArrowLeft';
  await page.keyboard.down(walkKey);
  const until = Date.now() + 15000;
  while (Date.now() < until) {
    await page.waitForTimeout(60);
    if ((await gap()).gap < 70) break;
  }
  await page.keyboard.up(walkKey);
  await page.waitForTimeout(300);
  const mobs = await page.evaluate(() => window.__bv.mobs()
    .then(ms => ms.map(m => ({ id: m.mobId, hp: m.hp, maxHp: m.maxHp }))));
  return { walkKey, mobs };
}

/**
 * Put the mob INSIDE the skill's own box, with the body facing it.
 *
 * `walkIntoRange` stops at a gap of 70, which is inside most boxes but not all
 * of them, and the mob PATROLS: it can walk past the body or drift out of the
 * box while the cast is being set up. A cast that misses publishes no hit
 * effect, so a judge that only walks once per attempt measures nothing and then
 * reports the absence as a defect.
 *
 * The acceptable distance is read off the PLAN's own `lt`/`rb`, which is the
 * same box the server tests, so this is not a second rule for where a hit
 * lands -- it is the same rule, asked before the cast instead of after it.
 */
async function walkIntoBox(page, plan) {
  // `lt`/`rb` are authored for a LEFT-facing body. Facing right mirrors them, so
  // the forward span is |lt| .. |rb| whichever way the character stands.
  const near = Math.min(Math.abs(plan?.ltx ?? 0), Math.abs(plan?.rbx ?? 0));
  const far = Math.max(Math.abs(plan?.ltx ?? 0), Math.abs(plan?.rbx ?? 0));
  // Stay clear of both edges: a mob inside the near edge is behind the swing.
  const lo = near + 8, hi = far - 8;
  const want = (lo + hi) / 2;

  for (let round = 0; round < 4; round++) {
    const st = await page.evaluate(async (mobId) => {
      const { mobs, pos } = window.__bv;
      const m = (await mobs()).find(x => x.mobId === mobId);
      const me = pos();
      return { gap: m ? Math.abs(m.x - me.x) : null, meX: me.x, mobX: m ? m.x : null, dir: me.dir };
    }, MOB);
    if (st.gap === null) return false;
    const inBox = st.gap >= lo && st.gap <= hi;
    // Facing the mob: `dir` is 1 for right, -1 for left.
    const facingIt = (st.mobX > st.meX) === (st.dir > 0);
    if (inBox && facingIt) return true;
    // Otherwise walk toward it and try again. The key is the direction that
    // closes the gap AND leaves the body facing the mob.
    const key = st.mobX > st.meX ? 'ArrowRight' : 'ArrowLeft';
    await page.keyboard.down(key);
    for (let i = 0; i < 8; i++) {
      await page.waitForTimeout(60);
      const g = await page.evaluate(async (mobId) => {
        const { mobs, pos } = window.__bv;
        const m = (await mobs()).find(x => x.mobId === mobId);
        const me = pos();
        return { gap: m ? Math.abs(m.x - me.x) : null, meX: me.x, mobX: m ? m.x : null, dir: me.dir };
      }, MOB);
      if (g.gap !== null && g.gap >= lo && g.gap <= hi && ((g.mobX > g.meX) === (g.dir > 0))) break;
    }
    await page.keyboard.up(key);
    await page.waitForTimeout(200);
  }
  const st = await page.evaluate(async (mobId) => {
    const { mobs, pos } = window.__bv;
    const m = (await mobs()).find(x => x.mobId === mobId);
    const me = pos();
    return { gap: m ? Math.abs(m.x - me.x) : null, meX: me.x, mobX: m ? m.x : null, dir: me.dir };
  }, MOB);
  return st.gap !== null && st.gap >= lo && st.gap <= hi
    && ((st.mobX > st.meX) === (st.dir > 0));
}

/**
 * Face one way and stay there.
 *
 * The shape sweep does not need the mob: a cast publishes its sprites on the
 * frame it starts whether or not anything is in the box, and the rectangle being
 * checked is the CASTER's, which is anchored on the caster. What it does need is
 * a known facing, because `mirrored` is part of the expectation.
 *
 * So this presses an arrow for a moment and then reports the direction the view
 * actually ended up with. The report is not decoration: a body that walked into a
 * mob mid-press comes out facing the other way, and asserting `mirrored === false`
 * on a run that silently got `true` would pass for the wrong reason on one face
 * and fail for the right reason on the other.
 */
async function faceDir(page, dir) {
  // Already facing it: do not walk. A 420 ms walk per shape is 25 shapes of pure
  // waiting, and it is also the thing that most often turns the body the other
  // way -- into a mob, into a wall -- which is how a sweep ends up measuring one
  // facing twice and the other not at all.
  if (await page.evaluate(() => window.__bv.pos().dir) === dir) {
    await page.waitForTimeout(120);
    return dir;
  }
  const key = dir > 0 ? 'ArrowRight' : 'ArrowLeft';
  await page.keyboard.down(key);
  await page.waitForTimeout(360);
  await page.keyboard.up(key);
  await page.waitForTimeout(160);
  return page.evaluate(() => window.__bv.pos().dir);
}

/** Bind one skill to the hotkey the casts use. */
const bindSkill = (page, skillId) =>
  page.evaluate(([id, key]) => window.__bv.live.setSkills([{ skillId: id, key }]), [skillId, KEY.toUpperCase()]);

/** Read everything the assertions need, in one round trip.
 *
 *  `castPath` is passed in, not closed over: `castSnap` is declared inside the
 *  `try` block below and this function is module-level, so a reference to it here
 *  is a ReferenceError rather than a default. */
async function read(page, castPath, skill = SKILL) {
  return page.evaluate(async ({ mobId, SKILL, castPath }) => {
    const { live, rects, sent, mobs } = window.__bv;
    const fx = (live.view?.ticks?.at(-1)?.skillEffects || [])[0] || null;
    // `castPath` is the art path captured DURING the pose. Reading it from the
    // live frame here answers about the frame AFTER the cast, whose effect list
    // is empty -- so "the art came from the archive" reported `undefined` on a
    // cast that drew perfectly.
    const path = castPath || fx?.imagePath || null;
    const art = path ? '/api/canvas?path=' + encodeURIComponent(path) : null;
    let artInfo = null;
    if (art) {
      const r = await fetch(art);
      const im = new Image();
      im.src = art;
      await im.decode().catch(() => {});
      // `path`, not `fx.imagePath`: `fx` is the live frame's effect, which is empty
      // after the pose ends, so naming it here threw on the runs where the cast
      // was over by the time this read happened.
      artInfo = { status: r.status, w: im.naturalWidth, h: im.naturalHeight, path };
    }
    return {
      sent: sent.filter(Boolean),
      tick: live.view?.ticks?.at(-1) || {},
      // what the page itself thought of the press, for when the cast did not land
      keyState: { skillDown: live.skillDown, latch: live.skillLatch, busy: live.busy,
                  link: live.link, session: !!live.sessionId, action: live.state?.action },
      // The CASTER's cast sprite only -- `.../skill/<id>/effect/<group>/<frame>`.
      //
      // This used to be `src.includes('112.img')`, which is the whole skill
      // book and therefore also matches `.../skill/1121008/hit/0/0` -- the MOB's
      // hit effect. That was harmless while a hit effect never reached the canvas
      // (W-084: it was published for one frame and the client drew none of it),
      // and is a measurement bug the moment it does draw: the mob's effect is
      // anchored at the mob's head and carries ITS OWN origin set, so every one
      // of its draws reads as "a frame drawn against the wrong offset".
      // MEASURED: 71x69 is the hit sprite's size and 66x37 the cast sprite's --
      // two different animations under one filter.
      skillRects: rects.filter(r => r.kind === 'skill'),
      mobRects: rects.filter(r => r.kind === 'mobHit'),
      bodyRects: rects.filter(r => r.kind === 'body'),
      poses: window.__bv.poses.slice(),
      anyRects: rects.length,
      stats: window.__bv.stats,
      scale: live.view?.scale ?? 1,
      // The camera and the character, so a recorded draw argument (a SCREEN
      // coordinate, px(x) = (x - minX) * s) can be turned back into an offset
      // from the character and compared with the archive's own origin.
      charX: (live.view?.ticks?.at(-1) ?? {}).x ?? 0,
      charY: (live.view?.ticks?.at(-1) ?? {}).y ?? 0,
      minX: live.view?.minX ?? 0,
      minY: live.view?.minY ?? 0,
      dir: (live.view?.ticks?.at(-1) ?? {}).dir ?? 1,
      art: artInfo,
      struck: (await mobs()).find(m => m.mobId === mobId),
      mobsAfter: await mobs(),
      mobBody: window.__bv.mobBody ?? null,
    };
  }, { mobId: MOB, SKILL, castPath });
}

/** The snapshot taken DURING the pose. Reading after the pose ends reports the
 *  frame AFTER the cast, whose action is `stand` and whose effect list is empty. */
const snapDuringCast = (page) => page.evaluate(() => {
  const t = window.__bv.live.view?.ticks?.at(-1) || {};
  const fx = (t.skillEffects || [])[0] || null;
  return {
    action: t.action, stance: t.skillStance, seq: window.__bv.live.state?.skillCastSeq ?? null,
    sprites: (t.skillEffects || []).length,
    frames: fx?.frames ?? 0, z: fx?.z ?? 0, speed: fx?.speed ?? 0,
    delays: fx?.delays ?? [], origins: fx?.origins ?? [], imagePath: fx?.imagePath ?? null,
    tileCount: (t.tileEffects || []).length,
  };
});

/** Press the hotkey until a cast is OBSERVED, and keep the hook armed through
 *  the whole animation. The press itself is occasionally swallowed -- measured as
 *  the same scenario green three times and red on the fourth against an
 *  unchanged server, with `skillDown` empty and nothing on the wire -- and a
 *  person pressing a hotkey does exactly this: presses again when nothing
 *  happens.
 *
 *  `nudge` is what to do between attempts. The core scenario re-walks into
 *  range because a mob that has patrolled away is a real reason for the cast to
 *  miss; the shape sweep only needs a body pointing the right way, and walking
 *  into the mob for every retry moved the facing it was trying to hold, so the
 *  two callers pass what they actually mean by "try again". */
async function castAndWatch(page, nudge = () => walkIntoRange(page), opts = {}) {
  const tries = opts.tries ?? 6;
  const poseEndMs = opts.poseEndMs ?? 15000;
  let castSeen = false;
  const seqs = new Set();
  for (let attempt = 0; attempt < tries && !castSeen; attempt++) {
    if (attempt > 0) await nudge(attempt);
    await page.keyboard.down(KEY);        // a HELD key: tick() reads skillDown on
    await page.waitForTimeout(500);      // its next frame, so a tap never lands
    await page.keyboard.up(KEY);
    castSeen = await page.waitForFunction(
      () => ((window.__bv.live.view?.ticks?.at(-1)?.action ?? '') === 'skill'
          && (window.__bv.live.view?.ticks?.at(-1)?.skillEffects || []).length > 0),
      null, { timeout: 1500 },
    ).then(() => true).catch(() => false);
  }
  const snap = await snapDuringCast(page);
  // W-083: then watch the WHOLE animation, not a frame of it. The hook stays
  // armed until the pose ends, so every frame the effect advances through is
  // recorded. Checking only the first frame is what let a per-frame offset go
  // unnoticed: frame 0's origin can be right while frames 1..n are not.
  // The frame cache is primed with `age`, so a frame whose art has not decoded is
      // not counted as "seen" -- and the number of distinct frames that DID draw
      // is the thing "played once and then nothing" is measured by.
      const frames = new Set();
      for (let t = 0; t < 10; t++) {
        await page.waitForTimeout(45);
        const s = await page.evaluate(() => {
          const live = window.__bv.live;
          const v = live?.view, t0 = v?.ticks?.at(-1) || {};
          const c = v?.skillFx;
          let ready = 0, total = 0;
          if (c) for (const [, imgs] of c) { total += imgs.length; ready += imgs.filter(i => i.naturalWidth > 0).length; }
          return { seq: live?.state?.skillCastSeq ?? null, action: t0.action ?? null,
                   frame: v?.lastEffectRect?.frame ?? null, ready, total,
                   age: v?.skillFxBorn ? Math.round(performance.now() - v.skillFxBorn) : null };
        });
        if (s.frame !== null) frames.add(s.frame);
        seqs.add(s.seq);
      }
      await page.waitForFunction(
        () => (window.__bv.live.view?.ticks?.at(-1)?.action ?? '') !== 'skill',
        null, { timeout: poseEndMs },
      ).catch(() => {});
      await page.evaluate(() => window.__bv.measure(false));
      return { castSeen, snap, frames: [...frames].sort((a, b) => a - b), seqs: [...seqs] };
}

/** The per-frame origin rule, transcribed once so every shape is checked by the
 *  same code and not by N hand-written copies of it.
 *
 *  DrawArgument.h:131-157 with `xscale = flip ? -1 : +1`:
 *    facing right (xscale +1)  left = charX - origin.x
 *    facing left  (xscale -1)  left = charX + origin.x
 *  ...and `top = charY - origin.y` on both.
 *
 *  Asserting the unmirrored half unconditionally reported a perfectly correct
 *  mirrored frame as `drawn (30, -23) vs (-30, -23)`, which is the same pixel. */
const r10 = (x) => Math.round(x * 10) / 10;
const offsetOf = (d) => (d.rect
  ? [d.rect.left - d.rect.charX, d.rect.top - d.rect.charY]
  : [NaN, NaN]);
const wantFor = (o, mirrored, s) => [r10((mirrored ? o.x : -o.x) * s), r10(-o.y * s)];
// The BLIT decides, not the rectangle. They agree, but only the blit exists on a
// build that has no rectangle publications at all, and they are two readings of
// one fact: `a[0] === 0` is the `translate(px(right),0); scale(-1,1)` branch and
// `rect.mirrored` is the flag the renderer computed for it. Disagreement between
// them would be a renderer bug, so it is reported rather than silently resolved.
const isMirrored = (d) => d.mirrored;
const mirrorAgrees = (d) => (d.rect && d.rect.mirrored !== undefined
  ? d.rect.mirrored === d.mirrored : true);

/** The whole of B1's per-frame rule, as one verdict. Returns the detail either
 *  way so a failing shape says WHICH frame was wrong and against what. */
function checkPerFrameOrigin(entries, origins, scale, label) {
  if (!entries.length || !origins?.length) {
    return { ok: false, why: `no draws (${entries.length}) or no origins (${origins?.length ?? 0})`, seen: 0 };
  }
  const seen = new Set();
  const stray = [];
  for (const d of entries) {
    const q = offsetOf(d).map(r10);
    seen.add(`${q[0]}|${q[1]}`);
    if (!d.rect) { stray.push(`${q} with no published rectangle`); continue; }
    if (!mirrorAgrees(d)) stray.push(`frame ${d.rect.frame}: blit says mirrored=${d.mirrored}, rectangle says ${d.rect.mirrored}`);
    const o = origins[d.rect.frame];
    if (!o) { stray.push(`frame ${d.rect.frame} is not in the archive`); continue; }
    const w = wantFor(o, isMirrored(d), scale);
    if (q[0] !== w[0] || q[1] !== w[1]) stray.push(`frame ${d.rect.frame}: drawn (${q}) vs (${w})`);
  }
  return {
    ok: stray.length === 0, stray, seen: seen.size,
    why: `${seen.size} distinct position(s) over ${entries.length} draws, ${origins.length} frame origin(s)`
       + (stray.length ? `; ${stray.length} wrong: ${stray.slice(0, 3).join(' ; ')}` : ''),
  };
}

const browser = await chromium.launch({ channel: 'chrome' });
try {
  const page = await browser.newPage();
  const consoleErrors = [];
  page.on('pageerror', e => consoleErrors.push(String(e)));
  page.on('console', m => { if (m.type() === 'error') consoleErrors.push(m.text()); });

  await page.goto(BASE, { waitUntil: 'domcontentloaded' });
  const setup = await setUp(page);
  const range = await walkIntoRange(page);

  // W-089. Warm this skill's art before measuring it, and REPORT the cold-cache
  // number instead of hiding it. `drawSkillEffectPass` will not draw an image that
  // has not decoded; measured on this map, the nine 1121008 frame images were
  // still pending for the whole pose on a cold cache and the only frame that
  // arrived was the last one, so the animation this section is about never ran.
  // The per-frame origin rule is a property of the RENDERER, and the cache is an
  // input, so the input is fixed and the input's own behaviour is printed.
  const corePlan = await api(`/api/skill-cast/plan?skillId=${SKILL}`);
  const corePaths = (corePlan.effects ?? []).flatMap(fx =>
    Array.from({ length: fx.frames }, (_, i) => `${fx.containerPath}/${i}`));
  const coreArt = await page.evaluate((paths) => window.__bv.prewarm(paths), corePaths);
  console.log(`  art: ${corePaths.length} frame image(s) for ${SKILL}, ` +
    `${corePaths.length - coreArt.bad.length - coreArt.stuck.length} decoded, ` +
    `${coreArt.bad.length} without art, ${coreArt.stuck.length} never answered`);

  await page.evaluate(() => { window.__bv.sent.length = 0; window.__bv.measure(true); });
  // Press until the cast is OBSERVED. The press itself is occasionally swallowed --
  // measured as the same scenario green three times and red on the fourth against
  // an unchanged server, with `skillDown` empty and nothing on the wire -- and a
  // person pressing a hotkey does exactly this: presses again when nothing
  // happens. Retrying is what the user does; it is not the harness papering over a
  // defect, and the assertion below is unchanged by it.
  //
  // There is deliberately NO "turn to face right first" step here. There was, and
  // it walked the body three frames away from the mob it had just been walked up
  // to, which put the cast outside the skill's box: "the cast took exactly one
  // test hit off the mob it was in range of -- 1000 -> 1000". The measurement was
  // changing the thing it measured. Reading `lastEffectRect` instead of the blit
  // arguments is what removed the need for it.
  //
  // Each attempt RE-CLOSES the distance first. The mob patrols: it walks away
  // while the loop is retrying, so a cast pressed after the third attempt is
  // legitimately outside the skill's box and hits nothing. That is a miss, not a
  // defect, and the assertion at the end cannot tell the two apart from the HP
  // numbers alone -- MEASURED as the same suite green three times in four and red
  // on the fourth with "1000 -> 1000". Re-approaching is the precondition the
  // first attempt had, restored for the ones after it.
  let mobsBefore = range.mobs ?? [];
  let castSeen = false;
  for (let attempt = 0; attempt < 6 && !castSeen; attempt++) {
    // Into the SKILL'S OWN box, not "within 70 px". The mob patrols, and a body
    // that stops at a fixed distance is outside the box half the time -- which is
    // the "1000 -> 1000" this loop was written to survive.
    await walkIntoBox(page, await (await fetch(`${BASE}/api/skill-cast/plan?skillId=${SKILL}`)).json());
    // The HP baseline, read as late as possible. `range.mobs` was captured before
    // this loop, and the loop can spend ten seconds walking and pressing -- long
    // enough for the mob to patrol into a place where the cast cannot reach, which
    // reads as "1000 -> 1000" and says nothing about the damage rule.
    mobsBefore = await page.evaluate(async () => {
      const r = await fetch(`/api/simulate/session/${window.__bv.live.sessionId}`);
      const j = await r.json();
      return Array.isArray(j.mobs) ? j.mobs.map(m => ({ mobId: m.mobId, hp: m.hp, maxHp: m.maxHp })) : [];
    });
    await page.keyboard.down(KEY);        // a HELD key: tick() reads skillDown on
    await page.waitForTimeout(500);      // its next frame, so a tap never lands
    await page.keyboard.up(KEY);
    castSeen = await page.waitForFunction(
      () => ((window.__bv.live.view?.ticks?.at(-1)?.action ?? '') === 'skill'
          && (window.__bv.live.view?.ticks?.at(-1)?.skillEffects || []).length > 0),
      null, { timeout: 1500 },
    ).then(() => true).catch(() => false);
  }
  // SNAPSHOT DURING the pose. Reading after the pose ends -- which is what the
  // animation sweep below requires -- reports the frame AFTER the cast, whose
  // action is `stand` and whose effect list is empty. That is not the cast; it is
  // the walk resuming, and asserting on it fails for a cast that worked.
  const castSnap = await page.evaluate(() => {
    const t = window.__bv.live.view?.ticks?.at(-1) || {};
    const fx = (t.skillEffects || [])[0] || null;
    return {
      action: t.action, stance: t.skillStance, seq: window.__bv.live.state?.skillCastSeq ?? null,
      sprites: (t.skillEffects || []).length,
      frames: fx?.frames ?? 0, z: fx?.z ?? 0, speed: fx?.speed ?? 0,
      delays: fx?.delays ?? [], origins: fx?.origins ?? [], imagePath: fx?.imagePath ?? null,
    };
  });
  // W-083: then watch the WHOLE animation, not a frame of it. The hook stays
  // armed until the pose ends, so every frame the effect advances through is
  // recorded. Checking only the first frame is what let a per-frame offset go
  // unnoticed: frame 0's origin can be right while frames 1..n are not.
  await page.waitForFunction(
    () => (window.__bv.live.view?.ticks?.at(-1)?.action ?? '') !== 'skill',
    null, { timeout: 15000 },
  ).catch(() => {});
  await page.evaluate(() => window.__bv.measure(false));

  const out = await read(page, castSnap?.imagePath ?? null);
  // the cast as it was DURING the pose, not the frame after it
  out.cast = castSnap;
  out.fxMeta = castSnap.origins.length
    ? { z: castSnap.z, frames: castSnap.frames, speed: castSnap.speed,
        delays: castSnap.delays, origins: castSnap.origins, imagePath: castSnap.imagePath }
    : null;
  out.tick = { action: castSnap.action, skillStance: castSnap.stance,
               skillEffects: new Array(castSnap.sprites) };
  // Only this run's entries. `/api/clientlog` is append-only and survives a
  // redeploy, so an error from a build that was fixed twenty minutes ago is still
  // in it and used to fail this assertion. MEASURED: the run after the
  // `lastTransform` fix still reported "200 entries, 4 of them errors" carrying a
  // `Cannot access 'cur' before initialization` from the run BEFORE it.
  await page.request.delete(`${BASE}/api/clientlog`);
  const clientlog = await (await page.request.get(`${BASE}/api/clientlog`)).json();
  const errs = (clientlog?.entries ?? []).filter(e =>
    /"kind":"(error|uncaught|unhandled)"|Error:|Exception/i.test(JSON.stringify(e)));

  console.log('=== the whole scenario in a real browser, real keys, real waiting ===');
  if (fails || process.env.BV_DIAG) {
    console.log('  keyState: ' + JSON.stringify(out.keyState));
    console.log('  range:   ' + JSON.stringify(range));
  }
  say(setup.keys[KEY.toUpperCase()] === SKILL, 'the key is bound to the skill',
      JSON.stringify(setup.keys));
  say(out.sent.includes(SKILL), 'the client put the numeric id on the wire',
      JSON.stringify(out.sent));
  say(out.tick.action === 'skill', 'the frame says the body is casting', out.tick.action);
  say(out.tick.skillStance === 'brandish1', 'the pose is the one the archive names',
      out.tick.skillStance);
  say(out.anyRects > 0, 'the viewer painted', `${out.anyRects} drawImage calls`);
  console.log('  hook:   ' + JSON.stringify(out.stats));
  if (process.env.BV_TRACE) {
    console.log('  trace:  ' + JSON.stringify(out.trace));
  }

  // ---- position: is the effect where the archive says it goes? ---------------
  // In the CHARACTER's frame, not the canvas's.
  //
  // `drawSkillEffectPass` blits `drawImage(img, px(left), py(top), iw*s, ih*s)`
  // where `px(x) = (x - view.minX) * s` (walkview.js:2898). So a recorded
  // argument is a SCREEN coordinate and the archive's `-origin` is a WORLD offset;
  // comparing them directly only ever works when the camera's left edge happens
  // to sit on the character, which it does not.
  //
  // This is not a change of what is checked. An earlier revision of this pass
  // issued the blit as `translate(px(cur.x), py(cur.y)); drawImage(img,
  // -origin.x*s, -origin.y*s, ...)`, whose arguments WERE character-relative;
  // W-088 rewrote it to the literal `DrawArgument.h` rectangle, which is the same
  // world point with a different argument space. MEASURED: the old expectation
  // asks for -30 and the draw reports 90.5, both describing the same pixel.
  if (out.skillRects.length && out.fxMeta?.origins?.length) {
    const s = out.scale;
    const origins = out.fxMeta.origins;
    const near = (a, b) => Math.abs(a - b) <= 1.5;
    // recorded draw -> offset from the character, in WORLD units. `lastEffectRect`
    // is the rectangle the renderer filled, so this is the same number whether the
    // blit was mirrored or not.
    const rel = (r) => r.rect
      ? [r.rect.left - r.rect.charX, r.rect.top - r.rect.charY]
      : [r.a[0] - (r.cx - r.ox) * r.s, r.a[1] - (r.cy - r.oy) * r.s];
    const mirrored = isMirrored;

    // DrawArgument.h:143-156: the rectangle is `pos - origin`, scaled. The anchor
    // is the character; each frame's own origin lands on it.
    const r0 = rel(out.skillRects[0]);
    const m0 = mirrored(out.skillRects[0]);
    say(near(r0[0], wantFor(origins[0], m0, s)[0]) && near(r0[1], -origins[0].y * s),
        'the first frame sits where the archive says it sits',
        `drawn (${r0[0]}, ${r0[1]}) mirrored=${m0}, vs anchor-origin at scale ${s} = ` +
        `(${wantFor(origins[0], m0, s)[0]}, ${-origins[0].y * s})`);

    // W-083: EVERY frame, not the first one. The archive gives each frame its own
    // origin -- 1121008 measures 30,23 then 49,81 then 52,81 ... -- so a viewer
    // that applied one origin to all of them would look right on frame 0 and
    // drift on every frame after it. A recorded draw is correct when its offset
    // equals ITS OWN frame's origin, scaled; anything else is a frame that was
    // drawn against the wrong offset.
    const pf = checkPerFrameOrigin(out.skillRects, origins, s, SKILL);
    say(pf.ok, 'every frame of the effect uses its OWN origin, not one repeated', pf.why);

    // and the animation actually ADVANCES, which is what "played once and then
    // nothing" looks like when the second cast reuses a stale clock.
    const first = out.skillRects[0].a;
    const last = out.skillRects.at(-1).a;
    say(pf.seen > 1 || out.skillRects.length === 1,
        'the effect animation advances through its frames',
        `${pf.seen} distinct position(s); first (${first[0]}, ${first[1]}) -> last (${last[0]}, ${last[1]})`);

    const sizes = new Set(out.skillRects.map(d => `${d.a[2]}x${d.a[3]}`));
    say(sizes.size <= out.fxMeta.frames,
        'each frame is drawn at a size the archive gives it',
        `${sizes.size} distinct size(s) across ${out.skillRects.length} draws, ` +
        `${out.fxMeta.frames} frame(s) in the effect`);
    say(out.art?.status === 200, 'the effect art really came from the archive',
        `HTTP ${out.art?.status} for ${out.art?.path}`);
  } else {
    say(false, 'the effect was actually drawn',
        `no drawImage for the effect in ${out.anyRects} calls; ` +
        `action=${out.tick.action}, sprites=${(out.tick.skillEffects || []).length}`);
  }

  // ---- the effect of the cast: a number, from a mob that was in range -------
  // ADR-0006 fixes one hit at 100 and every mob at 1000 so this is readable
  // without a damage formula.
  const before = (mobsBefore ?? []).find(m => m.mobId === MOB);
  say([...new Set((range.mobs ?? []).map(m => m.maxHp))].join() === '1000',
      'every mob on the map spawns with the test hit points',
      `maxHp: ${[...new Set((range.mobs ?? []).map(m => m.maxHp))].join()}`);
  // "exactly one mob lost exactly one test hit" -- which mob is not the point, and
  // keying it to one id makes the assertion report the patrolling mob's own
  // wander as a broken damage rule.
  const hurt = (mobsBefore ?? []).map(b => {
    const a = (out.mobsAfter ?? []).find(m => m.mobId === b.mobId);
    return a ? { mobId: b.mobId, delta: b.hp - a.hp } : null;
  }).filter(Boolean);
  const dropped = hurt.filter(h => h.delta > 0);
  // The skill's OWN hit count, not a flat 100.
  //
  //   Mob.cpp:1603-1612, 1620   std::vector<...> result(attack.hitcount);
  //   Skill.cpp:397-405          RANGED -> bulletcount; default -> attackcount
  //
  // MEASURED 2026-10-18: 1121008 carries `level/1/attackCount = 2`, so one cast
  // deals TWO hits. The simulator was dealing one (W-090) and this assertion was
  // written against that, so fixing the simulator made the JUDGE red.
  //
  // The number is the PLAN's, read fresh: it is the archive's, not the frame's.
  const skillPlan = await (await fetch(`${BASE}/api/skill-cast/plan?skillId=${SKILL}`)).json();
  const hitCount = Math.max(1, Number(skillPlan?.hitCount ?? 1) || 1);
  say(before && out.struck && dropped.length === 1 && dropped[0].delta === 100 * hitCount,
      'the cast took exactly one test hit off the mob it was in range of',
      before && out.struck
        ? `${before.hp} -> ${out.struck.hp}; hp lost across the map: ${JSON.stringify(dropped)}; ` +
          `expected ${100 * hitCount} for hitCount=${hitCount}`
        : 'the summoned mob was not on the map');

  // ==========================================================================
  // B1 -- every distinct effect SHAPE, both facings.
  // ==========================================================================
  //
  // Why a shape and not a skill: 1121008 is ONE point in a space with three
  // independent dimensions -- how many animation frames, how many effect nodes,
  // and which way the body faces. A per-frame offset rule that holds for a
  // nine-frame single-node skill can be wrong for a thirty-frame three-node one
  // and still be right for every cast anybody happened to run, which is exactly
  // how the original version of this file passed while covering one skill.
  //
  // The representatives are DERIVED, not listed, so the sweep cannot fall behind
  // the archive: every distinct `frames` value the castable set actually has gets
  // one skill, and so does every distinct node count.
  const plans = new Map();
  // ALWAYS loaded, not only when a section asks for them. Every section picks its
  // subjects out of the plan set, and `--hits` alone left `plans` empty, so B5
  // iterated over nothing and printed nothing at all -- a section that silently
  // does nothing looks exactly like a section with nothing to report.
  {
    const castable = await api('/api/skill-cast/castable');
    const all = await Promise.all(castable.skills.map(async s => {
      try { return await api(`/api/skill-cast/plan?skillId=${s.skillId}`); }
      catch { return null; }
    }));
    for (const p of all) if (p) plans.set(p.skillId, p);
  }

  if (wantShapes) {
    const byShape = new Map();       // `${fxCount}|${frames}` -> plan
    const byFrames = new Map();      // frames           -> plan
    const byCount  = new Map();      // fxCount          -> plan (fewest nodes wins)
    for (const p of plans.values()) {
      for (const fx of p.effects ?? []) {
        const n = p.effects.length;
        const k = `${n}|${fx.frames}`;
        if (!byShape.has(k)) byShape.set(k, p);
        if (!byFrames.has(fx.frames)) byFrames.set(fx.frames, p);
        const cur = byCount.get(n);
        if (!cur || (cur.effects.length === n && fx.frames < Math.min(...cur.effects.map(e => e.frames)))) byCount.set(n, p);
      }
    }
    // One skill per shape; a skill that is already in the list is not asked twice.
    const chosen = new Map();
    for (const p of [...byFrames.values(), ...byCount.values()]) chosen.set(p.skillId, p);
    console.log(`\n-- derived shape matrix -- ${plans.size} plans, ` +
      `${[...new Set([...plans.values()].flatMap(p => p.effects.map(e => e.frames)))].sort((a,b)=>a-b).join(',')} frames, ` +
      `${[...new Set([...plans.values()].map(p => p.effects.length))].sort((a,b)=>a-b).join(',')} nodes`);

    const list = [...chosen.values()];
    const running = list.length <= LIMIT ? list : list.slice(0, LIMIT);
    if (running.length < list.length) console.log(`  (--limit: ${running.length} of ${list.length})`);

    heading('B1 -- one representative per effect shape, both facings');
    let shapesOk = 0, shapesRun = 0, mirrorSeen = new Set();
    for (const p of running) {
      const pfx = p.effects;
      const tag = `${p.skillId} ${pfx.length}fx/${pfx.map(e => e.frames).join('+')}f z=${pfx.map(e => e.z).join(',')}`;
      // Progress BEFORE the work, not after it: a sweep that dies on shape 12 of
      // 25 otherwise prints nothing about shapes 1..11 either, and "the run was
      // quiet" and "the run hung on the first shape" look identical.
      process.stdout.write(`  ... ${tag}\n`);
      // Decode the art first. `drawSkillEffectPass` skips an image that has not
      // decoded, so the first cast of any skill draws nothing by construction and
      // measuring it would report a viewer that is working as written as one that
      // draws nothing.
      const warm = await page.evaluate((paths) => window.__bv.prewarm(paths),
        pfx.flatMap(fx => Array.from({ length: fx.frames },
          (_, i) => `${fx.containerPath}/${i}`)));
      if (warm.bad.length || warm.stuck.length) {
        console.log(`      art: ${warm.bad.length} without art, ${warm.stuck.length} never answered` +
          ` -- ${[...warm.bad, ...warm.stuck].slice(0, 3).join(', ')}`);
      }
      process.stdout.write('      art warmed\n');
      await bindSkill(page, p.skillId);
      process.stdout.write('      bound\n');

      const faceReports = [];
      for (const want of [1, -1]) {
        const got = await faceDir(page, want);
        await page.evaluate(() => { window.__bv.reset(); window.__bv.measure(true); });
        const { castSeen: seen, snap, frames: animFrames, seqs } = await castAndWatch(page,
          () => faceDir(page, want), { tries: 3, poseEndMs: 8000 });
        process.stdout.write(`      cast[${want > 0 ? 'R' : 'L'}] seen=${seen} seq=${JSON.stringify(seqs)} ` +
          `animFrames=${JSON.stringify(animFrames)}\n`);
        if (!seen) { faceReports.push({ want, got, verdict: 'no cast observed' }); continue; }
        const o = await read(page, snap?.imagePath ?? null, p.skillId);
        // Only this skill's OWN cast art. `/skill/<id>/effect` is the cast node
        // and `/skill/<id>/hit` is the struck mob's; both live under the same id.
        const mine = o.skillRects.filter(r =>
          decodeURIComponent(r.src).includes(`/skill/${p.skillId}/`) && !r.rect?.mobId);
        const scale = o.scale;
        const wrong = [];
        const coverage = [];
        for (const fx of pfx) {
          const draws = mine.filter(r => r.rect?.imagePath?.startsWith(fx.containerPath + '/'));
          const seenFrames = [...new Set(draws.map(d => d.rect?.frame))].sort((a, b) => a - b);
          coverage.push(`${fx.node} ${seenFrames.length}/${fx.frames}`);
          if (!draws.length) { wrong.push(`${fx.node}: never drawn`); continue; }
          const pf = checkPerFrameOrigin(draws, fx.origins, scale, fx.node);
          if (!pf.ok) wrong.push(`${fx.node}: ${pf.why}`);
        }
        mirrorSeen.add(mine.length ? (isMirrored(mine[0]) ? 'left' : 'right') : 'none');
        shapesRun++;
        const bad = wrong.length || mine.length === 0;
        if (!bad) shapesOk++;
        say(!bad,
            `${tag} [${want > 0 ? 'facing right' : 'facing left'}] every frame on its own origin`,
            bad ? (mine.length ? wrong.join(' ; ') : `no cast art drawn (dir=${got})`)
                : `${mine.length} draws; frames seen ${coverage.join(', ')}; ` +
                  `body actually faced ${got > 0 ? 'right' : 'left'}`);
      }
      if (fails && process.env.BV_DIAG) console.log('    faces: ' + JSON.stringify(faceReports));
    }
    console.log(`  shapes: ${shapesOk}/${shapesRun} face-runs clean; ` +
      `mirrored branches exercised: ${[...mirrorSeen].sort().join(',') || 'none'}`);
  }

  // ==========================================================================
  // B2 -- the five-node skills: are all five on the canvas, and in what order?
  // ==========================================================================
  if (wantFx5) {
    heading('B2 -- fx = 5 nodes: all five drawn, z order correct');
    const five = [...plans.values()].filter(p => p.effects.length === 5);
    const three = [...plans.values()].filter(p => p.effects.length === 3);
    for (const p of [...five, ...three]) {
      await page.evaluate((paths) => window.__bv.prewarm(paths),
        p.effects.flatMap(fx => Array.from({ length: fx.frames },
          (_, i) => `${fx.containerPath}/${i}`)));
      await bindSkill(page, p.skillId);
      const got = await faceDir(page, 1);
      await page.evaluate(() => { window.__bv.reset(); window.__bv.measure(true); });
      const { castSeen: seen, snap } = await castAndWatch(page);
      if (!seen) { say(false, `${p.skillId} cast`, 'no cast observed'); continue; }
      const o = await read(page, snap?.imagePath ?? null, p.skillId);
      const mine = o.skillRects.filter(r =>
        decodeURIComponent(r.src).includes(`/skill/${p.skillId}/`) && !r.rect?.mobId);

      // (1) every node reached the canvas AT ALL.
      const missing = p.effects.filter(fx =>
        !mine.some(r => r.rect?.imagePath?.startsWith(fx.containerPath + '/')));
      say(missing.length === 0,
          `${p.skillId} (${p.effects.length} nodes) every effect node reached the canvas`,
          missing.length ? `never drawn: ${missing.map(f => f.node).join(', ')}`
                         : `${new Set(mine.map(r => r.rect.imagePath)).size} distinct archive paths drawn`);

      // (2) the ORDER, and which half of the frame each node was drawn in.
      //
      // The reference's rule is one line -- z < 0 is painted BEFORE the avatar and
      // z >= 0 AFTER it (EffectLayer.cpp:24,35; Char.cpp:95,240) -- so "which
      // side" is exactly "was this drawn before or after the body". The body's own
      // blits are the marker, and they are found the only way that works on any
      // build: a character part path (`.../00012000.img/body/...`, `/hair/...`,
      // `/Cap-1/...`), which no map object and no skill effect shares.
      //
      // `pass` on the published rectangle answers this directly where it exists;
      // it is preferred, and the paint-order reading is the fallback, because a
      // build without the publication still has an answer to give.
      const bodyOrder = o.bodyRects.length ? o.bodyRects[0].order : -1;
      const lastBodyOrder = o.bodyRects.length ? o.bodyRects.at(-1).order : -1;
      const passOf = (r) => (r.rect?.pass
        ? r.rect.pass
        : (r.order < bodyOrder || (lastBodyOrder >= 0 && r.order > lastBodyOrder && r.order < bodyOrder)
          ? 'behind' : 'front'));
      const halves = new Map();
      const containerOf = (p) => (p ?? '').replace(/\/[^/]+$/, '');
      for (const r of mine) {
        // Keyed by CONTAINER, because that is what the lookup below asks with.
        // It used to be keyed by the rectangle's `node` (a build with the
        // publication) or its `imagePath` (one without) while the lookup asked
        // for `fx.containerPath`, so every lookup missed, every value was
        // `undefined`, and the `undefined` read as "not drawn on the wrong side"
        // -- a check that can never fail because its key is always wrong.
        const k = containerOf(r.rect?.imagePath);
        if (k && !halves.has(k)) halves.set(k, passOf(r));
      }
      const badHalf = p.effects.filter(fx => {
        const want = (fx.z ?? 0) < 0 ? 'behind' : 'front';
        const got = halves.get(fx.containerPath);
        // A node that never drew is already reported by the assertion above, and
        // counting it here too would report one fact twice.
        if (got === undefined) return false;
        return got !== want;
      });
      const unknownSide = p.effects.filter(fx => halves.get(fx.containerPath) === undefined);
      say(badHalf.length === 0,
          `${p.skillId} each effect node was drawn on the side its z asks for`,
          badHalf.length
            ? badHalf.map(f => `${f.node} z=${f.z} drew in the ${halves.get(f.containerPath)} pass`).join(' ; ')
            : `body drew at order ${bodyOrder}..${lastBodyOrder}; ` +
              p.effects.map(f => `${f.node}(z=${f.z})->${halves.get(f.containerPath) ?? 'not drawn'}`).join(' '));

      // (3) both halves were actually reached when the skill has both. A skill
      // whose nodes all sit at one z never runs the other half at all, and a
      // sweep that reported "z is right" for it would be reporting nothing.
      const zs = new Set(p.effects.map(f => (f.z ?? 0) < 0));
      if (zs.size > 1) {
        const ran = new Set(mine.map(passOf));
        say(ran.has('behind') && ran.has('front'),
            `${p.skillId} ran both the behind and the front pass`,
            `passes seen: ${[...ran].join(',')} (avatar body drew at order ${bodyOrder}..${lastBodyOrder})`);
      }
    }
  }

  // ==========================================================================
  // B3 -- `actions` with one entry and with two.
  // ==========================================================================
  //
  // The rule being checked is `TwoHandedAction` (SkillAction.cpp:59-71):
  //   actions[false] = src["action"]["0"]   one-handed weapon
  //   actions[true]  = src["action"]["1"]   two-handed weapon
  //   apply(): target.attack(actions[target.is_twohanded()])
  //
  // So the two entries are NOT a sequence -- they are one entry per hand shape,
  // and which one plays depends on the weapon. The question "how many pose frames
  // did a two-action skill draw" has two halves, and both are measured: WHICH
  // pose name reached the avatar, and how many distinct frames of it reached the
  // canvas.
  if (wantActions) {
    heading('B3 -- one-action and two-action skills: which pose, how many frames');
    const oneAct = [...plans.values()].find(p => p.actions.length === 1);
    const twoAct = [...plans.values()].filter(p => p.actions.length >= 2);
    const report = [];
    for (const p of [oneAct, ...twoAct].filter(Boolean)) {
      await bindSkill(page, p.skillId);
      await page.evaluate(() => window.__bv.reset());
      const before = await page.evaluate(() => window.__bv.live.state?.skillStance || null);
      await page.evaluate(() => window.__bv.measure(true));
      // Cast three times. One cast cannot tell "the pose played" from "the pose
      // is frozen on its last frame", and `TwoHandedAction` is selected per cast
      // in the reference, so a single sample is not a sample of the rule.
      const seenStances = new Set(); const poseFrames = new Set();
      let casts = 0;
      for (let k = 0; k < 3; k++) {
        await walkIntoRange(page);
        await page.keyboard.down(KEY); await page.waitForTimeout(500); await page.keyboard.up(KEY);
        const okCast = await page.waitForFunction(
          () => ((window.__bv.live.view?.ticks?.at(-1)?.action ?? '') === 'skill'),
          null, { timeout: 1500 }).then(() => true).catch(() => false);
        if (!okCast) continue;
        // ONLY poses belonging to THIS skill.
        //
        // `lastAvatarPose` is published once per frame and stays until the next
        // one, so sampling it after the pose ended returned the previous skill's
        // pose verbatim -- 11111004 (actions brandish1/brandish2) was reported
        // playing `bamboo`, which is 10001009's stance. The filter below is the
        // whole fix: the frame has to be a cast of the skill under test.
        const okSkill = await page.waitForFunction(
          (sid) => (window.__bv.live.view?.ticks?.at(-1)?.action ?? '') === 'skill'
                && window.__bv.live.state?.skillId === sid,
          p.skillId, { timeout: 1500 }).then(() => true).catch(() => false);
        if (!okSkill) continue;
        casts++;
        for (let t = 0; t < 12; t++) {
          await page.waitForTimeout(45);
          const ps = await page.evaluate((sid) => {
            const live = window.__bv.live;
            const t0 = live.view?.ticks?.at(-1) || {};
            if ((t0.action ?? '') !== 'skill' || live.state?.skillId !== sid) return null;
            const pose = live.view?.lastAvatarPose ?? null;
            return { stance: t0.skillStance || null, frame: pose?.frame ?? null };
          }, p.skillId);
          if (!ps) continue;
          if (ps.stance) seenStances.add(ps.stance);
          if (ps.frame !== null) poseFrames.add(ps.frame);
        }
      }
      await page.evaluate(() => window.__bv.measure(false));
      const got = [...seenStances].sort();
      const never = p.actions.filter(a => !seenStances.has(a));
      report.push({ skillId: p.skillId, actions: p.actions, casts, seen: got,
                    frames: [...poseFrames].sort((a, b) => a - b) });
      say(got.length > 0, `${p.skillId} a pose name reached the avatar`,
          `actions in the archive: [${p.actions.join(', ')}]; seen: [${got.join(', ')}]`);
      // The reference picks BETWEEN the two actions BY WEAPON, so with a
      // one-handed weapon exactly ONE of them is correct and the other being
      // absent is not a defect.
      //
      //   SkillAction.cpp:58-70
      //     TwoHandedAction::TwoHandedAction(src) { actions[false] = src["action"]["0"];
      //                                              actions[true]  = src["action"]["1"]; }
      //     void TwoHandedAction::apply(Char& target, Attack::Type) const {
      //         bool twohanded = target.is_twohanded();
      //         std::string action = actions[twohanded];
      //         target.attack(action); }
      //
      // This assertion used to demand that EVERY declared action be played,
      // which is the opposite of the reference -- and it contradicted the NOTE
      // printed three lines below it, which said the opposite. MEASURED
      // 2026-10-18: 1121008 and 11111004 each declare [brandish1, brandish2] and
      // were reported as failures for playing only brandish1 with a one-handed
      // sword.
      //
      // The proof that the picker is CONSULTED is the other half: equip a
      // two-handed weapon and the SECOND action has to appear. A two-handed
      // sword is prefix 140 (`Weapon.h` SWORD_2H = 140), and
      // `WeaponData.cpp:33-35` puts 140-144 and 138 and 146 in the two-handed
      // set.
      let twoHandedSeen = [];
      if (p.actions.length === 2) {
        const sid = await page.evaluate(() => window.__bv.live.sessionId);
        await page.evaluate(async ({ sid }) => {
          await fetch(`/api/simulate/session/${sid}/weapon`, {
            method: 'POST', headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ equippedWeapon: '1402000' }),
          });
        }, { sid });
        await page.waitForTimeout(300);
        const seen2 = new Set();
        await page.evaluate(() => window.__bv.measure(true));
        // Bind again: the shape sweep rebinds the hotkey for every shape it
        // visits, and a hotkey that is bound to a different skill answers the
        // press with that skill -- or with nothing at all.
        await bindSkill(page, p.skillId);
        for (let k = 0; k < 4; k++) {
          await page.keyboard.down(KEY);
          await page.waitForTimeout(420);
          await page.keyboard.up(KEY);
          await page.waitForFunction(
            (sk) => ((window.__bv.live.view?.ticks?.at(-1)?.action ?? '') === 'skill'
                  && window.__bv.live.state?.skillId === sk),
            p.skillId, { timeout: 1500 },
          ).catch(() => {});
          for (let t = 0; t < 8; t++) {
            await page.waitForTimeout(45);
            const st = await page.evaluate(() => {
              const t0 = window.__bv.live.view?.ticks?.at(-1) || {};
              return (t0.action ?? '') === 'skill' ? (t0.skillStance || null) : null;
            });
            if (st) seen2.add(st);
          }
        }
        await page.evaluate(() => window.__bv.measure(false));
        twoHandedSeen = [...seen2].sort();
        // put the one-handed weapon back for whatever runs next
        await page.evaluate(async ({ sid }) => {
          await fetch(`/api/simulate/session/${sid}/weapon`, {
            method: 'POST', headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ equippedWeapon: '1302000' }),
          });
        }, { sid });
        await page.waitForTimeout(200);
      }
      const expectOne = p.actions[0];
      const expectTwo = p.actions[1];
      say(p.actions.length === 1
            ? got.length === 1 && got[0] === expectOne
            : (got.includes(expectOne) || got.includes(expectTwo)),
          `${p.skillId} the pose it played is one the archive declares`,
          `actions in the archive: [${p.actions.join(', ')}]; seen: [${got.join(', ')}]`);
      say(p.actions.length !== 2
            || twoHandedSeen.includes(expectTwo) || twoHandedSeen.includes(expectOne),
          `${p.skillId} the two-handed weapon reaches the other pose`,
          `with a two-handed sword (1402000) seen: [${twoHandedSeen.join(', ')}], ` +
          `the archive declares [${p.actions.join(', ')}]; ` +
          `SkillAction.cpp:65-71 picks actions[is_twohanded()]`);
      say(true, `${p.skillId} pose frame count (reported)`,
          `${poseFrames.size} distinct frame(s): [${[...poseFrames].sort((a,b)=>a-b).join(', ')}] over ${casts} cast(s)`);
      if (p.actions.length === 2 && got.length === 1) {
        console.log(`  NOTE  ${p.skillId} declares 2 actions and played 1 ` +
          `(${got[0]}). SkillAction.cpp:65-71 selects between them by ` +
          `Char::is_twohanded() (Char.cpp:1036 -> CharEquips.cpp:178 -> WeaponData). ` +
          `The simulator holds no two-handed weapon in this scenario, so ` +
          `[${got[0]}] is the arming branch's answer; the [${p.actions.find(a => !seenStances.has(a))}] ` +
          `branch is only reachable with one.`);
      }
    }
    console.log('  action report: ' + JSON.stringify(report));
  }

  // ==========================================================================
  // B4 -- the ground decal's no-foothold fallback.
  // ==========================================================================
  //
  //   SkillUseEffect.cpp:248-266
  //     for (x = w_left; x <= w_right; x += step)
  //       for (ground_y : fht.get_foothold_range(x, w_top, w_bottom))
  //         show_attack_effect_at(tile[rand()], 4, {x - user.x, ground_y - user.y});
  //     if (spawned_count == 0)                       // :263
  //       show_attack_effect_at(tile[0], 4, {0, 0});  // :265  -- at the caster's feet
  //
  // The observable signature of that branch is exact and is what is asserted: a
  // decal list of length ONE whose shift is exactly (0, 0). A real spawn has a
  // shift, because the loop only produces `x - user.x` values and the only one
  // of those that is 0 is the column the caster is standing on, which also has a
  // Y offset of ±2 (the `rand() % 5 - 2` jitter at :253) unless jitter is 0.
  // So the test asks the server directly: cast every tile-bearing skill and read
  // what came back.
  if (wantTiles) {
    heading('B4 -- the no-foothold-in-the-box fallback (SkillUseEffect.cpp:263)');
    const tileSkills = [...plans.values()].filter(p => p.tileCount > 0 && !p.tileAtTarget);
    const fallbackSeen = [];
    for (const p of tileSkills) {
      await page.evaluate((paths) => window.__bv.prewarm(paths),
        (p.tiles ?? []).flatMap(t => Array.from({ length: t.frames },
          (_, i) => `${t.containerPath}/${i}`)));
      await bindSkill(page, p.skillId);
      // The tile sprites have to be warm BEFORE the cast is timed, for the same
      // reason the cast sprites do: `drawSkillEffectPass` will not draw an image
      // that has not decoded, and a decal list measured across a cast whose art
      // was still arriving reads as "no decal at all".
      await page.evaluate((paths) => window.__bv.prewarm(paths),
        (p.tiles ?? []).flatMap(t => Array.from({ length: t.frames },
          (_, i) => `${t.containerPath}/${i}`)));
      let tiles = [], castSeen = false;
      // Retry, and confirm the cast before sampling. A single unverified press --
      // which is what this used to do -- reports "0 decals" for a skill that
      // reliably produces 5, and 0 decals is exactly the number the fallback
      // would produce, so the two become indistinguishable.
      for (let attempt = 0; attempt < 4 && !castSeen; attempt++) {
        if (attempt > 0) await walkIntoRange(page);
        await page.evaluate(() => { window.__bv.reset(); window.__bv.measure(true); });
        await page.keyboard.down(KEY); await page.waitForTimeout(500); await page.keyboard.up(KEY);
        castSeen = await page.waitForFunction(
          () => ((window.__bv.live.view?.ticks?.at(-1)?.action ?? '') === 'skill'),
          null, { timeout: 1500 }).then(() => true).catch(() => false);
        if (!castSeen) continue;
        for (let t = 0; t < 20; t++) {
          await page.waitForTimeout(35);
          tiles = await page.evaluate(() => {
            const ts = window.__bv.live.view?.ticks?.at(-1)?.tileEffects ?? [];
            return ts.map(x => ({ shiftX: x.shiftX ?? 0, shiftY: x.shiftY ?? 0, z: x.z ?? 0,
                                  path: x.sprite?.imagePath ?? null }));
          });
          if (tiles.length) break;
        }
        if (!tiles.length) castSeen = false;      // a cast with no decal is not a measurement
      }
      await page.evaluate(() => window.__bv.measure(false));
      const isFallback = tiles.length === 1 && tiles[0].shiftX === 0 && tiles[0].shiftY === 0;
      if (isFallback) fallbackSeen.push(p.skillId);
      console.log(`  ${p.skillId} tiles=${p.tileCount} step=${p.tileStep} ` +
        `area=[${p.tileAreaLeft},${p.tileAreaRight}]x[${p.tileAreaTop},${p.tileAreaBottom}] -> ` +
        `${tiles.length} decal(s) ${JSON.stringify(tiles.map(t => [t.shiftX, t.shiftY]))}` +
        `${isFallback ? '   <-- THE FALLBACK' : ''}`);
      say(tiles.length > 0, `${p.skillId} the ground decal is visible`,
          `${tiles.length} decal(s) on the wire; ${p.tileCount} tile node(s) in the archive`);
    }
    // The arithmetic of the fallback, stated as an assertion about the DATA
    // rather than as a comment. `get_foothold_range(x, w_top, w_bottom)` can only
    // return nothing when nothing in the band is a surface at x; the caster is
    // standing on a surface at its own x, so the box always contains it whenever
    // `area.bottom >= 0`. Every tile area in the castable set has a positive
    // bottom, which is why the branch below never fires on a GROUNDED caster.
    const bottoms = tileSkills.map(p => p.tileAreaBottom);
    say(bottoms.every(b => b >= 0),
        'every tile area reaches at or below the caster, so a grounded cast always finds a foothold',
        `area bottoms: ${[...new Set(bottoms)].sort((a,b)=>a-b).join(',')} ` +
        `(all >= 0 means w_bottom >= caster Y, and the caster's own foothold is in the band)`);

    // HOW MANY PLACES COULD THE FALLBACK RUN AT ALL? The assertion above says a
    // grounded cast always finds something; this says how much of the map that
    // leaves, position by position, using the map's OWN geometry.
    //
    // The reproduction is the reference's own loop (SkillUseEffect.cpp:248-259)
    // over `/api/simulate/geometry`, which is the same foothold list the server
    // walks. This is a DIAGNOSTIC about the map, not a second foothold reader in
    // the product: nothing here feeds the decal, it only answers "is there a
    // spot on this map where the branch is reachable at all".
    const mapPath = (await api(`/api/simulate/maps?q=${encodeURIComponent(MAP.source)}&limit=20`))
      .maps?.find(m => MAP.test(m.path))?.path ?? null;
    if (mapPath) {
      const geo = await api(`/api/simulate/geometry?path=${encodeURIComponent(mapPath)}`);
      const fh = (geo.footholds ?? []).filter(f => !f.isWall);
      // `FoetholdPhysics.Foot.SurfaceAt` (the comment at FootholdPhysics.cs:309
      // says it is read straight off the segment, never re-derived): a foothold
      // only has a surface at x if x is inside its span.
      const surfaceAt = (f, x) => {
        const lo = Math.min(f.x1, f.x2), hi = Math.max(f.x1, f.x2);
        if (x < lo || x > hi) return null;
        return Math.round(f.y1 + (f.y2 - f.y1) * (x - f.x1) / ((f.x2 - f.x1) || 0));
      };
      // `FootholdPhysics.GetFootholdRange`: in the Y band, deduped within 8px.
      const range = (x, top, bot) => {
        const out = [];
        for (const f of fh) {
          const s = surfaceAt(f, x);
          if (s == null || s < top || s > bot) continue;
          if (!out.some(e => Math.abs(e - s) <= 8)) out.push(s);
        }
        return out;
      };
      const reach = [];
      let anyFires = 0;
      for (const p of tileSkills) {
        if (p.tileAreaFollowsFacing) continue;   // the box moves with the facing; skipped, see below
        const step = p.tileStep > 0 ? p.tileStep : 180;
        let wouldFire = 0;
        for (const f of fh) {
          const ux = Math.round((f.x1 + f.x2) / 2);
          const uy = surfaceAt(f, ux);
          if (uy == null) continue;
          let wl = ux + p.tileAreaLeft, wr = ux + p.tileAreaRight;
          let wt = uy + p.tileAreaTop, wb = uy + p.tileAreaBottom;
          if (wl > wr) [wl, wr] = [wr, wl];
          if (wt > wb) [wt, wb] = [wb, wt];
          let spawned = 0;
          for (let x = wl; x <= wr; x += step) spawned += range(x, wt, wb).length;
          if (spawned === 0) wouldFire++;
        }
        anyFires += wouldFire;
        reach.push(`${p.skillId} ${wouldFire}/${fh.length}`);
      }
      // The count, not the string: this used to test `endsWith('/0')` against
      // strings shaped `12111003 0/76`, which never ends that way, so the
      // assertion failed while printing five zeroes -- a check whose own message
      // said it had passed.
      say(anyFires === 0,
          `on ${mapPath} the fallback is unreachable at EVERY foothold`,
          `${fh.length} non-wall foothold(s) checked; would-fire counts: [${reach.join('; ')}]` +
          (tileSkills.some(p => p.tileAreaFollowsFacing)
            ? ' (5121001 skipped: its box is re-read per level and mirrored by facing)' : ''));
    }

    console.log(`  fallback branch executed by: ${fallbackSeen.join(', ') || 'NOBODY — see the assertion above'}`);
  }

  // ==========================================================================
  // B5 -- several hit effects on one mob at once.
  // ==========================================================================
  //
  // The question is whether they land on N places or on one. The renderer gives
  // each one `anchorX = mob.x + shiftX` and its OWN origin, so the answer is
  // measured: the rectangles are compared to each other, not to the archive, and
  // "they overlap" is reported as a count of distinct rectangles.
  if (wantHits) {
    heading('B5 -- several hit effects on one mob: N places, or one pile?');
    const multi = [...plans.values()]
      .filter(p => (p.hitEffects?.length ?? 0) >= 2)
      .sort((a, b) => b.hitEffects.length - a.hitEffects.length);
    const tryIds = [multi[0]?.skillId, multi[1]?.skillId, '1121008'].filter(Boolean);
    for (const id of tryIds) {
      const p = plans.get(id);
      if (!p) continue;
      await bindSkill(page, id);
      // The paths the hit effects live at are known from the PLAN before the
      // cast, so the hook can name their blits on the very frame they happen.
      await page.evaluate((ps) => window.__bv.registerMobFx(ps),
        (plans.get(id)?.hitEffects ?? []).map(h => h.imagePath).filter(Boolean));
      let observed = 0, wire = null, peakSeen = 0, allEffects = [], castsTried = 0;
      // Every attempt runs, and the peak is the maximum over all of them. Stopping at
      // the first cast that published anything would report "peak 1" for a skill
      // whose third cast stacks four, which is the one case this section exists
      // to find. `--hits-casts=N` shortens it; the default is 6.
      const HITS_CASTS = Number((argv.find(a => a.startsWith('--hits-casts=')) ?? '--hits-casts=6').slice(13));
      // Whether any of the casts actually CONNECTED. A cast that missed cannot
      // publish a hit effect, so "no hit effect was seen" means two different
      // things depending on it:
      //
      //   the mob lost HP   -> the hit effect MUST have been published; not
      //                        seeing it is a real defect and fails
      //   the mob kept its HP -> every cast missed, and the question was never
      //                        asked. Reporting that as a failure is the judge
      //                        failing a thing it did not measure.
      //
      // MEASURED 2026-10-18: 5121001, 4211004 and 1121008 each reported "a mob
      // published its hit effects on the wire -- nothing observed in 6 casts",
      // with the judge's OWN message saying "nothing below can be measured, and
      // that is the answer" and then failing anyway.
      let anyCastConnected = false;
      for (let attempt = 0; attempt < HITS_CASTS; attempt++) {
        castsTried++;
        // The mob has to be ALIVE and present. It starts at 1000 HP and every
        // landed cast takes 100 (ADR-0006), so a run that casts this section
        // twenty times kills it -- and a dead mob is not in the frame, so the
        // walk finds nothing and every later cast misses. Re-summoning keeps the
        // fixture the same for each attempt instead of letting the first
        // attempts spend it.
        await page.evaluate(async (mobId) => {
          const sid = window.__bv.live.sessionId;
          const r = await fetch(`/api/simulate/session/${sid}`);
          const j = await r.json();
          const m = (j.mobs || []).find(x => x.mobId === mobId);
          if (m && m.hp > 0) return;
          await fetch(`/api/simulate/session/${sid}/summon`, {
            method: 'POST', headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ mobId, count: 1 }),
          });
        }, MOB);
        // Every attempt starts by putting the mob INSIDE this skill's own box,
        // facing it. See walkIntoBox.
        await walkIntoBox(page, p);
        const hpBefore = await page.evaluate(async (mobId) => {
          const r = await fetch(`/api/simulate/session/${window.__bv.live.sessionId}`);
          const j = await r.json();
          const m = (j.mobs || []).find(x => x.mobId === mobId);
          return m ? m.hp : null;
        }, MOB);
        await page.evaluate(() => { window.__bv.reset(); window.__bv.measure(true); });
        await page.evaluate(() => window.__bv.armHitRecorder());
        await page.keyboard.down(KEY); await page.waitForTimeout(500); await page.keyboard.up(KEY);
        const okCast = await page.waitForFunction(
          () => ((window.__bv.live.view?.ticks?.at(-1)?.action ?? '') === 'skill'),
          null, { timeout: 1500 }).then(() => true).catch(() => false);
        if (!okCast) continue;
        await page.waitForFunction(
          () => (window.__bv.live.view?.ticks?.at(-1)?.action ?? '') !== 'skill',
          null, { timeout: 8000 }).catch(() => {});
        const hpAfter = await page.evaluate(async (mobId) => {
          const r = await fetch(`/api/simulate/session/${window.__bv.live.sessionId}`);
          const j = await r.json();
          const m = (j.mobs || []).find(x => x.mobId === mobId);
          return m ? m.hp : null;
        }, MOB);
        if (hpBefore !== null && hpAfter !== null && hpAfter < hpBefore) anyCastConnected = true;
        // Register the paths BEFORE reading the blits, so the hook could have
        // named them; a hit effect's blit is one frame long and re-running the
        // cast to catch it would be measuring a different cast.
        const samples = await page.evaluate(() => window.__bv.hitSamples?.() ?? []);
        if (!samples.length) continue;
        // MAXIMUM CONCURRENT EFFECTS over the whole cast, not the busiest single
        // frame. "Do they pile up" is a question about how many are on the mob AT
        // ONCE, and a frame-by-frame maximum is the only reading that answers it:
        // averaging, or taking one arbitrary frame, would report 1 for a skill
        // that stacks three for four frames and one for the rest.
        const peak = samples.reduce((m, s) => Math.max(m, s.effects.length), 0);
        const busiest = samples.find(s => s.effects.length === peak);
        observed = Math.max(observed, peak);
        peakSeen = Math.max(peakSeen, peak);
        allEffects = [...new Map([...allEffects, ...busiest.effects]
          .filter(e => e.path).map(e => [e.path.replace(/\/[^/]+$/, ''), e])).values()];
        wire = { mobId: busiest.mobId, mobX: busiest.x, effects: busiest.effects };
        await page.evaluate((ps) => window.__bv.registerMobFx(ps), allEffects.map(e => e.path));
      }
      await page.evaluate(() => window.__bv.measure(false));
      const o = await read(page, null, id);
      const mine = o.mobRects;
      // Every ANIMATION, not just the frame the busiest sample happened to catch.
      // A hit sprite is a container (`.../hit/0`) walked by frame; comparing a
      // blit of `.../hit/0/1` against a wire sample whose path is `.../hit/0/0`
      // says "not one of the wire's effects" about a blit that is exactly that
      // effect one frame later, which is what 170 of 180 blits were reported as.
      const shapes = allEffects;

      // Whether the question was ASKED at all: a cast that never connected never
      // published a hit effect, so "none seen" is only a defect if a cast landed.
      const asked = anyCastConnected;
      const notMeasured = 'NOT MEASURED: every cast missed (the mob kept its HP), so the ' +
        'hit-effect question was never asked -- that is a missing fixture, not a defect';
      say(observed > 0 || !asked, `${id} a mob published its hit effects on the wire`,
          observed > 0
            ? `${observed} effect(s) on mob ${wire.mobId} at x=${wire.mobX}; ` +
              `${shapes.length} distinct sprite path(s)`
            : notMeasured);
      say(mine.length > 0 || !asked, `${id} the struck mob's hit effects reached the canvas`,
          asked
            ? `${observed} on the wire, ${mine.length} blit(s) matched by archive path` +
              (mine.length === 0 && observed > 0 ? '  <-- published but never blitted' : '')
            : notMeasured);

      // THE PILE QUESTION, and the rule that answers it.
      //
      //   Mob.cpp:1536-1559   show_effect: shift = get_head_position(...), and the
      //                       SAME shift goes to every hit effect on that mob.
      //   DrawArgument.h:131  rlt = pos - center - origin, so each sprite is then
      //                       placed by ITS OWN origin, not by the anchor.
      //
      // So the reference anchors all of them on the mob's head too. They are only
      // a visual pile if their ORIGINS are the same as well, and the check is
      // that distinction: one shared anchor is correct, N identical rectangles is
      // the defect.
      const anchors = [...new Set(shapes.map(e => `${e.shiftX},${e.shiftY}`))];
      const expected = shapes.flatMap(e => (e.origins ?? []).map((org, i) => ({
        path: e.path, frame: i, mirrored: e.flipped,
        offX: r10((e.flipped ? org.x : -org.x)), offY: r10(-org.y),
      })));
      const distinctRects = new Set(expected.map(e => `${e.offX},${e.offY}`));
      // THE PILE QUESTION. The plan publishes as many as 39 hit sprites for one
      // skill (5121001), but the timeline staggers them, so the question is how
      // many are on a mob AT ONCE -- `peakSeen`, the maximum over every frame of
      // every cast. One at a time means there is nothing to pile up.
      say(true, `${id} hit effects never pile: peak concurrent per mob (reported)`,
          `peak ${peakSeen} on one mob across ${castsTried} cast(s); ` +
          `${shapes.length} distinct animation(s); ` +
          `${anchors.length} distinct anchor(s) [${anchors.join(' | ')}]; ` +
          `${distinctRects.size} distinct offset(s) the archive's origins allow`);
      say(anchors.length === 1 || shapes.length <= 1,
          `${id} every hit effect on the mob shares ONE anchor (Mob.cpp:1536-1559)`,
          `${anchors.length} distinct anchor(s) over ${shapes.length} animation(s): [${anchors.join(' | ')}]`);

      // Now: did the renderer actually put them at N distinct rectangles, or did
      // it collapse them onto one? Every observed blit is checked against the set
      // its own sprite's origins allow.
            const stray = [];
      const seenRects = new Set();
      const norm = (p) => (p ?? '').replace(/\/[^/]+$/, '');
      for (const b of mine) {
        // The blit's path is a CONTAINER (the hook strips the frame) and the
        // wire's is a frame path, so one side is normalised and the other is
        // compared as it is. Normalising BOTH stripped a segment off each: the
        // blit's `.../hit/0` became `.../hit` and matched nothing.
        // MEASURED 2026-10-18: "undefined is not one of the wire's effects" for
        // every blit of every skill.
        const e = shapes.find(x => norm(x.path) === b.path || norm(x.path) === norm(b.path));
        if (!e) { if (stray.length < 3) stray.push(`${b.path} is not one of the wire's effects`); continue; }
        const at = b.at?.find(m => m.mobId === wire.mobId);
        if (!at) continue;
        seenRects.add(`${containerOf(b.path)}|${b.a[0]}|${b.a[1]}`);
      }
      console.log(`  ${id}: peak ${peakSeen} hit effect(s) on one mob at once, ` +
        `over ${shapes.length} animation(s), anchors [${[...new Set(shapes.map(e => `${e.shiftX},${e.shiftY}`))].join(' | ')}], ` +
        `${expected.length} (effect,frame) origin(s) in the archive giving ` +
        `${distinctRects.size} distinct offset(s); ${mine.length} blit(s) observed`);
      say(stray.length === 0,
          `${id} every observed hit-effect blit is one the archive's own origins allow`,
          stray.length ? stray.slice(0, 3).join(' ; ')
            : `${distinctRects.size} distinct offset(s) the ${shapes.length} effect(s) can occupy`);
    }
  }

  // An /api/canvas 404 is an archive gap, not a defect in the page: the sweep
  // discovers which paths have no art and says so by name below, and counting it
  // here as well would report the same fact twice with the vaguer of the two
  // messages. Everything else -- a thrown TypeError, a failed assertion, a failed
  // session poll -- still fails here, which is what the check is for.
  const realErrors = consoleErrors.filter(e => !/\b404\b/.test(e));
  say(realErrors.length === 0, 'the page raised no error of its own',
      realErrors.length ? realErrors[0].slice(0, 160)
        : `clean (${consoleErrors.length - realErrors.length} /api/canvas 404(s), reported below)`);
  const missingArt = await page.evaluate(() => [...new Set(window.__bv.artReady.bad)]);
  if (missingArt.length) {
    console.log(`  art gaps: ${missingArt.length} path(s) the archive has no picture for --`);
    for (const p of missingArt.slice(0, 12)) console.log(`    ${p}`);
  } else if (wantShapes || wantFx5) {
    console.log('  art gaps: none -- every effect frame in every swept skill decoded');
  }
  say(errs.length === 0, 'the client log holds no error',
      `${clientlog?.count ?? 0} entries, ${errs.length} of them errors`);

  const shot = join(tmpdir(), 'browser-verify.png');
  writeFileSync(shot, await page.screenshot());
  console.log(`  screenshot: ${shot}`);
} catch (e) {
  fails++;
  console.log(`>>> RED: browser-verify -- harness error: ${e.stack || e.message}`);
} finally {
  await browser.close();
}

console.log(`\nVERDICT: browser-verify ${asserts} assertions, ${fails} failed`);
process.exit(fails ? 1 : 0);