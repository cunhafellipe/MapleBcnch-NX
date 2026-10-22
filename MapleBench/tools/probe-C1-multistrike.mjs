// C1 -- what does MultiStrike mean, and is the three-id guard earned?
//
// WHAT THIS IS FOR
// `SkillCastService.MultiStrike(string)` was transcribed from the SECONDARY
// reference (MapleNecrocer Skill.cs:168-178):
//
//     skillId is not ("1121008" or "1001005" or "1311012")
//
// W-083 removed it as a damage guard because it blocked 63 of 66 castable skills.
// W-084 kept the bool but drove it from `mobCount > 1`. What was never checked is
// whether the reference has this concept at all.
//
// It does not. The authoritative reference
// (`C:/MUWORK/GAME/MPSH/clean-client/src`) has no MultiStrike. It has:
//
//     SkillData.cpp:68  uint8_t attackcount = (uint8_t)sub["attackCount"].get_integer(1);
//     SkillData.cpp:69  uint8_t mobcount    = (uint8_t)sub["mobCount"].get_integer(1);
//     SkillData.cpp:70  uint8_t bulletcount = (uint8_t)sub["bulletCount"].get_integer(1);
//     Skill.cpp:394      attack.mobcount  = stats.mobcount;
//     Skill.cpp:397-405  switch (attack.type) {
//                           case Attack::RANGED: attack.hitcount = stats.bulletcount; break;
//                           default:            attack.hitcount = stats.attackcount; break; }
//
// "How many mobs" and "how many times each mob" are different questions. W-089
// replaced the bool with `hitCount` (= attackCount/bulletCount) and kept
// `mobCount` separate.
//
// WHAT IS CHECKED HERE
//   1. the archive's own numbers, read through /api/children -- valid on any build
//   2. the plan's `hitCount` / `hitCountNode` / `projectile` -- needs a server
//      built from the W-089 source; the probe says which state it is in rather
//      than reporting a red FAIL for "the server was not rebuilt"
//
// Run:  node MapleBench/tools/probe-C1-multistrike.mjs
//       (BASE=http://127.0.0.1:<port> -- REQUIRED, there is no default)

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
// A node that is not there answers with an empty list, never with an error.
const kids = async (path) => {
  const r = await fetch(`${BASE}/api/children?path=${path}`);
  if (!r.ok) return [];
  const j = await r.json();
  return Array.isArray(j) ? j : (j.value ?? []);
};

let pass = 0, fail = 0, skipped = 0;
const check = (name, ok, detail) => {
  if (ok) { pass++; console.log(`  PASS  ${name}${detail ? `  ${detail}` : ''}`); }
  else { fail++; console.log(`  FAIL  ${name}${detail ? `  ${detail}` : ''}`); }
};
const skip = (name, why) => { skipped++; console.log(`  SKIP  ${name}\n          ${why}`); };

const ids = (await get('/api/skill-cast/castable')).skills.map((s) => s.skillId);

const rows = [];
for (const id of ids) {
  const plan = await get(`/api/skill-cast/plan?skillId=${id}`);
  const list = await kids(`${plan.entryPath}/level/1`);
  const by = new Map(list.map((c) => [String(c.name).toLowerCase(), c]));
  rows.push({
    id,
    plan,
    attackCount: by.has('attackcount') ? Number(by.get('attackcount').value) : null,
    attackCountType: by.has('attackcount') ? by.get('attackcount').type : null,
    bulletCount: by.has('bulletcount') ? Number(by.get('bulletcount').value) : null,
    hasBall: (await kids(plan.entryPath)).some((c) => String(c.name).toLowerCase() === 'ball'),
  });
}

console.log('\n=== what the archive says, straight off level/1 ===');
console.log('  id          attackCount (type)  bulletCount  ball   plan.mobCount  plan.hitCount');
for (const r of rows) {
  console.log(
    `  ${r.id.padEnd(11)} ${String(r.attackCount).padEnd(9)} ${String(r.attackCountType).padEnd(9)}` +
    ` ${String(r.bulletCount).padEnd(12)} ${String(r.hasBall).padEnd(6)} ` +
    `${String(r.plan.mobCount).padEnd(13)} ${String(r.plan.hitCount)}`);
}

console.log('\n=== C1 claims ===');

