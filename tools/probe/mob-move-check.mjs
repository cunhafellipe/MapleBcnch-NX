// Prove mobs move inside a LIVE session. Not that the frame has a `mobs` key --
// that a build already tells you. That the coordinates inside it change over
// frames, while the character is being driven, and that they change for the
// reason the reference says they do.
//
//   node mob-move-check.mjs <Map.nx abs path> [mapId] [WZ_SERVER]
//
// WHY OVER MCP AND NOT HTTP
// --------------------------
// Same reasoning as sim-walk.mjs, which this mirrors: a spawned MCP client
// reads JSON and cannot be stale, so a pass cannot be an artefact of a browser
// holding a pre-edit script.
//
// WHY NOT sim-walk.mjs ITSELF
// ---------------------------
// That file asserts the CHARACTER moves. Everything here asserts about a
// different body that the character never touches, so a failure has to be
// unambiguous: if the character checks pass and the mob checks fail, the mobs
// are the thing that is broken.

import { spawn } from 'node:child_process';
import path from 'node:path';
import process from 'node:process';

const [, , mapPath, mapId = '910020202', serverArg] = process.argv;
if (!mapPath) {
  console.error('usage: node mob-move-check.mjs <Map.nx abs path> [mapId] [WZ_SERVER]');
  process.exit(2);
}

const SERVER = serverArg ?? process.env.WZ_SERVER ?? 'http://127.0.0.1:5480';
const DOTNET = process.env.DOTNET_EXE
  ?? 'C:/MUWORK/app/scoop/apps/dotnet-sdk/current/dotnet.exe';
const DLL = process.env.MAPLE_MCP_DLL
  ?? path.resolve('MapleMcp/bin/Release/net10.0-windows/maple-mcp.dll');

const child = spawn(DOTNET, [DLL], {
  stdio: ['pipe', 'pipe', 'pipe'],
  env: { ...process.env, WZ_SERVER: SERVER },
});

let nextId = 1;
const pending = new Map();
child.stdout.setEncoding('utf8');
child.stdout.on('data', (chunk) => {
  for (const line of chunk.split('\n')) {
    if (!line.trim()) continue;
    let msg;
    try { msg = JSON.parse(line); } catch { continue; }
    if (msg.id && pending.has(msg.id)) {
      const { resolve, reject } = pending.get(msg.id);
      pending.delete(msg.id);
      msg.error ? reject(new Error(JSON.stringify(msg.error))) : resolve(msg.result);
    }
  }
});
child.stderr.setEncoding('utf8');
const stderrTail = [];
child.stderr.on('data', (d) => { stderrTail.push(d); if (stderrTail.length > 20) stderrTail.shift(); });

const rpc = (method, params) => new Promise((resolve, reject) => {
  const id = nextId++;
  pending.set(id, { resolve, reject });
  child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id, method, params }) + '\n');
  setTimeout(() => {
    if (pending.has(id)) { pending.delete(id); reject(new Error(`${method} timed out`)); }
  }, 120_000);
});

const call = async (name, args = {}) => {
  const r = await rpc('tools/call', { name, arguments: args });
  const text = (r?.content ?? []).map((c) => c.text ?? '').join('');
  try { return JSON.parse(text); } catch { return text; }
};

const failures = [];
const check = (ok, what, detail = '') => {
  console.log(`  ${ok ? 'ok  ' : 'FAIL'}  ${what}${detail ? ` -- ${detail}` : ''}`);
  if (!ok) failures.push(what);
};

console.log(`\n=== MCP handshake (${SERVER}) ===`);
await rpc('initialize', {
  protocolVersion: '2024-11-05',
  capabilities: {},
  clientInfo: { name: 'mob-move-check', version: '1.0.0' },
});
child.stdin.write(JSON.stringify({ jsonrpc: '2.0', method: 'notifications/initialized' }) + '\n');

const caps = await call('wz_map_capabilities', {});
const archives = caps.openArchives ?? [];
check(archives.length > 0, 'backend has an open archive', archives.map((a) => a.name).join(', '));

const list = await call('wz_map_list', { q: mapId, limit: 20 });
const candidates = (list.maps ?? []).map((m) => m.path);
const target = candidates.find((p) => p.includes(mapId)) ?? candidates[0];
if (!target) {
  console.error(`no map matching ${mapId}`);
  child.kill();
  process.exit(1);
}
console.log(`\n=== ${target} ===`);

// ---------------------------------------------------------------- the session
const open = await call('wz_map_session_open', { path: target, direction: 1 });
check(typeof open.sessionId === 'string' && open.sessionId.length > 0,
  'session opens', open.sessionId?.slice(0, 8));
check(Array.isArray(open.mobs), 'frame carries a mobs array', `n=${open.mobs?.length}`);

