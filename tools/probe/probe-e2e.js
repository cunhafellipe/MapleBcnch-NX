(async () => {
  const app = window.app;
  const sim = app?.simulator;
  if (!sim) return { error: 'no simulator' };
  const sleep = (ms) => new Promise(r => setTimeout(r, ms));

  // The archive id is handed out in the order the archives were opened, so 'f1'
  // is Map.nx on one instance and something else on another. Resolve it by
  // NAME. A browser snippet has no exit code, so the red marker goes to the
  // page console AND comes back as the value cdp-eval prints.
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
  const fr = part.frames[0];

  // WHICH shape this frame carries.
  //
  // Measured on a private instance (2026-10-23, avatar frame 0 of stand1): the
  // composed look publishes `parts` -- 16 of them, each with
  // path/width/height/originX/originY/screen -- and the FRAME itself carries
  // neither `layers` nor `originX`/`originY`/`width`/`height`. avatar.js:590
  // reads `frames[index].parts`, so that is the shape an avatar has.
  //
  // The other two shapes are real too and stay in the resolver: an NX
  // animation frame publishes `layers` (animation.js:119) and a WZ
  // single-canvas frame IS the frame. The old code read `layers` only, so on
  // an avatar it fell back to `[fr]` and every field it then read was
  // undefined: `L.path.split` threw at :121, and `body.originY`/`body.height`
  // would have made `bottom` a NaN at :102 even if the throw were removed.
  const shape = fr.parts?.length ? 'parts' : fr.layers?.length ? 'layers' : 'frame';
  if (shape === 'frame') {
    throw new Error('>>> RED: avatar frame 0 carries neither parts nor layers, so there is nothing to place; frame keys: '
      + Object.keys(fr).join(','));
  }
  const items = shape === 'parts' ? fr.parts : fr.layers;

  // The frame the viewer actually drew. The product publishes it
  // (walkview.js:4596), so the probe's numbers describe that frame and not a
  // guess about which one the clock was on.
  const drawnFrame = v.lastAvatarPose?.frame ?? part.index ?? 0;
  const at = v.avatar.placement(tick.x, tick.y, dir, drawnFrame);

  // One element's rectangle in WORLD units, in the product's own arithmetic.
  //
  //   parts  -> avatar.js rectOf(): the part's `screen` offset is already
  //             anchored to the character (the feet sit at screen.y = 0) and
  //             the mirror negates the horizontal offset and nothing else.
  //   layers -> walkview.js:4050: the canvas is blitted top-left at
  //             (x - originX, y - originY), and the mirror flips the PIXELS
  //             about the origin node, so the rectangle itself does not move.
  const rectOfItem = (it) => {
    const w = it.width ?? 0;
    const h = it.height ?? 0;
    if (shape === 'parts') {
      const dx = it.screen?.x ?? 0;
      const dy = it.screen?.y ?? 0;
      return { x: at.flip ? at.cx - dx - w : at.cx + dx, y: at.cy + dy, w, h };
    }
    const ox = it.originX ?? 0;
    const oy = it.originY ?? 0;
    return { x: at.cx - ox, y: at.cy - oy, w, h };
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
      + JSON.stringify({ shape, at, rects: rects.slice(0, 3), bottom }));
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
    frameShape: shape,
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