// Every map, one measurement each: can a character START here, and is the
// point it starts on actually inside the map?
//
//   node sweep-spawns.mjs <port> [limit]
//
// scan-fallout.mjs answers "did the character fall", and it decides that with a
// hardcoded `y > 1200`. That threshold is an ASSUMPTION, not a measurement: a
// map whose geometry genuinely spans y=-184..2100 spawns its character at
// y=2072 on a real foothold and is perfectly walkable. This script replaces the
// threshold with the map's own bounds, read from the same footholds the
// character is standing on:
//
//   * did the session even start          (the HTTP-400 population)
//   * did it land on a foothold          (onGround + a foothold id)
//   * is that foothold inside the bounds (is the spawn point on the map)
//
// It also records the spawn portal, because "the portal is 200px above the
// ground" and "the portal is below the bottom foothold" are different bugs.

const PORT = process.argv[2] ?? '5510';
const LIMIT = Number(process.argv[3] ?? 0);
const BASE = `http://127.0.0.1:${PORT}`;
const { writeFileSync } = await import('node:fs');

async function post(p, b, t = 60000) {
  try {
    const r = await fetch(BASE + p, {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(b), signal: AbortSignal.timeout(t),
    });
    if (!r.ok) {
      let msg = '';
      try { msg = (await r.json()).error ?? (await r.json()).message ?? ''; } catch { }
      return { __httpError: r.status, error: msg };
    }
    return r.json();
  } catch (e) { return { __httpError: 0, error: String(e).slice(0, 80) }; }
}
async function get(p, t = 60000) {
  try {
    const r = await fetch(BASE + p, { signal: AbortSignal.timeout(t) });
    if (!r.ok) return { __httpError: r.status };
    return r.json();
  } catch (e) { return { __httpError: 0, error: String(e).slice(0, 80) }; }
}

const { maps } = await (await fetch(`${BASE}/api/simulate/maps?limit=100000`)).json();
const list = LIMIT > 0 ? maps.slice(0, LIMIT) : maps;
console.log(`sweeping ${list.length} maps`);

const rows = [];
for (const [i, m] of list.entries()) {
  const s = await post('/api/simulate/session', { path: m.path });
  if (s.__httpError || s.error) {
    rows.push({ path: m.path, ok: false, http: s.__httpError ?? 0, error: (s.error ?? '').slice(0, 120) });
  } else {
    const g = await get(`/api/simulate/geometry?path=${encodeURIComponent(m.path)}`);
    const fh = g.footholds ?? [];
    const ys = fh.length ? fh.flatMap(h => [h.y1, h.y2]) : [];
    const xs = fh.length ? fh.flatMap(h => [h.x1, h.x2]) : [];
    const spawnPortal = (g.portals ?? []).find(p => p.type === 0) ?? null;
    rows.push({
      path: m.path, ok: true,
      x: s.x, y: s.y, foothold: s.foothold, onGround: s.onGround === true,
      error: s.error ?? null,
      fhCount: fh.length,
      fhTop: ys.length ? Math.min(...ys) : null,
      fhBottom: ys.length ? Math.max(...ys) : null,
      fhLeft: xs.length ? Math.min(...xs) : null,
      fhRight: xs.length ? Math.max(...xs) : null,
      spawnPortalX: spawnPortal?.x ?? null,
      spawnPortalY: spawnPortal?.y ?? null,
      portals: (g.portals ?? []).length,
    });
    await post(`/api/simulate/session/${s.sessionId}/close`, {});
  }
  if (rows.length % 250 === 0) process.stderr.write(`${rows.length}/${list.length}\n`);
}

writeFileSync('shots/t20/sweep-spawns.json', JSON.stringify(rows, null, 1));

const started = rows.filter(r => r.ok);
const refused = rows.filter(r => !r.ok);
const noGround = started.filter(r => !r.onGround);
const noFootholds = started.filter(r => r.fhCount === 0);
// The spawn is on the map when the foothold it landed on spans the map's own
// vertical extent. A spawn 100px BELOW the lowest foothold is off the bottom.
const belowGround = started.filter(r =>
  r.fhBottom !== null && r.y > r.fhBottom + 5);
const aboveGround = started.filter(r =>
  r.fhTop !== null && r.y < r.fhTop - 5);

console.log(`\n=== ${rows.length} maps ===`);
console.log(`  started            : ${started.length}`);
console.log(`  refused (HTTP)     : ${refused.length}`);
console.log(`  no foothold data   : ${noFootholds.length}`);
console.log(`  not on ground      : ${noGround.length}`);
console.log(`  spawn BELOW lowest foothold : ${belowGround.length}`);
console.log(`  spawn ABOVE highest foothold: ${aboveGround.length}`);

const byErr = {};
for (const r of refused) (byErr[`http-${r.http}`] ??= []).push(r);
console.log('\n--- refusals ---');
for (const [k, v] of Object.entries(byErr)) {
  console.log(`  ${k}: ${v.length}`);
  console.log(`     e.g. ${v.slice(0, 4).map(x => x.path).join(', ')}`);
  const msgs = {};
  for (const x of v) { const k2 = (x.error || '').slice(0, 70); msgs[k2] = (msgs[k2] ?? 0) + 1; }
  for (const [m, n] of Object.entries(msgs).sort((a, b) => b[1] - a[1]).slice(0, 5))
    console.log(`       ${n}x  ${m}`);
}

if (belowGround.length) {
  console.log('\n--- spawned below the map (worst first) ---');
  for (const r of belowGround.sort((a, b) => (b.y - b.fhBottom) - (a.y - a.fhBottom)).slice(0, 15))
    console.log(`  ${r.path.padEnd(32)} spawn=(${r.x},${r.y}) fhY=${r.fhTop}..${r.fhBottom} portal=(${r.spawnPortalX},${r.spawnPortalY})`);
}
if (aboveGround.length) {
  console.log('\n--- spawned above the map (worst first) ---');
  for (const r of aboveGround.sort((a, b) => (a.fhTop - a.y) - (b.fhTop - b.y)).slice(0, 10))
    console.log(`  ${r.path.padEnd(32)} spawn=(${r.x},${r.y}) fhY=${r.fhTop}..${r.fhBottom} portal=(${r.spawnPortalX},${r.spawnPortalY})`);
}
if (noGround.length) {
  console.log('\n--- started but not on ground ---');
  for (const r of noGround.slice(0, 10))
    console.log(`  ${r.path.padEnd(32)} spawn=(${r.x},${r.y}) fh=${r.foothold} fhCount=${r.fhCount}`);
}
console.log('\n-> shots/t20/sweep-spawns.json');