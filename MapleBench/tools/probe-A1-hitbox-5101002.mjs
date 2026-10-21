// A1 — 5101002 (回马 / backspin) never hits, even with the mob inside the box.
//
// Prints, for every facing and every distance, the box our code would build,
// the mob's distance, and whether damage actually landed.
//
// Usage: node probe-A1-hitbox-5101002.mjs
import { openSession, summon, close, frame, frames, face, walkTo, plan, hpByMob, hitBox } from './probe-A-lib.mjs';

const SKILL = '5101002';
const CONTROL = '1121008';

const p = await plan(SKILL);
const c = await plan(CONTROL);

console.log(`plan ${SKILL}: lt=(${p.ltx},${p.lty}) rb=(${p.rbx},${p.rby}) ` +
    `hRange=${p.hRange} mobCount=${p.mobCount} collisionCount=${p.collisionCount} ` +
    `damageWaitTime=${p.damageWaitTime} totalTime=${p.totalTime}`);
console.log(`plan ${CONTROL}: lt=(${c.ltx},${c.lty}) rb=(${c.rbx},${c.rby}) ` +
    `hRange=${c.hRange} mobCount=${c.mobCount} collisionCount=${c.collisionCount}`);

// The reference, Combat.cpp:660-679, verbatim:
//   hrange = range.left() * attack.hrange
//   toleft : x in [origin.x + hrange , origin.x + range.right()]
//   right  : x in [origin.x - range.right() , origin.x - hrange]
function referenceBox(px, facingRight, q) {
  const hrange = Math.trunc(q.ltx * q.hRange);
  return facingRight
    ? { l: px - q.rbx, r: px - hrange }
    : { l: px + hrange, r: px + q.rbx };
}

async function castAt(dx, facingRight, skillId) {
  const sid = await openSession();
  try {
    await summon(sid, 1);
    let f = await frames(sid, 4, {});
    // walk the body so the mob lands `dx` px on the requested side
    const target = f.mobs.filter((m) => m.mobId === '9400101')[0].x - dx;
    f = await walkTo(sid, Math.round(target));
    // re-settle, then lock facing and cast in the SAME batch
    if (facingRight) await frames(sid, 1, { Right: true });
    else await frames(sid, 1, { Left: true });
    const before = await frame(sid);
    const mobBefore = before.mobs.filter((m) => m.mobId === '9400101')[0];
    const res = await frames(sid, 1, {
      [facingRight ? 'Right' : 'Left']: true,
      SkillId: skillId,
    });
    const mobAfter = res.mobs.filter((m) => m.mobId === '9400101')[0];
    const dmg = res.damage.filter((d) => d.mobId === '9400101');
    return {
      px: before.x, py: before.y, facing: before.facing,
      mobX: mobBefore ? mobBefore.x : NaN,
      mobY: mobBefore ? mobBefore.y : NaN,
      hpAfter: mobAfter ? mobAfter.hp : -1,
      dmg: dmg.length,
      dmgValue: dmg.map((d) => d.value).join(','),
      castFacing: res.facing,
    };
  } finally {
    await close(sid);
  }
}

for (const skillId of [SKILL, CONTROL]) {
  const q = skillId === SKILL ? p : c;
  console.log(`\n=== ${skillId}  lt=(${q.ltx},${q.lty}) rb=(${q.rbx},${q.rby}) hRange=${q.hRange} ===`);
  for (const facingRight of [true, false]) {
    console.log(`\n-- facing ${facingRight ? 'RIGHT' : 'LEFT'} --`);
    console.log('  dx   playerX   mobX    dist  | ourBox L..R        refBox L..R        | hpAfter dmg  verdict');
    for (const dx of [-200, -160, -120, -80, -50, -32, -20, 0, 20, 32, 50, 80, 120, 160, 200]) {
      const r = await castAt(dx, facingRight, skillId);
      const ours = hitBox(r.px, r.py, r.facing >= 0 ? 1 : -1, q);
      const ref = referenceBox(r.px, r.facing >= 0, q);
      const dist = r.mobX - r.px;
      // is the mob's centre inside the reference's x span?
      const inRefX = dist >= ref.l - r.px && dist <= ref.r - r.px;
      const hit = r.hpAfter < 1000;
      const flag = hit === inRefX ? '  ok' : ' **MISMATCH**';
      console.log(
        String(dx).padStart(5),
        r.px.toFixed(0).padStart(9),
        r.mobX.toFixed(0).padStart(7),
        dist.toFixed(0).padStart(7),
        '|',
        `${(ours.left - r.px).toFixed(0)}..${(ours.right - r.px).toFixed(0)}`.padStart(16),
        '|',
        `${(ref.l - r.px).toFixed(0)}..${(ref.r - r.px).toFixed(0)}`.padStart(16),
        '|',
        String(r.hpAfter).padStart(7),
        String(r.dmg).padStart(3),
        (hit ? ' HIT ' : ' miss'),
        flag,
      );
    }
  }
}