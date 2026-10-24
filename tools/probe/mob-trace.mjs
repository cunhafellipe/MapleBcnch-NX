// Trace ONE mob's coordinates frame by frame, straight off the live session.
//
//   node mob-trace.mjs <Map.nx abs path> [mapId] [WZ_SERVER] [mobIndex] [frames]
//
// WHY THIS EXISTS SEPARATELY
// --------------------------
// mob-move-check.mjs answers "did it move". This answers "how does it move",
// which is the question that tells you whether it is moving for the reference's
// reason. The reference rolls every frame but changes direction on only 3 of
// 200 outcomes (Mob.cs:354-375), so a correct mob walks in long straight
// stretches and turns rarely. A mob that changed direction every few frames
// would also pass "did it move", and would be the wrong animal.

import { spawn } from 'node:child_process';
import path from 'node:path';
import process from 'node:process';

const [, , mapPath, mapId = '261010002', serverArg, mobIdx = '0', frameArg = '90'] = process.argv;
if (!mapPath) {
  console.error('usage: node mob-trace.mjs <Map.nx abs path> [mapId] [WZ_SERVER] [mobIndex] [frames]');
  process.exit(2);
}

// The instance comes from the [WZ_SERVER] positional or from WZ_SERVER, and from
// nowhere else (task-139).  Both names are KEPT because both have measured
// callers: the positional is documented in the usage line above, and WZ_SERVER
// is read by MapleMcp/MapClient.cs:32 and set by tools/probe/maple_mcp.py:89.
// What goes away is the DEFAULT PORT.  Note what it used to be: 5480, the port
// pm/GATE.md:27 lists as banned.  A probe that defaults to a banned port and
// then talks to whatever is listening there is the worst form of this defect.
const SERVER_ARG = (serverArg || '').trim();
const SERVER_ENV = (process.env.WZ_SERVER || '').trim();
if (SERVER_ARG && SERVER_ENV && SERVER_ARG !== SERVER_ENV) {
  console.log(`>>> RED: the [WZ_SERVER] argument (${JSON.stringify(SERVER_ARG)}) and `
    + `WZ_SERVER (${JSON.stringify(SERVER_ENV)}) name different instances. Pass one.`);
  process.exit(1);
}
const SERVER_RAW = SERVER_ARG || SERVER_ENV;
if (!SERVER_RAW) {
  console.log('>>> RED: neither the [WZ_SERVER] argument nor WZ_SERVER is set, so there is no backend to measure.');
  console.log('       Set it to the instance under test, for example:');
  console.log("         node " + (process.argv[1] || 'this probe') + " <Map.nx> <mapId> http://127.0.0.1:<port>");
  console.log('         or export WZ_SERVER before running it.');
  console.log('       There is no default port on purpose: an older instance answers every one of these requests.');
  process.exit(1);
}
const SERVER = SERVER_RAW.replace(/\/+$/, '');
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

await rpc('initialize', {
  protocolVersion: '2024-11-05',
  capabilities: {},
  clientInfo: { name: 'mob-trace', version: '1.0.0' },
});
child.stdin.write(JSON.stringify({ jsonrpc: '2.0', method: 'notifications/initialized' }) + '\n');

const list = await call('wz_map_list', { q: mapId, limit: 20 });
const target = (list.maps ?? []).map((m) => m.path).find((p) => p.includes(mapId));
if (!target) {
  console.error(`no map matching ${mapId}`);
  child.kill();
  process.exit(1);
}

const idx = Number(mobIdx) || 0;
const frames = Number(frameArg) || 90;

const open = await call('wz_map_session_open', { path: target, direction: 1 });
const sid = open.sessionId;
const m0 = (open.mobs ?? [])[idx];
if (!m0) {
  console.error(`no mob at index ${idx} (map has ${open.mobs?.length ?? 0})`);
  await call('wz_map_session_close', { sessionId: sid });
  child.kill();
  process.exit(1);
}

console.log(`\n=== ${target}  mob[${idx}] id=${m0.mobId} ===`);
console.log(`spawn: x=${m0.x.toFixed(1)} y=${m0.y.toFixed(1)} fh=${m0.foothold}\n`);
console.log('frame      x       y     dir  facing  rev   fh');

const rows = [];
let lastDir = m0.direction;
let turns = 0;
let moving = 0;

for (let i = 1; i <= frames; i++) {
  const f = await call('wz_map_session_step', { sessionId: sid });
  const m = (f.mobs ?? [])[idx];
  if (!m) { console.error(`mob ${idx} vanished at frame ${i}`); break; }
  if (m.direction !== lastDir) turns++;
  if (m.direction !== 0) moving++;
  lastDir = m.direction;
  rows.push({ i, x: m.x, y: m.y, dir: m.direction, facing: m.facing, rev: m.reversals, fh: m.foothold });
  if (i <= 30 || i % 10 === 0) {
    console.log(
      `${String(i).padStart(5)}  ${m.x.toFixed(1).padStart(7)}  ${m.y.toFixed(1).padStart(6)}` +
      `  ${String(m.direction).padStart(4)}  ${String(m.facing).padStart(6)}` +
      `  ${String(m.reversals).padStart(4)}  ${m.foothold}`);
  }
}

const first = rows[0], last = rows[rows.length - 1];
const dx = last.x - m0.x;
console.log(`\nmob travelled dx=${dx.toFixed(1)} px over ${frames} frames`);
console.log(`frames with a heading: ${moving}/${frames}  (${(100 * moving / frames).toFixed(0)}%)`);
console.log(`heading changes:       ${turns}/${frames}  (${(100 * turns / frames).toFixed(1)}%)`);
console.log(`reversals reported:    ${last.rev}`);
console.log(`foothold changes:      ${new Set(rows.map((r) => r.fh)).size} distinct`);

// The reference's own numbers, for comparison. The roll changes direction on
// 3 of 200 outcomes, so a long straight run is the EXPECTED shape; the top
// band of the roll is a jump request, which a patrol does not do.
console.log('\nreference (Mob.cs:354-375): a heading change is 3/200 = 1.5% per frame,');
console.log('so ~' + Math.round(frames * 0.015) + ' expected in ' + frames + ' frames.');
if (turns > frames * 0.05) {
  console.log('WARNING: more turns than the exact-match roll allows.');
}

await call('wz_map_session_close', { sessionId: sid });
child.kill();
process.exit(0);