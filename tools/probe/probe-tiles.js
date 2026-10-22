(async () => {
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
  const map = await archiveId('Map.nx');
  if (map.error) { console.log(map.error); return map; }
  const out = {};
  for (const p of [`${map.id}/Map/Map1/109050001.img`, `${map.id}/Map/Map0/000020000.img`, `${map.id}/Map/Map9/910020202.img`]) {
    const r = await fetch('/api/mapedit/open', {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ path: p }),
    });
    if (!r.ok) { out[p] = 'open ' + r.status; continue; }
    const doc = await r.json();
    const layers = doc?.layers ?? [];
    let tiles = 0, objs = 0, withArt = 0, artPaths = new Set();
    for (const L of layers) {
      for (const t of (L.tiles ?? [])) {
        tiles++;
        if (t.art) { withArt++; artPaths.add(typeof t.art === 'string' ? t.art : t.art.path ?? JSON.stringify(t.art)); }
      }
      objs += (L.objs ?? []).length;
    }
    out[p] = {
      layers: layers.length, tiles, objs, tilesWithArt: withArt,
      sampleArt: [...artPaths].slice(0, 4),
    };
  }
  return out;
})()