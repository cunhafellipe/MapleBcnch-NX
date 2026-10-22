(() => {
  const v = window.app?.simulator?.viewer;
  if (!v) return { error: 'no viewer' };
  const c = v.canvas, g = c.getContext('2d');
  const part = v.avatar?.part;
  if (!part) return { error: 'no part' };
  const cur = v.ticks?.[v.ticks.length - 1] ?? v.current ?? null;
  if (!cur) return { error: 'no cur' };

  const view = v.view || null;
  const s = view?.scale ?? v.scale ?? 1;
  const ox = view?.minX ?? 0, oy = view?.minY ?? 0;
  const dpr = view?.dpr ?? 1;
  const px = (x) => (x - ox) * s;
  const py = (y) => (y - oy) * s;

  const dir = cur.dir ?? (cur.facing >= 0 ? 1 : -1);
  const at = v.avatar.placement(cur.x, cur.y, dir);
  const frame = part.frames[part.index];
  const anchorX = part.anchorX ?? frame.originX;
  const anchorY = part.anchorY ?? frame.originY;

  const out = {
    dpr, s, canvas: [c.width, c.height], cssSize: [c.clientWidth, c.clientHeight],
    minX: ox, minY: oy,
    cur: { x: cur.x, y: cur.y, dir },
    placement: at,
    types: { x: typeof at.x, y: typeof at.y, w: typeof at.w, h: typeof at.h },
  };

  // Reproduce EXACTLY what drawAvatar does for each layer.
  const layers = frame.layers?.length ? frame.layers : [frame];
  out.layers = [];
  for (const layer of layers) {
    const lx = at.flip
      ? at.x + at.w - (anchorX - layer.originX) - layer.width
      : at.x + (anchorX - layer.originX);
    const ly = at.y + (anchorY - layer.originY);
    const sx = px(lx), sy = py(ly);
    const sw = layer.width * s, sh = layer.height * s;
    out.layers.push({
      path: layer.path, w: layer.width, h: layer.height,
      origin: [layer.originX, layer.originY],
      screen: [sx, sy, sw, sh],
      hasBitmap: !!v.avatar.bitmaps.get(layer.path),
      bitmapSize: (() => {
        const im = v.avatar.bitmaps.get(layer.path);
        return im ? [im.naturalWidth, im.naturalHeight] : null;
      })(),
    });
  }

  // Whole-canvas pixel census.
  const data = g.getImageData(0, 0, c.width, c.height).data;
  let nonBlank = 0;
  for (let i = 0; i < data.length; i += 4) {
    if (data[i] || data[i+1] || data[i+2]) nonBlank++;
  }
  out.nonBlank = nonBlank;
  out.totalPx = data.length / 4;
  return out;
})()