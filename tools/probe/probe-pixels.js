(() => {
  const v = window.app?.simulator?.viewer;
  if (!v) return { error: 'no viewer' };
  const c = v.canvas, g = c.getContext('2d');
  const part = v.avatar?.part;
  const cur = v.ticks?.[v.ticks.length - 1] ?? v.current ?? null;
  if (!part || !cur) return { error: 'no part/cur' };
  const view = v.viewBox;
  if (!view) return { error: 'no viewBox' };
  const s = view.scale, ox = view.minX, oy = view.minY;
  const px = (x) => (x - ox) * s;
  const py = (y) => (y - oy) * s;

  const dir = cur.dir ?? (cur.facing >= 0 ? 1 : -1);
  const at = v.avatar.placement(cur.x, cur.y, dir);
  const frame = part.frames[part.index];
  const anchorX = part.anchorX ?? frame.originX;
  const anchorY = part.anchorY ?? frame.originY;
  const layers = frame.layers?.length ? frame.layers : [frame];

  // Union rect of every layer, in screen space, exactly as drawAvatar computes.
  let x0 = Infinity, y0 = Infinity, x1 = -Infinity, y1 = -Infinity;
  for (const layer of layers) {
    const lx = at.flip
      ? at.x + at.w - (anchorX - layer.originX) - layer.width
      : at.x + (anchorX - layer.originX);
    const ly = at.y + (anchorY - layer.originY);
    const sx = px(lx), sy = py(ly);
    const sw = layer.width * s, sh = layer.height * s;
    x0 = Math.min(x0, sx); y0 = Math.min(y0, sy);
    x1 = Math.max(x1, sx + sw); y1 = Math.max(y1, sy + sh);
  }

  const rx = Math.max(0, Math.floor(x0)), ry = Math.max(0, Math.floor(y0));
  const rw = Math.min(c.width - rx, Math.ceil(x1 - x0));
  const rh = Math.min(c.height - ry, Math.ceil(y1 - y0));

  const out = {
    avatarRectScreen: { x: +x0.toFixed(2), y: +y0.toFixed(2),
                        w: +(x1 - x0).toFixed(2), h: +(y1 - y0).toFixed(2) },
    readRect: { x: rx, y: ry, w: rw, h: rh },
    scale: s, zoom: v.zoom,
    onScreen: x1 > 0 && y1 > 0 && x0 < c.width && y0 < c.height,
  };
  if (rw <= 0 || rh <= 0) return { ...out, verdict: 'rect outside canvas' };

  // Census of what is actually painted in the avatar's own rect.
  const d = g.getImageData(rx, ry, rw, rh).data;
  let opaque = 0, nonBlank = 0;
  const colors = new Map();
  for (let i = 0; i < d.length; i += 4) {
    if (d[i+3] > 0) opaque++;
    if (d[i] || d[i+1] || d[i+2]) nonBlank++;
    const k = `${d[i]},${d[i+1]},${d[i+2]},${d[i+3]}`;
    colors.set(k, (colors.get(k) ?? 0) + 1);
  }
  out.rectPixels = { total: rw * rh, opaque, nonBlank };
  out.distinctColors = colors.size;
  out.topColors = [...colors.entries()].sort((a,b)=>b[1]-a[1]).slice(0,6)
    .map(([k,n]) => `${k} x${n}`);

  // Now the same rect with the avatar NOT drawn, by redrawing from a clone
  // that skips the blit. That isolates the sprite from the map art under it.
  const saved = v.ticks;
  const noAvatar = () => { v.avatarFrameForcedNull = true; };
  out.probe = 'compare-next';
  return out;
})()