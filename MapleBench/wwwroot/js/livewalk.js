// Drive the character with the keyboard, instead of playing a precomputed walk.
//
// WHY
// ---
// The play button existed because nothing else could exist. /step rebuilt the
// map and the body on every call, so there was no body to keep walking -- the
// only thing a client could do was ask for N frames and animate the answer.
// That is a recorder pretending to be a simulator.
//
// A simulator is what MapleNecrocer is: load a map, and you are in it. The
// reference sets AlwaysEnableKeyboardInput (RenderFormDraw.cs:51), runs a
// fixed 1/60s loop, and asks Keyboard.KeyDown every tick
// (MapleCharacter.cs:715/732). Nothing in it plays back anything.
//
// So: the body lives on the server (MapSimSessionStore), this file holds the
// arrow keys and asks for one frame per tick. The frame COUNT is what matters,
// not the wall clock -- the accumulator below converts real time into owed
// frames, so a slow request makes the walk late instead of different. Two runs
// pressing the same keys produce the same positions, which is what makes
// anything said about the map worth saying.
//
// The tick rate is the client's: 1/60s, from FixedUpdateDelta
// (RenderFormDraw.cs:176).
//
// AND WHEN THE SERVER TAKES THE BODY AWAY
// ---
// The body is a lease, not a place. MapSimSessionStore.Sweep() (line 1158)
// drops any body nobody has steered for IdleTimeoutMs = 120_000, and every
// later request for it answers 404. A read does NOT renew the lease -- only
// Step and Hold do -- so a tab that was hidden, or a machine that was asleep,
// comes back to a character that no longer exists.
//
// This file used to take that 404 as fatal and set running = false. Nothing
// ever opened another body, so the walk ended permanently: x, y, action and
// facing froze on the last frame the server happened to send. That freeze is
// the whole failure -- it is what "the character does not move and cannot turn"
// looked like, and it sent the hunt for an input handler that was right there.
//
// So a lost body is re-placed instead of mourned, at the position and facing it
// had when the server collected it, with the keys that are held RIGHT NOW still
// driving it. The other failures are different events and are not treated the
// same way -- see LIVE_FAILURES and onFrameError() below.

/** The client's fixed step, RenderFormDraw.cs:176. */
const FRAME_MS = 1000 / 60;

/**
 * A request that never comes back is worse than one that fails loudly: the
 * body would sit there being stepped in a client waiting forever, and the walk
 * would look exactly like a stuck animation. Ten seconds is far longer than a
 * local step takes and far shorter than a user tolerating a dead map.
 */
const REQUEST_TIMEOUT_MS = 10_000;

/**
 * Every way driving a body can fail, and what each one MEANS. These are not
 * interchangeable, and the difference decides whether to open another body,
 * wait, or stop:
 *
 *   session  the server collected this body (Sweep, or an explicit DELETE).
 *            Nothing is broken; there is simply nothing to steer.
 *            Open another one where this one stood.      -> keep going
 *   offline  nothing is listening on the port at all. The body may still be
 *            there.                                      -> wait, retry
 *   timeout  the connection opened and said nothing for 10s. The server is
 *            wedged, not gone.                            -> wait, retry
 *   server   the server answered with a refusal (5xx, or a frame carrying an
 *            error).                                      -> wait, retry
 *   route    404 on POST /session ITSELF -- "Request reached the end of the
 *            middleware pipeline without being handled". The endpoint is not
 *            in this build; retrying an absent route would hide that.
 *                                                        -> stop, report
 *   path     400: no such map path, or nothing to stand on where it was asked
 *            to stand.                                   -> stop, report
 */
const LIVE_FAILURES = {
  session: 'the server collected this character',
  offline: 'cannot reach the simulator server',
  timeout: 'the server stopped answering',
  server: 'the server refused the step',
  route: 'this build has no simulator endpoint',
  path: 'this map cannot be walked',
};

/** Failures no amount of waiting will fix. */
const FATAL_FAILURES = new Set(['route', 'path']);

// W-075: one shared empty list, so the older ticks of the trail all reference
// the same array instead of allocating one per tick per frame -- this map is
// rebuilt on every rAF while a body is live.
const EMPTY_SKILL_FX = [];

/** A failure with a name, so the retry policy never has to read English. */
class LiveFailure extends Error {
  constructor(kind, message) {
    super(message);
    this.kind = kind;
  }
}

/**
 * Tell a dead network from a dead server from a wedged one. fetch() collapses
 * all of them into a rejected promise with no status, so the name is all the
 * evidence there is.
 */
const transport = (err) => {
  const name = err?.name ?? '';
  if (name === 'TimeoutError' || name === 'AbortError')
    return new LiveFailure('timeout', `no answer within ${REQUEST_TIMEOUT_MS}ms`);
  return new LiveFailure('offline', err?.message ? String(err.message) : 'no connection');
};

/** One HTTP round trip. Transport problems throw; HTTP statuses do not. */
const call = async (method, path, body) => {
  const ac = new AbortController();
  const timer = setTimeout(() => ac.abort(), REQUEST_TIMEOUT_MS);
  let r;
  try {
    r = await fetch(path, {
      method,
      headers: body === undefined ? undefined : { 'Content-Type': 'application/json' },
      body: body === undefined ? undefined : JSON.stringify(body),
      signal: ac.signal,
    });
  } catch (err) {
    throw transport(err);
  } finally {
    clearTimeout(timer);
  }
  return r;
};

/** The server's own words, when it gives any. */
const reason = async (r) => {
  try {
    const j = await r.clone().json();
    if (j?.error) return String(j.error);
  } catch { /* the body was not JSON; the status line will do */ }
  return `${r.status} ${r.statusText}`;
};

/**
 * The weapon the live simulator puts in the character's hands.
 *
 * Two different things ride on a weapon and both must be set, so they are named
 * separately here rather than one field doing both jobs:
 *
 *   * `equippedWeapon` — the WZ weapon image, read for its `info/attack` integer
 *     to pick the swing stance group. Unarmed means NO swing pose at all
 *     (CharLook.cpp:594 returns before the stance table is indexed), so without
 *     this the attack button moves the hit box and nothing else, which reads as
 *     a broken attack.
 *   * `weapon` — the Afterimage image the hit box is measured from. Left unset
 *     the server falls back to barehands.img, which is the reference's own
 *     unarmed box, so that half is genuinely optional.
 *
 * 01302000 is a plain sword whose `info/attack` reads 1 out of this archive's
 * Weapon.nx (measured through /api/node), which selects
 * {stabO1, stabO2, swingO1, swingO2, swingO3} — all five confirmed present in
 * Character.nx via /api/simulate/look/stances. Not a guess: the id was read out
 * of the archive, not picked because it looks like a sword.
 */
const LIVE_WEAPON = '01302000';

/** POST /api/simulate/session -- put a body on the map. */
const openSession = async ({ path, x = null, y = null, direction = 1, invulnerable = false }) => {
  const r = await call('POST', '/api/simulate/session',
    { path, startX: x, startY: y, direction, equippedWeapon: LIVE_WEAPON, invulnerable });
  if (r.status === 404) {
    // Both of these are a 404 and they mean opposite things:
    //
    //   * a map path that does not exist -- the server says so, with the name
    //     in the message: "'__nope__.img' not found under 'f1/Map/Map1'."
    //   * the endpoint not being in this build at all -- "The request was
    //     refused (404) and the server gave no reason."
    //
    // Reading only the status would call the first one a missing build and the
    // second one a bad map, and both answers are wrong. The message is the
    // only thing that tells them apart, so read it before deciding.
    const why = await reason(r);
    const isPath = /not found under|not found|找不到/i.test(why);
    throw new LiveFailure(isPath ? 'path' : 'route', why);
  }
  if (r.status === 400) throw new LiveFailure('path', await reason(r));
  if (!r.ok) throw new LiveFailure('server', await reason(r));
  const s = await r.json();
  if (s.error) throw new LiveFailure('path', String(s.error));
  return s;
};

