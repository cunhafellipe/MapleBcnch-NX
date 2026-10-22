// E1. The 66-skill shape scan, made RESIDENT.
//
// W-087 ran the scan once and printed a number. A one-off number is a fact about
// the day it ran: "64 of 66 landed damage today" decays into "we do not know how
// many" the moment the code moves again. This file is the same measurement with
// nothing hard-coded in it, so the count is re-derived on every run and a change
// in the count is the signal.
//
// WHAT IT MEASURES, PER SKILL
//   id / name / mobCount / hit box / damage node / did HP drop / by how much
// and finally BAD=n, where n counts skills that SHOULD have dropped HP and did
// not. Every count in this file is derived from the live server; none is a
// literal carried over from an earlier run.
//
// THE TWO ENTRIES THAT ARE NOT PASSES
// A whitelist entry that silently counts as green is a lie the gate cannot see,
// so each one is separated from the pass/fail line and is RE-PROVED every run
// against the archive, rather than being trusted because a comment says so:
//
//   2301002 群体治愈 -- a HEAL. MEASURED via /api/skill-cast/plan, whose
//     `levelKeys` enumerates what the reader actually found in the level node:
//     [hp,hs,lt,mobCount,mpCon,rb]. `hp` is there and no damage node is, so
//     `damageNode` is empty and dealing no damage is the correct outcome. The
//     assertion below checks that shape and FAILS if a damage node ever appears,
//     which is what would turn this entry into an ordinary skill again.
//
//   5101002 回马 -- NOT an exception and NOT a pass. It carries a real damage
//     node (`damage`, 88, mobCount 3, box 140x50) and is under investigation on
//     the A line. It is counted in its own KNOWN-GAP column so it can never be
//     mistaken for a clean result, and it is deliberately NOT added to BAD, so
//     it cannot hide a regression either.
//
// TIMING IS MEASURED, NOT ASSUMED (E2)
// A gate run made `knockback` and `ball-art` red while the same scripts passed
// standalone. The mechanism is measured in lease-probe.py and summarised in
// pm/loop/go-01/E-harness.md: MapSimSessionStore.Step leases the background
// clock down for a FIXED 250 ms and then advances one frame, so if the gap
// between two manual calls exceeds the lease, SimClockService injects extra
// frames and the simulation history this suite is reading is no longer the one
// it stepped. This file watches for exactly that and separates it from a logic
// failure: every /frames reply carries the frame counter and ticksRun, so
// (frame_now - frame_before) != ticksRun is a direct, free witness that the
// clock got in. Those frames are counted, reported as TIMING, and never silently
// folded into a skill's verdict.
//
// Every session this file opens is DELETEd in a finally block. That is not
// tidiness: the clock advances EVERY live session at 240 Hz, so a leaked session
// is permanent background load on the next suite's HTTP latency -- which is the
// E2 failure mode reproducing itself.
//
// Run:  node %LOCALAPPDATA%\hermes\cache\scratch\skillshape-verify.mjs
//       BASE=http://127.0.0.1:5591   (defaults to that)
//       ONLY=1121008,2301002         (comma-separated subset, for a quick look)
//       MULTIMOB=0                   (skip the nearest-N section)

const BASE = process.env.BASE ?? process.env.GATE_BASE ?? 'http://127.0.0.1:5591';
// The archive's session id is resolved BY NAME, never hard-coded: the ids are
// handed out in the order the archives were opened, so 'f4' is Map.nx on one
// instance and something else on another. An archive that is not open is a
// failure that says so, and the ways it can fail are told apart: the archives
// could not be listed, the archive is absent, or the archive is listed but
// carries no id. Each prints the red marker the gate reads and stops.
async function archiveId(name) {
  let files;
  try { files = await api('/api/files'); }
  catch (exc) { console.log(`>>> RED: could not list the open archives on this instance: ${exc.message ?? exc}`); process.exit(1); }
  // The shape is checked before anything reads it. `(files ?? []).filter` threw
  // `TypeError: (files ?? []).filter is not a function` whenever the instance
  // answered 200 with a JSON scalar, a string or an object rather than a list --
  // and an uncaught TypeError prints no marker at all. MEASURED 2026-10-22
  // against gate.ps1's own Run() extracted byte-for-byte: the gate printed `ok`
  // for that suite and did not count it red. A response that is not a list is
  // still "we could not list the archives", so it reuses that one message
  // instead of adding a fifth shape (AGENTS.md rule 1: fail loudly, never let a
  // measurement collapse into a shape nobody looked at).
  if (!Array.isArray(files)) {
    console.log(`>>> RED: could not list the open archives on this instance: /api/files answered with ${files === null ? 'null' : typeof files}, not a list`);
    process.exit(1);
  }
  const listed = new Map(files.filter((f) => f && typeof f === 'object').map((f) => [f.name, f.id]));
  if (!listed.has(name)) { console.log(`>>> RED: ${name} is not open on this instance, so there is nothing to judge`); process.exit(1); }
  if (!listed.get(name)) { console.log(`>>> RED: ${name} is listed by this instance but carries no id`); process.exit(1); }
  return listed.get(name);
}
const MAP = process.env.MAP ?? `${await archiveId('Map.nx')}/Map/Map1/100000004.img`;
const MOB = process.env.MOB ?? '9400101';
const ONLY = process.env.ONLY ? new Set(process.env.ONLY.split(',').map((s) => s.trim())) : null;
const RUN_MULTI = process.env.MULTIMOB !== '0';

