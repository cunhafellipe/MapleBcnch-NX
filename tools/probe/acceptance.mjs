// Full acceptance run for the map-generation work.
//
//   node acceptance.mjs <baseUrl> <outJson>
//
// Every claim this project makes has to survive a real request against a real
// archive. A 200 on its own proves nothing — the previous round found a set the
// palette called usable that could not actually be placed, and the only reason
// it stayed hidden is that nobody looked at the pixels.
//
// Sections, in order of what they protect:
//   A  build-level invariants (capabilities, palette honesty)
//   B  the three features added this round (validate / batch / reopen-save)
//   C  persistence: restart the process, read the file back from disk
//   D  regression: the rules in docs/MAP-STRUCTURE.md still hold
//   E  the simulator: a character walks the map under the client's physics,
//      which is the only thing here that can answer whether the LINKS being
//      intact still leaves ground under the character's feet. Runs last
//      because it is read-only and must not disturb what C saved.
//   F  did it go anywhere: the checks that catch a walk which SUCCEEDS and
//      moves nowhere. Written after four bugs passed every other section.

import { writeFileSync } from 'node:fs';

const [, , base, out] = process.argv;
const checks = [];
let failed = 0;

const ok = (name, pass, detail) => {
  checks.push({ name, pass: !!pass, detail: String(detail ?? '') });
  if (!pass) failed++;
  console.log(`${pass ? 'PASS' : 'FAIL'}  ${name}${detail ? `  — ${detail}` : ''}`);
};

async function api(path, body) {
  const res = await fetch(base + path, {
    method: body ? 'POST' : 'GET',
    headers: { 'Content-Type': 'application/json' },
    body: body ? JSON.stringify(body) : undefined,
  });
  const text = await res.text();
  let json;
  try { json = JSON.parse(text); } catch { json = { raw: text.slice(0, 200) }; }
  return { status: res.status, json };
}

// ---------------------------------------------------------------- A. palette
console.log('\n=== A. 素材調色盤 ===');
const caps = (await api('/api/mapedit/capabilities')).json;
ok('capabilities reachable', caps.available, `maps=${caps.mapCount}`);

const tileSets = (await api('/api/mapedit/palette/sets?kind=Tile')).json;
ok('tile sets listed', Array.isArray(tileSets) && tileSets.length > 0, `${tileSets.length} sets`);

const noPixels = tileSets.filter((s) => !s.drawableEntries || s.drawableEntries === 0);
const noThumb = tileSets.filter((s) => !s.thumbPath);
ok('every set reports drawable entries', noPixels.length === 0,
   `${noPixels.length} without pixels` + (noPixels.length ? `: ${noPixels.slice(0,3).map(s=>s.name)}` : ''));
ok('every set with entries has a thumbnail', noThumb.length === 0,
   `${noThumb.length} without thumbPath`);

// The set used by hand earlier this session: it must be placeable, and the
// inventory must agree that it exists. This is the claim that was wrong once.
const af = tileSets.find((s) => s.name === 'ancientForest');
if (af) {
  const entries = (await api(`/api/mapedit/palette/entries?kind=Tile&path=${encodeURIComponent(af.path)}&limit=200`)).json;
  const bsc0 = entries.entries?.find((e) => e.u === 'bsc' && e.no === 0);
  ok('ancientForest bsc/0 exists in its own set', !!bsc0,
     bsc0 ? `${bsc0.w}x${bsc0.h}` : 'not found');
} else {
  ok('ancientForest present in sets', false, 'set missing');
}

// ------------------------------------------------------------- B. new work
console.log('\n=== B. 本輪新增的三項 ===');
const MAP = 'f1/Map/Map0/000000001.img';

const opened = await api('/api/mapedit/open', { path: MAP });
ok('map opens', opened.status === 200 && opened.json.layers?.length > 0,
   `${opened.json.layers?.length} layers`);

if (opened.status !== 200) {
  // Nothing downstream can be honest if the archive never opened. Say so and
  // stop rather than reporting a cascade of null-shaped failures.
  console.error('\nFATAL: the archive is not open. Open it first:');
  console.error('  node -e \'fetch("' + base + '/api/files/open",{method:"POST",'
    + 'headers:{"Content-Type":"application/json"},'
    + 'body:JSON.stringify({path:"<abs path to Map.nx>"})})\'');
  writeFileSync(out, JSON.stringify({ base, failed: checks.length, checks }, null, 1));
  process.exit(2);
}

