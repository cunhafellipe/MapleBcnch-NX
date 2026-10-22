// C2/C3 -- the three shapes the sweep found and the plan does not resolve.
//
//   CharLevel/<n>/effect  and  CharLevel/<n>/hit   (5001001, 5001002)
//   level/<n>/action      (1311001, 1311002)
//   affected              (2301002)
//   prepare               (15101003, 5101004)
//   effect_ship           (5211004, 5211005)
//   lt/rb at level 1 vs level max
//   pos: which values the archive actually uses
//
// Run:  node MapleBench/tools/probe-C2-pos-and-shapes.mjs

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
// A node that is not there is a 404 here, and that is a NORMAL answer to "does
// this skill carry X" -- so a miss answers empty instead of throwing.
const kids = async (path) => {
  const r = await fetch(`${BASE}/api/children?path=${path}`);
  if (!r.ok) return [];
  const j = await r.json();
  return Array.isArray(j) ? j : (j.value ?? []);
};
const plan = async (id) => get(`/api/skill-cast/plan?skillId=${id}`);

console.log('\n=== C2: what `pos` actually means (Mob.cpp:1543-1556) ===');
console.log('  reference, transcribed:');
console.log('      switch (pos) {');
console.log('      case 0: shift = get_head_position(Point<int16_t>()); break;');
console.log('      case 1: case 2: case 3: case 4: break;      // shift stays (0,0)');
console.log('      }');
console.log('  so pos is TWO-valued: 0 = the mob\'s HEAD (flipped), anything else =');
console.log('  the mob\'s ORIGIN/feet. SkillUseEffect.cpp:211 uses pos=1 for a');
console.log('  ground decal and says so in the comment -- it is the no-shift branch.');

const ids = (await get('/api/skill-cast/castable')).skills.map((s) => s.skillId);
const posHist = new Map();
const posExamples = new Map();
for (const id of ids) {
  const p = await plan(id);
  for (const h of p.hitEffects ?? []) {
    posHist.set(h.pos, (posHist.get(h.pos) ?? 0) + 1);
    if (!posExamples.has(h.pos)) posExamples.set(h.pos, `${id} hit/${h.group}`);
  }
}
console.log('\n  pos values in the 66 plans:');
for (const [v, n] of [...posHist].sort((a, b) => a[0] - b[0])) {
  const where = v === 0 ? 'mob HEAD' : 'mob FEET (no shift) -- reference treats 1..4 identically';
  console.log(`    pos=${v}  ${String(n).padStart(4)} sprite(s)   ${where}   e.g. ${posExamples.get(v)}`);
}

console.log('\n=== C3 shape: CharLevel (5001001 / 5001002) ===');
for (const id of ['5001001', '5001002']) {
  const p = await plan(id);
  console.log(`  ${id}  plan.actions=${JSON.stringify(p.actions)}  plan.effects=${JSON.stringify(p.effects.map((e) => e.node))}`);
  const cl = await kids(`${p.entryPath}/CharLevel`);
  console.log(`    CharLevel keys: ${cl.map((c) => c.name).join(' ')}`);
  for (const c of cl) {
    const e = await kids(`${p.entryPath}/CharLevel/${c.name}/effect`);
    const h = await kids(`${p.entryPath}/CharLevel/${c.name}/hit`);
    console.log(`      /${c.name}/effect ${JSON.stringify(e.map((x) => `${x.name}(${x.type})`))}`);
    console.log(`      /${c.name}/hit    ${JSON.stringify(h.map((x) => `${x.name}(${x.type})`))}`);
    for (const hn of h.map((x) => x.name)) {
      const hh = await kids(`${p.entryPath}/CharLevel/${c.name}/hit/${hn}`);
      console.log(`        /hit/${hn}     ${JSON.stringify(hh.map((x) => `${x.name}(${x.type}${x.value != null ? '=' + x.value : ''})`))}`);
    }
  }
}