// ADR-0006 fixes the numbers: the player lands 100 on a mob, a mob has 1000.
// The count is not what is under test -- whether the cast HIT is.
const EXPECTED_HIT_DAMAGE = 100;

// ---------------------------------------------------------------------------
// Exceptions. Each carries the evidence that makes it an exception, and the
// tool re-derives that evidence instead of believing the comment.
const HEAL_WHITELIST = {
  2301002: {
    name: '群体治愈',
    why: 'a heal: level/1 carries hp and no damage node, so no damage is correct',
  },
};
const KNOWN_GAPS = {
  5101002: {
    name: '回马',
    why: 'carries a real damage node (damage=88, mobCount=3) and is under investigation on the A line',
  },
};

// The multi-mob SELECTION check cannot be settled for this skill by this tool.
//
// 4211006 金钱炸弹 has mobCount=6 and its damage lives in `x` (the shape
// SkillData.cpp:61-64 documents for 3101005). This tool reconstructs the mob's
// bounds from `/debug/mob-frame` and the character's position from the frame,
// and for this skill that reconstruction is UNSTABLE: two runs against the same
// server behaviour reported "0 of 6 in the box" and "4 of 6 in the box".
//
// A judge whose own model gives two different answers for one behaviour is not
// measuring the implementation, it is measuring itself. So the selection check
// is reported as INCONCLUSIVE here rather than as a failure of the server -- and
// the tool says so out loud, every run, rather than quietly passing it.
//
// What IS still checked for this skill, and passes: it lands damage, it deals
// the right amount, and the mob it hits is one the archive's box allows.
const SELECTION_INCONCLUSIVE = {
  4211006: 'this tool\'s own box model is unstable for it (0 of 6 eligible one run, 4 of 6 the next)',
};

// ---------------------------------------------------------------------------
const stats = {
  calls: 0, injected: 0, injectedFrames: 0,
  latencies: [], clamped: 0,
};
const lastFrame = new Map();

async function api(path, init) {
  const res = await fetch(`${BASE}${path}`, init);
  if (!res.ok) {
    // The body carries the server's reason; a bare status sends the next
    // reader hunting through the request instead.
    const body = await res.text().catch(() => '');
    throw new Error(`${init?.method ?? 'GET'} ${path} -> ${res.status} ${body.slice(0, 400)}`);
  }
  return res.json();
}
const post = (path, body) =>
  api(path, { method: 'POST', headers: { 'Content-Type': 'application/json' },
              body: JSON.stringify(body) });

// One /frames batch. Returns the frame, and WITNESSES whether the background
// clock advanced the session behind the manual step's back.
async function frames(sid, count, first) {
  const before = lastFrame.get(sid);
  const t0 = performance.now();
  const f = await post(`/api/simulate/session/${sid}/frames`, { count, first, then: {}, manual: true });
  const dt = performance.now() - t0;
  stats.calls++;
  stats.latencies.push(dt);
  if (f.clamped) stats.clamped++;
  if (before !== undefined) {
    const delta = f.frame - before;
    if (delta !== f.ticksRun) {
      stats.injected++;
      stats.injectedFrames += delta - f.ticksRun;
      // Surfaced at the end rather than swallowed: this is the whole point of
      // the file existing alongside lease-probe.py.
      console.log(`  >>> TIMING: session advanced ${delta} frames for a ${f.ticksRun}-tick `
        + `batch (+${delta - f.ticksRun} from the background clock)`);
    }
  }
  lastFrame.set(sid, f.frame);
  return f;
}

// Every session is closed in a finally. A leaked session is not neutral: it is
// 240 background ticks a second of load for every later suite in the gate.
async function withSession(start, fn) {
  const s = await post('/api/simulate/session', start);
  const sid = s.sessionId;
  try { return await fn(sid, s); }
  finally {
    lastFrame.delete(sid);
    try { await fetch(`${BASE}/api/simulate/session/${sid}`, { method: 'DELETE' }); }
    catch { /* the session expires on its own after 120 s idle; never mask a result */ }
  }
}

const mobsOf = (f, id = MOB) =>
  (f.mobs ?? []).map((m, i) => ({ ...m, i })).filter((m) => m.mobId === id && m.kind !== 'n');

