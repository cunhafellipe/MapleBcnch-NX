// Sweep every map for the "character falls out of the map" defect.
//
//   node scan-fallout.mjs <port> [sampleSize]
//
// The user reports characters falling off some maps. Horizontal FallEdge and
// the map's Left/Right margins are already implemented (CharacterPhysics.cs:84
// EdgeMargin=20, :444-457 fallEdge), so the defect is almost certainly
// VERTICAL: a map whose footholds do not reach Map.Bottom, or a bounds value
// computed from a different rule than the reference's.
//
// Reference, MapleNecrocer\Client\MapleCharacter.cs:
//   :761-764  camera clamp against Map.Top / Map.Bottom - DisplaySize.Y
//   :829      if ((X > Map.Right - 20)) SpeedR = 0;
//   :793-801  FallEdge = FH.X1 - 10;   X < FallEdge -> jsFalling
//   :802      FindBelow(new Vector2(X + 10, Y - 5))
// Bounds, MapleMap.cs:175-194
//   VR mode:  Left=VRLeft  Top=VRTop  Right=VRRight  Bottom=VRBottom + 15
//   non-VR:   Left=Foothold.MinX1  Right=Foothold.MaxX2
//             Top = -centerY + 50
//             Bottom = -centerY + MapHeight - 55

const PORT = process.argv[2] ?? '5510';
const SAMPLE = Number(process.argv[3] ?? 5366);
const BASE = `http://127.0.0.1:${PORT}`;

async function post(p, b, t = 900) {
  // A failed call must not abort the sweep: one flaky request used to kill the
  // whole 5366-map run and lose every result.
  try {
    const r = await fetch(BASE + p, {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(b),
      signal: AbortSignal.timeout(t),
    });
    if (!r.ok) return { __httpError: r.status };
    return await r.json();
  } catch (e) {
    return { __httpError: 'net', __err: String(e).slice(0, 80) };
  }
}

const { maps } = await (await fetch(`${BASE}/api/simulate/maps?limit=100000`)).json();
const list = maps.slice(0, SAMPLE);
console.log(`scanning ${list.length} maps\n`);

const rows = [];
for (const m of list) {
  let d;
  try { d = await post('/api/simulate/session', { path: m.path, input: {} }); }
  catch (e) { rows.push({ path: m.path, verdict: 'start-threw', detail: String(e).slice(0, 60) }); continue; }
  if (d.__httpError || !d.sessionId) { rows.push({ path: m.path, verdict: `http-${d.__httpError ?? 'no-session'}` }); continue; }

  const s = d.sessionId;
  // Judge against THIS map's own bounds. A hardcoded y>1200 is wrong: 102020100
  // has bottom=3120 and spawning at y=2107 is correct. I got that wrong first.
  const b = d.bounds;
  if (!b) { rows.push({ path: m.path, verdict: 'no-bounds-in-frame' }); continue; }
  const spawnOutside = d.y < b.top || d.y > b.bottom || d.x < b.left || d.x > b.right;

  let maxY = d.y, fell = spawnOutside, frames = 0, escapeY = null;
  // Hold right AND down for four seconds: the down key is how a player walks
  // off a ledge, and it is the case the user is describing.
  for (let i = 0; i < 240 && !fell; i++) {
    const f = await post(`/api/simulate/session/${s}/frame`, { right: true, down: true });
    if (f.__httpError) break;
    frames++;
    if (f.y > maxY) maxY = f.y;
    const fb = f.bounds ?? b;
    // Left the map it was on: past the bottom, or far outside horizontally.
    if (f.y > fb.bottom + 60 || f.x < fb.left - 200 || f.x > fb.right + 200 || !Number.isFinite(f.y)) {
      fell = true; escapeY = Math.round(f.y);
    }
  }
  await post(`/api/simulate/session/${s}/close`, {});
  rows.push({ path: m.path, id: m.id, spawnY: d.y, boundsBottom: b.bottom, maxY: Math.round(maxY), frames, fell, escapeY });
  if (rows.length % 100 === 0) {
    process.stderr.write(`${rows.length}/${list.length}\n`);
    // Checkpoint: a full sweep of 5366 maps takes long enough that the runner
    // may kill it. Flush every 100 so a partial run is still usable.
    writeFileSync(`shots/fallout-progress-${rows.length}.json`, JSON.stringify(rows));
  }
}

const fellRows = rows.filter((r) => r.fell);
const errRows = rows.filter((r) => r.verdict);
const ok = rows.length - fellRows.length - errRows.length;

console.log(`\n=== ${rows.length} maps ===`);
console.log(`  stayed on the map : ${ok}`);
console.log(`  FELL OFF          : ${fellRows.length}`);
console.log(`  errors            : ${errRows.length}\n`);

if (fellRows.length) {
  console.log('--- fell off (worst first) ---');
  for (const r of fellRows.sort((a, b) => b.maxY - a.maxY).slice(0, 25))
    console.log(`  ${r.path.padEnd(34)} spawnY=${String(r.spawnY).padStart(7)} maxY=${String(r.maxY).padStart(7)} at frame ${r.frames}`);
}
if (errRows.length) {
  console.log('\n--- errors ---');
  const byKind = {};
  for (const r of errRows) (byKind[r.verdict] ??= []).push(r.path);
  for (const [k, v] of Object.entries(byKind))
    console.log(`  ${k}: ${v.length}  e.g. ${v.slice(0, 3).join(', ')}`);
}

// Do the fallen ones share a shape? Group by spawn depth.
if (fellRows.length) {
  const deep = fellRows.filter((r) => r.spawnY > 400).length;
  console.log(`\n  of those, ${deep}/${fellRows.length} spawned below y=400`);
}

import { writeFileSync, mkdirSync } from 'node:fs';
const outName = `shots/fallout-scan-${rows.length}.json`;
writeFileSync(outName, JSON.stringify(rows, null, 1));
console.log(`\nfull result -> ${outName}`);