const before = (await api('/api/mapedit/validate', { path: MAP })).json;
ok('validate: clean map passes', before.ok === true,
   `footholds=${before.footholds} broken=${before.brokenChains} errors=${(before.errors ?? []).length}`);

// validate must CATCH things, not merely agree with a clean map. Break it on
// purpose: point a foothold's next at an id that does not exist.
const doc = (await api(`/api/mapedit/doc?path=${encodeURIComponent(MAP)}`)).json;
const target = doc.footholds?.find((f) => f.next && f.next !== '0');
let brokeChain = false;
if (target) {
  const probed = (await api(`/api/mapedit/node?path=${encodeURIComponent(MAP)}&addr=${target.addr.join(',')}`)).json;
  const nextChild = probed.children?.find((c) => c.name === 'next');
  if (nextChild) {
    await api('/api/mapedit/edit', {
      path: MAP, op: { kind: 'setValue', addr: nextChild.addr, value: '999999' },
    });
    brokeChain = true;
  }
}
if (brokeChain) {
  const broken = (await api('/api/mapedit/validate', { path: MAP })).json;
  ok('validate: catches a severed foothold chain',
     broken.ok === false && broken.brokenChains > 0,
     `brokenChains=${broken.brokenChains}`);
  await api('/api/mapedit/undo', { path: MAP });
  const healed = (await api('/api/mapedit/validate', { path: MAP })).json;
  ok('undo repairs it', healed.ok === true, `errors=${(healed.errors ?? []).length}`);
} else {
  ok('validate: chain-break probe', false, 'could not find a foothold with a next to break');
}

// batch: many operations must collapse into ONE history entry, because the
// stack holds 200 and a generated floor exceeds that.
// batch is exercised in section D, after a tile exists to edit — see below.

// ------------------------------------------------------------------- write
console.log('\n=== C. 寫入與存檔 ===');
const place = await api('/api/mapedit/place', {
  path: MAP, kind: 'tile', x: -350, y: 200, layer: 0,
  set: 'ancientForest', u: 'bsc', no: 3, adoptLayerTs: true,
});
ok('place a tile', place.status === 200, JSON.stringify(place.json).slice(0, 90));

// Run the batch probe now. A freshly opened map can have empty layers, and
// "no tile to edit" is a state, not an excuse to report green.
console.log('\n=== D. batch（放置後）===');
const after0 = (await api(`/api/mapedit/doc?path=${encodeURIComponent(MAP)}`)).json;
const editables = [];
for (const layer of after0.layers ?? []) {
  for (const t of layer.tiles ?? []) {
    const n = (await api(`/api/mapedit/node?path=${encodeURIComponent(MAP)}&addr=${t.addr.join(',')}`)).json;
    const u = n.children?.find((c) => c.name === 'u' && c.value);
    if (u) editables.push(u);
  }
}
if (editables.length === 0) {
  ok('batch: an editable node exists after placing', false, 'still none — batch untested');
} else {
  const depthBefore = (await api(`/api/mapedit/doc?path=${encodeURIComponent(MAP)}`)).json.undoDepth;
  const ops = Array.from({ length: 300 }, () => ({
    kind: 'setValue', addr: editables[0].addr, value: editables[0].value,
  }));
  const batch = (await api('/api/mapedit/batch', { path: MAP, ops, label: 'acceptance' })).json;
  ok('batch applies every op', batch.applied === 300, `applied=${batch.applied}/${batch.requested}`);
  // The batch must add exactly ONE entry to the history, whatever its size.
  // Comparing against the depth before it is the only correct assertion:
  // whatever else this run has done also occupies history, and that is fine.
  ok('batch collapses history to one entry', batch.undoDepth === depthBefore + 1,
     `${depthBefore} -> ${batch.undoDepth} for 300 ops against a 200-entry stack`);
  const undone = (await api('/api/mapedit/undo', { path: MAP })).json;
  ok('one undo reverts the whole batch', /acceptance/.test(undone.applied ?? ''),
     `applied="${undone.applied}"`);
}

const finalCheck = (await api('/api/mapedit/validate', { path: MAP })).json;
ok('map still valid after the whole run', finalCheck.ok === true,
   `errors=${(finalCheck.errors ?? []).length} broken=${finalCheck.brokenChains}`);

const save = (await api('/api/mapedit/save', { path: MAP })).json;
ok('save verifies', save.verified === true,
   `${Number(save.seconds).toFixed(2)}s reopenMs=${save.reopenMs} diffs=${(save.differences||[]).length}`);

