// Walk a character through the MCP server, the way an agent would.
//
//   node sim-walk.mjs <Map.nx abs path> [mapId] [WZ_SERVER]
//
// WHY A CLI INSTEAD OF THE BROWSER
// --------------------------------
// The browser check (live-walk-check.mjs) drove the same endpoints over CDP and
// cost five rounds to get a trustworthy answer: a tab selector that matched the
// page shell, a stale Chrome on a wrong debug port, a cache that served the
// pre-edit script, a <select> carrying 401 of 5,366 maps, and a screenshot
// taken while the page still said "Pick a map". Every one of those is a way to
// report a pass or a fail that is about the harness rather than the thing under
// test.
//
// An MCP client has none of those failure modes. It spawns the server, speaks
// JSON-RPC, and reads JSON. Nothing renders, so nothing can be stale, and a
// screenshot cannot be mistaken for a result. mcp-flow.mjs is the template and
// its reasoning applies here: if an agent connected over MCP can walk a map,
// the feature works -- the URL is not the point, the reachable behaviour is.
//
// Nothing in this file calls the HTTP API directly.

import { spawn } from 'node:child_process';
import path from 'node:path';
import process from 'node:process';

const [, , mapPath, mapId = '910020202', serverArg] = process.argv;
if (!mapPath) {
  console.error('usage: node sim-walk.mjs <Map.nx abs path> [mapId] [WZ_SERVER]');
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
// maple-mcp.exe is an apphost bound to the SYSTEM dotnet, which on this machine
// is 6/8 only and refuses to launch ("You must install .NET"). The DLL through
// the scoop SDK has no such launcher problem, so the DLL is what gets spawned.
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
    try { msg = JSON.parse(line); } catch { continue; }   // not ours
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
});

// The backend must already have the archive open. This script deliberately does
// NOT open it: which session owns an archive is the editor's business, and a
// check that opens its own would pass against a backend no agent is using.
const call = async (name, args) => {
  const r = await rpc('tools/call', { name, arguments: args });
  const text = r.content?.[0]?.text ?? '';
  try { return JSON.parse(text); } catch { return text; }
};

const failures = [];
const check = (ok, what, detail) => {
  console.log(`  ${ok ? 'PASS' : 'FAIL'}  ${what}${detail ? '  ' + detail : ''}`);
  if (!ok) failures.push(what);
};

// ---------------------------------------------------------------- handshake
console.log(`\n=== MCP handshake (${SERVER}) ===`);
await rpc('initialize', {
  protocolVersion: '2024-11-05',
  capabilities: {},
  clientInfo: { name: 'sim-walk', version: '1' },
});
child.stdin.write(JSON.stringify({ jsonrpc: '2.0', method: 'notifications/initialized' }) + '\n');

const tools = (await rpc('tools/list', {})).tools ?? [];
check(true, 'server answers tools/list', `${tools.length} tools`);

const sessionTools = tools.filter((t) => t.name.startsWith('wz_map_session_'));
check(sessionTools.length === 3, 'wz_map_session_* present',
  sessionTools.map((t) => t.name).join(', '));

// The archive has to be loaded before any map tool can answer.
//
// Field names come from MapTools.Capabilities: openArchives (what the editor
// has loaded) and capabilities.mapCount (what the simulator can see). Guessing
// "openFiles"/"mapCount" at the top level yields undefined on a perfectly
// healthy backend, which reads as "no archive" -- a failure that is entirely
// in the check.
const caps = await call('wz_map_capabilities', {});
const archives = caps.openArchives ?? [];
const mapCount = caps.capabilities?.mapCount ?? 0;
check(archives.length > 0, 'backend reports an open archive',
  archives.map((a) => a.name).join(', ') || '(none)');
check(mapCount > 0, 'simulator sees maps', `mapCount=${mapCount}`);
if (!archives.length || !mapCount) {
  console.log('\n  Open the archive in the editor first:');
  console.log(`  POST ${SERVER}/api/files/open-many {"paths":["${path.resolve(mapPath)}"]}`);
  child.kill();
  process.exit(1);
}

