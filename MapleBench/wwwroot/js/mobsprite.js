/**
 * The creatures on the map, drawn from the Mob and Npc archives.
 *
 * WHY THIS IS A SEPARATE FILE
 * ---------------------------
 * walkview.js knows how to lay a map out and how to order things by depth. It
 * knows nothing about WZ node shapes, and it used to paint a mob as a pink slab
 * with a circle on it -- a drawn shape that says "there is something here" and
 * nothing else. The mobs on a map are not decoration: they are the reason the
 * map reads as a place rather than a diagram, and with 2,054 mob images open and
 * not one pixel drawn, the whole world was empty. Adding node-shape knowledge to
 * walkview.js would put a second, drifting idea of "what a mob looks like" next
 * to the one the server already knows how to answer.
 *
 * WHAT IS COPIED FROM THE REFERENCE, AND FROM WHERE
 * -------------------------------------------------
 * The COMPOSITION is not copied here, because the C# already transliterates it
 * from the same source this file cites and re-deriving it in JS would create a
 * second implementation that can disagree:
 *
 *   MPSH clean-client/src/Mob/Mob.cpp:38     extend_id(mid, 7) -- the id padding
 *   MPSH clean-client/src/Mob/Mob.cpp:114-133 the action names per Stance
 *   MPSH clean-client/src/Animation.cpp:283-333 the per-frame delay accumulator
 *
 * What IS here, because it is the clock and the drawing decision:
 *
 *   :1224-1226  `Time += 17; if (Time > delay) { ... }`, where delay is that
 *               frame's own delay node and the counter resets to 0, not to the
 *               overshoot -- the same rule avatar.js uses for the character.
 *
 * THE ACTION A MOB PLAYS
 * ----------------------
 * The server sends a `stance` name, because the mapping from an animation state
 * to a WZ action is Mob.cpp's and not this file's. What is chosen here is the
 * FALLBACK, for a mob whose art carries no `move`: a creature walking its
 * chain with its legs frozen reads as a glitch, and the client falls back to
 * `stand` for exactly this reason.
 */

import { api } from './api.js';
import { t } from './i18n.js';

/**
 * The client's fixed step -- RenderFormDraw.cs:176. The same 17 ms avatar.js
 * advances its own clock by, so the two cannot drift apart.
 */
const TICK_MS = 17;

// The action a mob is playing, and whether its clock runs, is NOT decided here.
//
// It used to be, in an exported `mobAction(mob)` that read `mob.direction` and
// `mob.dead`. Nothing called it -- `step()` reads the SERVER's `stance` and
// `frameIndex` instead -- so it was a second authority over the same fact that
// nobody consulted, and it was wrong besides: it asked for `die1` on `dead`, and
// `dead` only becomes true AFTER the death animation's frames have elapsed
// (MobPatrol.cs:450-456). The state it wanted is `dying`, which is what
// MobAnimation.cs now keys on.
//
// The reasoning it carried is kept, because it is the reasoning behind step()'s
// own fallbacks:
//
//   `move` when the mob is travelling and `stand` when it is not -- the same
//   two-state shape the character uses (MapleCharacter.cs:142-143 names
//   `stand1`/`walk1` as the player's resting and travelling states). The clock
//   runs in BOTH: Client/Mob.cs:202 sets `Mob.AnimRepeat = true` when a mob is
//   spawned and never clears it, and :390-398 picks the action from
//   MoveDirection alone -- the action NAME follows the mob's state, the animation
//   does not stop because the mob stopped walking. A monster whose `stand`
//   carries 8 frames is idling through them: the breathe, the tail, the head
//   turn. `die1` is the exception -- it plays once and holds, because the mob is
//   gone afterwards.

/**
 * The action a mob is playing, and whether its clock should run.
 *
 * `move` when it is travelling and `stand` when it is not, which is the same
 * two-state shape the character uses (MapleCharacter.cs:142-143 names
 * `stand1`/`walk1` as the player's resting and travelling states).
 *
 * The clock runs in BOTH states. Client/Mob.cs:202 sets `Mob.AnimRepeat = true`
 * when a mob is spawned and never clears it, and :390-398 picks the action from
 * MoveDirection alone -- the action NAME follows the mob's state; the animation
 * does not stop because the mob stopped walking. A monster whose `stand`
 * carries 8 frames is idling through them: the breathe, the tail, the head
 * turn. Freezing it on frame 0 left every mob a paper cutout on a map that
 * otherwise animates.
 */