// ------------------------------------------------------------- E. simulator
// The character walk is the only thing here that answers a question /validate
// cannot: the links can be intact while there is no ground under the character.
// It is read-only, so it runs after the save rather than instead of it.
console.log('\n=== E. 角色模擬 ===');
const simCaps = (await api('/api/simulate/capabilities')).json;
ok('simulator reachable', simCaps.available === true,
   `archives=${(simCaps.mapArchives || []).join(',')} maps=${simCaps.mapCount}`);

const right = (await api('/api/simulate/character', { path: MAP, direction: 1 })).json;
const left = (await api('/api/simulate/character', { path: MAP, direction: -1 })).json;

// Whatever the outcome, the verdict has to be one the endpoint documents.
// Walking into a portal is arriving somewhere, not crossing this map — it is
// its own verdict and was missing here, so a map that exits correctly read as
// "other" and failed the check that exists to catch exactly that.
const verdictOf = (r) =>
  r.reachedEdge ? 'crossed'
  : r.reachedPortal ? 'portal'
  : r.fellOut ? 'blocked'
  : r.chainEndedShort ? 'chain-ended'
  : 'other';

ok('walk right returns a known verdict', verdictOf(right) !== 'other',
   `${verdictOf(right)} pct=${Number(right.percentOfMap).toFixed(1)} ticks=${right.ticks}`);
ok('walk left returns a known verdict', verdictOf(left) !== 'other',
   `${verdictOf(left)} pct=${Number(left.percentOfMap).toFixed(1)} ticks=${left.ticks}`);

// The physics constants are echoed so a result can be reproduced. If they drift,
// a walk that passed last week is not comparable to one that passes today.
ok('walk reports the client constants', right.stepSeconds > 0 && right.maxWalkSpeed > 0,
   `step=1/${Math.round(1 / right.stepSeconds)}s accel=${right.acceleration} max=${right.maxWalkSpeed}`);

// A link stub has no geometry. Saying "walkable" about one would be a lie, so
// the endpoint has to refuse it rather than answer with a zero.
const stub = right.ticks === 0 && right.footholds === 0;
ok('a map with no foothold is not called walkable', !(stub && right.reachedEdge),
   stub ? 'link stub, refused' : `${right.footholds} footholds`);

// The jump and drop paths exist on the character; without this they would be
// code no run ever reaches.
const jumped = (await api('/api/simulate/character', { path: MAP, direction: 1, jumpEvery: 60 })).json;
ok('jump path runs without error', typeof jumped.ticks === 'number',
   `jumpEvery=60 ticks=${jumped.ticks} verdict=${verdictOf(jumped)}`);
const dropped = (await api('/api/simulate/character', { path: MAP, direction: 1, dropAtStep: 120 })).json;
ok('drop-through path runs without error', typeof dropped.ticks === 'number',
   `dropAtStep=120 ticks=${dropped.ticks} verdict=${verdictOf(dropped)}`);

// The chain walk and the character walk must not be the same thing wearing
// different names. If they ever agree on every map, one of them is lying.
const chain = (await api('/api/simulate/walk', { path: MAP })).json;
ok('character walk is not a relabelled chain walk',
   Math.abs(Number(chain.percentOfMap) - Number(right.percentOfMap)) > 0.01
     || chain.steps !== right.ticks,
   `chain ${chain.steps} steps vs character ${right.ticks} steps`);

// Every blocked verdict must be able to say what to change. A report an agent
// cannot act on is not a result — the whole point of running the walk is to get
// from "this map is broken" to "extend foothold N".
if (right.fellOut) {
  const advice = (await api(
    `/api/simulate/advice?path=${encodeURIComponent(MAP)}&x=${right.finalX}&y=${right.finalY}&dir=1`
  )).json;
  ok('a blocked map can be advised on',
     !!advice.suggestion && advice.suggestion.length > 20,
     `${advice.problem} -> ${(advice.suggestion ?? '').slice(0, 80)}`);
}

// ------------------------------------------------------- F. did it go anywhere
//
// Everything above asks whether a request SUCCEEDS. This section asks whether
// the character actually MOVED, because four bugs in a row passed every other
// check while the character stood still:
//
//   18,000 frames   23/23 passed
//   jump peaked 0.3px   23/23 passed
//   handover gated on reachedEnd   23/23 passed
//
// A green suite meant nothing because none of it could see the difference
// between "walked" and "returned without error". These three rules can.