/**
 * POST /api/simulate/session/{id}/weapon -- change what the body is holding.
 *
 * Separate from composing the LOOK because they answer different questions and the
 * swing poses are on the wrong side of the network boundary from the art. Which
 * stances a swing may use is the weapon's own `info/attack` integer read out of the
 * WZ (WeaponData.cpp:35) indexing a table (CharLook.cpp:648, :682-696) -- server-side
 * state. Equipping in the picker only changes the layers that get drawn. Without
 * this the figure can hold a sword while swinging with an axe.
 *
 * `.img` is stripped because the picker speaks the same dialect as every other
 * equip slot (`01302000.img`) and the server resolves the bare id.
 *
 * @param {string|null} id session id; a falsy one is a no-op rather than an error,
 *   because "no session yet" is the state every call before the first frame is in
 *   and the caller cannot tell it from "the session died".
 */
const setEquippedWeapon = async (id, image) => {
  if (!id) return null;
  const weapon = image ? String(image).replace(/\.img$/i, '') : null;
  const r = await call('POST', `/api/simulate/session/${id}/weapon`, { equippedWeapon: weapon });
  // Not a LiveFailure: a server without this route simply keeps whatever weapon it
  // had, which is strictly better than tearing the walk down over a cosmetic gap.
  if (!r.ok) return null;
  return r.json();
};

/**
 * POST /api/simulate/session/{id}/equip -- the WHOLE equipped set, as one replacement.
 *
 * A REPLACEMENT, not a patch, and the distinction is the server's reason for
 * designing the route that way: a client that posted only what changed would leave
 * it unable to tell "removed" from "never equipped", and the sheet it builds would
 * keep stat contributions from items that are off the character.
 *
 * `Head`, `Body`, `Hair-1` and `Face-1` are dropped here rather than server-side,
 * because they are the character's own images and not equipment; the server filters
 * them too, but a payload that already excludes them is honest about what it means.
 *
 * @param {string|null} id session id; falsy is a no-op, as above
 * @param {Record<string,string>} slots slot -> item image, `.img` optional
 * @returns {Promise<object|null>} the new frame, or null if the server declined
 */
const setEquipment = async (id, slots) => {
  if (!id || !slots) return null;
  const items = {};
  for (const [slot, image] of Object.entries(slots)) {
    if (!image) continue;
    if (slot === 'Head' || slot === 'Body' || slot === 'Hair-1' || slot === 'Face-1') continue;
    items[slot] = String(image).replace(/\.img$/i, '');
  }
  const r = await call('POST', `/api/simulate/session/${id}/equip`, { items });
  if (!r.ok) return null;
  return r.json();
};

/**
 * POST /api/simulate/session/{id}/summon -- put mobs on the map, MobForm.cs:107-124.
 *
 * The reference's form has two boxes and no other input: the mob id (picked from
 * the grid, so always a bare id) and a count (typed, MobForm.cs:112), and one
 * click spawns that many within a 200px window either side of the character
 * (MobForm.cs:116). Whatever lands outside the map's walkable extent is dropped
 * SILENTLY -- MobForm.cs:117 is an `if` with no `else`.
 *
 * That silence is why the answer carries `rejected`. SummonResult
 * (MapSimSessionStore.cs:757-775) is a benchmark addition, declared as such in
 * its own remarks: the reference cannot tell you that 3 of 5 were dropped, so a
 * client that copied it verbatim would leave the user believing five appeared.
 * The strings arrive from the server already carrying the reason.
 *
 * `.img` is stripped for the same reason the weapon helper strips it: MobForm.cs:109
 * concatenates `"Mob/" + MobID + ".img"`, so the id is the part in between, and a
 * caller who pastes a whole image path gets the reference's own silent `return`
 * (no mob, no complaint) -- which is exactly the failure `rejected` now reports.
 *
 * @param {string|null} id session id; a falsy one is a no-op rather than an error,
 *   because "no session yet" is the state every call before the first frame is in
 *   and the caller cannot tell it from "the session died".
 * @param {string} mobId e.g. `100100`
 * @param {number} count clamped by the server to 1..64 (SimulatorEndpoints.cs:300)
 * @param {string|null} kind `m` or `n`; null lets the archive decide
 */
const summonMobs = async (id, mobId, count = 1, kind = null) => {
  if (!id) return null;
  const mob = String(mobId ?? '').trim().replace(/\.img$/i, '');
  const r = await call('POST', `/api/simulate/session/${id}/summon`, { mobId: mob, count, kind });
  // Not a LiveFailure, for the same reason the weapon route is not one: a server
  // without this route has simply never heard of mobs, and that is strictly
  // better than tearing down a walk that is otherwise fine.
  if (!r.ok) return null;
  return r.json();
};

/**
 * POST /api/simulate/session/{id}/despawn -- MobForm.cs:126-143, the Remove button.
 *
 * The reference walks the whole sprite list and kills every mob whose LocalID is
 * in `Mob.SummonedList` (MobForm.cs:132-139). Here the server tracks summons by
 * INDEX rather than by id (MapSimSessionStore.cs:300-320), because a map's own
 * `life` entries can name the same id -- twelve mobs sharing one id is ordinary
 * -- and matching by id would take the map's own spawns with the summons. The
 * count that comes back is that index set's size, so it says what was removed
 * here rather than what the reference's looser match would have removed.
 *
 * Same tolerance as the weapon helper and for the same reasons: no session is a
 * no-op, and a 404 from a build without the route leaves the mobs standing
 * instead of ending the walk.
 *
 * @param {string|null} id session id
 */
const despawnMobs = async (id) => {
  if (!id) return null;
  const r = await call('POST', `/api/simulate/session/${id}/despawn`);
  if (!r.ok) return null;
  return r.json();
};

/**
 * GET /api/skill-cast/castable -- the skills the reference's SkillForm lists.
 *
 * The rows arrive already filtered: SkillForm.cs:85-137 walks every Skill/*.img
 * and keeps only what has `hit`, and either `level/1/lt` (pre-bb, :103) or
 * `common`+`common/lt` (:122-124), plus `effect` -- and then adds exactly three
 * cells, id / icon / name (:111 and :132). Nothing is filtered again here,
 * because the one rule this client could get wrong is the reference's own
 * `preBb` switch and the server already made that decision.
 *
 * @returns {Promise<{preBb:boolean, count:number, skills:Array<{skillId:string, bookId:string, iconPath:string, name:string}>}|null>}
 */
const castableSkills = async () => {
  const r = await call('GET', '/api/skill-cast/castable');
  if (!r.ok) return null;
  return r.json();
};

/**
 * POST /api/simulate/session/{id}/skills -- replace the whole hotkey list.
 *
 * A REPLACEMENT and not a patch, which is the reference's own shape rather than
 * a REST convenience: SkillForm.cs:168-174 rebuilds `Skill.HotKeyList` from
 * scratch on every combo change -- `Clear()`, then one `AddOrReplace` per row of
 * the Use List grid. The grid it rebuilds from is the full list, so every change
 * is a whole list. A patch endpoint would be a second way to say the same thing
 * and the reference has only one.
 *
 * `rejected` is the server's answer about the rows it would not take, already
 * worded (for example a key that is not one of the 26 the reference's combo
 * offers -- SkillForm.Designer.cs:50). The caller shows them; it never invents
 * its own reason, because the two exclusions the reference enforces
 * (SkillForm.cs:156-162 -- one key per skill, one skill per key) are applied
 * THERE and only there.
 *
 * Same tolerance as the weapon and summon helpers: no session is a no-op, and a
 * build without the route leaves the body walking rather than ending the walk.
 *
 * @param {string|null} id session id
 * @param {Array<{skillId:string, key:string}>} bindings the WHOLE list
 */
const setSkillBindings = async (id, bindings) => {
  if (!id) return null;
  const rows = (bindings ?? []).map((b) => ({ skillId: String(b.skillId), key: String(b.key) }));
  const r = await call('POST', `/api/simulate/session/${id}/skills`, { bindings: rows });
  if (!r.ok) return null;
  return r.json();
};

/**
 * GET /api/skill-cast/plan?skillId= -- what the archive says about a cast.
 *
 * Read-only and session-free, which is what makes it usable as a PRELOAD: the
 * effect sprite list can be fetched before any cast exists, so the art is already
 * decoded when the user presses a key (W-077).
 */
