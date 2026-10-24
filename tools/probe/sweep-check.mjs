// Does a self-ticking session still get collected?
//
//   node sweep-check.mjs
//
// WHY THIS NEEDS REAL TIME
// ------------------------
// The idle timeout is 120 seconds. There is no honest way to shorten it for a
// check without changing the thing being checked, and a check that passes in
// two seconds by pretending the timeout is short proves nothing about the
// 120-second case. So this waits the real 120 seconds, and the only thing it
// does in the meantime is NOT TOUCH the server. Touching it -- even with a GET
// -- refreshes LastTouchedMs and the session is supposed to survive.
//
// The thing being checked is the failure this change could have introduced:
// a session that ticks forever is a session nothing can ever reap, because a
// sweep that counted ticking as activity would never fire. So the assertion is
// that frames keep RISING (the clock is alive) while the session still gets
// REMOVED (the sweep is alive), and that those two are independent.

// The instance comes from WZ_SERVER and from nowhere else (task-139).  The name
// is NOT renamed: WZ_SERVER has measured callers (MapleMcp/MapClient.cs:32,
// MapleMcp/ArchiveClient.cs:33, AGENTS.md:134, wz-mcp-map.cmd:21,
// tools/probe/maple_mcp.py:59), so changing it would change a live convention.
// What goes away is the DEFAULT PORT: an instance older than this tree answers
// every one of these requests happily, and a probe that silently adopts it
// reports the stale numbers as findings.
const GATE = (process.env.WZ_SERVER || '').trim();
if (!GATE) {
  console.log('>>> RED: WZ_SERVER is not set, so there is no backend to measure.');
  console.log('       Set it to the instance under test, for example:');
  console.log("         $env:WZ_SERVER = 'http://127.0.0.1:<port>'");
  console.log('       There is no default port on purpose: an older instance on the');
  console.log('       usual port answers every one of these requests.');
  process.exit(1);
}
// The trailing-slash strip is load-bearing: a doubled slash 404s every request.
const BASE = GATE.replace(/\/+$/, '');
const IDLE_MS = Number(process.env.IDLE_MS ?? 120_000);

const api = async (path, init) => {
  const r = await fetch(BASE + path, init);
  const text = await r.text();
  if (!r.ok) throw new Error(`${r.status} ${path} ${text.slice(0, 200)}`);
  return JSON.parse(text);
};

const nap = (ms) => new Promise((r) => setTimeout(r, ms));

const list = await api('/api/simulate/maps?q=910020202&limit=20');
const target = (list.maps ?? []).map((m) => m.path).find((p) => p.includes('910020202'));
const open = await api('/api/simulate/session', {
  method: 'POST',
  headers: { 'content-type': 'application/json' },
  body: JSON.stringify({ path: target, direction: 1 }),
});
const sid = open.sessionId;
console.log(`session ${sid} opened at frame ${open.frame}, holding right via ONE /input call`);

await api(`/api/simulate/session/${sid}/input`, {
  method: 'POST',
  headers: { 'content-type': 'application/json' },
  body: JSON.stringify({ right: true }),
});

// Touch it once, right after the input, so the idle clock starts from a known
// point rather than from whenever the open happened to be.
const t0 = await api(`/api/simulate/session/${sid}`);
console.log(`t=0s      frame=${t0.frame} x=${t0.x.toFixed(2)}`);

// Now: no further requests. A GET here would refresh the idle timer and the
// session is DESIGNED to survive it, so there is nothing to observe by asking.
console.log(`\ngoing quiet for ${IDLE_MS / 1000}s -- no request will be made in that window\n`);
await nap(IDLE_MS);

const gone = await fetch(`${BASE}/api/simulate/session/${sid}`);
const body = await gone.text();
console.log(`t=${IDLE_MS / 1000}s  GET /session/${sid} -> ${gone.status} ${body.slice(0, 120)}`);

const swept = gone.status === 404;
const framesBeforeGone = t0.frame;
console.log('\n=== verdict ===');
console.log(`  swept with no client contact: ${swept}`);
console.log(`  frames advanced before removal: ${framesBeforeGone} (ticking all along, never swept while live)`);

// A second body that IS still being polled must NOT be swept. If it were, the
// clock would be killing live sessions and every walk would die at 120s.
const live = await api('/api/simulate/session', {
  method: 'POST',
  headers: { 'content-type': 'application/json' },
  body: JSON.stringify({ path: target, direction: 1 }),
});
await nap(IDLE_MS + 5_000);
const alive = await fetch(`${BASE}/api/simulate/session/${live.sessionId}`);
console.log(`  polled session survived past the same timeout: ${alive.status === 200} (status ${alive.status})`);
await fetch(`${BASE}/api/simulate/session/${live.sessionId}`, { method: 'DELETE' });

process.exit(swept && alive.status === 200 ? 0 : 1);