console.log('\n=== F. 有沒有真的走 ===');

// Deterministic sample so a failure can be re-run.
const SAMPLE_SEED = 20261012;
const SAMPLE_N = 120;

const mulberry32 = (a) => () => {
  a |= 0; a = (a + 0x6D2B79F5) | 0;
  let t = Math.imul(a ^ (a >>> 15), 1 | a);
  t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
  return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
};

// F1 — a walk across a multi-segment chain must cross more than one foothold.
// Measured on f1/Map/Map6/610030510.img: 4 segments after the fix, 1 before it.
// One segment means the character stopped on the first foothold it reached.
{
  const t = (await api('/api/simulate/trace?path=' +
    encodeURIComponent('f1/Map/Map6/610030510.img') +
    '&x=0&y=0&dir=1&climb=0&steps=300&jumpEvery=0&dropAtStep=0')).json;
  const seq = [];
  for (const k of (t.ticks ?? [])) {
    if (seq[seq.length - 1] !== k.foothold) seq.push(k.foothold);
  }
  const segments = new Set(seq).size;
  const span = t.ticks?.length ? Math.abs(t.ticks.at(-1).x - t.ticks[0].x) : 0;
  ok('a walk crosses more than one foothold', segments > 1 && span > 200,
     `${segments} segments, ${span}px  ${seq.slice(0, 6).join('→')}`);
}

// F2 — across the fleet, walks must cross MORE THAN ONE foothold and keep
// crossing. Measured against the handover bug on 2026-10-12: that build moved
// 748px and reported pct=8.3 (and 91.7 the other way), which looks exactly
// like a healthy walk. Distance alone cannot tell "advanced along the chain"
// from "paced back and forth on one foothold", because a character that keeps
// stepping past a foothold's end and snapping back nets out the same X.
//
// So the fleet rules count SEGMENTS, not pixels. Both numbers are reported:
// the pixels because a regression there still matters, the segments because
// they are what actually has signal.
const listAll = (await api('/api/simulate/maps?limit=2000')).json;
const allPaths = (listAll.maps ?? []).map((m) => m.path).sort();
const rnd = mulberry32(SAMPLE_SEED);
const pool = allPaths.slice();
for (let i = pool.length - 1; i > 0; i--) {          // Fisher-Yates, seeded
  const j = Math.floor(rnd() * (i + 1));
  [pool[i], pool[j]] = [pool[j], pool[i]];
}
const samplePaths = pool.slice(0, Math.min(SAMPLE_N, pool.length));