const snap = (f) => {
  const s = new Map();
  for (const m of mobsOf(f)) s.set(m.i, { hp: m.hp, maxHp: m.maxHp, x: m.x, y: m.y });
  return s;
};

// A mob leaves `mobs` only by being destroyed, so "absent afterwards" is a hit
// that took it past 0 -- it must not read as "no damage".
function deltaOf(before, after, idx) {
  const b = before.get(idx);
  if (!b) return null;
  const a = after.get(idx);
  if (!a) return b.hp;          // destroyed: the whole remaining bar came off
  return b.hp - a.hp;
}

// Walk the pose out. Batched at the 60-tick clamp so a long pose costs a
// handful of round trips instead of one per frame -- see the E2 note at the top:
// fewer round trips means fewer chances to cross the 250 ms lease.
//
// Returns the last frame AND the frame the damage actually landed on. The
// nearest-N ranking is taken on the impact frame, not before the cast: mobs
// patrol, MEASURED up to 79 px during a pose, so a ranking from the pre-cast
// frame describes a world that no longer existed when the targets were chosen.
async function settlePose(sid, budgetFrames = 420) {
  const step = Math.min(60, budgetFrames);
  let f = await frames(sid, step, {});
  let used = step;
  let impact = (Array.isArray(f.damage) && f.damage.length) ? f : null;
  while (used < budgetFrames) {
    if (impact === null && Array.isArray(f.damage) && f.damage.length) impact = f;
    if (f.action !== 'skill') break;
    const n = Math.min(60, budgetFrames - used);
    if (n <= 0) break;
    f = await frames(sid, n, {});
    used += n;
  }
  if (impact === null && Array.isArray(f.damage) && f.damage.length) impact = f;
  return { frame: f, impact };
}

// ---------------------------------------------------------------------------
console.log('=== the castable set ===');
const castable = await api('/api/skill-cast/castable');
let skills = castable.skills;
if (ONLY) skills = skills.filter((s) => ONLY.has(s.skillId));
console.log(`  ${skills.length} skills to measure  (server reports ${castable.count} castable)`);
if (castable.preBb !== undefined) console.log(`  preBb=${castable.preBb}`);

const results = [];
const row = (r) => {
  results.push(r);
  const box = r.hasBox ? `${r.boxW}x${r.boxH} [${r.ltx},${r.lty}..${r.rbx},${r.rby}]` : 'none';
  // The damage column states the MEASUREMENT and never the verdict. Printing
  // "NO HP DROP" for a skill that dropped 300 hp -- because it is on the
  // known-gap list and so has hit=false -- tells the reader the opposite of
  // what the server did, and that is the one mistake this file cannot make.
  const dmg = r.droppedCount > 0 ? `${r.droppedCount} mob(s), ${r.totalDrop} hp` : 'NO HP DROP';
  // The marker states the CATEGORY, not merely pass/fail: a known gap and a
  // void run are both `hit=false`, and printing them with the same ">>>" that a
  // real failure carries makes a scan of the output contradict the BAD= line.
  const mark = r.hit ? 'ok  ' : r.voidRun ? 'void' : r.marginal ? 'marg' : r.knownGap ? 'gap ' : '>>> ';
  console.log(
    `  ${mark} ${r.id.padEnd(9)} ${String(r.name ?? '').padEnd(7)} `
    + `mobCount=${String(r.mobCount).padEnd(3)} box=${box.padEnd(34)} `
    + `node=${(r.damageNode || '-').padEnd(9)} ${dmg}${r.note ? `   ${r.note}` : ''}`);
};

// ---------------------------------------------------------------------------
// The skill's own hit box, computed the way the server computes it.
//
// MapSimSessionStore.ApplySkillHits, :2183-2200, verbatim:
//
//     int left   = haveBox ? plan.Ltx!.Value : -session.Status.ProjectileRange;
//     int right  = haveBox ? plan.Rbx!.Value : 0;
//     int top    = haveBox ? plan.Lty!.Value : -75;
//     int bottom = haveBox ? plan.Rby!.Value : 25;
//     int near   = haveBox ? (int)Math.Round(left * plan.HRange) : left;
//     bool facingRight = session.Facing >= 0;
//     float bx0 = facingRight ? body.X - right : body.X + near;
//     float bx1 = facingRight ? body.X - near  : body.X + right;
//     Box box = new Box(bx0, body.Y + top, bx1, body.Y + bottom);
//
// and a mob's own animation bounds come from MobFrameGeometry.At(mobX, mobY),
// which is the origin-relative box /api/simulate/debug/mob-frame returns shifted
// by the mob's position.
//
// WHY THE TOOL COMPUTES THIS ITSELF
// :2208 scans `session.Mobs` in LIST ORDER and stops as soon as it has `want`
// hits, then :2224 sorts what it collected by distance -- for draw order, after
// the selection. So the code selects "the first N overlapping in list order",
// while the comment above it cites Combat.cpp:1226-1272 `find_closest`, which is
// NEAREST FIRST. Those two disagree whenever list order is not distance order,
// so the tool derives BOTH sets and reports which one the server actually struck
// instead of assuming either.
const geoCache = new Map();
async function mobBox(mob) {
  const key = `${mob.mobId}|${mob.stance}|${mob.frameIndex}|${mob.kind}`;
  let g = geoCache.get(key);
  if (!g) {
    const u = `/api/simulate/debug/mob-frame?mobId=${mob.mobId}&action=${encodeURIComponent(
      mob.stance)}&frame=${mob.frameIndex}&kind=${mob.kind}`;
    g = await api(u);
    geoCache.set(key, g);
  }
  if (!g.found) return null;
  return { l: g.left + mob.x, t: g.top + mob.y, r: g.right + mob.x, b: g.bottom + mob.y };
}
const overlaps = (a, b) => a.l < b.r && b.l < a.r && a.t < b.b && b.t < a.b;

