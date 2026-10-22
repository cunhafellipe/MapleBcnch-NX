(async () => {
  const app = window.app;
  const sim = app?.simulator;
  const v = sim?.viewer;
  if (!v) return { error: 'no viewer' };

  const sleep = (ms) => new Promise(r => setTimeout(r, ms));

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

  // Fresh load of the target map so nothing from the previous one is cached.
  const open = await fetch('/api/mapedit/open', {
    method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ path: `${mapArchive.id}/Map/Map1/109050001.img` }),
  });
  if (!open.ok) return { error: 'open ' + open.status };

  await sim.open();
  const map = (sim.maps ?? []).find((m) => String(m.id) === '109050001');
  if (!map) return { error: 'map missing' };
  await sim.walk(map);

  await sim.live?.start?.({ path: v.path });
  await sleep(900);

  // Walk right, then left, so tiles come into view and load.
  const fire = (type, code) => window.dispatchEvent(
    new KeyboardEvent(type, { code, key: code, bubbles: true }));
  fire('keydown', 'ArrowRight');
  await sleep(1500);
  fire('keyup', 'ArrowRight');
  await sleep(400);
  fire('keydown', 'ArrowLeft');
  await sleep(1200);
  fire('keyup', 'ArrowLeft');
  await sleep(600);

  const view = v.viewBox;
  const cur = v.ticks?.[v.ticks.length - 1] ?? v.current ?? null;
  v.zoom = 1.6;
  for (let i = 0; i < 800; i++) if (cur) v.follow(cur.x, cur.y);
  v.draw();
  await sleep(300);

  const c = v.canvas, g = c.getContext('2d');
  const data = g.getImageData(0, 0, c.width, c.height).data;
  let nonBlank = 0;
  for (let i = 0; i < data.length; i += 4) {
    if (data[i] || data[i+1] || data[i+2]) nonBlank++;
  }

  return {
    map: v.path,
    canvas: [c.width, c.height],
    scale: +(v.viewBox?.scale ?? 0).toFixed(3),
    zoom: v.zoom,
    cur: cur ? { x: +cur.x.toFixed(2), y: cur.y, action: cur.action, dir: cur.dir } : null,
    avatar: {
      ready: !!v.avatar?.ready,
      state: v.avatar?.part?.state ?? null,
      index: v.avatar?.part?.index ?? null,
      frames: v.avatar?.part?.frames?.length ?? 0,
      bitmaps: v.avatar?.bitmaps?.size ?? 0,
      missing: v.avatar?.missingLayers ?? [],
    },
    nonBlank,
    totalPx: data.length / 4,
    loadedArt: v.art?.size ?? null,
  };
})()