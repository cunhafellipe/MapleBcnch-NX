// A4 — is the hit settled on the cast frame, or `damageWaitTime` frames later?
//
// Reference: NO delay. Combat::apply_move (Combat.cpp:605) builds the Attack,
// calls find_closest (:724) and sends the attack (:826) inside one call — no
// queue, no timer, no deferred pass. `grep -rn damageWaitTime clean-client/src`
// returns ZERO hits: the node does not exist in the authoritative reference.
//
// Ours: SkillCastService.DamageWaitTime (SkillCastService.cs:566-591) is a
// hardcoded id -> ms table transcribed from MapleNecrocer `Skill.cs:53-150`, the
// SECONDARY reference. It is published on the plan and turned into `mobTarget`
// timeline ticks, but ApplySkillHits resolves every hit on the cast frame.
//
// So the code is RIGHT and the published plan is misleading. This probe prints the
// delay the plan claims next to the frame the hp actually drops.
//
// Placement: the facing is chosen from the skill's own box so the mob is inside it
// whatever way round the box was authored — `rbx < 0` means the box reaches to the
// character's right, `rbx > 0` to its left. Without that, a skill whose box points
// away from the mob reads as "no hit" and proves nothing about timing.
//
// Usage: node probe-A4-damage-timing.mjs
import { openSession, summon, close, frame, frames, plan, walkTo, mob, MOB, START_HP } from './probe-A-lib.mjs';

const SKILLS = ['1121008', '5101002', '5211004', '2121007', '2321008', '1111008'];
const OFFSET = 40;   // where to park the mob, relative to the body

/** The dist span this skill's box covers on the facing we will use (Combat.cpp:660-679). */
function span(p, facingRight) {
  const hrange = Math.trunc(p.ltx * p.hRange);
  // facing right -> world [px - rbx, px - hrange]; facing left -> [px + hrange, px + rbx]
  return facingRight ? [-p.rbx, -hrange] : [hrange, p.rbx];
}

console.log('skill      mobCount damageWaitTime totalTime  planFirstMobTargetTick  planDelayFrames  hpDropsOnCastFrame');
const rows = [];
for (const s of SKILLS) {
  const p = await plan(s).catch(() => null);
  if (!p) { console.log(`${s}  (not in the open archives)`); continue; }

  const facingRight = p.rbx < 0;              // which way this box actually reaches
  const [lo, hi] = span(p, facingRight);
  const offset = Math.min(Math.max(OFFSET, lo + 10), hi - 10);
  const targetTick = p.timeline.find((t) => t.kind === 'mobTarget')?.tick;

  const sid = await openSession();
  try {
    await summon(sid, 1);
    await frames(sid, 18, {});
    const f0 = await frame(sid);
    // park the mob `offset` px on the side the box reaches
    await walkTo(sid, Math.round(mob(f0).x - offset));
    const before = await frames(sid, 1, { [facingRight ? 'Right' : 'Left']: true });
    const m0 = mob(before);
    let dropFrame = null;
    const hpTrail = [];
    for (let i = 0; i < 16; i++) {
      const res = await frames(sid, 1, {
        [facingRight ? 'Right' : 'Left']: true,
        ...(i === 0 ? { SkillId: s } : {}),
      });
      const m = mob(res);
      hpTrail.push(m ? m.hp : -1);
      if (m && m.hp < START_HP && dropFrame === null) dropFrame = i;
    }
    const distAtCast = m0 ? m0.x - before.x : NaN;
    const inBox = distAtCast >= lo && distAtCast <= hi;
    rows.push({ s, wait: p.damageWaitTime, total: p.totalTime, targetTick, dropFrame, inBox, distAtCast, mobCount: p.mobCount });
    console.log(
      String(s).padEnd(10),
      String(p.mobCount).padStart(8),
      String(p.damageWaitTime).padStart(15),
      String(p.totalTime).padStart(9),
      String(targetTick ?? '-').padStart(23),
      String(targetTick === undefined ? '-' : (targetTick / 6).toFixed(1)).padStart(16),
      String(dropFrame ?? 'never').padStart(22),
    );
    console.log(`           facing ${facingRight ? 'RIGHT' : 'LEFT'} box span dist [${lo}, ${hi}], mob at ${distAtCast.toFixed(0)} ` +
      `(${inBox ? 'inside' : 'OUTSIDE — row is about the box, not the timing'})`);
    console.log(`           hp by frame: [${hpTrail.join(', ')}]`);
  } finally {
    await close(sid);
  }
}

console.log('\n================ A4 VERDICT ================');
for (const r of rows) {
  if (!r.inBox) { console.log(`  ${r.s}: mob outside the box (dist ${r.distAtCast.toFixed(0)}) — INCONCLUSIVE.`); continue; }
  const claim = r.targetTick === undefined ? '-' : (r.targetTick / 6).toFixed(1);
  console.log(`  ${r.s}: plan claims first mobTarget at tick ${r.targetTick} (~${claim} frames). ` +
    `hp drops on frame ${r.dropFrame} of the cast. ` +
    (r.dropFrame === 0
      ? 'IMMEDIATE. The reference has no delay either, so the BEHAVIOUR matches; the PUBLISHED tick does not.'
      : `DELAYED by ${r.dropFrame} frames — this would NOT match the reference.`));
}
const conclusive = rows.filter((r) => r.inBox);
const immediate = conclusive.filter((r) => r.dropFrame === 0);
console.log(`\n  ${immediate.length} of ${conclusive.length} in-box skills settle damage on the cast frame.`);
console.log('  damageWaitTime is a MapleNecrocer table (SkillCastService.cs:566-591) with no counterpart in the');
console.log('  reference; it feeds the plan\'s timeline and nothing else. The fix is to stop PUBLISHING it as');
console.log('  if it were the hit schedule — a file change in SkillCastService, outside this workstream.');