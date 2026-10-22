(async () => {
  const out = {};
  for (const p of ['f1/Map/Map1/109050001.img', 'f1/Map/Map0/000020000.img', 'f1/Map/Map9/910020202.img']) {
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