// C3 -- is there a SECOND "only one node name" reader left in the cast plan?
//
// WHAT THIS IS FOR
// W-087 exists because the damage reader accepted exactly one node name and 22 of
// 66 castable skills therefore read as "no damage". That was one switch of three.
// This walks EVERY node name the reference reads out of a skill and asks, for
// this archive's 66 castable skills: does the node exist, and does the current
// plan surface it?
//
// The reference (`C:/MUWORK/GAME/MPSH/clean-client/src`) node names, by call site:
//
//   SkillData.cpp:45-47   src["action"][0]
//   SkillData.cpp:49-50   strsrc["h"+level]
//   SkillData.cpp:55-107  src["level"][n] -> damage, mad, fixdamage, mastery,
//                                   attackCount, mobCount, bulletCount,
//                                   bulletConsume, hpCon, mpCon, prop, range,
//                                   moneyCon, itemCon, itemConNo, cooltime,
//                                   hp, mp, time, x, y, z, lt, rb
//   SkillData.cpp:110     src["elemAttr"]
//   SkillData.cpp:120     src["invisible"]
//   Skill.cpp:46          src["CharLevel"]["10"]["effect"].size()
//   Skill.cpp:47          src["effect0"].size()
//   Skill.cpp:59-60       src["effect"]["0"].data_type(), src["effect"]["1"].size()
//   Skill.cpp:86-87       src["prepare"]["0"].data_type()
//   Skill.cpp:96          src["tile"]
//   Skill.cpp:101-103     src["special"]["0"|"1"|"2"]
//   Skill.cpp:107         src["affected"]["0"].data_type()
//   Skill.cpp:113-115     src["CharLevel"]["10"]["hit"], src["level"]["1"]["hit"],
//                         src["hit"]["0"], src["hit"]["1"]
//   Skill.cpp:158-175     src["action"]["0"|"1"], src["prepare"]["action"],
//                         src["level"]["1"]["action"]
//   Skill.cpp:201-202     src["ball"], src["level"]["1"]["ball"]
//   SkillUseEffect.cpp:165  src["tile"]
//   SkillUseEffect.cpp:176  tilesrc["effectDistance"]
//   SkillUseEffect.cpp:189  src["level"]["30"], then src["level"]["1"], lt+rb
//
// Run:  node MapleBench/tools/probe-C3-node-shapes.mjs

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
  const j = await get(`/api/children?path=${path}`);
  return Array.isArray(j) ? j : (j.value ?? []);
};

const ids = (await get('/api/skill-cast/castable')).skills.map((s) => s.skillId);

const rows = [];
for (const id of ids) {
  const plan = await get(`/api/skill-cast/plan?skillId=${id}`);
  const ep = plan.entryPath;
  const entry = await kids(ep);
  const names = new Set(entry.map((c) => String(c.name).toLowerCase()));

  // level/* : the reference iterates these, it does not index a fixed name.
  const levels = (await kids(`${ep}/level`)).map((c) => c.name);
  const lvKeys = new Set();
  const lvTypes = new Map();
  for (const l of levels) {
    for (const c of await kids(`${ep}/level/${l}`)) {
      const n = String(c.name).toLowerCase();
      lvKeys.add(n);
      if (!lvTypes.has(n)) lvTypes.set(n, new Set());
      lvTypes.get(n).add(c.type);
    }
  }

  const ltEntry = names.has('lt') && names.has('rb');

  rows.push({
    id,
    ep,
    entryNames: [...names].sort(),
    levelNames: levels,
    levelKeys: [...lvKeys].sort(),
    // masterLevel the REFERENCE computes: stats.size() -- the COUNT of level
    // nodes (SkillData.cpp:117), not the largest numeric name.
    levelCount: levels.length,
    levelMaxName: levels.length ? Math.max(...levels.map(Number).filter(Number.isFinite)) : 0,
    // Compared case-insensitively against the reference's own spelling rather than
    // against a hand-typed lowercase literal: the first version of this probe typed
    // 'charelevel' (an extra e), missed both skills, and reported "0 of 66" with
    // total confidence.
    hasCharLevel: entry.some((c) => String(c.name).toLowerCase() === 'charlevel'),
    hasPrepare: names.has('prepare'),
    hasAffected: names.has('affected'),
    hasSpecial: names.has('special'),
    hasBall: names.has('ball'),
    hasEffect0: names.has('effect0'),
    hasLevel1Action: lvKeys.has('action'),
    hasLevel1Hit: lvKeys.has('hit'),
    hasLevel1Ball: lvKeys.has('ball'),
    ltOnEntryRoot: ltEntry,
    types: {
      attackCount: [...(lvTypes.get('attackcount') ?? [])],
      mobCount: [...(lvTypes.get('mobcount') ?? [])],
      range: [...(lvTypes.get('range') ?? [])],
      damage: [...(lvTypes.get('damage') ?? [])],
      lt: [...(lvTypes.get('lt') ?? [])],
      effectDistance: [...(lvTypes.get('effectdistance') ?? [])],
    },
    // tile/effectDistance
    tileEffectDistance: null,
    plan: {
      maxLevel: plan.maxLevel,
      skillLevel: plan.skillLevel,
      damageNode: plan.damageNode,
      damage: plan.damage,
      hRange: plan.hRange,
      mobCount: plan.mobCount,
      tileStep: plan.tileStep,
      ltx: plan.ltx, lty: plan.lty, rbx: plan.rbx, rby: plan.rby,
      actions: plan.actions,
      effectNodes: plan.effects.map((e) => e.node),
      hitGroups: [...new Set(plan.hitEffects.map((h) => h.group))].length,
    },
  });

  if (names.has('tile')) {
    const t = await kids(`${ep}/tile`);
    const ed = t.find((c) => String(c.name).toLowerCase() === 'effectdistance');
    rows[rows.length - 1].tileEffectDistance = ed ? `${ed.type}:${ed.value}` : null;
  }
}

