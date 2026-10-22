(async () => {
  const v = window.app?.simulator?.viewer;
  if (!v) return { error: 'no viewer' };
  const c = v.canvas, g = c.getContext('2d');

  const cur = v.ticks?.[v.ticks.length - 1] ?? v.current ?? null;
  if (!cur) return { error: 'no cur' };

  // The camera lerps at 0.015/frame toward centring; after a zoom change it needs
  // many frames to arrive. follow() once per stepped frame is the contract, so
  // drive it that many times rather than snapping the camera by hand -- that
  // keeps the reported position one the reference would actually produce.
  for (let i = 0; i < 600; i++) v.follow(cur.x, cur.y);
  v.draw();

  const view = v.viewBox;
  const s = view.scale;
  const px = (x) => (x - view.minX) * s;
  const py = (y) => (y - view.minY) * s;

  const dir = cur.dir ?? (cur.facing >= 0 ? 1 : -1);
  const at = v.avatar.placement(cur.x, cur.y, dir);
  const part = v.avatar.part;
  const frame = part.frames[part.index];
  const anchorX = part.anchorX ?? frame.originX;
  const anchorY = part.anchorY ?? frame.originY;
  const layers = frame.layers?.length ? frame.layers : [frame];

  let x0 = Infinity, y0 = Infinity, x1 = -Infinity, y1 = -Infinity;
  const layerRects = [];
  for (const layer of layers) {
    const lx = at.flip
      ? at.x + at.w - (anchorX - layer.originX) - layer.width
      : at.x + (anchorX - layer.originX);
    const ly = at.y + (anchorY - layer.originY);
    const sx = px(lx), sy = py(ly), sw = layer.width * s, sh = layer.height * s;
    layerRects.push({ path: layer.path, screen: [+sx.toFixed(1), +sy.toFixed(1), +sw.toFixed(1), +sh.toFixed(1)] });
    x0 = Math.min(x0, sx); y0 = Math.min(y0, sy);
    x1 = Math.max(x1, sx + sw); y1 = Math.max(y1, sy + sh);
  }

  // Freeze the clock so both comparison passes draw the SAME pose.
  part.time = -1e9;
  v.draw();
  const withA = g.getImageData(0, 0, c.width, c.height).data.slice();
  const real = v.drawAvatar;
  let calls = 0;
  v.drawAvatar = function () { calls++; };
  v.draw();
  const withoutA = g.getImageData(0, 0, c.width, c.height).data.slice();
  v.drawAvatar = real;
  part.time = 0;

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
  // Restore a normal frame so the screenshot shows a real pose.
  v.draw();

  const body = layers[layers.length - 1];
  const bodyBottomWorld = at.y + (anchorY - body.originY) + body.height;

  return {
    canvas: [c.width, c.height],
    zoom: v.zoom, scale: +s.toFixed(4),
    cam: { x: +v.cam.x.toFixed(2), y: +v.cam.y.toFixed(2) },
    cur: { x: +cur.x.toFixed(3), y: cur.y, dir, action: cur.action },
    placement: at,
    placementTypes: Object.fromEntries(Object.entries(at).map(([k, x]) => [k, typeof x])),
    layerRects,
    avatarRectScreen: [+x0.toFixed(1), +y0.toFixed(1), +(x1 - x0).toFixed(1), +(y1 - y0).toFixed(1)],
    onScreen: x1 > 0 && y1 > 0 && x0 < c.width && y0 < c.height,
    drawAvatarCalls: calls,
    pixelsChangedByAvatar: diff,
    diffBBox: diff ? { x: bx0, y: by0, w: bx1 - bx0 + 1, h: by1 - by0 + 1 } : null,
    feet: {
      curY: cur.y,
      bodyBottomWorld,
      deltaFromTickY: +(bodyBottomWorld - cur.y).toFixed(6),
      screenTickY: +py(cur.y).toFixed(1),
      screenBodyBottomY: +py(bodyBottomWorld).toFixed(1),
    },
  };
})()