const getSkillCastPlan = async (skillId) => {
  if (!skillId) return null;
  const r = await call('GET', `/api/skill-cast/plan?skillId=${encodeURIComponent(skillId)}`);
  if (!r.ok) return null;
  return r.json();
};

/**
 * POST /api/simulate/session/{id}/skills/clear -- drop every binding.
 *
 * Not the same as posting an empty list, and the distinction is the reference's:
 * SkillForm.cs:168 clears the dictionary on every change, but a form the user
 * closed (:184-187, which only hides the tooltip and lets the form itself hide
 * at :44) leaves `Skill.HotKeyList` alive in the static `Skill` -- it is
 * session state, not a setting. So this is the explicit route the server
 * offers, and clearing is reported back with a count so the panel can say what
 * was actually dropped.
 *
 * @param {string|null} id session id
 * @returns {Promise<{cleared:number}|null>}
 */
const clearSkillBindings = async (id) => {
  if (!id) return null;
  const r = await call('POST', `/api/simulate/session/${id}/skills/clear`);
  if (!r.ok) return null;
  return r.json();
};

/** POST /api/simulate/session/{id}/frame -- one physics step. */
const stepFrame = async (id, input) => {
  const r = await call('POST', `/api/simulate/session/${id}/frame`, input);
  if (r.status === 404)
    throw new LiveFailure('session', `the server has no live character ${id}`);
  if (r.status === 400) throw new LiveFailure('path', await reason(r));
  if (!r.ok) throw new LiveFailure('server', await reason(r));
  const f = await r.json();
  if (f.error) throw new LiveFailure('server', String(f.error));
  return f;
};

/**
 * N physics steps for the price of one round trip.
 *
 * The server runs the same Step() the one-frame route runs, once per count, in
 * order, at the same fixed 1/60s step -- so this is a transport change and not
 * a change in what is simulated. It matters because a browser catch-up of 8
 * frames cost 8 round trips, and the time those cost was being discarded by the
 * caller's clock, so the body stepped slower the more expensive its frames were.
 *
 * @param {string} id session id
 * @param {{count:number, first:object, then:object}} payload
 */
const stepFrames = async (id, payload) => {
  // NOT `manual: true`, deliberately.
  //
  // The measurement suites ask for it, because they need the server to stop
  // advancing the session behind their back (W-089: a 250 ms lease is a guess,
  // and a caller that thinks for 200 ms gets ten frames injected).
  //
  // This client must NOT ask for it, because this client is a TAB: a tab that is
  // not focused has its timers throttled to about 1 Hz, and with the server's
  // own clock stopped the simulation would advance one frame per second instead
  // of sixty. The lease is renewed every ~16 ms while the tab is active, so it
  // never expires there, and when the tab IS throttled the server's free clock
  // is what keeps the world moving -- which is what a person watching an editor
  // expects.
  const r = await call('POST', `/api/simulate/session/${id}/frames`, payload);
  // A server without the batch route answers 404 exactly as it does for a dead
  // session. Falling back to the one-frame route keeps an older build walking
  // rather than reporting a session that is still perfectly alive.
  if (r.status === 404 && !(await reason(r)).match(/no live character/)) {
    let f = null;
    for (let i = 0; i < payload.count; i++) {
      f = await stepFrame(id, i === 0 ? payload.first : payload.then);
    }
    return f;
  }
  if (r.status === 404)
    throw new LiveFailure('session', `the server has no live character ${id}`);
  if (r.status === 400) throw new LiveFailure('path', await reason(r));
  if (!r.ok) throw new LiveFailure('server', await reason(r));
  const f = await r.json();
  if (f.error) throw new LiveFailure('server', String(f.error));
  return f;
};

// Ending a body is a DELETE, not a POST: the route is
// MapDelete("/session/{id}") (SimulatorEndpoints.cs) and there is no
// /session/{id}/end at all. POSTing to a path nothing answers is not a
// harmless no-op -- it throws, and the catch below swallows it, so the body
// stayed in the MapSimSessionStore singleton until the server exited.
const del = async (path) => {
  const r = await call('DELETE', path);
  if (!r.ok) throw new Error(`${r.status} ${r.statusText}`);
  return r.json();
};

/**
 * Bring a dead character back at the map's spawn portal.
 *
 * `POST /api/simulate/session/{id}/revive`, answered by
 * `MapSimSessionStore.Revive` — `Stage::respawn(portalid)` /
 * `Player::respawn(pos, underwater)` (Stage.cpp:497-508, Player.cpp:210-251).
 */
const reviveSession = async (id) => {
  const r = await call('POST', `/api/simulate/session/${id}/revive`, {});
  if (r.status === 404) throw new LiveFailure('session', `the server has no live character ${id}`);
  if (!r.ok) throw new LiveFailure('server', await reason(r));
  const f = await r.json();
  if (f.error) throw new LiveFailure('server', String(f.error));
  return f;
};

/**
 * Turn damage-taking on or off for a live character.
 *
 * `POST /api/simulate/session/{id}/invulnerable` — `player.is_invincible()`
 * (UIStateGame.cpp:794 for the damage path, Player.cpp:1252 for the attack one).
 *
 * It was a START parameter only and nothing a user does could reach it, so on a
 * mob map the character was dead within seconds and every mechanic past "the mob
 * touches you" was unobservable.
 */
const setInvulnerable = async (id, value) => {
  const r = await call('POST', `/api/simulate/session/${id}/invulnerable`, { value });
  if (r.status === 404) throw new LiveFailure('session', `the server has no live character ${id}`);
  if (!r.ok) throw new LiveFailure('server', await reason(r));
  return r.json();
};

export class LiveWalk {
  /**
   * @param {import('./walkview.js').WalkViewer} view a loaded map view to draw
   */
  constructor(view) {
    this.view = view;
    // Registered back onto the viewer so the equip bar can reach the session
    // without either side importing the other: `chooseEquip` changes the art, and
    // the swing stances live on the server behind this id.
    if (view) view.live = this;
    this.sessionId = null;
    this.state = null;
    // One revive in flight at a time. The button stays enabled and the key handler
    // is wired to the same call, and a double revive would put the body at the
    // portal twice while the camera is still catching up with the first.
    this.reviving = false;
    this.keys = { left: false, right: false, up: false, down: false, jump: false, attack: false };
    // Damage numbers from the last walk, cleared wherever the keys are: a body
    // that has just been placed somewhere new should not arrive trailing the
    // last map's fight still rising off its head.
    this.damage = [];
    this.trail = [];
    this.accumulator = 0;
        this.last = 0;
        this.running = false;
        // Bumped by stop(). `tick` is async and parks on `await stepFrames()`, so a
        // tick can still be in flight when stop() runs and start() has already set
        // `running` back to true. Such a stale tick would sail past the
        // `if (this.running)` check at the bottom of tick() and queue another frame,
        // leaving an orphaned rAF loop that no later stop() can cancel -- one extra
        // 60Hz /frames poller per map switch, which is what wedged the renderer
        // after a few switches. The generation is captured on entry and compared on
        // the way out, so a tick that spans a stop/start schedules nothing.
        this.gen = 0;
        this.error = '';
    this.busy = false;
    // Jump is edge-triggered, not a held level: the client sets DoJump on the
    // frame the key goes down (MapleCharacter.cs:775-777), so holding it must
    // not produce a hop every frame. Track the previous frame's value.
    this.jumpLatch = false;
    // Initialised for the same reason and with the same shape as jumpLatch
    // above. `attack` is read as an edge (`this.keys.attack &&
    // !this.attackLatch`), and `!undefined` is true -- so without this a
    // key held from before start(), or a batch that throws before the latch
    // is written, both produce an unsolicited swing on the next frame.
    this.attackLatch = false;

    // key -> skill id, in the server's own direction. Skill.HotKeyList is
    // `Dictionary<Input, string>` (Skill.cs:19) -- key first -- and Skill.cs:324-330
    // reads it that way: for each key held, cast the id it maps to. So the
    // client's map is the same map, and `keys` above is free to keep its own
    // movement meanings without the two tables ever having to agree on a shape.
    //
    // Set ONLY from the server's own answer (see setSkills below), never from
    // what the user picked: the reference drops a duplicate before it reaches
    // the dictionary (SkillForm.cs:156-162), so the local list and the effective
    // one are allowed to differ, and only the server's copy is the truth.
    this.skillKeys = new Map();
    // The same edge discipline as attackLatch, for the same reason. A cast is
    // NOT a held level: Skill.cs:321 reads the hotkey inside a per-tick
    // `KeyDown` sweep, so a held key in the reference fires once per cast --
    // and the reference's own gate for a second one is `Skill.PlayEnded`, which
    // the cast sets false (:157) and the animation clears (:410). Without a
    // latch here a held key would re-cast on every 1/60s frame the lock is down
    // for, which is a flurry of requests the server then throws away.
    this.skillLatch = false;
    // W-075: which cast the effect animation was last started for. See paint().
    this.skillFxCastSeq = -1;
    // The hotkey that is down RIGHT NOW, as an id, or ''. A level rather than a
    // one-shot flag, because the reference is a per-tick `KeyDown` sweep
    // (Skill.cs:321-330) rather than an edge: hold the key and the reference
    // casts again the moment the previous animation ends, with no second
    // keydown. Modelling it as a one-shot would make holding the key strictly
    // weaker than tapping it, which is backwards.
    this.skillDown = '';

    // How this body was placed, kept because a replacement has to be placed the
    // same way and that is the one thing start() knows and the reconnect path
    // cannot work out for itself.
    this.opts = null;
    // 'idle' no body has ever been asked for
    // 'live' this.sessionId answers frames
    // 'lost' there is nothing to steer; retryAt says when to try again
    this.link = 'idle';
    this.failure = '';
    this.attempts = 0;
    this.retryAt = 0;
    this.retryMs = 250;

    this.onKeyDown = this.onKeyDown.bind(this);
    this.onKeyUp = this.onKeyUp.bind(this);
    this.tick = this.tick.bind(this);
  }

