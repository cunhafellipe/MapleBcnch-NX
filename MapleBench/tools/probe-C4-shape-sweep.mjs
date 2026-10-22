// C4 -- sweep every plan field across all 66 castable skills and sort them into
// "identical everywhere" and "differs on one or two skills".
//
// WHAT THIS IS FOR
// W-087 found a reader that accepted exactly one node name and lost 22 skills.
// That was found by looking at ONE field. This is the same sweep over EVERY field
// the plan carries, plus the node names behind each, so a shape that only a
// couple of skills use cannot hide.
//
// It also probes the node shapes the authoritative reference reads that this
// archive still carries -- CharLevel, prepare, affected, level/*/action --
// because "no skill in the 66 uses it" and "the reader never looks" are
// different findings and only one of them is a defect.
//
// Run:  node MapleBench/tools/probe-C4-shape-sweep.mjs

// The backend comes from BASE and from nowhere else (task-134).  The variable
// is NOT renamed: pm/loop/go-01/E-harness.md and this file's own header both
// document BASE as this family's override, so changing it would change a live
// calling convention.  What goes away is the DEFAULT PORT: an instance older
// than this tree answers every one of these requests happily, and a probe that
// silently measures it reports the stale numbers as findings.
// The shape is the one tools/verify/suites/skillshape-verify.mjs uses; the
// trailing-slash strip is load-bearing (a doubled slash 404s every request).
const BASE_ENV = (process.env.BASE || '').trim();
const OTHER_BASE = (process.env.GATE_BASE || '').trim();
if (BASE_ENV && OTHER_BASE && BASE_ENV !== OTHER_BASE) {
  console.log(`>>> RED: BASE and GATE_BASE name different instances: `
    + `BASE=${JSON.stringify(BASE_ENV)} GATE_BASE=${JSON.stringify(OTHER_BASE)}. `
    + `Set BASE only.`);
  process.exit(1);
}
if (OTHER_BASE && !BASE_ENV) {
  console.log(`>>> RED: GATE_BASE is set (${JSON.stringify(OTHER_BASE)}) but BASE is not. `
    + `This probe family reads BASE; set BASE and unset GATE_BASE.`);
  process.exit(1);
}
if (!BASE_ENV) {
  console.log('>>> RED: BASE is not set, so there is no backend to measure.');
  console.log('       Set it to the instance under test, for example:');
  console.log("         BASE=http://127.0.0.1:<port> node " + (process.argv[1] || 'this probe'));
  console.log('       There is no default port on purpose: an older instance on the');
  console.log('       usual port answers every one of these requests.');
  process.exit(1);
}
const BASE = BASE_ENV.replace(/\/+$/, '');

const get = async (p) => {
  const r = await fetch(`${BASE}${p}`);
  if (!r.ok) throw new Error(`GET ${p} -> ${r.status}`);
  return r.json();
};
// A node that is not there is a 404 here, and "does this skill carry X" is a
// normal question, so a miss answers empty instead of throwing.
const kids = async (path) => {
  const r = await fetch(`${BASE}/api/children?path=${path}`);
  if (!r.ok) return [];
  const j = await r.json();
  return Array.isArray(j) ? j : (j.value ?? []);
};

const ids = (await get('/api/skill-cast/castable')).skills.map((s) => s.skillId);

