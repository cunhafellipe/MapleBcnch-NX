(() => {
  const v = window.app?.simulator?.viewer;
  if (!v) return { error: 'no viewer' };
  const cur = v.ticks?.[v.ticks.length - 1] ?? v.current ?? null;
  return {
    hasViewObj: !!v.view,
    viewKeys: v.view ? Object.keys(v.view) : null,
    view: v.view ? {
      minX: v.view.minX, minY: v.view.minY,
      scale: v.view.scale, dpr: v.view.dpr,
      cssWidth: v.view.cssWidth, cssHeight: v.view.cssHeight,
    } : null,
    curPos: cur ? { x: cur.x, y: cur.y } : null,
    ticksLen: v.ticks?.length ?? 0,
    hasFollow: typeof v.follow === 'function',
    ownScale: v.scale ?? null,
    ownMinX: v.minX ?? null,
    ownMinY: v.minY ?? null,
  };
})()