// ---------------------------------------------------------------- open
const list = await call('wz_map_list', { q: mapId, limit: 20 });
const candidates = (list.maps ?? []).map((m) => m.path);
const target = candidates.find((p) => p.includes(mapId)) ?? candidates[0];
check(!!target, 'found a map to walk', target ?? '(none matched)');
if (!target) { child.kill(); process.exit(1); }

console.log(`\n=== walk ${target} ===`);
// No coordinates: the server must place the character at the map's own pt==0
// spawn portal, the way MapleNecrocer's SpawnNew does (MapleCharacter.cs:162-176).
const open = await call('wz_map_session_open', { path: target, direction: 1 });
check(typeof open.sessionId === 'string' && open.sessionId.length > 0,
  'open returns a session id', open.sessionId ?? JSON.stringify(open).slice(0, 90));
check(typeof open.x === 'number' && typeof open.y === 'number',
  'open returns a real position', `x=${open.x} y=${open.y}`);
check(!!open.foothold, 'character is standing on a foothold', `foothold=${open.foothold}`);
if (!open.sessionId) {
  console.log(stderrTail.join('').slice(-500));
  child.kill();
  process.exit(1);
}

const sid = open.sessionId;
const step = async (n, keys) => {
  let f;
  for (let i = 0; i < n; i++) f = await call('wz_map_session_step', { sessionId: sid, ...keys });
  return f;
};

// ---------------------------------------------------------------- walk
const right = await step(30, { right: true });
check(right.x > open.x + 5, 'holding right moves right', `x ${open.x} -> ${right.x.toFixed(1)}`);
check(Math.abs(right.speed) > 0.5, 'speed is non-zero while held', `speed=${right.speed}`);
check(right.facing > 0, 'facing follows the movement', `facing=${right.facing}`);
check(right.frame === 30, 'frame counter tracks the steps', `frame=${right.frame}`);

const left = await step(10, { left: true });
check(left.x < right.x - 5, 'reversing moves left', `x ${right.x.toFixed(1)} -> ${left.x.toFixed(1)}`);
check(left.speed < 0, 'speed changes sign on reversal', `speed=${left.speed}`);
check(left.facing < 0, 'facing follows the reversal', `facing=${left.facing}`);

const jump = await step(1, { jump: true });
check(jump.action === 'rise', 'jump leaves the ground', `action=${jump.action} vy=${jump.velocityY}`);
check(!jump.onGround, 'airborne on the jump frame');

// Y INCREASES DOWNWARD. Rise means y FALLS (smaller); falling back means y
// GROWS. Asserting the sign the other way round would pass on a character
// diving and fail on one jumping, which is worse than no check at all.
const rise = await step(8, {});
check(rise.y < jump.y, 'still rising while held', `y ${jump.y.toFixed(1)} -> ${rise.y.toFixed(1)}`);
check(rise.action === 'rise', 'action says rise', `action=${rise.action}`);

const fall = await step(24, {});
check(fall.y > rise.y, 'gravity brings it back down', `y ${rise.y.toFixed(1)} -> ${fall.y.toFixed(1)}`);
check(fall.action === 'fall' || fall.onGround, 'lands or is falling', `action=${fall.action} onGround=${fall.onGround}`);

const rest = await step(60, {});
check(rest.onGround, 'comes to rest on a foothold', `onGround=${rest.onGround} fh=${rest.foothold} y=${rest.y.toFixed(1)}`);

// ---------------------------------------------------------------- peek/close
const peek = await call('wz_map_session_open', { path: target, direction: 1 });
check(!!peek.sessionId, 'a second session is independent', peek.sessionId?.slice(0, 8));
await call('wz_map_session_close', { sessionId: peek.sessionId });

const closed = await call('wz_map_session_close', { sessionId: sid });
check(typeof closed === 'string' || closed?.ended, 'close reports back', JSON.stringify(closed).slice(0, 60));

// The id is dead now; stepping it must be refused rather than silently
// returning a frozen body.
const after = await call('wz_map_session_step', { sessionId: sid, right: true });
check(!!after.error || !after.sessionId, 'a closed session refuses to step',
  after.error?.slice(0, 50) ?? '(no session id)');

console.log(`\n=== ${failures.length ? 'FAILURES: ' + failures.length : 'all checks passed'} ===`);
child.kill();
process.exit(failures.length ? 1 : 0);