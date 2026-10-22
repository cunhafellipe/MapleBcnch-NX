(async () => {
  const app = window.app;
  const sim = app?.simulator;
  if (!sim) return { error: 'no simulator' };

  // The task's map id lives in Map1, not Map0: f1/Map/Map1/109050001.img.
  const path = 'f1/Map/Map1/109050001.img';
  const open = await fetch('/api/mapedit/open', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ path }),
  });
  if (!open.ok) return { error: 'open ' + open.status, body: (await open.text()).slice(0, 300) };

  await sim.open();
  const maps = sim.maps ?? [];
  const map = maps.find((m) => String(m.id) === '109050001')
          ?? maps.find((m) => JSON.stringify(m).includes('109050001'));
  if (!map) return { error: 'map not in list', count: maps.length };

  await sim.walk(map);

  const v = sim.viewer;
  if (!v) return { error: 'no viewer after walk' };

  // Zoom in: at 1 the 26x32 sprite is 12.6 px on screen, which is real but not
  // legible. zoom is a plain multiplier on the fit-to-width scale.
  v.zoom = 4;
  v.draw();

  return {
    mapId: map.id ?? map.path,
    viewerPath: v.path,
    zoom: v.zoom,
    canvas: [v.canvas.width, v.canvas.height],
    state: v.avatar?.part?.state ?? null,
    frames: v.avatar?.part?.frames?.length ?? 0,
    bitmaps: v.avatar?.bitmaps?.size ?? 0,
    ready: !!v.avatar?.ready,
    reason: v.avatar?.reason ?? null,
    missingLayers: v.avatar?.missingLayers?.length ?? 0,
  };
})()