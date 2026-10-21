// A3 — hRange = 1.5 (only 4121008 carries it). Which edge does the multiplier
// move, and where is the real boundary?
//
// Reference, Combat.cpp:660:
//     int16_t hrange = static_cast<int16_t>(range.left() * attack.hrange);
// then Combat.cpp:662-679 mirrors it by facing. hrange is the NEAR edge ONLY;
// `range.right()` is untouched. hrange is the skill's `range` node / 100
// (SkillData.cpp:77, default 1.0). Skill.cpp:433 forces it back to 1.0 only when
// the reach came from the character's absolute projectile range (a RANGED cast),
// never for a melee one.
//
//   4121008  lt=-200 rb=200 hRange=1.5 -> facing LEFT near edge dist = -300
//   1111008  lt=-200 rb=200 hRange=1.0 -> facing LEFT near edge dist = -200   (CONTROL: same box, NO multiplier)
//   1121008  lt=-150 rb=-10 hRange=1.0 -> facing LEFT near edge dist = -150   (CONTROL: different box)
//
// If hRange acts on the near edge, 4121008's boundary sits exactly
// 200*(1.5-1.0) = 100px further out than its control 1111008's.
//
// Method: one cast per FRESH SESSION (a mob has 1000hp and every hit takes 100, so
// a session survives 9 casts and then the mob is dead and every later row reads as
// a miss). Each attempt parks the body at a different x and the DISTANCE IS
// MEASURED, not commanded — the mob patrols (MEASURED: one sample wandered from
// x=630 to x=3620 while the body stood still), so a commanded distance lands
// somewhere else. Rows where the map's own mobs filled mobCount are dropped, and
// the mob's own animation bounds put ~20px of slack on the boundary
// (Mob::is_in_range uses `animations.at(stance).get_bounds()`, Mob.cpp:1875-1884).
//
// Usage: node probe-A3-hrange.mjs
import { openSession, summon, close, frame, frames, plan, walkTo, mob, MOB, START_HP } from './probe-A-lib.mjs';

const SKILLS = ['4121008', '1111008', '1121008'];
const FACE_RIGHT = false;      // every edge below is quoted for facing LEFT
// MEASURED mob half-width, not assumed: 1111008's box edge is -200 and it HITS at
// dist -204 but MISSES at -228, so the mob's own bounds reach at least 4px and less
// than 28px past its origin. 4121008's edge is -300 and it MISSES at -316, which
// tightens the upper bound to <16. Intersected: 4 <= half-width < 16. 12 is used.
const MOB_HALF = 12;
const AMBIGUOUS = 8;           // a row this close to an edge is not scored
const ATTEMPTS = 26;

// Deterministic spread of body positions: the map's walkable width, sampled evenly.
const L = 1610, R = 2310;
const PARKS = Array.from({ length: ATTEMPTS }, (_, i) => Math.round(L + ((R - L) * i) / (ATTEMPTS - 1)));

const plans = {};
for (const s of SKILLS) plans[s] = await plan(s);

/** One cast, one fresh session, with the body parked at `bodyX`. */
async function castAt(skillId, bodyX) {
  const sid = await openSession();
  try {
    await summon(sid, 1);
    await frames(sid, 18, {});
    await walkTo(sid, bodyX);
    const before = await frames(sid, 1, { Left: true });   // face LEFT, then cast
    const m0 = mob(before);
    const res = await frames(sid, 1, { Left: true, SkillId: skillId });
    const m1 = mob(res);
    return {
      bodyX: before.x, mobX: m0 ? m0.x : NaN,
      dist: m0 ? m0.x - before.x : NaN,
      hit: !!(m1 && m0 && m1.hp < m0.hp),
      hp: m1 ? m1.hp : -1,
      others: res.mobs.filter((m) => m.mobId !== MOB && m.hp < START_HP).length,
      facing: before.facing,
    };
  } finally {
    await close(sid);
  }
}

