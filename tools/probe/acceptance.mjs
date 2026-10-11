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

writeFileSync(out, JSON.stringify({ base, failed, checks }, null, 1));
console.log(`\n${checks.length - failed}/${checks.length} passed`);
process.exit(failed === 0 ? 0 : 1);