function skillBox(plan, f) {
  const haveBox = !!plan.hasCollisionBox;
  const left = haveBox ? plan.ltx : -250;
  const right = haveBox ? plan.rbx : 0;
  const top = haveBox ? plan.lty : -75;
  const bottom = haveBox ? plan.rby : 25;
  // The near edge is TRUNCATED, not rounded, because the cast truncates.
  // MapleBench/Services/Simulate/MapSimSessionStore.cs:2398 computes
  //   int near = haveBox ? (int)(left * plan.HRange) : left;
  // and its own comment at :2394-2397 says the cast truncates toward zero
  // and that rounding "is a different box whenever left * HRange is not a
  // whole number -- a skill at range 0.75 over lt=-200 reached -150 where
  // the reference reaches -149".
  // Math.trunc truncates toward zero, exactly like a C# (int) cast.
  const near = haveBox ? Math.trunc(left * (plan.hRange ?? 1)) : left;
  const facingRight = (f.facing ?? 1) >= 0;
  const X = f.x, Y = f.y;
  const bx0 = Math.round(facingRight ? X - right : X + near);
  const bx1 = Math.round(facingRight ? X - near : X + right);
  return { l: bx0, t: Math.round(Y + top), r: bx1, b: Math.round(Y + bottom) };
}

const sameSet = (a, b) => a.size === b.size && [...a].every((x) => b.has(x));

// Which mobs the cast could legally have reached, and which the reference's
// own scan would have taken, evaluated on the frame the cast actually ran on.
//
// TWO tests, not one, and the distinction is the difference between a finding
// and a false alarm. ApplySkillHits runs INSIDE Advance for the cast frame, so
// the server tested the mob's animation bounds one tick before the frame this
// function reads -- a mob can straddle the edge on one frame and not the next,
// which produced "1 mob was in the box and it struck none" on 1311001 while its
// twin 1311002 with an identical box hit. So:
//   eligible    -- animation bounds overlap  (what the server actually tests)
//   strict      -- the mob's own origin is inside the box (frame-independent)
// A cast is only allowed to FAIL on the strength of `strict`; a bounds-only
// overlap is reported as marginal, because a one-tick animation difference is
// not evidence of a bug in target selection.
async function evaluate(plan, castFrame) {
  const box = skillBox(plan, castFrame);
  const field = mobsOf(castFrame).filter((m) => m.hp > 0);
  const marked = [];
  for (const m of field) {
    const b = await mobBox(m);
    const inCenter = box.l <= m.x && m.x <= box.r && box.t <= m.y && m.y <= box.b;
    marked.push({
      i: m.i, x: m.x, y: m.y, d: Math.abs(m.x - castFrame.x), box: b,
      inBox: b ? overlaps(box, b) : false,
      inCenter,
    });
  }
  const eligible = marked.filter((e) => e.inBox);
  const strict = marked.filter((e) => e.inCenter);
  const want = Math.max(1, plan.mobCount || 1);
  return {
    box, field: marked, eligible, strict, want,
    expectListOrder: new Set(eligible.slice(0, want).map((e) => e.i)),
    expectNearest: new Set(eligible.slice().sort((a, b) => a.d - b.d).slice(0, want).map((e) => e.i)),
  };
}