// Fields to profile. Each is reduced to a comparable scalar/short-string.
const fields = {
  bookId: (p) => p.bookId,
  actions: (p) => (p.actions ?? []).join('+'),
  actionCount: (p) => (p.actions ?? []).length,
  planEffectNodes: (p) => (p.effects ?? []).map((e) => e.node).sort().join('+'),
  planAffected: (p) => (p.affected ?? []).map((e) => e.imagePath).join('+') || '(none)',
  planActionsResolved: (p) => (p.actions ?? []).join('+'),
  effectZ: (p) => (p.effects ?? []).map((e) => `${e.node}:${e.z}`).sort().join('+'),
  effectBullet: (p) => (p.effects ?? []).filter((e) => e.bullet).map((e) => e.node).sort().join('+'),
  hitGroups: (p) => [...new Set((p.hitEffects ?? []).map((h) => h.group))].length,
  hitPos: (p) => [...new Set((p.hitEffects ?? []).map((h) => h.pos))].sort().join('+'),
  hitZ: (p) => [...new Set((p.hitEffects ?? []).map((h) => h.z))].sort().join('+'),
  tileCount: (p) => p.tileCount,
  tileStep: (p) => p.tileStep,
  tileArea: (p) => `${p.tileAreaLeft},${p.tileAreaRight},${p.tileAreaTop},${p.tileAreaBottom}`,
  tileAtTarget: (p) => p.tileAtTarget,
  tileAreaFollowsFacing: (p) => p.tileAreaFollowsFacing,
  hasCollisionBox: (p) => p.hasCollisionBox,
  // W-089: `multiStrike` is gone. `hitCount` is the reference's `attack.hitcount`
  // (attackCount / bulletCount) and `mobCount` is `attack.mobcount` -- two
  // different axes that the old bool conflated. `projectile` is the reference's
  // own `ball`-node flag and is what picks which of the two nodes `hitCount`
  // reads.
  hitCount: (p) => p.hitCount ?? 'ABSENT (pre-W-089 build)',
  hitCountNode: (p) => p.hitCountNode ?? 'ABSENT (pre-W-089 build)',
  projectile: (p) => p.projectile ?? 'ABSENT (pre-W-089 build)',
  hitSelector: (p) => p.hitSelector ?? 'ABSENT (pre-W-089 build)',
  collisionCount: (p) => p.collisionCount,
  totalTime: (p) => p.totalTime,
  mobCount: (p) => p.mobCount,
  hRange: (p) => p.hRange,
  maxLevel: (p) => p.maxLevel,
  skillLevel: (p) => p.skillLevel,
  damagePresent: (p) => p.damage == null ? 'absent' : 'present',
  damageNode: (p) => p.damageNode || '(none)',
  effectMs: (p) => p.effectMs === 0 ? 0 : 'nonzero',
  boxWidth: (p) => p.boxWidth,
  boxHeight: (p) => p.boxHeight,
};

const plans = [];
for (const id of ids) plans.push(await get(`/api/skill-cast/plan?skillId=${id}`));

console.log(`\n=== field profile over ${plans.length} castable plans ===`);
console.log('  field                     distinct   rarest value(s)');
const rarity = [];
for (const [name, pick] of Object.entries(fields)) {
  const vals = plans.map(pick);
  const counts = new Map();
  for (const v of vals) counts.set(String(v), (counts.get(String(v)) ?? 0) + 1);
  const sorted = [...counts].sort((a, b) => a[1] - b[1]);
  const same = counts.size === 1;
  rarity.push({ name, distinct: counts.size, counts, same });
  const rare = same ? `${sorted[0][1]}x "${sorted[0][0]}"`
    : sorted.slice(0, 3).map(([v, n]) => `${n}x "${v}"`).join('  ');
  console.log(`  ${name.padEnd(24)} ${String(counts.size).padStart(6)}    ${rare}`);
}

console.log('\n=== the interesting half: fields that differ on only 1-3 skills ===');
const odd = rarity.filter((r) => !r.same && r.distinct > 1)
  .map((r) => ({ name: r.name, rare: [...r.counts].filter(([, n]) => n <= 3) }))
  .filter((r) => r.rare.length);
for (const r of odd) {
  console.log(`  ${r.name}`);
  for (const [v, n] of r.rare) {
    const who = plans.filter((p) => String(fields[r.name](p)) === v).map((p) => p.skillId);
    console.log(`      ${String(n).padStart(2)}x ${JSON.stringify(v)}  -> ${who.join(' ')}`);
  }
}