  /** Put a character on the map and start listening. */
  async start({ path, x = null, y = null, direction = 1 }) {
    this.stop();
    this.error = '';
    this.failure = '';
    this.opts = { path, x, y, direction };
    try {
      const s = await this.place();
      // The handover. From here this body writes viewer.ticks and viewer.frame
      // itself, at 60Hz, out of server-held state -- so anything else that wrote
      // them has to let go now, not "when the next frame lands". loadWalk is
      // the other writer, and it left its play/seek strip on screen as well;
      // both go, or the strip drives a frame counter this body is moving.
      this.view?.releaseWalk();
      this.attach();
      this.running = true;
      this.last = performance.now();
      this.accumulator = 0;
      this.raf = requestAnimationFrame(this.tick);
      this.paint();
      return s;
    } catch (err) {
      this.error = err.message;
      this.failure = err.kind ?? '';
      this.link = 'idle';
      this.paint();
      throw err;
    }
  }

  /**
   * Bring the character back at the map's spawn portal.
   *
   * `Stage::respawn(portalid)` / `Player::respawn(pos, underwater)` —
   * Stage.cpp:497-508 and Player.cpp:210-251, reached from the revive prompt at
   * UIStateGame.cpp:548-556.
   *
   * This exists because a death used to be terminal here. MEASURED on
   * f4/Map/Map0/000000000.img: a 50 HP character killed on frame 0 by 4220000
   * could not swing, could not cast a skill, could not walk, and had nothing to
   * come back to — which is exactly what "the player cannot attack" looks like
   * from the outside, and nothing on screen said the character was dead.
   */
  /**
 * Turn damage-taking on or off, and remember it for the next session on this map.
 *
 * Deliberately remembered: a simulator that has to be told "don't kill me" again
 * every time the map is re-opened is one nobody switches on, and the mechanics
 * being watched are the ones after the first touch.
 */
async setInvulnerable(value) {
    this.opts.invulnerable = value;
    if (!this.sessionId) return null;
    try {
      const f = await setInvulnerable(this.sessionId, value);
      this.state = f;
      return f;
    } catch (err) {
      this.error = String(err?.message ?? err);
      return null;
    }
  }

  async revive() {
    if (!this.sessionId || this.reviving) return null;
    this.reviving = true;
    try {
      const f = await reviveSession(this.sessionId);
      this.state = f;
      // The trail is where the body has been; a revive puts it somewhere else on
      // the map, so the line drawn through it would jump across the map.
      this.trail = [{ x: f.x, y: f.y }];
      this.damage = [];
      this.failure = '';
      // The walk's own rAF loop redraws and re-renders the HUD, so the banner
      // disappears on the next frame without this having to poke anything.
      return f;
    } catch (err) {
      this.failure = 'revive';
      this.error = String(err?.message ?? err);
      return null;
    } finally {
      this.reviving = false;
    }
  }

  /**
   * Open a session and adopt it.
   *
   * Deliberately touches NEITHER running NOR the key listeners NOR the trail: a
   * replacement is the same character on the same map, and it is placed where
   * the old one stood. Detaching here is what would silently throw away a
   * direction key the user is still holding.
   */
  async place() {
    const o = this.opts;
    const s = await openSession({
      path: o.path,
      x: o.x ?? null,
      y: o.y ?? null,
      direction: o.direction ?? 1,
      // Remembered by `setInvulnerable`; a map re-opened keeps the setting, which
      // is the whole point of having one.
      invulnerable: !!o.invulnerable,
    });
    this.sessionId = s.sessionId;
    this.state = s;
    if (this.trail.length === 0) this.trail = [{ x: s.x, y: s.y }];
    this.link = 'live';
    this.failure = '';
    this.attempts = 0;
    this.retryMs = 250;
    this.retryAt = 0;
    this.error = '';
    return s;
  }

  /**
   * Write the body's last position and facing into opts so the next session is
   * opened there. Without this a reconnect teleports the character back to the
   * spawn portal, which is the other half of "it stopped, and then it jumped".
   *
   * Server-side Start() takes startX/startY and snaps to the foothold under
   * them (FindStart, falling back to FindBelow), the same probe the spawn uses
   * two pixels higher up -- so a body that was standing on a foothold comes
   * back standing on it, and one that was in the air comes back above where it
   * was rather than on the map's default portal.
   */
  rememberPlace() {
    const s = this.state;
    if (!s || !this.opts) return;
    if (Number.isFinite(s.x)) this.opts.x = Math.round(s.x);
    if (Number.isFinite(s.y)) this.opts.y = Math.round(s.y);
    if (Number.isFinite(s.facing)) this.opts.direction = s.facing >= 0 ? 1 : -1;
  }

  /**
   * Re-place a body that is not there any more, or wait out a server that is
   * not answering. This is the path the old file did not have: a 404 used to
   * end the walk for good.
   */
  async reconnect() {
    this.rememberPlace();
    try {
      await this.place();
      return true;
    } catch (err) {
      const kind = err?.kind ?? 'server';
      this.failure = kind;
      this.attempts++;
      if (FATAL_FAILURES.has(kind)) {
        this.error = err.message;
        this.link = 'idle';
        this.running = false;
        return false;
      }
      // Transient. Report the waiting in status() instead of stranding the
      // last frame's error on screen, and come back with a wider gap each time
      // so a server that stays down costs one request every few seconds rather
      // than sixty a minute.
      this.error = '';
      this.link = 'lost';
      this.retryAt = performance.now() + this.retryMs;
      this.retryMs = Math.min(4000, this.retryMs * 2);
      return false;
    }
  }

  /** Stop stepping and let the server drop the body. */
  stop() {
      this.running = false;
      // Retire any tick that is already parked on a network await: it will resume
      // after this call and would otherwise queue a frame on its way out.
      this.gen += 1;
      if (this.raf) cancelAnimationFrame(this.raf);
    this.raf = null;
    this.detach();
    this.link = 'idle';
    this.failure = '';
    this.attempts = 0;
    this.retryAt = 0;
    if (this.sessionId) {
      const id = this.sessionId;
      this.sessionId = null;
      del(`/api/simulate/session/${id}`).catch(() => {});
    }
  }

  /**
   * Hand the server the weapon the equip bar just put on, so the swing poses match
   * the sword being drawn.
   *
   * No-op when there is no session yet, or when the server has no such route: the
   * figure keeps whatever weapon the session started with, which degrades to the
   * old behaviour instead of failing the walk.
   *
   * @param {string|null} image e.g. `01302000.img`; null/empty disarms
   * @returns {Promise<object|null>} the new frame, or null if nothing changed
   */
  async setWeapon(image) {
    const frame = await setEquippedWeapon(this.sessionId, image);
    if (frame) this.state = frame;
    return frame;
  }