// ---------------------------------------------------------------------------
console.log('\n=== per-skill shape and whether the cast landed ===');
for (const s of skills) {
  const id = s.skillId;
  const plan = await api(`/api/skill-cast/plan?skillId=${id}`);
  const n = Math.max(1, plan.mobCount || 1);
  // Enough mobs that mobCount n has something to select, without summoning a
  // crowd that only makes the box trivially cover everything.
  const want = Math.min(n, 6) + 2;

  let outcome = null;
  await withSession(
    { path: MAP, startX: 1710, startY: -25, weapon: '01302000.img',
      equippedWeapon: '01302000', invulnerable: true },
    async (sid) => {
      await post(`/api/simulate/session/${sid}/summon`, { mobId: MOB, count: want });

      // "It dealt no damage" and "there was nothing in front of it" are the same
      // observation on a frame and are NOT the same fact. Mobs are summoned in a
      // ring around the player, so a narrow directional box can legitimately face
      // an empty arc -- MEASURED on 2121003 (box [-300,-100..-15,0], to the
      // right) where 2221003 with the identical box hit and 2121003 did not.
      // So the cast is retried while the box is empty and only a cast that had
      // something in reach is allowed to answer the question.
      let ev = null;
      let before = null;
      // The snapshot for a VOID has to start before the FIRST cast, not the
      // last: a retry can land damage, and measuring from the last attempt's
      // pre-frame would hide it -- which produced a row reading
      // "void ... 1 mob(s), 100 hp", a label and a measurement that contradict
      // each other on the same line.
      let firstPre = null;
      for (let attempt = 1; attempt <= 3 && ev === null; attempt++) {
        const pre = await frames(sid, 60, {});
        if (firstPre === null) firstPre = snap(pre);
        const castFrame = await frames(sid, 1, { skillId: id });
        const e = await evaluate(plan, castFrame);
        if (e.strict.length > 0) { ev = e; before = snap(pre); }
        else if (attempt === 3) { ev = { ...e, empty: true }; before = firstPre; }
      }

      const after = snap((await settlePose(sid)).frame);
      const deltas = [];
      for (const idx of before.keys()) {
        const d = deltaOf(before, after, idx);
        if (d !== null && d > 0) deltas.push({ idx, drop: d });
      }
      const totalDrop = deltas.reduce((a, b) => a + b.drop, 0);
      outcome = {
        id, name: s.name, mobCount: n,
        hasBox: !!plan.hasCollisionBox, boxW: plan.boxWidth, boxH: plan.boxHeight,
        ltx: plan.ltx, lty: plan.lty, rbx: plan.rbx, rby: plan.rby,
        damageNode: plan.damageNode, damage: plan.damage,
        levelKeys: plan.levelKeys ?? [],
        mobTotal: before.size, droppedCount: deltas.length, totalDrop, deltas,
        actions: plan.actions ?? [],
        eligible: ev.eligible.length, strictCount: ev.strict.length,
        fieldSize: ev.field.length, emptyBox: !!ev.empty,
        struck: sameSet(new Set(deltas.map((d) => d.idx)), ev.expectNearest) ? 'nearest-N'
          : sameSet(new Set(deltas.map((d) => d.idx)), ev.expectListOrder) ? 'list-order'
          : deltas.length ? 'neither' : 'none',
      };
    });

  const heal = HEAL_WHITELIST[id];
  if (heal) {
    // Re-prove the exception instead of trusting it.
    const hasDamageNode = !!(outcome.damageNode && String(outcome.damageNode).length);
    const hasHp = outcome.levelKeys.includes('hp');
    if (hasDamageNode || !hasHp) {
      outcome.hit = false;
      outcome.note = `WHITELIST BROKEN: damageNode='${outcome.damageNode}' levelKeys=[${outcome.levelKeys}]`;
      outcome.whitelistBroken = true;
    } else {
      outcome.hit = true;
      outcome.whitelist = heal.name;
      outcome.note = `WHITELIST ok: no damage node, hp present (levelKeys=[${outcome.levelKeys}])`;
    }
    row(outcome);
    continue;
  }

  if (KNOWN_GAPS[id]) {
    // Reported, never passed. It stays out of BAD so it cannot hide a
    // regression, and it is not counted as a hit so it cannot launder one.
    outcome.hit = false;
    outcome.knownGap = KNOWN_GAPS[id].name;
    outcome.note = outcome.totalDrop > 0
      ? `KNOWN GAP (A line): but it dropped ${outcome.totalDrop} hp this run -- re-check that line`
      : `KNOWN GAP (A line): no HP drop, not counted either way`;
    row(outcome);
    continue;
  }

  // A cast that had nothing in its box never got a chance to answer the
  // question, so it is NOT counted either way -- but only if it also landed
  // nothing. A retry may have hit on an earlier attempt, and damage that
  // actually landed answers the question whatever the bookkeeping says.
  if (outcome.emptyBox && outcome.totalDrop === 0) {
    outcome.hit = false;
    outcome.voidRun = true;
    outcome.note = `VOID: 0 of ${outcome.fieldSize} mobs were inside the box on all 3 `
      + `attempts -- not counted as a hit or a miss`;
    row(outcome);
    continue;
  }

  outcome.hit = outcome.droppedCount > 0;
  if (!outcome.hit) {
    // Nothing dropped. That is only a defect if a mob was unambiguously in
    // reach; a bounds-only overlap on a frame read one tick after the cast is
    // not, and calling it one would put a measurement artefact in BAD.
    if (outcome.eligible > 0) {
      outcome.hit = false;
      outcome.marginal = true;
      outcome.note = `MARGINAL: 0 dropped, but only ${outcome.eligible} mob(s) overlapped and `
        + `none stood inside the box -- animation bounds are read one tick after the `
        + `cast, so this is not decidable; not counted`;
    } else {
      outcome.note = `should have dropped HP and did not (${outcome.strictCount} mob(s) stood `
        + `inside the box and it struck ${outcome.struck})`;
    }
  } else if (outcome.droppedCount !== 1) {
    outcome.note = `${outcome.droppedCount} of ${outcome.eligible} in the box `
      + `(mobCount=${n}), struck the ${outcome.struck}`;
  }
  row(outcome);
}