const tally = (fn) => rows.filter(fn).map((r) => r.id);
const report = (label, list) =>
  console.log(`  ${label.padEnd(46)} ${String(list.length).padStart(3)} / ${rows.length}  ${list.slice(0, 8).join(' ')}${list.length > 8 ? ' ...' : ''}`);

console.log('\n=== node shapes the reference reads that exist on some skill ===');
report('CharLevel (ByLevel use-effect + hit)', tally((r) => r.hasCharLevel));
report('prepare (cast sprite / action / hold)', tally((r) => r.hasPrepare));
report('affected (post-cast animation)', tally((r) => r.hasAffected));
report('special (EXPLOSION / Genesis x3)', tally((r) => r.hasSpecial));
report('ball (projectile)', tally((r) => r.hasBall));
report('effect0 (MultiUseEffect)', tally((r) => r.hasEffect0));
report('level/*/action (ByLevelAction)', tally((r) => r.hasLevel1Action));
report('level/*/hit (BySkillLevelHitEffect)', tally((r) => r.hasLevel1Hit));
report('level/*/ball (BySkillLevelBullet)', tally((r) => r.hasLevel1Ball));
report('lt+rb on the entry ROOT', tally((r) => r.ltOnEntryRoot));
report('tile/effectDistance', tally((r) => r.tileEffectDistance != null));

console.log('\n=== maxLevel: reference stats.size() vs largest level NAME ===');
const maxMismatch = rows.filter((r) => r.plan.maxLevel !== r.levelCount);
report('plan.maxLevel != level-node COUNT', maxMismatch.map((r) => r.id));
for (const r of maxMismatch.slice(0, 12)) {
  console.log(`      ${r.id}  plan.maxLevel=${r.plan.maxLevel}  levelCount=${r.levelCount}  levelMaxName=${r.levelMaxName}`);
}
const nameMismatch = rows.filter((r) => r.levelMaxName !== r.levelCount);
report('levelMaxName != levelCount (name shape)', nameMismatch.map((r) => r.id));

console.log('\n=== effect nodes the plan surfaces ===');
const effNodes = new Map();
for (const r of rows) for (const n of r.plan.effectNodes) effNodes.set(n, (effNodes.get(n) ?? 0) + 1);
console.log('  ' + [...effNodes].map(([k, v]) => `${k}x${v}`).join('  '));
report('has effect0 but plan shows no effect0', tally((r) => r.hasEffect0 && !r.plan.effectNodes.includes('effect0')));
report('has ball but plan shows no ball', tally((r) => r.hasBall && !r.plan.effectNodes.includes('ball')));

console.log('\n=== WZ types actually used for the numeric nodes ===');
const types = {};
for (const r of rows) for (const [k, v] of Object.entries(r.types)) if (v.length) (types[k] ??= new Set()).add(...v);
for (const [k, v] of Object.entries(types)) console.log(`  ${k.padEnd(16)} ${[...v].sort().join(', ')}`);

// ---- W-089: the reader followed each shape, not just the node name ---------
//
// Everything above is a SURVEY of the archive and holds on any build. The
// assertions below ask whether the PLAN now resolves each shape the way the
// reference does, and they need a server built from the W-089 source.
let pass = 0, fail = 0, skipped = 0;
const check = (name, ok, detail) => {
  if (ok) { pass++; console.log(`  PASS  ${name}${detail ? `  ${detail}` : ''}`); }
  else { fail++; console.log(`  FAIL  ${name}${detail ? `  ${detail}` : ''}`); }
};
const skip = (name, why) => { skipped++; console.log(`  SKIP  ${name}\n          ${why}`); };

