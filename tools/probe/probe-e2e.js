(async () => {
  const app = window.app;
  const sim = app?.simulator;
  if (!sim) return { error: 'no simulator' };
  const sleep = (ms) => new Promise(r => setTimeout(r, ms));

  // NOTE ON THE RED MARKER, and it is a trap worth naming (F6).
  //
  // This file uses BOTH styles, and they land in DIFFERENT streams:
  //   - the RETURNED errors below (`return { error: ... }`) come back as the
  //     value cdp-eval prints, so they are on STDOUT;
  //   - the THROWN `>>> RED:` messages (a `throw new Error(...)`) are caught
  //     by tools/probe/cdp-eval.mjs:25-28, which prints them on STDERR
  //     (`EXC: ...`) and exits 1, leaving stdout EMPTY.
  //
  // The `>>> RED` convention in this repository is a STDOUT one
  // (tools/verify/AGENTS.md rule 14: the gate greps the suite's stdout). So
  // a red from a THROWN marker here is invisible to a stdout-only reader and
  // only the EXIT CODE carries it. This probe has no downstream today, so it
  // costs nothing -- but if it is ever wired into a gate, the red has to be
  // read from the exit code, or the gate will read it as green.

  // The archive id is handed out in the order the archives were opened, so 'f1'
  // is Map.nx on one instance and something else on another. Resolve it by
  // NAME. This helper uses the RETURNED-error style, so its marker comes back
  // as the value cdp-eval prints -- STDOUT. (The THROWN style lands on
  // STDERR; see the note at the top of this file.)
  const archiveId = async (name) => {
    let files;
    try { files = await (await fetch('/api/files')).json(); }
    catch (exc) { return { error: `>>> RED: could not list the open archives on this instance: ${exc.message ?? exc}` }; }
    const hit = (files ?? []).find((f) => f && f.name === name);
    if (!hit) return { error: `>>> RED: ${name} is not open on this instance, so there is nothing to judge` };
    if (!hit.id) return { error: `>>> RED: ${name} is listed by this instance but carries no id` };
    return { id: hit.id };
  };
  const mapArchive = await archiveId('Map.nx');
  if (mapArchive.error) { console.log(mapArchive.error); return mapArchive; }

  const open = await fetch('/api/mapedit/open', {
    method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ path: `${mapArchive.id}/Map/Map1/109050001.img` }),
  });
  if (!open.ok) return { error: 'open ' + open.status };

  const map = (sim.maps ?? []).find((m) => String(m.id) === '109050001');
  if (!map) return { error: 'map missing' };
  await sim.walk(map);

  await sim.live?.start?.({ path: sim.viewer.path });
  await sleep(1200);

  const fire = (type, code) => window.dispatchEvent(
    new KeyboardEvent(type, { code, key: code, bubbles: true }));
  fire('keydown', 'ArrowRight');
  await sleep(1800);
  fire('keyup', 'ArrowRight');
  await sleep(600);

  const v = sim.viewer;
  const tick = v.ticks?.[v.ticks.length - 1];
  if (!tick) return { error: 'no tick after walk' };

  const want = tick.action === 'walk' ? 'walk1' : 'stand1';
  if (v.avatar.part?.state !== want) {
    v.avatarState = '';
    const ok = await v.avatar.load(want);
    if (!ok) return { error: 'load ' + want, reason: v.avatar.reason };
    v.avatarState = want;
  }

  v.ticks = [tick];
  v.frame = 0;
  v.zoom = 1.6;
  for (let i = 0; i < 800; i++) v.follow(tick.x, tick.y);

  const c = v.canvas, g = c.getContext('2d');
  const part = v.avatar.part;
  part.time = -1e9;
  part.index = 0;

  v.draw();
  const withA = g.getImageData(0, 0, c.width, c.height).data.slice();
  const real = v.drawAvatar;
  let calls = 0;
  v.drawAvatar = function (...a) { calls++; return real.apply(this, a); };
  v.draw();
  const observed = calls;
  v.drawAvatar = function () {};
  v.draw();
  const withoutA = g.getImageData(0, 0, c.width, c.height).data.slice();
  v.drawAvatar = real;

  let diff = 0, bx0 = Infinity, by0 = Infinity, bx1 = -Infinity, by1 = -Infinity;
  for (let i = 0; i < withA.length; i += 4) {
    if (withA[i] !== withoutA[i] || withA[i+1] !== withoutA[i+1] ||
        withA[i+2] !== withoutA[i+2] || withA[i+3] !== withoutA[i+3]) {
      diff++;
      const p = i / 4;
      const x = p % c.width, y = Math.floor(p / c.width);
      if (x < bx0) bx0 = x; if (x > bx1) bx1 = x;
      if (y < by0) by0 = y; if (y > by1) by1 = y;
    }
  }
  part.time = 0;
  v.draw();

  const view = v.viewBox;
  const s = view.scale;
  const px = (x) => (x - view.minX) * s;
  const py = (y) => (y - view.minY) * s;
  // The viewer resolves the facing the same way when it draws (walkview.js:4583),
  // so the probe must not resolve it differently.
  const dir = tick.dir ?? (tick.facing >= 0 ? 1 : -1);
  // The frame the viewer actually drew. The product publishes it
  // (walkview.js:4596-4607, `frame: index`), and that `index` is the ARRAY
  // index into `part.frames` -- the same index `avatar.partsAt(index)` and
  // `avatar.placement(..., index)` take (avatar.js:590, :616).
  //
  // The ELEMENTS below are read from THAT frame, not from frame 0. Before,
  // the placement came from `drawnFrame` while the elements came from
  // `part.frames[0]`, so the published `placement` box (w/h/originX/originY
  // come from the drawn frame) and the published rectangles (from frame 0)
  // could describe two different frames, while the comment above claimed
  // they described the drawn one. MEASURED 2026-10-23: `drawnFrame` is 0 in
  // this run, so the numbers are identical either way -- what changed is that
  // the code now does what the comment says.
  const drawnFrame = v.lastAvatarPose?.frame ?? part.index ?? 0;
  const fr = part.frames[drawnFrame];
  if (!fr) {
    throw new Error('>>> RED: the viewer drew frame ' + drawnFrame + ' but the avatar publishes only '
      + part.frames.length + ' frame(s)');
  }

  // WHICH shape this frame carries.
  //
  // MEASURED on a private instance (2026-10-23, avatar frame 0 of stand1): the
  // composed look publishes `parts` -- 16 of them, each with
  // path/width/height/originX/originY/screen -- and the FRAME itself carries
  // neither `layers` nor `originX`/`originY`/`width`/`height`. avatar.js:590
  // reads `frames[index].parts`, so that is the shape an avatar has.
  //
  // The other two shapes belong to OTHER subjects and cannot occur here: a
  // `layers` stack is an ANIMATION concept (animation.js:115-119) and a WZ
  // single-canvas frame is a third thing again. This probe reads only
  // `v.avatar.part` (above), and avatar.js:486-488 keeps only the frames that
  // HAVE parts:
  //   const frames = (look.frames?.length ? look.frames : [look])
  //     .filter((f) => f.parts?.length)
  // so every frame in `part.frames` has a non-empty `parts` (MEASURED:
  // everyFrameHasNonEmptyParts = true, anyFrameHasLayers = false).
  //
  // The old `layers` branch was therefore DEAD code, and its comment cited
  // walkview.js:4050 -- which is drawMob(), a MOB's single canvas -- for an
  // animation's layer blit (animation.js:218), and credited that path with a
  // mirror it does not perform. It is gone rather than defended: what stays
  // is the guard below, which CAN fire.
  const items = fr.parts;
  if (!items || !items.length) {
    throw new Error('>>> RED: avatar frame ' + drawnFrame + ' carries no parts, so there is nothing to place; frame keys: '
      + Object.keys(fr).join(','));
  }

  const at = v.avatar.placement(tick.x, tick.y, dir, drawnFrame);

  // One element's rectangle in WORLD units, in the product's own arithmetic:
  // avatar.js rectOf() (avatar.js:680-697). The part's `screen` offset is
  // already anchored to the character (the feet sit at screen.y = 0) and the
  // mirror negates the horizontal offset and nothing else.
  //
  // It is a TRANSCRIPTION of the product's arithmetic, not a call into it:
  // the probe is a second implementation on purpose, so that a change in the
  // product shows up as a difference between the two.
  const rectOfItem = (it) => {
    const w = it.width ?? 0;
    const h = it.height ?? 0;
    const dx = it.screen?.x ?? 0;
    const dy = it.screen?.y ?? 0;
    return { x: at.flip ? at.cx - dx - w : at.cx + dx, y: at.cy + dy, w, h };
  };
  const rects = items.map(rectOfItem);

  // The lowest world line the avatar covers. The old code took the LAST layer
  // and called it "the body"; on an avatar there is no such element -- the body
  // is one part among sixteen -- so the bottom is the union's bottom edge.
  const bottom = rects.length ? Math.max(...rects.map((r) => r.y + r.h)) : at.cy;

  // A probe that cannot measure must say so instead of publishing zeros or
  // NaN (rule 1). Every number below comes from one of these.
  const finite = (n) => typeof n === 'number' && Number.isFinite(n);
  if (!rects.length || !finite(bottom) ||
      [at.cx, at.cy, at.w, at.h, at.originX, at.originY].some((n) => !finite(n)) ||
      rects.some((r) => ![r.x, r.y, r.w, r.h].every(finite))) {
    throw new Error('>>> RED: the avatar placement could not be measured (a non-finite number): '
      + JSON.stringify({ drawnFrame, at, rects: rects.slice(0, 3), bottom }));
  }
  const nameOf = (it) => (it.path ? String(it.path).split('/').pop() : '(no path)');

  const data = g.getImageData(0, 0, c.width, c.height).data;
  let nonBlank = 0;
  for (let i = 0; i < data.length; i += 4) {
    if (data[i] || data[i+1] || data[i+2]) nonBlank++;
  }

  // The union of the computed rectangles, in the canvas' own units, so the
  // numbers the probe computes can be compared with the pixels the avatar
  // actually changed. The measured box is in DEVICE pixels and the computed
  // one in CSS pixels, so the device ratio is carried with them.
  //
  // WHAT THE RELATION BETWEEN THE TWO IS NOT: the measured box is NOT
  // necessarily inside the computed union. It is the PIXEL GRID footprint of
  // the analytic union, so it rounds outward by up to about half a device
  // pixel per edge. MEASURED 2026-10-23 at dpr=1 on a private instance: the
  // measured box sat 0.8 px ABOVE the union's top, 0.6 px BELOW its bottom and
  // 0.2 px past its right edge (0 on the left). A second measurement, on
  // another instance, put the bottom 0.4 px below and, at dpr=2, the left and
  // the right 0.1 px outside. The code asserts nothing about the relation, and
  // nothing should: a check of the form "measured inside computed" would go
  // red on a correct probe.
  const dpr = window.devicePixelRatio || 1;
  const ux0 = Math.min(...rects.map((r) => px(r.x)));
  const uy0 = Math.min(...rects.map((r) => py(r.y)));
  const ux1 = Math.max(...rects.map((r) => px(r.x) + r.w * s));
  const uy1 = Math.max(...rects.map((r) => py(r.y) + r.h * s));

  return {
    map: v.path,
    canvas: [c.width, c.height],
    scale: +s.toFixed(3), zoom: v.zoom,
    cam: { x: +v.cam.x.toFixed(2), y: +v.cam.y.toFixed(2) },
    tick: { x: +tick.x.toFixed(3), y: +tick.y.toFixed(3), action: tick.action,
            dir, onGround: tick.onGround },
    avatar: { ready: !!v.avatar.ready, state: part.state, frames: part.frames.length,
              bitmaps: v.avatar.bitmaps.size, missing: v.avatar.missingLayers ?? [] },
    placement: at,
    placementTypes: Object.fromEntries(Object.entries(at).map(([k, x]) => [k, typeof x])),
    // The shape this probe measured. It is 'parts' and cannot be anything else
    // here (see the resolver above); the field stays because the value it
    // published before is the value it publishes now.
    frameShape: 'parts',
    elementCount: items.length,
    drawnFrame,
    avatarPose: v.lastAvatarPose ?? null,
    // `layerRects` now describes the elements of the frame that was DRAWN: the
    // avatar's `parts` (16 rects -- the composed look) and not `layers`, which
    // is a different thing (one resource's stacked canvases, animation.js:119).
    layerRects: rects.map((r, i) => ({ part: nameOf(items[i]),
      world: [+r.x.toFixed(2), +r.y.toFixed(2), +r.w.toFixed(2), +r.h.toFixed(2)],
      screen: [+px(r.x).toFixed(1), +py(r.y).toFixed(1), +(r.w * s).toFixed(1), +(r.h * s).toFixed(1)] })),
    drawAvatarCalls: observed,
    pixelsChangedByAvatar: diff,
    avatarRectScreen: diff ? { x: bx0, y: by0, w: bx1 - bx0 + 1, h: by1 - by0 + 1 } : null,
    dpr,
    computedUnionScreen: { x: +ux0.toFixed(1), y: +uy0.toFixed(1), w: +(ux1 - ux0).toFixed(1), h: +(uy1 - uy0).toFixed(1) },
    measuredBoxCss: diff ? { x: +(bx0 / dpr).toFixed(1), y: +(by0 / dpr).toFixed(1),
      w: +((bx1 - bx0 + 1) / dpr).toFixed(1), h: +((by1 - by0 + 1) / dpr).toFixed(1) } : null,
    nonBlank, totalPx: data.length / 4,
    feet: {
      tickY: tick.y,
      avatarBottomWorld: +bottom.toFixed(6),
      deltaFromTickY: +(bottom - tick.y).toFixed(6),
      screenTickY: +py(tick.y).toFixed(1),
      screenAvatarBottomY: +py(bottom).toFixed(1),
    },
  };
})()