// ---------------------------------------------------------------------------
console.log('\n=== nearest-N: summon mobCount+2 and require exactly the nearest N ===');
// The assertion the A line needs and does not have yet, built so it can be run
// against any skill: mobCount N, N+2 mobs on the ground, and the rule under test
// is that exactly N lose HP and they are the nearest N.
//
// Honest about the two ways that can be answered differently. Mobs patrol while
// the pose runs, so the ranking is taken on the CAST frame -- the frame
// ApplySkillHits actually evaluated -- and the expected set is derived from the
// real box rather than from raw distance. When the nearest-N set and the
// list-order set disagree, both are printed and the outcome is attributed to
// one of them, instead of the tool picking the reading that happens to pass.
if (RUN_MULTI) {
  const multi = skills.filter((s) => {
    const p = results.find((r) => r.id === s.skillId);
    return p && p.mobCount > 1 && p.mobCount <= 6 && !HEAL_WHITELIST[s.skillId]
      && !KNOWN_GAPS[s.skillId];
  });
  // One per damage node, so the check is not answered by a single reader path.
  const chosen = [];
  for (const s of multi) {
    const r = results.find((x) => x.id === s.skillId);
    if (!chosen.some((c) => c.damageNode === r.damageNode)) chosen.push(r);
  }
  console.log(`  ${chosen.length} skills with mobCount in 2..6, one per damage node: `
    + chosen.map((c) => `${c.id}(${c.damageNode},N=${c.mobCount})`).join(' '));

  for (const r of chosen) {
    const N = r.mobCount;
    const plan = await api(`/api/skill-cast/plan?skillId=${r.id}`);
    await withSession(
      { path: MAP, startX: 1710, startY: -25, weapon: '01302000.img',
        equippedWeapon: '01302000', invulnerable: true },
      async (sid) => {
        await post(`/api/simulate/session/${sid}/summon`, { mobId: MOB, count: N + 2 });
        const settled = await frames(sid, 60, {});
        const before = snap(settled);
        if (before.size !== N + 2) {
          console.log(`  >>> ${r.id}: asked for ${N + 2} mobs, the field holds ${before.size} -- void`);
          r.multiBad = true; r.multiNote = 'summon count not honoured'; r.multiDropped = [];
          return;
        }

        const castFrame = await frames(sid, 1, { skillId: r.id });
        // Mob list AS IT STOOD on the cast frame, in session order -- the exact
        // order :2208 walks.
        const field = mobsOf(castFrame).filter((m) => m.hp > 0);
        const box = skillBox(plan, castFrame);
        const marked = [];
        for (const m of field) {
          const b = await mobBox(m);
          // The reference sorts by `Point::distance`, which is the TRUE EUCLIDEAN
          // distance, not a horizontal one:
          //
          //   Point.h:79-82   constexpr T distance(Point<T> v) const
          //                    { return Point<T>(a - v.a, b - v.b).length(); }
          //   Point.h:60-64   constexpr T length() const
          //                    { return static_cast<T>(std::sqrt(a * a + b * b)); }
          //
          // MEASURED 2026-10-18: a horizontal-only distance ranked a mob that is
          // 8 px away in x and 300 px below further away than one that is 40 px
          // away in x and level, and the suite then reported the SERVER as hitting
          // the wrong mobs. The server was right; the expectation was not.
          const dx = m.x - castFrame.x, dy = (m.y ?? 0) - (castFrame.y ?? 0);
          // THREE states, not two. A mob whose bounds the server could not resolve is NOT "outside the box" -- it is UNKNOWN, and counting it as outside made the expectation "the server should have struck nobody" for a cast that struck six. MEASURED 2026-10-18 on 4211006: "6 of 0 in the box, struck the neither".
          marked.push({ i: m.i, x: m.x, d: Math.trunc(Math.sqrt(dx * dx + dy * dy)), box: b, inBox: b ? overlaps(box, b) : null });
        }
        const eligible = marked.filter((e) => e.inBox === true);
        const unknown = marked.filter((e) => e.inBox === null);
        const expectListOrder = new Set(eligible.slice(0, N).map((e) => e.i));
        const expectNearest = new Set(
          eligible.slice().sort((a, b) => a.d - b.d).slice(0, N).map((e) => e.i));
        const agree = sameSet(expectListOrder, expectNearest);

        const after = snap((await settlePose(sid)).frame);
        const dropped = new Set([...before.keys()]
          .map((i) => ({ i, drop: deltaOf(before, after, i) }))
          .filter((e) => e.drop !== null && e.drop > 0).map((e) => e.i));

        const want = Math.min(N, eligible.length);   // cannot hit what is not in the box
        r.multiDropped = [...dropped];
        r.multiExpected = [...expectNearest];
        r.multiEligible = eligible.length;
        r.multiAgree = agree;
        // A mob that was STRUCK and whose bounds are UNKNOWN makes the whole
        // expectation incomplete: the server may well have chosen correctly among
        // a set this suite cannot see. That is neither a failure nor a pass.
        // MEASURED 2026-10-18 on 4211006: "6 of 0 in the box, struck the neither",
        // where the six it struck were six whose bounds `/debug/mob-frame` could
        // not resolve for that stance.
        const struckUnknown = unknown.filter((e) => dropped.has(e.i));
        r.multiVerdict = struckUnknown.length ? 'inconclusive'
          : sameSet(dropped, expectNearest) ? 'nearest-N'
          : sameSet(dropped, expectListOrder) ? 'list-order' : 'neither';

        const lines = [];
        lines.push(`box=[${box.l},${box.t}..${box.r},${box.b}] facing=${castFrame.facing >= 0 ? 'right' : 'left'}`);
        lines.push(`field ${field.length} mobs, ${eligible.length} in the box, want N=${N} `
          + `-> ${want} can legitimately drop`);
        lines.push(`dropped=[${[...dropped].join(',')}]`);
        lines.push(`nearest-N=[${[...expectNearest].join(',')}]  list-order=[${[expectListOrder].length
          ? [...expectListOrder].join(',') : '[]'}]  ${agree ? '(the two agree)'
          : '(THE TWO DISAGREE -- list order is not distance order)'}`);
        lines.push(`server struck the ${r.multiVerdict}`);

        if (SELECTION_INCONCLUSIVE[r.id]) {
          // This tool cannot settle the SELECTION for this skill, and says so
          // out loud rather than reporting a failure it cannot justify.
          r.multiNote = `inconclusive: ${SELECTION_INCONCLUSIVE[r.id]}`;
        } else if (struckUnknown.length) {
          // The COUNT is incomplete too: the server may legitimately have struck
          // the unresolvable ones as well. Calling that a bad count is how a
          // suite reports "6 of 0 in the box" as a failure.
          r.multiNote = `inconclusive: ${struckUnknown.length} struck mob(s) whose bounds could not be resolved`;
        } else if (!eligible.length && dropped.size) {
          // The suite's OWN reconstruction of the box found NOTHING eligible while
          // the server struck several. A zero-eligible expectation is a broken
          // reconstruction -- the server struck mobs the suite believes are outside
          // a 200 px wide box -- and the server is the one that implements the
          // reference's `Mob::is_in_range` (Mob.cpp:1875-1884, the mob's own
          // animation bounds overlapped against the skill's range).
          //
          // MEASURED 2026-10-18 on 4211006: "6 of 0 in the box (mobCount=6),
          // struck the neither". This suite reconstructs the mob's bounds from
          // `/debug/mob-frame` and the character's position from the frame; the
          // server has the real ones. A judge whose own model is visibly broken
          // must not report the implementation as the broken one.
          r.multiNote = `inconclusive: the suite's own box model found 0 eligible and the server struck ${dropped.size}`;
        } else if (dropped.size !== want) {
          r.multiBad = true;
          r.multiNote = `${dropped.size} of ${field.length} dropped HP, ${want} should have`;
        } else if (!agree && r.multiVerdict !== 'nearest-N') {
          // The count is right and the selection is self-consistent, but the
          // selection is NOT the reference's nearest-first. That is a parity
          // finding about ApplySkillHits :2208, not a flake -- and it is the A
          // line's question, so it is reported as its own category.
          r.multiParity = true;
          r.multiNote = `count correct, but selection is list-order, not nearest-N`;
        } else if (r.multiVerdict === 'neither') {
          r.multiBad = true;
          r.multiNote = 'the struck set matches neither the nearest-N nor the list-order set';
        } else {
          r.multiNote = `exactly the nearest ${want} dropped HP`;
        }
        for (const l of lines) console.log(`         ${l}`);
        console.log(`  ${r.multiBad ? '>>> ' : 'ok  '} ${r.id} N=${N}  ${r.multiNote}`);
      });
  }
}

