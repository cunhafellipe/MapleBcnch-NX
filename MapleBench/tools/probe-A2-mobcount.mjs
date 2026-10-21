// A2 — does a cast hit exactly `mobCount` mobs, and are they the NEAREST ones?
// The 66-skill sweep that motivated this only ever had ONE mob inside the box, so
// the mobCount>1 path had never run.
//
// Reference, Combat.cpp:1226-1272 `find_closest`:
//     std::multimap<uint16_t, int32_t> distances;
//     for (auto& mmo : *objs)
//         if (mob && mob->is_alive() && mob->is_in_range(range))
//             distances.emplace(mob->get_position().distance(origin), mob->get_oid());
//     for (auto& iter : distances) { if (targets.size() >= objcount) break; targets.push_back(iter.second); }
//
// `objcount` bounds the OUTPUT. Every in-range mob is weighed first. Distance is
// `Point::distance` -> `static_cast<int16_t>(sqrt(dx*dx + dy*dy))` — both axes,
// truncated (Point.h:60-65, :80-83). Ties keep scan order.
//
// A mob's identity across a hit is its INDEX in session.Mobs, never its id and
// never its x (MapSimSessionStore.cs:1196-1201): a map's own `life` entries can
// name one id, and a hit knocks the mob back.
//
// DISCRIMINATING POWER: the script also prints what the OLD code would have
// chosen (the first `mobCount` in session order) and refuses to call the
// measurement a PASS unless those two predictions actually differ somewhere — a
// check that cannot fail is not a check.
//
// Usage: node probe-A2-mobcount.mjs
import { openSession, summon, close, frame, frames, plan, MOB, START_HP } from './probe-A-lib.mjs';

const SKILLS = ['1121008', '5211004', '15101003', '1111008', '21100001', '2121007', '10001009'];
const MOB_HALF = 25;     // mob animation bounds slack around its origin (see probe-A3)

/** Reference distance: sqrt(dx*dx+dy*dy), truncated, as uint16 (Combat.cpp:1243). */
const refDist = (dx, dy) => Math.trunc(Math.sqrt(dx * dx + dy * dy));

/** The box our code builds (Combat.cpp:660-679), as dist spans. */
function boxSpan(p, facingRight) {
  const hrange = Math.trunc(p.ltx * p.hRange);
  const l = facingRight ? -p.rbx : hrange;
  const r = facingRight ? -hrange : p.rbx;
  return [Math.min(l, r), Math.max(l, r)];
}

async function run(skillId) {
  const p = await plan(skillId);
  const want = p.mobCount;
  const sid = await openSession();
  try {
    const n = want + 2;                       // N+2 so "exactly N" is falsifiable
    const su = await summon(sid, n);
    await frames(sid, 4, {});
    await frames(sid, 1, { Right: true });     // lock facing right
    const before = await frame(sid);
    const idx = new Set(su.indexes);
    const mobs = before.mobs
      .map((m, i) => ({ i, id: m.mobId, x: m.x, y: m.y, hp: m.hp }))
      .filter((m) => idx.has(m.i));

    const [lo, hi] = boxSpan(p, true);
    for (const m of mobs) {
      const d = m.x - before.x;
      m.dist = refDist(d, m.y - before.y);
      m.inBox = d >= lo - MOB_HALF && d <= hi + MOB_HALF;
      m.ambiguous = Math.min(Math.abs(d - (lo - MOB_HALF)), Math.abs(d - (hi + MOB_HALF))) < MOB_HALF;
    }

    const res = await frames(sid, 1, { Right: true, SkillId: skillId });
    const after = res.mobs;
    const hurt = mobs.filter((m) => after[m.i] && after[m.i].hp < m.hp);
    const got = hurt.map((m) => m.i).sort((a, b) => a - b);

    const clean = mobs.filter((m) => m.inBox && !m.ambiguous);
    const eq = (a, b) => a.length === b.length && a.every((v, i) => v === b[i]);
    // reference: nearest `want` among the in-range, ties by scan order. AMBIGUOUS
    // mobs are kept in the candidate pool — they may well be in range — but they
    // are not required to behave, so they are not scored.
    const pool = mobs.filter((m) => m.inBox);
    const expectNearest = [...pool].sort((a, b) => a.dist - b.dist || a.i - b.i)
      .slice(0, want).map((m) => m.i).sort((a, b) => a - b);
    // the OLD code: first `want` in session order
    const expectOld = [...pool].sort((a, b) => a.i - b.i)
      .slice(0, want).map((m) => m.i).sort((a, b) => a - b);

    return {
      want, n, spawned: mobs.length, px: before.x, py: before.y, lo, hi,
      mobs, got, expectNearest, expectOld,
      after,
      countOk: got.length === Math.min(want, clean.length),
      nearestOk: eq(got, expectNearest),
      oldOk: eq(got, expectOld),
      predictionsDiffer: !eq(expectNearest, expectOld),
      inBoxCount: clean.length,
      poolCount: pool.length,
    };
  } finally {
    await close(sid);
  }
}

let anyDiscriminating = false;
let allPass = true;
for (const s of SKILLS) {
  const r = await run(s);
  if (r.predictionsDiffer) anyDiscriminating = true;
  const pass = r.countOk && r.nearestOk;
  if (!pass) allPass = false;
  console.log(`\n=== ${s}  mobCount=${r.want}  summoned N+2=${r.n}  placed=${r.spawned} ===`);
  console.log(`body @ (${r.px.toFixed(0)},${r.py.toFixed(0)})  box dist span [${r.lo}, ${r.hi}]  in-box = ${r.poolCount} (${r.inBoxCount} unambiguous)`);
  console.log('  slot    x      y   refDist  inBox | hp after cast');
  for (const m of r.mobs) {
    const hpAfter = r.after[m.i] ? r.after[m.i].hp : '?';
    console.log(
      String(m.i).padStart(6), m.x.toFixed(0).padStart(6), m.y.toFixed(0).padStart(6),
      String(m.dist).padStart(9), String(m.inBox ? (m.ambiguous ? 'ambig' : 'IN') : 'out').padStart(6),
      '|', String(hpAfter).padStart(6), hpAfter < START_HP ? '  <- HIT' : '',
    );
  }
  console.log(`  hit slots           : [${r.got.join(',')}]  (${r.got.length} of ${r.n} summoned)`);
  console.log(`  NEAREST-${r.want} (reference) : [${r.expectNearest.join(',')}]   count-ok=${r.countOk}  nearest-ok=${r.nearestOk}`);
  console.log(`  FIRST-${r.want}-BY-SESSION    : [${r.expectOld.join(',')}]   matches-observed=${r.oldOk}   predictions differ=${r.predictionsDiffer}`);
  console.log(`  VERDICT: ${pass ? 'PASS (exactly N, and they are the N nearest)'
    : r.countOk && r.oldOk && r.predictionsDiffer ? 'FAIL (exactly N, but the WRONG N — session order, not distance)'
      : `FAIL (hit ${r.got.length}, wanted ${Math.min(r.want, r.inBoxCount)})`}`);
}

console.log('\n================ A2 VERDICT ================');
console.log(`At least one skill where the two predictions disagree: ${anyDiscriminating}` +
  (anyDiscriminating ? '' : '   <-- THE CHECK CANNOT DETECT THE BUG; widen SKILLS or N+2'));
console.log(allPass
  ? 'Every skill hit exactly mobCount mobs and they were the nearest ones.'
  : 'At least one skill did not.');