/**
 * One mob or NPC's art, held across frames.
 *
 * Per mob rather than per map: a map's twelve spawns can name a dozen ids from
 * three archives, and keying the cache on the id is what makes the second frame
 * of a walk free. The key includes the kind, because a mob id and an NPC id
 * share one numeric space and resolve against different archives -- keying on
 * the id alone would let one shadow the other.
 */
export class MobSprite {
  /**
   * @param {string} id the id the map's `life` entry named
   * @param {string} kind `m` or `n`
   */
  constructor(id, kind = 'm') {
    this.id = id;
    this.kind = kind;
    /** null | {actions: Map<string, {frames, index, time}>, action: string} */
    this.art = null;
    /** canvas session path -> decoded Image. Null means "asked, no pixels". */
    this.bitmaps = new Map();
    /** Why there is no sprite, in words, for the HUD. */
    this.reason = '';
    this.imagePath = '';
    /** Fetched once; a repeat load short-circuits on it. */
    this.loading = null;
  }

  /** The cache key. `m|9409000` and `n|9409000` are different creatures. */
  get key() { return `${this.kind}|${this.id}`; }

  /** Drop every decoded bitmap and forget the composed art. */
  release() {
    for (const img of this.bitmaps.values()) {
      if (!img) continue;
      try { img.src = ''; } catch { /* already detached */ }
    }
    this.bitmaps = new Map();
    this.art = null;
    this.reason = '';
    this.imagePath = '';
    // The "asked and it was not there" memo goes with it, exactly as it does in
    // avatar.js: a Mob.nx opened AFTER a map was first walked has to be
    // findable, and a memo that outlived the release would report the first,
    // emptier answer forever.
    this.loading = null;
  }

  /**
   * Fetch this mob's actions and their frames, once.
   *
   * Returns false and sets `reason` when no open archive holds it, so the
   * caller can draw the fallback AND say that it did. A silent fallback that
   * looks like success is the failure this file exists to prevent.
   *
   * @returns {Promise<boolean>} whether there is art to draw
   */
  async load() {
    if (this.art?.actions.size) return true;
    if (this.loading) return this.loading;

    this.loading = (async () => {
      const dto = await api.mobSprite({ mobId: this.id, kind: this.kind }).catch(() => null);

      if (!dto || !dto.hasArt || !dto.actions?.length) {
        // "No open archive holds this mob" and "the archive holds it but this id
        // has no frames" are different facts, and the difference is the whole
        // reason this is reported rather than drawn over.
        this.reason = t('no {0} art for {1} in the open archives')
          .replace('{0}', this.kind === 'n' ? t('NPC') : t('mob'))
          .replace('{1}', this.id);
        this.art = null;
        // NOT memoized as permanently missing: an archive opened later is
        // exactly that case, and a negative that outlives it would keep the
        // placeholder on screen through a working lookup.
        this.loading = null;
        return false;
      }

      const actions = new Map();
      for (const a of dto.actions) {
        const frames = (a.frames || []).filter((f) => f.width > 0 && f.height > 0);
        if (frames.length) actions.set(a.name, { frames, index: 0, time: 0 });
      }

      if (!actions.size) {
        this.reason = t('no {0} art for {1} in the open archives')
          .replace('{0}', this.kind === 'n' ? t('NPC') : t('mob'))
          .replace('{1}', this.id);
        this.art = null;
        this.loading = null;
        return false;
      }

      this.art = { actions, action: '' };
      this.imagePath = dto.imagePath || '';
      this.reason = '';
      await this.preload(this.art);
      return true;
    })();

    return this.loading;
  }

  /**
   * Fetch every frame bitmap up front, so playback does not stutter.
   *
   * Failed decodes are STORED as null rather than skipped: an <img> whose
   * decode failed is still a truthy object, so the draw loop's `if (!img)`
   * could not tell "no bitmap" from "no pixels" and would blit a broken image
   * (a silent no-op) and the mob would come out with a hole in it.
   */
  async preload(art) {
    const wanted = new Map();
    for (const [, entry] of art.actions) {
      for (const f of entry.frames) wanted.set(f.path, f);
    }

    await Promise.all([...wanted.values()].map(async (f) => {
      const img = new Image();
      img.src = api.canvasUrl(f.path);
      // decode() rejects on a 404 body (it is not an image), which is exactly
      // the signal wanted -- onerror alone does not fire for a decoded-but-empty
      // response, and this is the only way to ask "did I actually get pixels".
      const ok = await img.decode().then(() => true, () => false);
      this.bitmaps.set(f.path, ok ? img : null);
    }));
  }

