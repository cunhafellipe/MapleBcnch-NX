// WHY do 155 maps refuse to open? Classify every scanned map by the shape of
// its own data instead of by the exception it throws.
//
//   node diag-maps.mjs <port> <listOfPaths.txt>
//
// The scan reports "HTTP 400" as one bucket. This asks the map instead: does it
// carry geometry, is it a link stub, how many footholds and portals, and which
// portal would be the spawn. It prints the differences that separate the maps
// that work from the maps that do not, which is a measurement.

const PORT = process.argv[2] ?? '5510';
const BASE = `http://127.0.0.1:${PORT}`;
const LIST = process.argv[3];
const { readFileSync, writeFileSync } = await import('node:fs');

async function get(p, t = 30000) {
  const r = await fetch(BASE + p, { signal: AbortSignal.timeout(t) });
  if (!r.ok) return { __httpError: r.status };
  return r.json();
}
async function post(p, b, t = 30000) {
  const r = await fetch(BASE + p, {
    method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(b), signal: AbortSignal.timeout(t),
  });
  if (!r.ok) {
    let msg = '';
    try { msg = (await r.json()).error ?? ''; } catch { }
    return { __httpError: r.status, error: msg };
  }
  return r.json();
}

const paths = readFileSync(LIST, 'utf8').split(/\r?\n/).map(s => s.trim()).filter(Boolean);
const rows = [];
for (const [i, path] of paths.entries()) {
  const doc = await post('/api/mapedit/open', { path });
  if (doc.__httpError) { rows.push({ path, docError: doc.error }); continue; }
  const geo = await get(`/api/simulate/geometry?path=${encodeURIComponent(path)}`);
  const fh = geo.footholds ?? [];
  // The portal DTO serialises `type`, not the FootholdPhysics.Portal field
  // names -- the flat DTO drops IsSpawn. Filter on what the wire actually has.
  const spawn = (geo.portals ?? []).filter(p => p.type === 0);
  rows.push({
    path,
    ok: true,
    hasGeometry: doc.hasGeometry,
    link: doc.link ?? null,
    mapMark: doc.mapMark,
    fh: fh.length,
    portals: (geo.portals ?? []).length,
    ladders: (geo.ladders ?? []).length,
    life: (doc.life ?? []).length,
    layers: (doc.layers ?? []).length,
    spawnCount: spawn.length,
    spawnX: spawn[0]?.x ?? null,
    spawnY: spawn[0]?.y ?? null,
    boundsComputed: doc.boundsComputed,
  });
  if (rows.length % 50 === 0) process.stderr.write(`${rows.length}/${paths.length}\n`);
}

writeFileSync('shots/t20/diag-maps.json', JSON.stringify(rows, null, 1));

const ok = rows.filter(r => r.ok);
console.log(`\n=== ${rows.length} maps, ${ok.length} opened ===`);

// Split by whether the map can physically hold a character.
const groups = {
  'no footholds': ok.filter(r => r.fh === 0),
  'has footholds': ok.filter(r => r.fh > 0),
  'doc failed': rows.filter(r => !r.ok),
};
for (const [k, v] of Object.entries(groups)) console.log(`  ${k.padEnd(14)}: ${v.length}`);

console.log('\n--- no-foothold maps by shape ---');
const byShape = {};
for (const r of groups['no footholds']) {
  const k = `link=${r.link ?? '-'} layers=${r.layers} portals=${r.portals} life=${r.life} ladders=${r.ladders}`;
  (byShape[k] ??= []).push(r.path);
}
for (const [k, v] of Object.entries(byShape).sort((a, b) => b[1].length - a[1].length))
  console.log(`  ${v.length.toString().padStart(4)}  ${k}\n        e.g. ${v.slice(0, 2).join(', ')}`);

console.log('\n--- with footholds: spawn portal presence ---');
const withFh = groups['has footholds'];
const noSpawn = withFh.filter(r => r.spawnCount === 0);
console.log(`  ${noSpawn.length} have NO type-0 portal; ${withFh.length - noSpawn.length} do`);
for (const r of noSpawn.slice(0, 10))
  console.log(`     ${r.path} fh=${r.fh} portals=${r.portals} mapMark=${r.mapMark}`);