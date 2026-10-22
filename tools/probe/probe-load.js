(async () => {
  const app = window.app;
  const sim = app?.simulator;
  if (!sim) return { error: 'no simulator' };

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

  // The task's map id lives in Map1, not Map0: <Map.nx id>/Map/Map1/109050001.img.
  const path = `${mapArchive.id}/Map/Map1/109050001.img`;
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