console.log('\n=== C3 shape: level/<n>/action (1311001 / 1311002) ===');
for (const id of ['1311001', '1311002']) {
  const p = await plan(id);
  const lv = await kids(`${p.entryPath}/level`);
  const withAction = [];
  for (const l of lv) {
    const a = (await kids(`${p.entryPath}/level/${l.name}`)).filter((c) => c.name === 'action');
    if (a.length) withAction.push(`${l.name}=${a[0].value}`);
  }
  console.log(`  ${id}  plan.actions=${JSON.stringify(p.actions)}  entry/action=${JSON.stringify((await kids(`${p.entryPath}/action`)).map((c) => c.name + '(' + c.type + '=' + c.value + ')'))}`);
  console.log(`      level/*/action (${withAction.length} levels): ${withAction.join(' ')}`);
}

console.log('\n=== C3 shape: affected / prepare ===');
for (const id of ['2301002']) {
  const p = await plan(id);
  console.log(`  ${id} affected ${JSON.stringify((await kids(`${p.entryPath}/affected`)).map((c) => `${c.name}(${c.type}${c.value != null ? '=' + c.value : ''})`))}`);
  for (const n of ['0', '1']) {
    console.log(`      affected/${n} ${JSON.stringify((await kids(`${p.entryPath}/affected/${n}`)).map((c) => `${c.name}(${c.type})`))}`);
  }
  console.log(`      plan.effects=${JSON.stringify(p.effects.map((e) => e.node))}`);
}
for (const id of ['15101003', '5101004']) {
  const p = await plan(id);
  const pre = await kids(`${p.entryPath}/prepare`);
  console.log(`  ${id} prepare ${JSON.stringify(pre.map((c) => `${c.name}(${c.type}${c.value != null ? '=' + c.value : ''})`))}`);
  console.log(`      entry/action ${JSON.stringify((await kids(`${p.entryPath}/action`)).map((c) => `${c.name}(${c.type}=${c.value})`))}`);
  console.log(`      plan.actions=${JSON.stringify(p.actions)}`);
}

console.log('\n=== C4 shape: effect_ship (read by NEITHER reference) ===');
for (const id of ['5211004', '5211005']) {
  const p = await plan(id);
  const ship = await kids(`${p.entryPath}/effect_ship`);
  console.log(`  ${id} effect_ship ${JSON.stringify(ship.map((c) => `${c.name}(${c.type})`))}`);
  console.log(`      effect        ${JSON.stringify((await kids(`${p.entryPath}/effect`)).map((c) => `${c.name}(${c.type})`))}`);
  console.log(`      plan.effects  ${JSON.stringify(p.effects.map((e) => e.node))}`);
}

console.log('\n=== C3: lt/rb read at the CHARACTER level vs at max level ===');
let diff = 0;
for (const id of ids) {
  const p = await plan(id);
  const one = await kids(`${p.entryPath}/level/1`);
  const lt1 = one.find((c) => c.name === 'lt');
  const rb1 = one.find((c) => c.name === 'rb');
  const box1 = lt1 && rb1 ? [lt1.extra?.x, lt1.extra?.y, rb1.extra?.x, rb1.extra?.y].join(',') : null;
  const top = p.maxLevel > 0 ? (await kids(`${p.entryPath}/level/${p.maxLevel}`)) : [];
  const ltT = top.find((c) => c.name === 'lt');
  const rbT = top.find((c) => c.name === 'rb');
  const boxT = ltT && rbT ? [ltT.extra?.x, ltT.extra?.y, rbT.extra?.x, rbT.extra?.y].join(',') : null;
  if (box1 !== boxT) {
    diff++;
    console.log(`  ${id.padEnd(11)} level/1 = ${String(box1).padEnd(26)} level/${p.maxLevel} = ${String(boxT).padEnd(26)} plan = ${p.ltx},${p.lty},${p.rbx},${p.rby}`);
  }
}
console.log(`  ${diff} of ${ids.length} skills carry a different box at level 1 than at the top level.`);

console.log('\n=== C1: is `bulletCount` ever a real number? ===');
for (const id of ids) {
  const p = await plan(id);
  const lv = await kids(`${p.entryPath}/level/${p.skillLevel}`);
  const bc = lv.find((c) => c.name === 'bulletCount');
  if (bc) console.log(`  ${id} bulletCount = ${bc.type}:${bc.value}`);
}