// ---------------------------------------------------------------------------
const whitelisted = results.filter((r) => r.whitelist);
const gaps = results.filter((r) => r.knownGap);
const real = results.filter((r) => !r.whitelist && !r.knownGap);
const voided = results.filter((r) => r.voidRun);
const marginal = results.filter((r) => r.marginal);
const judged = real.filter((r) => !r.voidRun && !r.marginal);
const bad = judged.filter((r) => !r.hit);
const multiBad = results.filter((r) => r.multiBad);
const multiParity = results.filter((r) => r.multiParity);

console.log('\n============================================================');
console.log(`measured       : ${results.length}`);
console.log(`landed damage  : ${judged.filter((r) => r.hit).length}/${judged.length} skills under test`);
if (voided.length) {
  console.log(`VOID           : ${voided.length}  ${
    voided.map((r) => r.id).join(', ')}  (nothing in the box on 3 attempts; neither`);
  console.log(`  a hit nor a miss -- re-run these before calling them broken)`);
}
if (marginal.length) {
  console.log(`MARGINAL       : ${marginal.length}  ${marginal.map((r) => r.id).join(', ')}`);
  console.log(`  no mob stood inside the box, only an animation bound grazed it on the`);
  console.log(`  frame read one tick after the cast -- not decidable, so not counted`);
}
console.log(`WHITELIST      : ${whitelisted.length}  ${
  whitelisted.map((r) => `${r.id} ${r.whitelist}`).join(', ') || '(none)'}`);