const summary = {};
for (const s of SKILLS) {
  const p = plans[s];
  const hrange = Math.trunc(p.ltx * p.hRange);
  const near = FACE_RIGHT ? p.rbx : hrange;
  const far = FACE_RIGHT ? hrange : p.rbx;
  const lo = Math.min(near, far), hi = Math.max(near, far);
  console.log(`\n=== ${s}  lt=${p.ltx} rb=${p.rbx} hRange=${p.hRange} mobCount=${p.mobCount} ===`);
  console.log(`  predicted span (facing LEFT) at dist [${lo}, ${hi}]` +
    `${p.hRange === 1 ? '   (hRange = 1: the box as authored)' : '   <== hRange moved the NEAR edge -200 -> -300'}`);
  console.log('  parkX   bodyX   mobX    dist  | mob hp | others | verdict');
  const usable = [];
  for (const park of PARKS) {
    const r = await castAt(s, park);
    if (r.facing >= 0) { console.log(String(park).padStart(7) + '  facing did not turn LEFT — row dropped'); continue; }
    const ok = r.others < p.mobCount;
    // BOTH edges, because 1121008's box is negative on both sides.
    const expectHit = r.dist >= lo - MOB_HALF && r.dist <= hi + MOB_HALF;
    const nearEdge = Math.min(Math.abs(r.dist - (lo - MOB_HALF)), Math.abs(r.dist - (hi + MOB_HALF)));
    const ambig = nearEdge < AMBIGUOUS;
    const verdict = !ok ? 'INCONCLUSIVE (map mobs filled mobCount)'
      : ambig ? 'AMBIGUOUS (within the mob half-width of an edge)'
        : r.hit === expectHit ? 'consistent' : 'MISMATCH';
    if (ok && !ambig) usable.push({ dist: r.dist, hit: r.hit });
    console.log(
      String(park).padStart(7), r.bodyX.toFixed(0).padStart(7), r.mobX.toFixed(0).padStart(7),
      r.dist.toFixed(0).padStart(7), '|', String(r.hp).padStart(7), '|',
      String(r.others).padStart(7), '|', verdict,
    );
  }
  const hits = usable.filter((u) => u.hit).map((u) => u.dist).sort((a, c) => a - c);
  const miss = usable.filter((u) => !u.hit).map((u) => u.dist).sort((a, c) => a - c);
  const mism = usable.filter((u) => u.hit !== (u.dist >= lo - MOB_HALF && u.dist <= hi + MOB_HALF)).length;
  console.log(`  scored rows ${usable.length}   hit ${hits.length}   miss ${miss.length}   MISMATCH ${mism}`);
  console.log(`  HIT  dist: ${hits.map((d) => d.toFixed(0)).join(', ') || '(none)'}`);
  console.log(`  MISS dist: ${miss.map((d) => d.toFixed(0)).join(', ') || '(none)'}`);
  // hRange can only move the NEAR edge, which for a LEFT-facing cast is the
  // NEGATIVE side. Bracket it from the negative hits and the misses beyond them,
  // NOT from max(all hits) — the far edge can be positive and would swallow the
  // comparison.
  const negHits = hits.filter((d) => d < 0);
  const nearHit = negHits.length ? Math.min(...negHits) : null;   // furthest OUT, not nearest 0
  const nearMiss = nearHit === null ? null
    : (miss.filter((d) => d < nearHit).sort((a, c) => c - a)[0] ?? null);
  const mid = nearHit !== null && nearMiss !== null ? (nearHit + nearMiss) / 2 : null;
  console.log(`  NEAR edge ${near}: farthest negative HIT ${nearHit?.toFixed(0) ?? '-'} | ` +
    `nearest MISS beyond it ${nearMiss?.toFixed(0) ?? '-'} | measured boundary mid ${mid?.toFixed(0) ?? '-'}`);
  if (mid !== null) {
    console.log(`  => predicted boundary ${(near - MOB_HALF).toFixed(0)}; error ${(mid - (near - MOB_HALF)).toFixed(0)}px. ` +
      `${mism === 0 ? 'Every scored row agrees with the predicted box.' : mism + ' scored row(s) disagree.'}`);
  }
  summary[s] = { near, lo: nearMiss, hi: nearHit, boundary: mid, mism };
}

console.log('\n================ A3 VERDICT ================');
// A bracket is an INTERVAL (nearestMiss, farthestHit], and the honest test is
// whether the predicted boundary falls inside it — not whether a midpoint matches,
// because the sample rarely lands close to the edge and the midpoint of a 100px
// bracket carries no more information than the bracket itself.
const inInterval = (r) => r.lo === null || r.hi === null ? null : (r.lo < r.near - MOB_HALF && r.near - MOB_HALF <= r.hi);
for (const s of SKILLS) {
  const r = summary[s];
  const ok = inInterval(r);
  console.log(`${s.padEnd(9)} hRange=${String(plans[s].hRange).padEnd(4)} near edge ${String(r.near).padStart(5)} ` +
    `predicted boundary ${String(r.near - MOB_HALF).padStart(5)}  measured bracket ` +
    `${r.lo === null ? 'n/a' : `(${r.lo.toFixed(0)}, ${r.hi.toFixed(0)}]`}  ` +
    `scored-row mismatches ${r.mism}  ` +
    `${ok === null ? 'NOT BRACKETED' : ok ? 'predicted boundary IS inside the measured bracket' : 'predicted boundary is OUTSIDE the measured bracket'}`);
}
const a = summary['4121008'], b = summary['1111008'];
if (a.lo !== null && b.lo !== null) {
  // How far apart are the two boundaries? a is further out (more negative), so the
  // difference a-b is bounded by [a.lo - b.hi, a.hi - b.lo].
  // 4121008's boundary sits FURTHER OUT, i.e. more negative, so the signed
  // difference is negative and its MAGNITUDE is what carries the prediction.
  const dLo = a.lo - b.hi, dHi = a.hi - b.lo;       // signed, both negative
  const magLo = -dHi, magHi = -dLo;                  // |difference|
  const predicted = b.near - a.near;                 // 200 * (1.5 - 1.0) = 100, positive
  console.log(`\nboundary separation (|4121008 - 1111008|) is constrained to ` +
    `[${magLo.toFixed(0)}, ${magHi.toFixed(0)}]px; predicted ${predicted}px ` +
    `(= 200 * (1.5 - 1.0)). ` +
    `${magLo <= predicted && predicted <= magHi
      ? 'AGREES — hRange moved the NEAR edge by exactly lt*(hRange-1), as Combat.cpp:660 does.'
      : 'DISAGREES with the near-edge reading.'}`);
}