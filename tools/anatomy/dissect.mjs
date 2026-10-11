// Anatomise real maps so the map grammar can be written from evidence rather
// than from intuition.
//
//   node dissect.mjs <baseUrl> <outJson> [mapCount]
//
// For each sampled map this records: bounds, layer occupancy, which tile sets a
// layer actually uses, object prefixes and their footprint, foothold topology,
// portals, life, reactors and ladders. The point is to find the rules that hold
// across maps -- a structure that appears in all ten is a rule; one that appears
// in one map is that map's business.

const [, , base, out, countArg] = process.argv;
const want = Number(countArg ?? 10);

async function api(path, body) {
  const res = await fetch(base + path, {
    method: body ? 'POST' : 'GET',
    headers: { 'Content-Type': 'application/json' },
    body: body ? JSON.stringify(body) : undefined,
  });
  const text = await res.text();
  let json;
  try { json = JSON.parse(text); } catch { json = { raw: text.slice(0, 300) }; }
  if (!res.ok) throw new Error(`${path} -> HTTP ${res.status} ${JSON.stringify(json).slice(0, 200)}`);
  return json;
}

const stat = (arr, pick) => {
  const m = new Map();
  for (const x of arr ?? []) {
    const k = pick(x);
    if (k == null) continue;
    m.set(k, (m.get(k) ?? 0) + 1);
  }
  return [...m.entries()].sort((a, b) => b[1] - a[1]);
};

const analysis = [];
const failures = [];

// Sample across the id space rather than the first N: consecutive ids are
// sibling maps of one region and would all look alike.
const list = await api('/api/mapedit/maps?limit=2000');
const pool = list.maps.filter((m) => m.name);            // named maps only
console.error(`pool: ${pool.length} named maps of ${list.total}`);

const step = Math.max(1, Math.floor(pool.length / want));
const picked = [];
for (let i = 0; i < pool.length && picked.length < want; i += step) picked.push(pool[i]);

for (const row of picked) {
  try {
    await api('/api/mapedit/open', { path: row.path });
    const d = await api(`/api/mapedit/doc?path=${encodeURIComponent(row.path)}`);

    const layers = (d.layers ?? []).map((l) => ({
      index: l.index,
      ts: l.ts ?? null,
      tiles: (l.tiles ?? []).length,
      objs: (l.objs ?? []).length,
      backs: l.layerBackCount ?? 0,
      tileU: stat(l.tiles ?? [], (t) => t.u),
      objPrefix: stat(l.objs ?? [], (o) => (o.os ?? '').split('/')[0]),
      // Where the tiles sit, which is what tells ground from wall from ceiling.
      tileYs: stat(l.tiles ?? [], (t) => (t.y >= 0 ? 'below0' : 'above0')),
    }));

    const fh = d.footholds ?? [];
    const fhStat = {
      count: fh.length,
      orientations: stat(fh, (f) => (f.x1 === f.x2 ? 'flat' : f.y1 === f.y2 ? 'wall' : 'slope')),
      hasNext: fh.filter((f) => f.next && f.next !== '0').length,
      yRange: fh.length ? [Math.min(...fh.map((f) => Math.min(f.y1, f.y2))),
                           Math.max(...fh.map((f) => Math.max(f.y1, f.y2)))] : null,
    };

    analysis.push({
      id: row.id,
      name: row.name,
      path: row.path,
      bgm: d.bgm ?? null,
      mapMark: d.mapMark ?? null,
      bounds: d.bounds ?? null,
      counts: {
        layers: layers.length, backs: (d.backs ?? []).length, footholds: fh.length,
        ladders: (d.ladders ?? []).length, portals: (d.portals ?? []).length,
        life: (d.life ?? []).length, reactors: (d.reactors ?? []).length,
        rects: (d.rects ?? []).length, art: (d.art ?? []).length,
        tiles: layers.reduce((s, l) => s + l.tiles, 0),
        objs: layers.reduce((s, l) => s + l.objs, 0),
      },
      layers,
      footholds: fhStat,
      objFull: stat((d.layers ?? []).flatMap((l) => l.objs ?? []), (o) => o.os),
      objL1: stat((d.layers ?? []).flatMap((l) => l.objs ?? []), (o) => o.l1),
      objL0: stat((d.layers ?? []).flatMap((l) => l.objs ?? []), (o) => o.l0),
      life: stat(d.life ?? [], (l) => l.id),
      portalNames: stat(d.portals ?? [], (p) => p.pn),
      reactorIds: (d.reactors ?? []).map((r) => r.id).slice(0, 20),
      tileSetsUsed: [...new Set((d.layers ?? []).map((l) => l.ts).filter(Boolean))],
    });
    console.error(`ok ${row.id} ${row.name} (${analysis.at(-1).counts.tiles} tiles, ${analysis.at(-1).counts.objs} objs)`);
  } catch (e) {
    failures.push({ id: row.id, name: row.name, error: String(e.message).slice(0, 200) });
    console.error(`FAIL ${row.id} ${row.name}: ${e.message.slice(0, 120)}`);
  }
}

const { writeFileSync } = await import('node:fs');
writeFileSync(out, JSON.stringify({ analysed: analysis.length, failures, maps: analysis }, null, 1));
console.error(`\nwrote ${out}: ${analysis.length} maps, ${failures.length} failures`);
process.exit(0);