  /**
   * Replace the whole equipped set, so the sheet the server keeps matches what
   * the character is wearing.
   *
   * `setWeapon` above is still the route for the WEAPON alone, because the weapon
   * is also a physics fact -- its `info/attack` selects the swing stance group
   * (WeaponData.cpp:35 -> CharLook.cpp:648) -- and that path is exercised by every
   * existing harness. This one carries the rest of the armour, which changes
   * accuracy, max HP and max damage and nothing about the pose.
   *
   * @param {Record<string,string>} slots slot -> item image
   * @returns {Promise<object|null>} the new frame, or null if nothing changed
   */
  async setEquipment(slots) {
    const frame = await setEquipment(this.sessionId, slots);
    if (frame) this.state = frame;
    return frame;
  }

  /**
   * Summon mobs around the body -- the reference's MobForm.cs:107-124 button.
   *
   * `this.state` is DELIBERATELY NOT touched here, and that is the one place this
   * differs from setWeapon above. The summon answer is a SummonResult
   * (MapSimSessionStore.cs:757-775) and despawn's is a bare `{removed}` -- neither
   * carries a frame, and neither carries `mobs`. Writing `this.state = r.frame`
   * would therefore assign `undefined` and the walk would stop: paint() guards on
   * `if (this.state)` (livewalk.js:827) and would hand the viewer `ticks: []`,
   * which is the same blank-canvas-and-no-body failure the 404 path took a whole
   * reconnect policy to avoid. The mobs a summon creates appear on the NEXT
   * frame, because stepFrames() is what carries `mobs` (livewalk.js:848).
   *
   * @param {string} mobId bare id, e.g. `100100`; `.img` is stripped
   * @param {number} count
   * @returns {Promise<object|null>} the SummonResult, or null when nothing ran
   */
  async summon(mobId, count = 1) {
    return summonMobs(this.sessionId, mobId, count);
  }

  /**
   * Remove every summoned mob and leave the map's own spawns alone --
   * MobForm.cs:126-143.
   *
   * `this.state` is untouched for the same reason as in summon() above: the
   * answer is `{removed}` and carries no frame. The mobs stop drawing on the next
   * frame the loop fetches, because the server killed them there.
   *
   * @returns {Promise<object|null>} `{removed}`, or null when nothing ran
   */
  async despawn() {
    return despawnMobs(this.sessionId);
  }

  /**
   * The skills the reference's SkillForm would list -- SkillForm.cs:51's grid of
   * id / icon / name, pre-filtered by :85-137.
   *
   * Lazy by construction: the caller asks on first open and caches. Nothing in
   * the walk needs this list, and the reference builds its grid inside
   * `SkillForm_Shown` (:41-137) rather than at startup -- a cost paid when the
   * form opens, not when the client launches.
   *
   * @returns {Promise<object|null>} `{preBb, count, skills}`, or null if unasked
   */
  async castable() {
    return castableSkills();
  }

  /**
   * Replace the whole hotkey list -- SkillForm.cs:168-174.
   *
   * `this.skillKeys` is written from the RESPONSE, never from `bindings`. The
   * server applies the two exclusions the reference enforces in
   * `comboBox1_SelectedIndexChanged` (SkillForm.cs:156-162 -- a skill may hold
   * one key, a key one skill) and answers with what survived; the client keeps
   * no second opinion. That is also what makes the edge in onKeyDown correct:
   * a key the server refused simply is not in `skillKeys`, so it is not a hotkey.
   *
   * A route the server does not have answers null, and the caller's bindings are
   * then left alone -- a panel that emptied itself because the endpoint was
   * absent would be worse than one that quietly did not work.
   *
   * @param {Array<{skillId:string, key:string}>} bindings the WHOLE list
   * @returns {Promise<object|null>} `{bindings, count, rejected}`, or null
   */
  async setSkills(bindings) {
    const r = await setSkillBindings(this.sessionId, bindings);
    if (!r) return null;
    const map = new Map();
    // The server answers with its own `{key: skillId}` (Skill.HotKeyList's
    // direction, Skill.cs:19). Read that, not the request: `rejected` exists
    // precisely because the two can differ.
    const answer = r.bindings ?? {};
    for (const [key, id] of Object.entries(answer)) map.set(String(key).toUpperCase(), String(id));
    this.skillKeys = map;
    // The key the user is holding down is no longer a trigger if the change
    // unbound it, so the next keydown of it must not be swallowed by a latch
    // left true from the previous cast.
    this.skillLatch = false;
    // W-077. Warm the bound skills' effect art NOW.
    //
    // `drawSkillEffects` skips any frame whose image has not decoded yet
    // (`naturalWidth === 0`) — correctly, because drawing an undecoded image is a
    // silent no-op. But the art is fetched lazily, ON THE FIRST CAST, and an
    // <img> cannot decode before its bytes arrive. So the first cast of every
    // skill played with nothing on screen, and the user read it as "skills do
    // not show effects".
    //
    // Confirmed in a real browser 2026-10-17: after binding, the view's cache
    // held the nine /frames images and every one reported naturalWidth 0 until
    // the request completed; only a SECOND cast drew anything.
    //
    // The fix is not to draw undecoded images but to have them ready before the
    // user presses anything.
    for (const id of Object.values(answer)) this.warmSkillArt(id);
    return r;
  }

  /**
   * Preload one skill's effect sprites into the view's own image cache.
   *
   * Fire and forget on purpose: this runs on a panel interaction, and making the
   * panel wait for a round of PNGs would make binding feel slow to solve a
   * problem nobody should ever notice.
   */
  async warmSkillArt(skillId) {
    const view = this.view;
    if (!view) return;
    try {
      const r = await getSkillCastPlan(skillId);
      const cache = (view.skillFx ??= new Map());
      for (const fx of r?.effects ?? []) {
        if (!fx?.imagePath) continue;
        const container = fx.imagePath.replace(/\/[^/]+$/, '');
        if (cache.has(container)) continue;
        const n = Math.max(1, fx.frames | 0);
        const imgs = [];
        for (let i = 0; i < n; i++) {
          const img = new Image();
          img.src = `/api/canvas?path=${encodeURIComponent(`${container}/${i}`)}`;
          imgs.push(img);
        }
        cache.set(container, imgs);
      }
    } catch {
      // A cache warm-up that fails is not a binding failure. The first cast simply
      // behaves the way it did before this existed.
    }
  }

  /**
   * Drop every binding -- the server's /skills/clear.
   *
   * The map is emptied HERE rather than only on the server, because the hotkey
   * is read on this side every keydown: a panel that cleared the list while the
   * key still cast would be a control that says it stopped and did not.
   *
   * @returns {Promise<{cleared:number}|null>}
   */
  async clearSkills() {
    const r = await clearSkillBindings(this.sessionId);
    if (!r) return null;
    this.skillKeys = new Map();
    this.skillLatch = false;
    return r;
  }

  attach() {
    window.addEventListener('keydown', this.onKeyDown);
    window.addEventListener('keyup', this.onKeyUp);
  }

  detach() {
    window.removeEventListener('keydown', this.onKeyDown);
    window.removeEventListener('keyup', this.onKeyUp);
    this.keys = { left: false, right: false, up: false, down: false, jump: false, attack: false };
    // Damage numbers from the last walk, cleared wherever the keys are: a body
    // that has just been placed somewhere new should not arrive trailing the
    // last map's fight still rising off its head.
    this.damage = [];
    this.jumpLatch = false;
    this.attackLatch = false;
    // The held hotkey goes with the other keys. `skillKeys` does NOT: the
    // bindings belong to the session the panel posted them to, and a body that
    // has been stopped and started again is the same session on the same map.
    this.skillDown = '';
    this.skillLatch = false;
  }