console.log('\n=== W-089: does the plan resolve each shape? ===');
const byId = new Map(rows.map((r) => [r.id, r]));
const charLevelSkills = tally((r) => r.hasCharLevel);
const levelActionSkills = tally((r) => r.hasLevel1Action);
const affectedSkills = tally((r) => r.hasAffected);

const w089 = rows.some((r) => r.plan.hitSelector != null);
if (!w089) {
  skip('CharLevel skills read CharLevel/<n>/effect and /hit', 'server not rebuilt');
  skip('empty entry action falls back to level/<n>/action', 'server not rebuilt');
  skip('affected is surfaced on its own', 'server not rebuilt');
  skip('lt/rb read at the CHARACTER level', 'server not rebuilt');
} else {
  for (const id of charLevelSkills) {
    const p = byId.get(id).plan;
    check(`${id} cast sprite comes from CharLevel`,
      (p.effectNodes ?? []).some((n) => String(n).startsWith('CharLevel/')),
      JSON.stringify(p.effectNodes));
    check(`${id} hit sprite comes from CharLevel`,
      (p.hitEffects ?? []).length > 0 && String(p.hitEffects[0].imagePath).includes('/CharLevel/'),
      p.hitEffects?.[0]?.imagePath);
    check(`${id} hitSelector is ByLevelHitEffect`, p.hitSelector === 'ByLevelHitEffect', p.hitSelector);
  }
  for (const id of levelActionSkills) {
    const p = byId.get(id).plan;
    // SkillAction.cpp:47-59 ByLevelAction: level/<skilllevel>/action.
    const lv = (await kids(`${byId.get(id).ep}/level/1`)).find((c) => c.name === 'action');
    check(`${id} action comes from level/<n>/action`,
      p.actions.length === 1 && p.actions[0] === lv?.value,
      `plan ${JSON.stringify(p.actions)}  level/1 ${JSON.stringify(lv?.value)}`);
  }
  for (const id of affectedSkills) {
    const p = byId.get(id).plan;
    check(`${id} carries an affected sprite`, (p.affected ?? []).length === 1,
      JSON.stringify((p.affected ?? []).map((a) => a.imagePath)));
    check(`${id} affected is NOT in the cast sprite list`,
      !(p.effectNodes ?? []).includes('affected'), JSON.stringify(p.effectNodes));
  }
  // The box is read at the character's level (Skill.cpp:317-319, :459-461).
  const boxDiff = [];
  for (const r of rows) {
    const lv = await kids(`${r.ep}/level/${r.plan.skillLevel}`);
    const lt = lv.find((c) => c.name === 'lt');
    const rb = lv.find((c) => c.name === 'rb');
    if (!lt || !rb) continue;
    if (r.plan.ltx !== lt.extra?.x || r.plan.lty !== lt.extra?.y
      || r.plan.rbx !== rb.extra?.x || r.plan.rby !== rb.extra?.y) {
      boxDiff.push(`${r.id} plan=${r.plan.ltx},${r.plan.lty},${r.plan.rbx},${r.plan.rby}` +
        ` level/${r.plan.skillLevel}=${lt.extra?.x},${lt.extra?.y},${rb.extra?.x},${rb.extra?.y}`);
    }
  }
  check('the collision box is the CHARACTER level lt/rb, not the top level', boxDiff.length === 0,
    `${boxDiff.length} wrong: ${boxDiff.slice(0, 4).join(' | ')}`);
}

console.log(`\n  ${pass} passed, ${fail} failed, ${skipped} skipped`);
console.log(`\n  shapes found in the archive: CharLevel=${charLevelSkills.length} ` +
  `level/*/action=${levelActionSkills.length} affected=${affectedSkills.length}`);

console.log('\n=== per-skill dump ===');
console.log('  id           maxLvl cnt entryKeys | levelKeys');
for (const r of rows) {
  console.log(`  ${r.id.padEnd(12)} ${String(r.plan.maxLevel).padStart(3)} ${String(r.levelCount).padStart(4)} ` +
    `${r.entryNames.join(' ')} | ${r.levelKeys.join(' ')}`);
}

console.log('\n' + JSON.stringify(rows.map((r) => ({
  id: r.id, ep: r.ep, entry: r.entryNames, levelCount: r.levelCount, levelMaxName: r.levelMaxName,
  planMaxLevel: r.plan.maxLevel, planSkillLevel: r.plan.skillLevel, planDamageNode: r.plan.damageNode,
  planEffectNodes: r.plan.effectNodes, planHitGroups: r.plan.hitGroups, planActions: r.plan.actions,
  planHitSelector: r.plan.hitSelector ?? null,
  planHitCount: r.plan.hitCount ?? null,
  tileEffectDistance: r.tileEffectDistance, planTileStep: r.plan.tileStep,
  box: [r.plan.ltx, r.plan.lty, r.plan.rbx, r.plan.rby],
  types: r.types,
})), null, 1));

process.exit(fail === 0 ? 0 : 1);