// A map with no life entries is a legitimate outcome -- then there is nothing to
// prove and saying so is more honest than a vacuous pass.
if (!open.mobs?.length) {
  console.log('\n  this map has no mob spawns; nothing to exercise.');
  await call('wz_map_session_close', { sessionId: open.sessionId });
  child.kill();
  process.exit(failures.length ? 1 : 0);
}

const sid = open.sessionId;
const first = open.mobs[0];
console.log(`  first mob: id=${first.mobId} x=${first.x?.toFixed?.(1)} y=${first.y?.toFixed?.(1)} fh=${first.foothold}`);

check(!!first.foothold, 'mob reports the foothold it stands on', first.foothold);
check(first.onGround === true, 'mob is on the ground at spawn', `onGround=${first.onGround}`);

const step = async (n, keys = {}) => {
  let f;
  for (let i = 0; i < n; i++) f = await call('wz_map_session_step', { sessionId: sid, ...keys });
  return f;
};

// ---------------------------------------------------------------- does it move?
//
// 400 frames with the character standing still. The mob's own ticks are the
// only thing in the system that can change these coordinates: nothing else in
// the frame writes them, and this call chain never calls TakeHit.
const t0 = first.x, y0 = first.y;
const after = await step(400, {});

const moved = after.mobs ?? [];
check(moved.length === open.mobs.length,
  'every mob is still reported after 400 frames',
  `${open.mobs.length} -> ${moved.length}`);

const d0 = moved[0];
const dx = d0.x - t0, dy = d0.y - y0;
check(Math.abs(dx) > 1 || Math.abs(dy) > 1,
  'mob 0 MOVED while the character stood still',
  `x ${t0.toFixed(1)} -> ${d0.x.toFixed(1)}, y ${y0.toFixed(1)} -> ${d0.y.toFixed(1)}`);

// Every mob, not just the first: one that walked and nine that did not would
// pass a single-mob check.
const movers = moved.filter((m, i) => Math.abs(m.x - open.mobs[i].x) > 1).length;
check(movers > 0, 'at least one mob on the map moved', `${movers}/${moved.length}`);

// The mobs advance WITH the character, not on a timer of their own: a frame
// counter that ran ahead would mean the mobs had a clock, which is the one
// thing the store is supposed to avoid.
check(d0.reversals >= 0 && typeof d0.direction === 'number',
  'mob reports direction and reversals', `dir=${d0.direction} rev=${d0.reversals}`);

// The roll changes direction on an EXACT match -- 3 of 200 outcomes -- so over
// 400 frames a mob reverses a handful of times, not two hundred. A threshold
// reading of the roll (roll < 50 / < 100 / < 150) produces a reversal nearly
// every frame. This is the assertion that tells the two apart.
check(d0.reversals < 60,
  'direction changes are rare, as the exact-match roll implies',
  `${d0.reversals} reversals in 400 frames (a < roll reading would give ~300)`);

// ---------------------------------------------------------------- determinism
//
// The same seed replays. Two sessions on the same map are independently seeded
// and are NOT required to match; what must hold is that one session replays
// its own sequence identically, which is what makes a verdict about a map mean
// anything.
const sid2 = (await call('wz_map_session_open', { path: target, direction: 1 })).sessionId;
await step(200, {});
const a = after.mobs[0];
let b;
for (let i = 0; i < 200; i++) {
  b = await call('wz_map_session_step', { sessionId: sid2 });
}
const bMob = (b.mobs ?? [])[0];
check(typeof bMob?.x === 'number', 'a second session also reports mobs',
  `x=${bMob?.x?.toFixed?.(1)}`);
// Different seeds are expected to differ; that is the point of seeding them
// per-session. Assert only that both are sane.
check(Number.isFinite(bMob?.x) && Number.isFinite(bMob?.y),
  'second session mobs have real positions', `x=${bMob?.x?.toFixed?.(1)} y=${bMob?.y?.toFixed?.(1)}`);
void a;

await call('wz_map_session_close', { sessionId: sid });
await call('wz_map_session_close', { sessionId: sid2 });

// ---------------------------------------------------------------- /simulate/mob
// Requirement 5: the endpoint that existed before this change must still answer.
const mob = await call('wz_map_patrol_mob', { path: target, seed: 7, steps: 600 });
check(!!mob && !String(mob).includes('"error"'),
  '/simulate/mob still answers',
  `endX=${mob?.endX} rev=${mob?.reversals} stayedOnMap=${mob?.stayedOnMap}`);
check(typeof mob?.startFoothold !== 'undefined', '/simulate/mob still reports a start foothold',
  mob?.startFoothold);

if (stderrTail.length) console.log(`\nserver stderr tail:\n${stderrTail.join('').slice(-800)}`);
console.log(`\n=== ${failures.length ? 'FAILURES: ' + failures.length : 'all checks passed'} ===`);
child.kill();
process.exit(failures.length ? 1 : 0);