  onKeyDown(e) {
    // W-081. THE VIEWER'S BOOKMARKS own 1/2/3, before anything else.
    //
    // Before the hotkey, because a skill may be bound to a digit -- the panel
    // offers A through Z (SkillForm.Designer.cs:50), so a digit is not a hotkey
    // the panel can produce, but a hand-edited binding could still shadow one and a
    // bookmark that can be stolen is not a bookmark. Before the walk keys, because
    // `keyFor()` maps letters, not digits, and would otherwise ignore them
    // silently -- a bookmark that does nothing reads as a broken one.
    //
    // The viewer's own guard decides whether this is a bookmark at all: a digit
    // typed into the mob picker's search box is a digit, not a navigation, and the
    // guard is the same field test `skillFor()` uses.
    if (this.view?.viewerBookmarkKey?.(e)) return;
    // The hotkey is read FIRST, before the movement guard below. It has to be:
    // `keyFor()` answers null for every key that is not a walk/jump/attack key,
    // and a bound hotkey is by definition not one -- so a check placed after
    // `if (!k) return;` could never see a hotkey at all. skillFor() re-tests
    // keyFor() itself, so admitting a hotkey here cannot take a key the walk
    // already owns.
    const skill = this.skillFor(e);
    if (skill) {
      // W-065. THE ID, NOT THE LETTER.
      //
      // This line read `skill.key` — the bound KEY — and the frame loop then
      // sent it as the `skillId` field (:1158-1173). The server reads that field
      // with `int.TryParse` (SkillCastService.Plan, :231) and returns null for
      // anything that is not a number, so `CastSkill` returned at
      // `if (plan is null) return;` (MapSimSessionStore.cs:1479) EVERY time.
      // Measured against the running server, same endpoint, same frame:
      //
      //   {"skillId": "A"}        -> action='stand'  skillId=''             no cast
      //   {"skillId": "1121008"}  -> action='skill'  skillStance='brandish1' cast
      //
      // `skillFor` has always returned BOTH (`:1009 return { key, skillId }`),
      // and `skillId` was computed and then never read by anything. The binding
      // itself succeeds with `rejected: []`, so the panel reports the key bound
      // and pressing it does nothing, with no error anywhere on screen.
      //
      // This is the half W-050 could not prove: "that pressing a bound key now
      // casts... a browser keystroke is not something an HTTP suite can send."
      // Every server-side suite passed throughout, because they all post the
      // numeric id directly and never go through this line.
      this.skillDown = skill.skillId;
      this.skillLatch = false;
      return;
    }

    // Do not steal the editor's own shortcuts, and never scroll the page out
    // from under the map while someone is walking.
    const k = this.keyFor(e.key);
    if (!k) return;

    // Ctrl is attack, and it has to be admitted BEFORE the modifier guard
    // below, because pressing Ctrl sets e.ctrlKey on its own keydown -- the
    // guard would otherwise reject the very key it exists to allow.
    //
    // Only the bare Control keydown is taken, never a combo. Ctrl+something is
    // the editor's and the browser's (app.js:2845-2863 binds Ctrl+P, Ctrl+S,
    // Ctrl+Shift+F and Ctrl+Shift+W), and swallowing those to make a swing
    // would be a worse trade than asking for a different key. By the time a
    // modifier is held with another key, e.key is that other key, so the
    // `e.key === 'Control'` test alone already excludes every combo.
    if (e.key === 'Control') {
      const target = e.target;
      if (target && (target.tagName === 'INPUT' || target.tagName === 'TEXTAREA')) return;
      e.preventDefault();
      this.keys.attack = true;
      return;
    }

    if (e.ctrlKey || e.metaKey || e.altKey) return;
    const target = e.target;
    if (target && (target.tagName === 'INPUT' || target.tagName === 'TEXTAREA')) return;
    e.preventDefault();
    this.keys[k] = true;
  }

  onKeyUp(e) {
    const k = this.keyFor(e.key);
    if (!k) {
      // A hotkey is not a movement key, so keyFor() cannot clear it. Without
      // this, a tap would leave `skillDown` armed and a cast would fire long
      // after the key came up -- which is exactly the auto-repeat a latch is
      // there to stop.
      const hot = this.skillFor(e);
      if (hot) this.skillDown = '';
      return;
    }
    this.keys[k] = false;
  }

  /**
   * The skill `key` is bound to on this body, or null.
   *
   * Everything that could make this unsafe is rejected here rather than at the
   * call site, because there are two call sites (onKeyDown and onKeyUp) and a
   * guard that lives in only one of them is a guard that leaks.
   *
   *   * A key the walk already owns is never a hotkey. `keyFor()` binds the four
   *     ARROWS, space and Z -- and deliberately not WASD, see its own comment and
   *     clean-client `IO/Input/Keyboard.cpp:78-81`. The reference's twenty-six
   *     skill letters (SkillForm.Designer.cs:50) are free precisely because it
   *     does not spend them on movement, so binding `W` and finding that it
   *     casts is correct, while binding an arrow and finding that walking
   *     stopped would be a broken control with no error anywhere. The panel says
   *     so rather than hiding it.
   *
   *     (This paragraph used to claim `keyFor()` "maps WASD, the arrows, space
   *     and Z" and cited livewalk.js:783-800. Both halves went false when WASD
   *     was removed from `keyFor()`; the stale text survived because the one
   *     suite that would have noticed could not report a failure to the gate.
   *     W-056.)
   *
   *   * Ctrl and the other modifiers are the editor's, per onKeyDown's own
   *     reasoning above (app.js:2845-2863), so they never cast either. `e.key`
   *     is the letter, not the modifier, when a combo is held -- which means
   *     this test is what keeps Ctrl+D from being both "open the palette" and
   *     "cast D".
   *
   * The key is normalised to upper case because the reference's combo is
   * upper-case by construction: SkillForm.Designer.cs:50 lists "A".."Z" and
   * `Enum.Parse(..., true)` at SkillForm.cs:173 is case-insensitive, so "a"
   * and "A" are the same `Input` value there. Treating them as two keys would
   * make a shift a second hotkey.
   *
   * @returns {{key:string, skillId:string}|null}
   */
  skillFor(e) {
    if (e.ctrlKey || e.metaKey || e.altKey) return null;
    const target = e.target;
    // W-071. The field guard, narrowed to the case that actually needs it.
    //
    // Refusing every keydown whose target is a SELECT is right in general -- a
    // dropdown elsewhere in the editor should not fire a skill. It was wrong for
    // THIS dropdown: the hotkey panel is a grid of <select>s and contains no text
    // field at all, and the whole workflow is "pick a key here, press it there".
    // With the blanket rule, the key that was just bound was swallowed by the
    // control that bound it, and the panel stayed open on top of that. Nothing
    // errored. 26 green suites, a green end-to-end proof, and a skill that could
    // not be cast by a person.
    //
    // So the guard stands for every field that is not the hotkey panel's own
    // dropdowns, which are marked `data-skill-panel`.
    const inSkillPanel = typeof target?.closest === 'function'
      && target.closest('[data-skill-panel]');
    if (target && !inSkillPanel
        && (target.tagName === 'INPUT' || target.tagName === 'TEXTAREA'
            || target.tagName === 'SELECT')) return null;
    // `length !== 1` rejects the arrow and function keys in one test: the
    // reference's combo holds single characters only (SkillForm.Designer.cs:50),
    // so nothing else can ever be in `skillKeys` and this is just a guard
    // against a keyup/keydown pair disagreeing.
    if (typeof e.key !== 'string' || e.key.length !== 1) return null;
    const key = e.key.toUpperCase();
    if (this.keyFor(e.key)) return null;
    const skillId = this.skillKeys.get(key);
    if (!skillId) return null;
    return { key, skillId };
  }

  /**
   * Whether the body is locked inside a cast right now.
   *
   * The server refuses a second one (MapSimSessionStore.cs:636, `if
   * (session.SkillPoseTicks > 0) return;`) for the same reason the reference
   * does: Skill.cs:321 only reads the hotkeys when `Skill.PlayEnded`, and a
   * cast sets it false (:157) until the animation finishes (:410). So asking
   * while one is playing is a request the answer is already known for -- and
   * the frame loop skips it rather than spending a round trip on a no.
   *
   * Read off the last frame the server sent, which is the same state the server
   * itself is holding: `action` is "skill" for exactly the ticks the cast lock
   * runs down (MapSimSessionStore.cs:1436).
   */
  castBusy() {
    return this.state?.action === 'skill';
  }