  /**
   * Pick the frame to blit for the action the SERVER says is playing.
   *
   * @param {object} mob one entry of the frame's mob list
   * @returns {{action: string, frame: object, img: Image}|null} what to blit,
   *   or null when nothing is drawable yet
   */
  step(mob) {
    if (!this.art || !this.art.actions.size) return null;

    // The server owns the clock. It sends `stance` (the WZ action name) and
    // `frameIndex`/`frameCount`, advanced on the session's own tick -- so the
    // frame a mob shows is a fact about the simulation and not about how fast
    // this tab happens to paint. See MobAnimation.cs.
    //
    // The local clock that used to live here was the second authority: it
    // advanced on rAF while the mob's POSITION advanced on the session tick,
    // and nothing on the wire said which was right. It is gone, not merged --
    // two clocks disagree by exactly the difference between them.
    //
    // A mob whose art carries no `move` holds its stand frame while travelling
    // rather than going blank: legs frozen mid-stride read as a rendering
    // failure. Same fallback as before, still the viewer's call because it is
    // about what this creature's art has, not about what the game is doing.
    let name = mob?.stance || '';
    if (!this.art.actions.has(name)) name = this.art.actions.has('stand') ? 'stand' : '';
    if (!this.art.actions.has(name)) name = [...this.art.actions.keys()][0];

    const entry = this.art.actions.get(name);
    this.art.action = name;

    // Clamped, because the server's index is authoritative and this list was
    // filtered to drawable frames (mobsprite.js:162) -- a mob can name a frame
    // the browser has no pixels for.
    const count = entry.frames.length;
    let index = Number.isFinite(mob?.frameIndex) ? Math.trunc(mob.frameIndex) : 0;
    index = ((index % count) + count) % count;

    const frame = entry.frames[index];
    const img = this.bitmaps.get(frame?.path);
    if (!frame || !img) return null;
    return { action: name, frame, img };
  }

  /**
   * Blit one NAMED action rather than the one the mob's state says it is playing.
   *
   * The only caller is the projectile: `Mob.cpp:1125` draws `data.ball` — the
   * animation at `attackN/info/ball`, published as the action name
   * `attackN/ball` — at the entry's position while the shot is in the air, even
   * though the mob itself is playing `attackN`. The server names both on the same
   * frame, so nothing here has to know which attack a shot belongs to.
   *
   * @param {object} mob one entry of the frame's mob list
   * @param {string} name the published action name, e.g. `attack2/ball`
   * @param {number} index the server's frame index for that action
   * @returns {{action: string, frame: object, img: Image}|null} what to blit
   */
  stepNamed(mob, name, index) {
    if (!this.art || !name) return null;
    const entry = this.art.actions.get(name);
    if (!entry || !entry.frames.length) return null;
    const count = entry.frames.length;
    const i = Number.isFinite(index) ? Math.trunc(index) : 0;
    const frame = entry.frames[((i % count) + count) % count];
    const img = this.bitmaps.get(frame?.path);
    if (!frame || !img) return null;
    return { action: name, frame, img };
  }

  /** Whether there is anything to draw at all. */
  get ready() { return !!(this.art && this.art.actions.size); }
}

/**
 * The whole map's worth of mob sprites, keyed by the server's id+kind.
 *
 * `requested` is what makes this honest rather than eager: every map asks for
 * its mobs' art, and a map opened without Mob.nx would otherwise fire a request
 * per spawn to learn nothing. A key is marked requested the first time a mob
 * with it is asked about, so a later open of the archive can still fill it in.
 */
export class MobSpriteCache {
  constructor() { this.sprites = new Map(); }

  /**
   * The sprite for one mob, loading it at most once.
   *
   * @param {object} mob one entry of the frame's mob list
   * @returns {MobSprite|null}
   */
  get(mob) {
    if (!mob?.mobId) return null;
    const kind = mob.kind || 'm';
    const key = `${kind}|${mob.mobId}`;
    let sprite = this.sprites.get(key);
    if (!sprite) {
      sprite = new MobSprite(mob.mobId, kind);
      this.sprites.set(key, sprite);
    }
    // Fired and NOT awaited, for the same reason avatar.js does: draw() runs
    // inside a requestAnimationFrame callback and must not block on a round
    // trip. Until it settles, step() returns null and the slab is drawn with
    // the HUD saying why -- so the first frame after a map loads is honest
    // rather than blank.
    if (!sprite.art && !sprite.loading) sprite.load().then(() => this.onReady?.()).catch(() => {});
    return sprite;
  }

  /** Let go of every decoded bitmap. Called when the map changes. */
  release() {
    for (const s of this.sprites.values()) s.release();
    this.sprites = new Map();
  }
}