// 1. `attackCount` really is on the archive, and the guarded id carries it.
const withAc = rows.filter((r) => r.attackCount != null);
check('attackCount present on 10 of 66', withAc.length === 10, `got ${withAc.length}`);
check('every attackCount is a Long', withAc.every((r) => r.attackCountType === 'Long'),
  withAc.map((r) => `${r.id}:${r.attackCountType}`).join(' '));
const brandish = rows.find((r) => r.id === '1121008');
check('1121008 carries attackCount = 2 (the guard called it SINGLE-strike)',
  brandish?.attackCount === 2, `got ${brandish?.attackCount}`);

// 2. `bulletCount` is not a real number anywhere on this archive.
const withBc = rows.filter((r) => r.bulletCount != null);
check('bulletCount exists on exactly 1 skill and reads 0',
  withBc.length === 1 && withBc[0].bulletCount === 0,
  withBc.map((r) => `${r.id}=${r.bulletCount}`).join(' '));

// 3. The reference's OTHER branch input, `level/1/ball` (Skill.cpp:202). Nothing
//    carries it on this archive, so `projectile` is decided by the entry `ball`
//    alone and the ranged branch never has a second source.
const leveledBall = [];
for (const r of rows) {
  if ((await kids(`${r.plan.entryPath}/level/1`)).some((c) => String(c.name).toLowerCase() === 'ball')) {
    leveledBall.push(r.id);
  }
}
check('no skill carries level/1/ball', leveledBall.length === 0, leveledBall.join(' '));

// 4. The plan fields. These need a server built from the W-089 source.
const hasHitCount = rows.some((r) => r.plan.hitCount != null);
if (!hasHitCount) {
  console.log('\n  NOTE  the running server predates W-089: no plan carries `hitCount`,');
  console.log('        so the three plan-level assertions below were NOT RUN.');
  skip('plan.hitCount matches the archive', 'server not rebuilt');
  skip('plan.hitCountNode names the node', 'server not rebuilt');
  skip('plan.multiStrike is gone', 'server not rebuilt');
} else {
  let bad = 0;
  for (const r of rows) {
    const want = r.hasBall ? (r.bulletCount ?? r.attackCount ?? 1) : (r.attackCount ?? r.bulletCount ?? 1);
    if (r.plan.hitCount !== want) { bad++; console.log(`          ${r.id} want ${want} got ${r.plan.hitCount}`); }
  }
  check('plan.hitCount == attackCount (bulletCount when the skill has a ball)',
    bad === 0, `${bad} mismatches`);

  const wantNode = (r) => {
    for (const n of r.hasBall ? ['bulletCount', 'attackCount'] : ['attackCount', 'bulletCount']) {
      const raw = n === 'attackCount' ? r.attackCount : r.bulletCount;
      if (raw != null) return n;
    }
    return '';
  };
  const nodeBad = rows.filter((r) => r.plan.hitCountNode !== wantNode(r));
  check('plan.hitCountNode names the node it read', nodeBad.length === 0,
    nodeBad.map((r) => `${r.id} want "${wantNode(r)}" got "${r.plan.hitCountNode}"`).join(' '));

  check('plan.multiStrike is GONE from the record',
    rows.every((r) => !('multiStrike' in r.plan)),
    'the three-magic-number bool was deleted, not renamed');
}

console.log('\n=== the number W-089 changes ===');
const derived = (r) => r.hasBall ? (r.bulletCount ?? r.attackCount ?? 1) : (r.attackCount ?? r.bulletCount ?? 1);
const summary = {
  skills: rows.length,
  attackCountPresent: withAc.length,
  attackCountValues: [...new Set(rows.map((r) => r.attackCount).filter((v) => v != null))].sort((a, b) => a - b),
  hitCountGt1: rows.filter((r) => derived(r) > 1).map((r) => `${r.id}=${derived(r)}`),
  mobCountGt1: rows.filter((r) => r.plan.mobCount > 1).length,
  // The old bool was driven from mobCount, which is why it claimed 56 of 66.
  oldBoolTrue: rows.filter((r) => r.plan.multiStrike === true).length,
  distinctAxes: rows.filter((r) => (derived(r) > 1) !== (r.plan.mobCount > 1)).length,
};
console.log(JSON.stringify(summary, null, 2));
console.log(`\n  ${pass} passed, ${fail} failed, ${skipped} skipped`);
process.exit(fail === 0 ? 0 : 1);