  keyFor(key) {
    switch (key) {
      // THE REFERENCE'S MOVEMENT IS THE FOUR ARROW KEYS AND NOTHING ELSE.
      // clean-client `IO/Input/Keyboard.cpp:78-81`:
      //
      //     keymap[InputKey::LEFT]  = Mapping(KeyType::Id::ACTION, KeyAction::Id::LEFT);
      //     keymap[InputKey::RIGHT] = Mapping(KeyType::Id::ACTION, KeyAction::Id::RIGHT);
      //     keymap[InputKey::UP]    = Mapping(KeyType::Id::ACTION, KeyAction::Id::UP);
      //     keymap[InputKey::DOWN]  = Mapping(KeyType::Id::ACTION, KeyAction::Id::DOWN);
      //
      // `A`, `S`, `D` and `W` appear in that file's key LIST (:32) but are bound
      // to nothing — reading the list as the bindings is exactly the mistake that
      // put WASD here. WASD was this project's own addition, never declared as
      // one, and it cost something real: `skillFor` below checks `keyFor()` BEFORE
      // `skillKeys`, so a skill bound to A/D/W/S could never be pressed while the
      // panel reported it bound. The reference's twenty-six skill letters
      // (SkillForm.Designer.cs:50) are free precisely because it does not spend
      // them on movement.
      case 'ArrowLeft': return 'left';
      case 'ArrowRight': return 'right';
      case 'ArrowUp': return 'up';
      case 'ArrowDown': return 'down';
      case ' ': case 'Spacebar': return 'jump';
      // Z is the ONE remaining departure from the reference's key map, and it is
      // declared here rather than assumed. The reference attacks on LEFT_CONTROL
      // alone (MapleCharacter.cs:436-450 reading `KeyDown(LEFT_CONTROL)`), and
      // Ctrl cannot reach this handler by itself -- app.js owns Ctrl for the
      // palette, save and search, and a browser reserves Ctrl+W for "close tab" in
      // a way preventDefault does not reliably survive. Two keys for one action
      // beats one key that silently does nothing.
      //
      // It still costs something: Z is one of the reference's twenty-six skill
      // letters, so `MapSimSessionStore.MovementKeys` refuses a skill bound to it
      // rather than accepting a binding that could never fire. That exclusion is
      // why it is Z alone now and not five letters.
      //
      // Keyboard, not a mouse button, and Ctrl is the primary.
      // Not a click: the canvas is also where the map picker selects a tile,
      // and a swing that simultaneously picked a foothold would be two
      // commands on one button.
      case 'Control': return 'attack';
      case 'z': case 'Z': return 'attack';
      default: return null;
    }
  }

  /** One animation frame: ask for however many physics frames real time owes. */
  async tick(now) {
      if (!this.running) return;
      // Captured before the first await below; `stop()` bumps it, so a tick that
      // outlives its own start() finds a different number and does not reschedule.
      const gen = this.gen;
    // `this.last` must be stamped with the moment the time was TAKEN, not with
    // `now`. This loop awaits a network round trip per stepped frame (see the
    // batch below), so by the time it comes back for another turn, `now` is
    // stale and a fresh `Date.now()` here would silently drop every millisecond
    // the awaited frames cost.
    //
    // That is what makes a body that cannot jump: each turn measures only the
    // time since the PREVIOUS turn started, so a batch of N frames -- which took
    // N round trips -- is credited with one interval's worth of elapsed time.
    // The accumulator never reaches FRAME_MS, the body is stepped slower than
    // real time, and the jump edge, being one frame wide, is sampled inside a
    // batch that is not running at all.
    this.accumulator += Math.min(250, now - this.last);
    this.last = now;

    // A body with no session cannot be stepped, so re-place it before the walk
    // is owed a single frame. This runs on THIS loop rather than on a timer of
    // its own: a second interval would keep asking while the tab is hidden and
    // would need its own teardown in stop().
    //
    // The keys are deliberately NOT detached across the gap. The direction key
    // held at the moment the server collected the body is the direction still
    // being held, and the replacement keeps walking it -- which is the whole
    // point: the walk resumes instead of ending.
    if (this.link !== 'live' && this.opts && !this.busy && now >= this.retryAt) {
      this.busy = true;
      try {
        await this.reconnect();
      } finally {
        this.busy = false;
      }
      this.paint();
    }

    // Cap the catch-up. A tab that was backgrounded owes thousands of frames,
    // and replaying them all at once teleports the body through whatever it
    // walked over -- the very thing this view exists to rule out.
    let owed = Math.floor(this.accumulator / FRAME_MS);
    this.accumulator -= owed * FRAME_MS;
    if (owed > 8) owed = 8;

    if (owed > 0 && !this.busy && this.link === 'live' && this.sessionId) {
      this.busy = true;
      try {
        // One round trip for the whole catch-up, not one per 1/60s tick.
        //
        // Awaiting stepFrame() N times charged N round trips to N ticks, and
        // because `last` was stamped from a stale `now` on each turn, the time
        // those round trips actually cost was thrown away. The body then
        // stepped slower the more expensive its frames were, and at a steady
        // state stopped stepping altogether -- which reads as physics that will
        // not jump and mobs that walk in slow motion. The simulation was never
        // wrong; it was being handed fewer ticks than the wall clock spent.
        //
        // The server runs the same Step() the one-frame route runs, once per
        // count, in order, at the same fixed step. Nothing is approximated.
        const jump = this.keys.jump && !this.jumpLatch;
        // An edge, not a level, for the reason MapSimInput.Attack documents:
        // RegularAttack::execute fires on a timer crossing ATTACK_FP, so a held
        // key in the reference auto-swings at the weapon's speed. Here one press
        // is one swing, and the latch stops the rest of the batch from turning
        // one tap into a flurry.
        const attack = this.keys.attack && !this.attackLatch;
        // The same shape as `attack` above, for the same reason, and with one
        // more gate in front of it. `skillDown` is what is physically held
        // (set on keydown, cleared on keyup); `skillLatch` is what has already
        // been sent this press. The cast lock is skipped rather than sent:
        // the server refuses a cast while one is playing
        // (MapSimSessionStore.cs:636) exactly as the reference refuses it
        // (Skill.cs:321, `if (Skill.PlayEnded && !Game.Player.InLadder)`), so
        // asking mid-animation spends a round trip to be told no.
        //
        // Reading it off `skillDown` rather than off the keydown event is what
        // makes a HELD key behave like the reference's per-tick sweep: the
        // latch stops the rest of this batch, and the next batch -- which is
        // after the animation has ended -- casts again.
        const skill = this.skillDown && !this.skillLatch && !this.castBusy()
          ? this.skillDown
          : '';
        const held = {
          left: this.keys.left,
          right: this.keys.right,
          up: this.keys.up,
          down: this.keys.down,
          jump,
          attack,
          // Absent rather than empty on the no-cast case: the server reads
          // `SkillId is { Length: > 0 }` (MapSimSessionStore.cs:600), so null
          // and "" both mean "no cast", and sending the field only when there
          // is one keeps the request the same shape it was before this
          // existed.
          ...(skill ? { skillId: skill } : {}),
        };
        // Only the FIRST step sees the edges; the rest are a held state, which
        // is exactly what the key is by the time the batch runs.
        const rest = { ...held, jump: false, attack: false, skillId: null };

        const f = await stepFrames(this.sessionId, { count: owed, first: held, then: rest });
        this.state = f;
        this.trail.push({ x: f.x, y: f.y });

        // Every swing that connected comes back on the frame as a number to
        // draw, already placed at the victim's head by the server. Stamped here
        // rather than in paint() because paint() runs on the browser's clock.
        const swings = f.damage ?? [];
        if (swings.length) {
          const born = performance.now();
          for (const d of swings) {
            this.damage.push({ x: d.x, y: d.y, value: d.value, miss: d.miss, born });
          }
          // Bounded: the numbers expire on their own, but a held attack key
          // can outrun the expiry between two paints on a slow frame.
          if (this.damage.length > 64) this.damage = this.damage.slice(-32);
        }

        // The camera moves ONCE per stepped frame, here, and not in paint().
        // Both of the reference's lerp coefficients are per 1/60s tick
        // (MapleCharacter.cs:751 and 755) applied by the loop that moves the
        // character, so feeding them rendered frames instead of stepped ones
        // would make the camera's speed depend on the browser's paint rate.
        this.view?.follow(f.x, f.y);
        this.jumpLatch = this.keys.jump;
        this.attackLatch = this.keys.attack;
        // Latched against the key that is still down, not against whether one
        // was sent. The two differ exactly when the cast lock was up -- and
        // that difference is the reference's behaviour, not an accident of
        // this shape: a key pressed during an animation is held through it and
        // casts when the animation ends (Skill.cs:321 gates on `PlayEnded`,
        // not on a keydown having happened), so latching on "sent" here would
        // drop that press entirely.
        this.skillLatch = !!this.skillDown;
      } catch (err) {
        this.onFrameError(err);
      } finally {
        this.busy = false;
        // Carry the clock forward from where the batch actually ENDED, so the
        // round trip is owed to the next batch instead of being discarded.
        this.last = performance.now();
      }

      // Keep the trail bounded. A long walk would otherwise grow without
      // limit and slow every frame that has to copy it.
      if (this.trail.length > 1200) this.trail = this.trail.slice(-600);
      this.paint();
    }

    if (this.running && gen === this.gen) this.raf = requestAnimationFrame(this.tick);
      }

