// A5 — collisionCount vs mobCount: two fields, are they ever different, and which
// one selects targets?
//
// Answer from the source: they are identical BY CONSTRUCTION.
// SkillCastService.cs:1077-1087
//     int mobCount       = ReadIntNode(atLevel, "mobCount", 1);
//     double hRange      = ReadRealNode(atLevel, "range", 100.0) / 100.0;
//     int wait           = DamageWaitTime(skillId);      // hardcoded MapleNecrocer table
//     int collisionCount = mobCount;                     // <-- derived, not read
//     int totalTime      = wait + 6 * 7;                 // <-- derived, not read
// Neither `collisionCount` nor `totalTime` is read from the archive, so neither can
// disagree with `mobCount`. ApplySkillHits (MapSimSessionStore.cs:2207-2281) reads
// only `plan.MobCount`.
//
// Nor does either name exist in the authoritative reference: grep over
// clean-client/src finds 0 hits for `collisionCount` and 0 for `damageWaitTime`.
// The reference's own count is `SkillData.cpp:69 sub["mobCount"].get_integer(1)`
// flowing to `Skill.cpp:394 attack.mobcount = stats.mobcount` and then to
// `Combat.cpp:1265 if (targets.size() >= objcount) break`.
//
// This probe measures it anyway, over every castable skill, so the claim rests on
// numbers and not on reading one assignment.
//
// Usage: node probe-A5-collision-vs-mobcount.mjs
import { get, plan } from './probe-A-lib.mjs';

const castable = await get('/api/skill-cast/castable');
const ids = (castable.skillIds ?? castable.skills ?? castable).map?.((x) => (typeof x === 'string' ? x : x.skillId)) ?? [];
console.log(`castable skills reported: ${ids.length}`);

const rows = [];
let errors = 0;
for (const id of ids) {
  let p;
  try { p = await plan(id); } catch { errors++; continue; }
  rows.push({
    id,
    mobCount: p.mobCount,
    collisionCount: p.collisionCount,
    totalTime: p.totalTime,
    damageWaitTime: p.damageWaitTime,
    // totalTime is claimed to be `wait + 42`; check it rather than trust it
    derivedTotal: p.damageWaitTime + 42,
  });
}

const differ = rows.filter((r) => r.collisionCount !== r.mobCount);
const totalOff = rows.filter((r) => r.totalTime !== r.derivedTotal);
const dist = new Map();
for (const r of rows) dist.set(r.mobCount, (dist.get(r.mobCount) ?? 0) + 1);

console.log(`plans read: ${rows.length} (errors: ${errors})`);
console.log(`skills where collisionCount !== mobCount : ${differ.length}`);
if (differ.length) {
  for (const r of differ) console.log(`  ${r.id}: mobCount=${r.mobCount} collisionCount=${r.collisionCount}`);
}
console.log(`skills where totalTime !== damageWaitTime + 42 : ${totalOff.length}`);
if (totalOff.length) {
  for (const r of totalOff.slice(0, 10)) console.log(`  ${r.id}: totalTime=${r.totalTime} wait+42=${r.derivedTotal}`);
}
console.log('\nmobCount distribution:');
for (const k of [...dist.keys()].sort((a, b) => a - b)) {
  console.log(`  mobCount=${String(k).padStart(3)} : ${dist.get(k)} skills`);
}
console.log('\nsample rows:');
console.log('  skill     mobCount  collisionCount  damageWaitTime  totalTime');
for (const r of rows.slice(0, 12)) {
  console.log(`  ${r.id.padEnd(9)} ${String(r.mobCount).padStart(8)} ${String(r.collisionCount).padStart(15)} ` +
    `${String(r.damageWaitTime).padStart(15)} ${String(r.totalTime).padStart(10)}`);
}

console.log(`\nVERDICT: collisionCount differs from mobCount in ${differ.length} of ${rows.length} skills.`);
if (differ.length === 0) {
  console.log('  They are the same number in every case, because collisionCount is ASSIGNED mobCount');
  console.log('  (SkillCastService.cs:1086) rather than read. The field cannot carry information.');
}