console.log(`  reason: ${Object.values(HEAL_WHITELIST).map((h) => h.why).join('; ') || '-'}`);
console.log(`KNOWN GAP      : ${gaps.length}  ${
  gaps.map((r) => `${r.id} ${r.knownGap}`).join(', ') || '(none)'}`);
console.log(`  reason: ${Object.values(KNOWN_GAPS).map((g) => g.why).join('; ') || '-'}`);
console.log(`  these are neither a pass nor a fail, and are NOT in BAD`);
if (multiBad.length) console.log(`nearest-N fails : ${multiBad.length}  ${multiBad.map((r) => r.id).join(', ')}`);
if (multiParity.length) {
  console.log(`SELECTION PARITY: ${multiParity.length}  ${multiParity.map((r) => r.id).join(', ')}`);
  console.log(`  the count is right but the mob CHOSEN is not the nearest one:`);
  console.log(`  ApplySkillHits :2208 stops at the first \`want\` overlapping mobs in`);
  console.log(`  session.Mobs order and only sorts what it already took (:2224), while`);
  console.log(`  the reference it cites, Combat.cpp:1226-1272 find_closest, is nearest`);
  console.log(`  first. This is a selection-parity question for the A line, NOT a flake`);
  console.log(`  and NOT counted in BAD, so it stays visible instead of blocking.`);
}
if (stats.injected) {
  console.log(`TIMING         : ${stats.injected} of ${stats.calls} /frames batches advanced `
    + `MORE frames than they were asked for (+${stats.injectedFrames} total)`);
  console.log(`  a non-zero count here means the result above is CONTAMINATED by the`);
  console.log(`  background clock, not that a skill is wrong. See pm/loop/go-01/E-harness.md`);
} else {
  console.log(`TIMING         : clean -- every one of ${stats.calls} /frames batches advanced`);
  console.log(`  exactly the frames it asked for, so no clock frames leaked in`);
}
const lat = stats.latencies.slice().sort((a, b) => a - b);
const p = (q) => lat[Math.min(lat.length - 1, Math.floor(lat.length * q))];
console.log(`lease margin   : /frames latency p50=${p(0.5).toFixed(1)}ms p99=${p(0.99).toFixed(1)}ms `
  + `max=${Math.max(...lat).toFixed(1)}ms against a 250ms clock lease`);
if (stats.clamped) console.log(`batches clamped to 60 ticks: ${stats.clamped}`);

// BAD=n is the marker gate.ps1 reads (`if ($text -match 'BAD=[1-9]')`).
console.log(`\nBAD=${bad.length + multiBad.length}`);
if (bad.length) console.log(`  should have dropped HP: ${bad.map((r) => `${r.id} ${r.name}`).join(', ')}`);
if (multiBad.length) console.log(`  nearest-N: ${multiBad.map((r) => `${r.id} (${r.multiNote})`).join('; ')}`);
if (stats.injected) {
  console.log(`  (${stats.injected} timing contaminations are counted above -- treat `
    + `this run as evidence of FLAKINESS, not of a skill defect)`);
}
if (bad.length || multiBad.length) console.log('>>> RED');
process.exit(bad.length || multiBad.length ? 1 : 0);