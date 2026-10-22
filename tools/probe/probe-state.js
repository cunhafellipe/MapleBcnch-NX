(() => {
  const app = window.app;
  const sim = app?.simulator;
  const v = sim?.viewer;
  if (!v) {
    return {
      stage: 'no viewer',
      hasApp: !!app,
      hasSim: !!sim,
      simKeys: sim ? Object.keys(sim).slice(0, 40) : null,
      appKeys: app ? Object.keys(app).slice(0, 40) : null,
    };
  }
  const part = v.avatar?.part;
  const cur = v.ticks?.[v.ticks.length - 1] ?? v.current ?? null;
  const out = {
    stage: 'viewer',
    mapPath: v.path ?? null,
    scale: v.scale ?? null,
    canvasSize: [v.canvas?.width, v.canvas?.height],
    hasAvatar: !!v.avatar,
    hasPart: !!part,
    ready: !!v.avatar?.ready,
    reason: v.avatar?.reason ?? null,
    missingLayers: v.avatar?.missingLayers?.length ?? 0,
    bitmaps: v.avatar?.bitmaps?.size ?? 0,
    state: part?.state ?? null,
    index: part?.index ?? null,
    frames: part?.frames?.length ?? 0,
    anchor: part ? [part.anchorX ?? null, part.anchorY ?? null] : null,
    cur: cur ? { x: cur.x, y: cur.y, action: cur.action, dir: cur.dir ?? null,
                 facing: cur.facing ?? null, foothold: cur.foothold ?? null } : null,
    placement: null,
  };
  if (part && cur) {
    try {
      out.placement = v.avatar.placement(cur.x, cur.y, cur.dir ?? (cur.facing >= 0 ? 1 : -1));
    } catch (e) { out.placement = 'THREW: ' + e.message; }
    const frame = part.frames[part.index];
    out.frame = frame ? { w: frame.width, h: frame.height, ox: frame.originX, oy: frame.originY,
                          layers: frame.layers?.length ?? 0 } : null;
  }
  return out;
})()