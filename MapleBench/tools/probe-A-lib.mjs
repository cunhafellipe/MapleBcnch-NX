// Shared harness for the probe-A-* scripts (A1..A5).
// Every helper prints NUMBERS, never "ok".
import { setTimeout as sleep } from 'node:timers/promises';

export const BASE = process.env.MB_BASE ?? 'http://127.0.0.1:5591';
export const MAP = 'f4/Map/Map1/100000004.img';
export const MOB = '9400101';
export const START_HP = 1000;   // ADR-0006: every mob has 1000 hp
export const PLAYER_DMG = 100;  // ADR-0006: every player hit deals 100

async function j(method, url, body) {
  const r = await fetch(BASE + url, {
    method,
    headers: body === undefined ? {} : { 'content-type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  const t = await r.text();
  if (!r.ok) throw new Error(`${method} ${url} -> ${r.status} ${t.slice(0, 300)}`);
  return t.length ? JSON.parse(t) : null;
}

export const get = (u) => j('GET', u);
export const post = (u, b) => j('POST', u, b);
export const del = (u) => j('DELETE', u);

/**
 * Empty collections are omitted by the serializer, so a frame that dealt no
 * damage has NO `damage` key at all. Every reader normalises them to [] here
 * rather than each probe guarding for itself.
 */
export function frame(sid) {
  return get(`/api/simulate/session/${sid}`).then(normalise);
}

function normalise(f) {
  f.mobs ??= [];
  f.damage ??= [];
  return f;
}

export function normaliseFrame(f) {
  return normalise(f);
}

export async function plan(skillId) {
  return get(`/api/skill-cast/plan?skillId=${skillId}`);
}

export async function openSession() {
  const s = await post('/api/simulate/session', { path: MAP, invulnerable: true });
  const sid = s.sessionId;
  await post(`/api/simulate/session/${sid}/invulnerable`, { value: true });
  return sid;
}

export async function summon(sid, count, mobId = MOB) {
  const r = await post(`/api/simulate/session/${sid}/summon`, { mobId, count });
  return r;
}

export async function close(sid) {
  try { await del(`/api/simulate/session/${sid}`); } catch { /* already gone */ }
}

/** Advance `n` frames holding `input` (reused for every frame of the batch). */
export async function frames(sid, n, input = {}) {
  if (n <= 0) return frame(sid);
  return post(`/api/simulate/session/${sid}/frames`, { count: n, first: input, then: input })
    .then(normalise);
}

/** Hold a direction until the body is at `targetX`, then release. */
export async function walkTo(sid, targetX, maxTicks = 400) {
  let f = await frame(sid);
  for (let i = 0; i < maxTicks; i++) {
    const d = targetX - f.x;
    if (Math.abs(d) <= 1) break;
    const dir = d > 0 ? { Right: true } : { Left: true };
    f = await frames(sid, 1, dir);
  }
  await frames(sid, 1, {});
  return frame(sid);
}

/** Hold a direction for one frame so `facing` settles, then release. */
export async function face(sid, right) {
  const dir = right ? { Right: true } : { Left: true };
  await frames(sid, 2, dir);
  await frames(sid, 1, {});
  return frame(sid);
}

/**
 * Walk the body to `targetX`, THEN hold `faceRight` for one frame so the cast
 * happens with the required facing.
 *
 * Facing cannot be had for free: the direction key you hold IS the facing, so a
 * body that walks right is facing right. Standing still for one frame of the
 * opposite key costs ~2.5px of drift and buys the facing, which is what a
 * distance sweep on the far side of the body needs.
 *
 * Returns the frame the cast should be issued from, with mob positions read from
 * THAT frame — a mob read before the walk is stale by however long the walk took.
 */
export async function standAt(sid, targetX, faceRight, maxTicks = 300) {
  let f = await frame(sid);
  for (let i = 0; i < maxTicks && Math.abs(f.x - targetX) > 1; i++) {
    const next = await frames(sid, 4, { [f.x < targetX ? 'Right' : 'Left']: true });
    if (next.x === f.x) break;            // against a wall
    f = next;
  }
  f = await frames(sid, 1, { [faceRight ? 'Right' : 'Left']: true });
  return f;
}

/**
 * One cast, issued from the frame `standAt` left, then wait out the pose.
 *
 * `SkillPoseTicks` refuses a second cast until the first one's pose has played
 * (MapSimSessionStore.cs:1861), so back-to-back casts on one body are silently
 * DROPPED and the probe reads them as misses. Every cast therefore waits.
 */
export async function castOnce(sid, skillId, faceRight, waitFrames = 60) {
  const before = await frame(sid);
  const res = await frames(sid, 1, {
    [faceRight ? 'Right' : 'Left']: true, SkillId: skillId,
  });
  await frames(sid, waitFrames, {});
  return { before, res };
}

/**
 * Converge on "the mob is `want` px from the body", then hold `faceRight` for one
 * frame so the cast has its facing.
 *
 * The mob PATROLS (MEASURED: one sample wandered from x=630 to x=3620 while the
 * body stood still), so a single "walk to mobX - want" computed once lands
 * somewhere else entirely by the time the walk ends. Re-reading the mob after each
 * short burst and re-aiming converges instead, because the body moves faster than
 * the patrol does.
 *
 * Returns the frame the cast should be issued from, or null if it cannot get
 * within `tol` — a caller must treat null as OFF-TARGET, not as a miss.
 */
export async function convergeDist(sid, want, faceRight, tol = 25, rounds = 12) {
  let f = await frame(sid);
  let best = null, bestErr = Infinity;
  for (let r = 0; r < rounds; r++) {
    const m = mob(f);
    if (!m) return null;
    const err = (m.x - f.x) - want;
    if (Math.abs(err) < bestErr) { bestErr = Math.abs(err); best = f; }
    if (Math.abs(err) <= tol) { f = await frames(sid, 1, { [faceRight ? 'Right' : 'Left']: true }); return f; }
    const next = await frames(sid, 6, { [err < 0 ? 'Right' : 'Left']: true });
    if (next.x === f.x) break;                       // against a wall
    f = next;
  }
  if (bestErr > tol) return null;                    // never got there
  // re-aim once at the best frame's distance is not possible; just take where we are
  return frames(sid, 1, { [faceRight ? 'Right' : 'Left']: true });
}

/** The mob this project uses, by its own id — map `life` entries share others. */
export const mob = (f, mobId = MOB) => f.mobs.find((m) => m.mobId === mobId);

/** Only the summons: the map's own life entries share other ids. */
export const summons = (f, mobId = MOB) => f.mobs.filter((m) => m.mobId === mobId);

/** Identity of a mob for one probe run is its (x) — the pit this project already hit. */
export const byPos = (f, mobId = MOB) =>
  summons(f, mobId)
    .map((m, i) => ({ i, x: m.x, y: m.y, hp: m.hp }))
    .sort((a, b) => a.x - b.x);

/** Snapshot hp keyed by a stable position bucket (mobs walk, so re-read each time). */
export function hpByMob(f, mobId = MOB) {
  return summons(f, mobId).map((m) => ({ x: m.x, y: m.y, hp: m.hp }));
}

export const fmt = (n, d = 1) => (typeof n === 'number' ? n.toFixed(d) : String(n));

/** Replicates ApplySkillHits' box maths so a probe can print it without a cast. */
export function hitBox(px, py, facing, p) {
  const haveBox = p.hasCollisionBox;
  const left = haveBox ? p.ltx : -250;
  const right = haveBox ? p.rbx : 0;
  const top = haveBox ? p.lty : -75;
  const bottom = haveBox ? p.rby : 25;
  const near = haveBox ? Math.round(left * p.hRange) : left;
  const facingRight = facing >= 0;
  return {
    near,
    left: facingRight ? px - right : px + near,
    right: facingRight ? px - near : px + right,
    top: py + top,
    bottom: py + bottom,
  };
}

export { sleep };