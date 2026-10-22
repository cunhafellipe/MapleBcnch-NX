(async () => {
  const app = window.app;
  const v = app?.simulator?.viewer;
  if (!v) return { error: 'no viewer' };

  const sleep = (ms) => new Promise(r => setTimeout(r, ms));
  try { app.simulator.live?.stop?.(); } catch (e) { /* not running */ }

  // The composed part is stand1 while the tick says action=walk. step() then
  // returns null by design (avatar.js:350: state mismatch -> no frame), so the
  // avatar is skipped and only the fallback figure draws. Compose walk1 for the
  // state this tick actually maps to, which is what a live walk does on its own
  // when the pose and the tick agree.
  const cur0 = v.ticks?.[v.ticks.length - 1] ?? null;
  if (!cur0) return { error: 'no tick' };
  const want = cur0.action === 'walk' ? 'walk1' : 'stand1';
  v.avatarState = '';
  const ok = await v.avatar.load(want);
  if (!ok) return { error: 'load failed', want, reason: v.avatar.reason };
  v.avatarState = want;

  v.ticks = [cur0];
  v.zoom = 1.6;
  for (let i = 0; i < 800; i++) v.follow(cur0.x, cur0.y);
  v.draw();

  const c = v.canvas, g = c.getContext('2d');
  const part = v.avatar.part;
  const heldIndex = part.index;
  part.time = -1e9;

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

  const view = v.viewBox;
  const s = view.scale;
  const py = (y) => (y - view.minY) * s;
  const cur = v.ticks[0];
  const dir = cur.dir ?? 1;
  const at = v.avatar.placement(cur.x, cur.y, dir);
  const frame = part.frames[heldIndex];
  const anchorY = part.anchorY ?? frame.originY;
  const layers = frame.layers?.length ? frame.layers : [frame];
  const body = layers[layers.length - 1];
  const bottom = at.y + (anchorY - body.originY) + body.height;

  const data = g.getImageData(0, 0, c.width, c.height).data;
  let nonBlank = 0;
  for (let i = 0; i < data.length; i += 4) {
    if (data[i] || data[i+1] || data[i+2]) nonBlank++;
  }

  return {
    map: v.path,
    canvas: [c.width, c.height],
    scale: +s.toFixed(3), zoom: v.zoom,
    tick: { x: +cur.x.toFixed(3), y: +cur.y.toFixed(3), action: cur.action, dir, onGround: cur.onGround },
    avatar: {
      ready: !!v.avatar.ready, state: part.state, index: heldIndex,
      frames: part.frames.length, bitmaps: v.avatar.bitmaps.size,
      missing: v.avatar.missingLayers ?? [], reason: v.avatar.reason,
    },
    placement: at,
    placementTypes: Object.fromEntries(Object.entries(at).map(([k, x]) => [k, typeof x])),
    drawAvatarCalls: observedCalls,
    pixelsChangedByAvatar: diff,
    avatarRectScreen: diff ? { x: bx0, y: by0, w: bx1 - bx0 + 1, h: by1 - by0 + 1 } : null,
    nonBlank, totalPx: data.length / 4,
    feet: {
      tickY: cur.y,
      avatarBottomWorld: +bottom.toFixed(6),
      deltaFromTickY: +(bottom - cur.y).toFixed(6),
      screenTickY: +py(cur.y).toFixed(1),
      screenAvatarBottomY: +py(bottom).toFixed(1),
    },
  };
})()