  /**
   * What a failed frame means, and what it costs.
   *
   * 'session' is the one this file used to treat as the end of the walk. It is
   * in fact the least interesting failure there is: the lease expired, the body
   * is gone, and one more POST puts another exactly where the last one stood.
   * Stopping here is what made the character look broken.
   *
   * The dead-server cases keep the walk alive on this same loop, so the
   * character resumes -- still facing the way it was, still under whatever key
   * is held -- as soon as the server answers again. Only a map that cannot be
   * walked, or a build without the endpoint, stops it, because neither will fix
   * itself and retrying forever would only hide them.
   */
  onFrameError(err) {
    const kind = err?.kind ?? 'server';
    this.failure = kind;
    if (kind === 'session') {
      // That id is known dead: forget it, so stop() does not try to DELETE it.
      this.sessionId = null;
      this.link = 'lost';
      this.retryAt = 0;          // reopen on the very next animation frame
      this.error = '';
    } else if (FATAL_FAILURES.has(kind)) {
      this.error = err.message;
      this.link = 'idle';
      this.running = false;
    } else {
      // offline / timeout / server: the id may still be perfectly good, so keep
      // it and simply stop asking for a moment.
      this.link = 'lost';
      this.retryAt = performance.now() + this.retryMs;
      this.retryMs = Math.min(4000, this.retryMs * 2);
      this.error = '';
    }
    this.paint();
  }

  /**
   * Hand the current position to the existing renderer.
   *
   * WalkViewer already draws the map art, the ground, a figure and a trail --
   * it was built for a trace and happens to suit a live body too. Reusing it
   * means the interactive view and the diagnostic view cannot disagree about
   * how the map looks.
   */
  paint() {
    const v = this.view;
    if (!v) return;
    if (this.state) {
      v.ticks = this.trail.map((p, i) => ({
              x: p.x,
              y: p.y,
              action: this.state.action || (this.state.onLadder ? 'climb' : 'walk'),
              // The swing stance the server picked for this attack, carried across
              // because actionFor() reads it from the tick. Without this field the
              // viewer sees action:'attack' with no stance to draw and silently keeps
              // the stand pose -- which is exactly what an unarmed character does, so
              // the symptom is indistinguishable from "attack is not implemented".
              attackStance: this.state.attackStance,
              // The skill pose's own name, carried across for the same reason
              // the swing's is above: the server holds it for the length of the
              // cast (MapSimSessionStore.cs:653-654) and clears it on the first
              // idle frame (:614-618), so a viewer that did not keep it would
              // have to guess -- and guessing a stance name is guessing about
              // data only the server can read, which is what actionFor()
              // already refuses to do for the swing.
              skillStance: this.state.skillStance,
              onGround: this.state.onGround,
              dir: this.state.facing >= 0 ? 1 : -1,
              step: i,
              // W-075. The cast's OWN effect sprites, on the current tick and only
              // the current tick.
              //
              // `paint()` handed over `mobs`, `sheet` and `damageNumbers` and never
              // this, so `walkview.drawSkillEffects` read `cur?.skillEffects`,
              // found undefined on every tick, and returned at its first line --
              // 60 times a second, for every cast, forever. The server had them
              // the whole time: `MapSimFrame.SkillEffects` carries the list and
              // `CastSkill` fills it (MapSimSessionStore.cs:1515), and it is
              // cleared only when the pose ends (:1444), so it is non-empty for
              // the whole cast.
              //
              // Only the LAST tick carries them, because they are a "now": the
              // trail replays older ticks for the character, and a 40-frame-old
              // sprite at an old position is not what the cast drew.
              skillEffects: i === this.trail.length - 1
                ? (this.state.skillEffects ?? EMPTY_SKILL_FX)
                : EMPTY_SKILL_FX,
              // W-088. The cast's ground decals, on the LAST tick only, for the
              // same reason `skillEffects` is: the server holds them for the
              // animation's own length (MapSimSession.TileEffectUntil) and they
              // are a "now" — replaying a 40-frame-old decal at an old
              // position would draw the ground the character used to be standing
              // over. `EMPTY_SKILL_FX` is a shared frozen empty array, so the
              // off-last ticks cost nothing.
              tileEffects: i === this.trail.length - 1
                ? (this.state.tileEffects ?? EMPTY_SKILL_FX)
                : EMPTY_SKILL_FX,
            }));
      v.frame = v.ticks.length - 1;

      // W-075/W-083: a NEW cast restarts the sprite animation.
      //
      // This keyed on the SKILL ID, which is not a cast's identity: casting the
      // same skill twice changed nothing here, so `skillFxBorn` stayed on the
      // first cast's clock and the second cast's animation started wherever that
      // stale clock happened to be -- measured as "the first cast animates, the
      // second does not, and the pose still plays".
      //
      // The server now counts casts (`SkillCastSeq`), which is monotonic within a
      // session, so no sequence of casts can collide.
      const castSeq = this.state.skillCastSeq || 0;
      if (castSeq !== this.skillFxCastSeq) {
        this.skillFxCastSeq = castSeq;
        v.skillFxBorn = performance.now();
      }
      // The mobs come straight off the frame the server sent, for the same
      // reason the ticks do: the simulation owns their positions. Deriving a
      // mob's position here would be a second, drifting implementation of the
      // same patrol, and a mob that disagreed with the character beside it would
      // be worse than one that was simply not drawn.
      v.mobs = this.state.mobs ?? [];
      // The character sheet, handed over the same way: the SERVER rebuilt it
      // when a weapon was equipped (MapSimSessionStore.SetEquippedWeapon ->
      // BuildStatus), so a viewer that recomputed it would show a character the
      // simulation is not running. Carried beside the ticks for the same reason
      // attackStance is -- a view that cannot describe the character it is
      // drawing cannot be scrubbed through.
      v.sheet = this.state.status ?? null;
            // The numbers are handed over whole and the viewer culls what has
            // finished. Sharing the array rather than copying means a number cannot
            // be drawn twice by two viewers, and a viewer that starts late does not
            // replay a fight's worth of numbers it was not there for.
            v.damageNumbers = this.damage;
          }
    v.draw();
  }

  /** A one-line status for the HUD. */
  status() {
    // While the body is being re-placed this is the most important thing on
    // the line: it is the difference between "this map is broken" and "the
    // server was restarted", and from the canvas alone the user cannot tell
    // the two apart.
    if (this.link === 'lost')
      return `${LIVE_FAILURES[this.failure] ?? 'reconnecting'} — attempt ${this.attempts}`;
    if (this.error) return this.error;
    if (!this.sessionId) return 'press a map to place the character';
    const s = this.state;
    if (!s) return '';
    return [
      `x=${s.x.toFixed(0)} y=${s.y.toFixed(0)}`,
      `speed=${s.speed.toFixed(2)}`,
      s.onLadder ? 'on ladder' : s.onGround ? 'grounded' : 'airborne',
      `frame ${s.frame}`,
    ].join(' · ');
  }
}
