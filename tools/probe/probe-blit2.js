(() => {
  const v = window.app?.simulator?.viewer;
  if (!v) return { error: 'no viewer' };
  const c = v.canvas, g = c.getContext('2d');
  const part = v.avatar?.part;
  const cur = v.ticks?.[v.ticks.length - 1] ?? v.current ?? null;
  if (!part || !cur) return { error: 'no part/cur' };

  const view = v.viewBox;
  if (!view) return { error: 'no viewBox (draw() never ran)' };
  const s = view.scale, ox = view.minX, oy = view.minY, dpr = view.dpr ?? 1;
  const px = (x) => (x - ox) * s;
  const py = (y) => (y - oy) * s;

  const dir = cur.dir ?? (cur.facing >= 0 ? 1 : -1);
  const at = v.avatar.placement(cur.x, cur.y, dir);
  const frame = part.frames[part.index];
  const anchorX = part.anchorX ?? frame.originX;
  const anchorY = part.anchorY ?? frame.originY;

  const out = {
    view: { minX: ox, minY: oy, scale: s, dpr,
            cssW: view.cssWidth, cssH: view.cssHeight },
    canvas: [c.width, c.height],
    cur: { x: cur.x, y: cur.y, dir, action: cur.action, foothold: cur.foothold ?? null },
    placement: at,
    placementTypes: Object.fromEntries(Object.entries(at).map(([k, x]) => [k, typeof x])),
    anchor: [anchorX, anchorY],
    frame: { w: frame.width, h: frame.height },
    cam: v.cam,
    layers: [],
  };

  const layers = frame.layers?.length ? frame.layers : [frame];
  for (const layer of layers) {
    const lx = at.flip
      ? at.x + at.w - (anchorX - layer.originX) - layer.width
      : at.x + (anchorX - layer.originX);
    const ly = at.y + (anchorY - layer.originY);
    const im = v.avatar.bitmaps.get(layer.path);
    out.layers.push({
      path: layer.path,
      world: [lx, ly],
      screen: [px(lx), py(ly), layer.width * s, layer.height * s],
      bitmap: im ? [im.naturalWidth, im.naturalHeight] : null,
    });
  }

  // Feet contact: bottom edge of the drawn rect vs the character's world y.
  const body = layers[layers.length - 1];
  const rectBottomWorld = at.y + (anchorY - body.originY) + body.height;
  out.feet = {
    curY: cur.y,
    placementBottomWorld: at.y + at.h,
    bodyBottomWorld: rectBottomWorld,
    deltaFromTickY: rectBottomWorld - cur.y,
    screenBottomY: py(rectBottomWorld),
    screenTickY: py(cur.y),
  };

  const data = g.getImageData(0, 0, c.width, c.height).data;
  let nonBlank = 0;
  for (let i = 0; i < data.length; i += 4) {
    if (data[i] || data[i+1] || data[i+2]) nonBlank++;
  }
  out.nonBlank = nonBlank;
  out.totalPx = data.length / 4;
  return out;
})()