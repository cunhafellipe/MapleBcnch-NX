(async () => {
  const app = window.app;
  const v = app?.simulator?.viewer;
  if (!v) return { error: 'no viewer' };

  const sleep = (ms) => new Promise(r => setTimeout(r, ms));
  try { app.simulator.live?.stop?.(); } catch (e) { /* not running */ }

  // draw() reads this.ticks[this.frame], so the trace frame index has to point
  // at the tick being measured -- not just at a one-element array.
  const tick = v.ticks?.[v.ticks.length - 1] ?? null;
  if (!tick) return { error: 'no tick' };

  const want = tick.action === 'walk' ? 'walk1' : 'stand1';
  if (v.avatar.part?.state !== want) {
    v.avatarState = '';
    const ok = await v.avatar.load(want);
    if (!ok) return { error: 'load failed', want, reason: v.avatar.reason };
    v.avatarState = want;
  }

  v.ticks = [tick];
  v.frame = 0;
  v.zoom = 1.6;
  for (let i = 0; i < 800; i++) v.follow(tick.x, tick.y);

  const c = v.canvas, g = c.getContext('2d');
  const part = v.avatar.part;

  // Freeze the frame clock so both passes draw the identical pose.
  part.time = -1e9;
  part.index = 0;

  v.draw();
  const withA = g.getImageData(0, 0, c.width, c.height).data.slice();

  const real = v.drawAvatar;
  let calls = 0;
  v.drawAvatar = function (...a) { calls++; return real.apply(this, a); };
  v.draw();
  const observedCalls = calls;
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
  await sleep(120);

  const view = v.viewBox;
  const s = view.scale;
  const px = (x) => (x - view.minX) * s;
  const py = (y) => (y - view.minY) * s;
  const dir = tick.dir ?? 1;
  const at = v.avatar.placement(tick.x, tick.y, dir);
  const frameObj = part.frames[0];
  const anchorX = part.anchorX ?? frameObj.originX;
  const anchorY = part.anchorY ?? frameObj.originY;
  const layers = frameObj.layers?.length ? frameObj.layers : [frameObj];
  const body = layers[layers.length - 1];
  const bottom = at.y + (anchorY - body.originY) + body.height;

  const data = g.getImageData(0, 0, c.width, c.height).data;
  let nonBlank = 0;
  for (let i = 0; i < data.length; i += 4) {
    if (data[i] || data[i+1] || data[i+2]) nonBlank++;
  }

  const layerRects = layers.map((L) => {
    const lx = at.flip
      ? at.x + at.w - (anchorX - L.originX) - L.width
      : at.x + (anchorX - L.originX);
    const ly = at.y + (anchorY - L.originY);
    return { part: L.path.split('/').pop(),
             screen: [+px(lx).toFixed(1), +py(ly).toFixed(1),
                      +(L.width * s).toFixed(1), +(L.height * s).toFixed(1)] };
  });

  return {
    map: v.path,
    canvas: [c.width, c.height],
    scale: +s.toFixed(3), zoom: v.zoom,
    cam: { x: +v.cam.x.toFixed(2), y: +v.cam.y.toFixed(2) },
    tick: { x: +tick.x.toFixed(3), y: +tick.y.toFixed(3), action: tick.action,
            dir, onGround: tick.onGround, foothold: tick.foothold ?? null },
    avatar: { ready: !!v.avatar.ready, state: part.state, frames: part.frames.length,
              bitmaps: v.avatar.bitmaps.size, missing: v.avatar.missingLayers ?? [] },
    placement: at,
    placementTypes: Object.fromEntries(Object.entries(at).map(([k, x]) => [k, typeof x])),
    layerRects: layerRects,
    drawAvatarCalls: observedCalls,
    pixelsChangedByAvatar: diff,
    avatarRectScreen: diff ? { x: bx0, y: by0, w: bx1 - bx0 + 1, h: by1 - by0 + 1 } : null,
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