{
  const walks = [];
  let traceBudget = 120;          // /trace caps at 500 steps; sample the segment count
  for (const p of samplePaths) {
    const r = (await api('/api/simulate/character',
      { path: p, direction: 1, climb: -1 })).json;
    if (r.footholds === 0) continue;                  // link stub: nothing to walk
    const width = Math.max(1, r.bounds.right - r.bounds.left);
    const w = { path: p, ticks: r.ticks, footholds: r.footholds,
                pct: Math.abs(r.finalX - r.startX) / width * 100,
                // "Arrived" means crossed the map or entered a portal. A walk
                // that stopped because it ran out of ground said so, and that is
                // a legitimate answer when the chain really ends there.
                claimsArrival: !!(r.reachedEdge || r.reachedPortal) };

    if (traceBudget-- > 0) {
      const t = (await api('/api/simulate/trace?path=' + encodeURIComponent(p) +
        '&x=0&y=0&dir=1&climb=0&steps=300&jumpEvery=0&dropAtStep=0')).json;
      if (t.ticks?.length) {
        const seq = [];
        for (const k of t.ticks) if (seq[seq.length - 1] !== k.foothold) seq.push(k.foothold);
        w.segments = new Set(seq).size;
        w.chain = t.ticks.length;
      }
    }
    walks.push(w);
  }

  const ticked = walks.filter((w) => w.ticks !== undefined);
  const median = ticked.map((w) => w.ticks).sort((a, b) => a - b)[
    Math.floor(ticked.length / 2)] ?? 0;
  ok('walks actually travel (median ticks)', walks.length >= 40 && median >= 40,
     `${walks.length} maps, median=${median} ticks, max=${Math.max(...ticked.map((w) => w.ticks))}`);

  // THE rule, and it took three attempts to state correctly.
  //
  //   v1  "a walk that crossed one foothold did not move"    → false alarm:
  //       a map whose whole floor is one 750px segment is legitimate
  //   v2  "...and claimed arrival"                             → still false:
  //       same map, same long segment, legitimately reaches the edge
  //   v3  "...and did not take too many ticks"                 → still false:
  //       a long segment takes a long time; ticks measure the map, not the bug
  //
  // The signal is not distance, segment count or duration. It is a direct
  // geometric check: is the character actually where the foothold it says it is
  // standing on? On the broken build it reported x=749 while sitting on a
  // foothold that ends at x=51 — probe confirmed 27 underneath at x=300.
  // That is checkable, needs no threshold, and cannot misfire on a real map.
  const measured = walks.filter((w) => w.segments !== undefined);
  const checked = [];
  for (const w of measured.slice(0, 40)) {
    const t = (await api('/api/simulate/trace?path=' + encodeURIComponent(w.path) +
      '&x=0&y=0&dir=1&climb=0&steps=300&jumpEvery=0&dropAtStep=0')).json;
    const tk = t.ticks ?? [];
    if (!tk.length) continue;
    // Only a frame where the character is ON GROUND. A trace that ends mid-fall
    // (action=fall, onGround=false) has no foothold under it by definition —
    // checking those flagged three healthy maps for being airborne.
    let end = null;
    for (let i = tk.length - 1; i >= 0; i--) {
      if (tk[i].onGround && tk[i].action === 'walk' && tk[i].foothold) { end = tk[i]; break; }
    }
    if (!end) continue;
    const endX = end.x, endY = end.y, endId = end.foothold;
    const pr = (await api('/api/simulate/probe?path=' + encodeURIComponent(w.path) +
      `&x=${endX}&y=${endY}`)).json;
    const under = (pr.covering ?? []).some((c) => c.id === endId);
    checked.push({ ...w, endX, endY, endId, under });
  }
  const floating = checked.filter((w) => !w.under);
  ok('a walk ends standing on the foothold it names', floating.length === 0,
     floating.length === 0
       ? `${checked.length} walks verified against /probe`
       : `NOT UNDER ITS OWN FOOTHOLD: ${floating.slice(0, 3).map((w) => `${w.path.split('/').pop()} x=${w.endX} on ${w.endId}`).join(', ')}`);

  // Both directions, because they are separate code paths with separate edge
  // constants (MapleCharacter.cs:795 left is X1-10, :847 right is X2+5). Same
  // honesty rule: a one-foothold walk is only a defect if it claims arrival.
  let leftStuck = 0, rightStuck = 0, leftShort = 0, rightShort = 0;
  for (const p of samplePaths.slice(0, 30)) {
    for (const dir of [1, -1]) {
      const r = (await api('/api/simulate/character',
        { path: p, direction: dir, climb: -1 })).json;
      if (r.footholds <= 8) continue;
      if (r.ticks <= 10) (dir > 0 ? () => rightShort++ : () => leftShort++)();
      if (!(r.reachedEdge || r.reachedPortal)) continue;

      const t = (await api('/api/simulate/trace?path=' + encodeURIComponent(p) +
        '&x=0&y=0&dir=' + dir + '&climb=0&steps=300&jumpEvery=0&dropAtStep=0')).json;
      const tk = t.ticks ?? [];
      let end = null;
      for (let i = tk.length - 1; i >= 0; i--) {
        if (tk[i].onGround && tk[i].action === 'walk' && tk[i].foothold) { end = tk[i]; break; }
      }
      if (!end) continue;
      const pr = (await api('/api/simulate/probe?path=' + encodeURIComponent(p) +
        `&x=${end.x}&y=${end.y}`)).json;
      const under = (pr.covering ?? []).some((c) => c.id === end.foothold);
      if (!under) (dir > 0 ? () => rightStuck++ : () => leftStuck++)();
    }
  }
  ok('both directions end on real ground', rightStuck === 0 && leftStuck === 0,
     `right=${rightStuck} left=${leftStuck} ended somewhere /probe disagrees with`);
  ok('both directions behave alike', Math.abs(rightShort - leftShort) <= 6,
     `right=${rightShort} left=${leftShort} stopped within 10 ticks`);
}

writeFileSync(out, JSON.stringify({ base, failed, checks }, null, 1));
console.log(`\n${checks.length - failed}/${checks.length} passed`);
process.exit(failed === 0 ? 0 : 1);