// ---- the shapes behind the C3 findings ------------------------------------
console.log('\n=== reference node shapes, probed directly ===');
const shape = async (id, path) => {
  const plan = plans.find((p) => p.skillId === id);
  const list = await kids(`${plan.entryPath}${path}`);
  return list.map((c) => `${c.name}(${c.type}${c.value != null ? '=' + c.value : ''})`);
};

for (const id of ['5001001', '5001002']) {
  console.log(`  ${id} CharLevel          ${JSON.stringify(await shape(id, '/CharLevel'))}`);
  for (const lv of await shape(id, '/CharLevel')) {
    const n = lv.slice(0, lv.indexOf('('));
    console.log(`      CharLevel/${n}      ${JSON.stringify(await shape(id, `/CharLevel/${n}`))}`);
  }
}
for (const id of ['15101003', '5101004']) {
  console.log(`  ${id} prepare            ${JSON.stringify(await shape(id, '/prepare'))}`);
  for (const n of ['0', 'action']) {
    console.log(`      prepare/${n}          ${JSON.stringify(await shape(id, `/prepare/${n}`))}`);
  }
}
console.log(`  2301002 affected         ${JSON.stringify(await shape('2301002', '/affected'))}`);
for (const id of ['1311001', '1311002']) {
  const lv = (await kids(`${plans.find((p) => p.skillId === id).entryPath}/level`)).map((c) => c.name);
  const acts = [];
  for (const l of lv) {
    const a = (await kids(`${plans.find((p) => p.skillId === id).entryPath}/level/${l}`))
      .filter((c) => c.name.toLowerCase() === 'action');
    if (a.length) acts.push(`${l}:${a[0].value}`);
  }
  console.log(`  ${id} level/*/action    ${acts.length ? acts.join(' ') : '(none)'}`);
  console.log(`      entry/action        ${JSON.stringify(await shape(id, '/action'))}`);
}
for (const id of ['5211004', '5211005']) {
  console.log(`  ${id} effect_ship        ${JSON.stringify(await shape(id, '/effect_ship'))}`);
  console.log(`      effect              ${JSON.stringify(await shape(id, '/effect'))}`);
}
console.log(`  4211004 hit              ${JSON.stringify(await shape('4211004', '/hit'))}`);

// Which skills carry a String-typed `damage`, i.e. the W-087 width trap.
console.log('\n=== WZ type of the damage-family nodes (the W-087 trap) ===');
const dmgTypes = new Map();
for (const p of plans) {
  const lv = await kids(`${p.entryPath}/level/${p.skillLevel}`);
  for (const c of lv) {
    const n = String(c.name).toLowerCase();
    if (!['damage', 'damagepc', 'mad', 'maddpc', 'fixdamage', 'x', 'attackcount',
      'mobcount', 'bulletcount', 'range', 'effectdistance'].includes(n)) continue;
    const k = `${n}:${c.type}`;
    if (!dmgTypes.has(k)) dmgTypes.set(k, []);
    dmgTypes.get(k).push(p.skillId);
  }
}
for (const [k, who] of [...dmgTypes].sort()) console.log(`  ${k.padEnd(28)} ${who.length}  ${who.slice(0, 6).join(' ')}${who.length > 6 ? ' ...' : ''}`);

console.log('\n=== entry-root node names across the 66 ===');
const entryNames = new Map();
for (const p of plans) {
  for (const c of await kids(p.entryPath)) {
    const k = c.name;
    if (!entryNames.has(k)) entryNames.set(k, []);
    entryNames.get(k).push(p.skillId);
  }
}
for (const [k, who] of [...entryNames].sort((a, b) => a[1].length - b[1].length)) {
  console.log(`  ${k.padEnd(16)} ${String(who.length).padStart(3)}  ${who.slice(0, 5).join(' ')}${who.length > 5 ? ' ...' : ''}`);
}