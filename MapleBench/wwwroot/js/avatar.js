/**
 * The character, drawn from the archive's own character art.
 *
 * WHY THIS IS A SEPARATE FILE
 * ---------------------------
 * walkview.js knows how to lay a map out and how to order things by depth. It
 * knows nothing about WZ node shapes, and adding node-shape knowledge to it
 * would put a second, drifting idea of "what a character looks like" next to the
 * one it already draws. The mapping from an action to a pose, and the frame
 * timer, are the whole of this file's job.
 *
 * WHAT IS COPIED FROM THE REFERENCE, AND FROM WHERE
 * -------------------------------------------------
 * The COMPOSITION is not copied here, because the C# already transliterates it
 * from the same source this file cites and re-deriving it in JS would create a
 * second implementation that can disagree:
 *
 *   MPSH clean-client/src/Character/Look/CharLook.cpp:84-200   the assembly
 *     .../Look/Body.cpp:36-118          body + head parts, per-part shifts
 *     .../Look/BodyDrawInfo.cpp:27-100  the five per-pose anchors
 *     .../Look/Hair.cpp:30-95           hair, shifted by gethairpos() - brow
 *     .../Look/Face.cpp:92-97           the face marker, at getfacepos()
 *     .../Look/AvatarLayers.cpp:38-118  z_of / slots_of / claimed / priority_of
 *
 * What IS here, because it is the animation clock and the drawing decision:
 *
 *   :142-143  `StandType = "stand1"; WalkType = "walk1";` -- the two action
 *             names this composes from, and nothing else.
 *   :146  FlipX is a boolean, not an eight-way direction: MapleCharacterEx.cs:
 *          130/134 sets it false for MoveDirection.Left and true for Right, and
 *          nothing in the client ever sets a direction index. So the archive
 *          holds ONE set of character sprites per action and the facing is a
 *          mirror -- which is why this file has a `flip` and no `direction`.
 *   :1224-1226 the frame clock: `Time += 17; if (Time > delay) { ... }`, where
 *             delay is that frame's own delay node and the counter resets to 0,
 *             not to the overshoot.
 *   :1237-1251 `AnimZigzag` for stand1/stand2/alert. That is why a standing
 *             character sways rather than loops.
 *   :1253-1262 otherwise frame += 1, and past FrameCount it wraps to 0.
 */

import { api } from './api.js';
import { t } from './i18n.js';

/**
 * The client's fixed step, RenderFormDraw.cs:176 -- and the same 17 ms the
 * avatar frame clock advances by, since the client runs the whole game on that
 * loop. One number, used as both, so the two cannot drift apart.
 */
const TICK_MS = 17;

/** The reference's own default when a frame carries no `delay` node. */
const DEFAULT_DELAY_MS = 100;

/**
 * The hair and face a composed avatar wears by default.
 *
 * Both ids are real ones from the open Character.nx, not placeholders: the
 * composition needs a hair layer and a face marker to have anything to arbitrate
 * between, and a head with neither is not what a character looks like. They are
 * overridable per instance because they are an EQUIPMENT choice, not a property
 * of the body.
 */
const DEFAULT_HAIR = '00030000.img';
const DEFAULT_FACE = '00020000.img';

/**
 * Worn on the first composed look, so the feature is visible without a click.
 *
 * Every clothing slot is filled. That is a change from three slots, and it was
 * made because a half-dressed character is not a character -- it was reported
 * that Pants, Glove, Cape and Longcoat were all empty while the user could see
 * a pickable slot for each of them. A default set that leaves pickable slots
 * empty is indistinguishable from those slots being broken.
 *
 * THE CAP IS DELIBERATELY ABSENT, and that is a measurement, not a taste call.
 *
 * Cap/01000000.img was the previous default and its `stand1/0/default` layer is
 * a 45x47 canvas placed at screen (-25,-69) -- LARGER than the entire 39x35
 * head, with 303 opaque pixels landing inside the 26x16 face box at (-21,-60),
 * i.e. 118% of the face area. It is a wide-brimmed hat whose brim sits across
 * the eyes. That reads as "the face is far too high, with an oversized blank
 * jaw below it", and it was chased for a while as a coordinate bug before the
 * ink count found it.
 *
 * The face geometry itself is right, and worth recording because it looks
 * wrong until you check it:
 *   BODY.neck (-4,-32) - HEAD.neck (0,15) + HEAD.brow (-4,-5) = (-8,-52)
 * which is BodyDrawInfo.cpp:99 verbatim, and the rendered shift measures
 * (-8,-52) exactly. `DrawArgument.h:143` (`rlt = pos - center - origin`)
 * confirms the screen-space convention is `shift - origin`. So do NOT "fix" the
 * face by moving it -- every number here already equals the benchmark, and
 * adding a fudge offset would be the first place the project stops matching it.
 *
 * The ids are real ones out of Character.nx, in the archive's own numbering:
 * Coat 104, Longcoat 105, Pants 106, Shoes 107, Glove 108, Cape 1102,
 * Weapon 1302 (EquipSlotTable.cs:98-106). They are DEFAULTS, not requirements --
 * `setEquip()` replaces the whole map, and Cap-1 stays pickable.
 */
const DEFAULT_EQUIP = {
  Coat: '01040000.img',
  Longcoat: '01050000.img',
  Pants: '01060000.img',
  Shoes: '01070000.img',
  Glove: '01080000.img',
  Cape: '01102000.img',
  'Weapon-1': '01302000.img',
  // Hair and Face, because nothing downstream supplies them. `Compose` falls back
  // to a default for the body and head SHEETS (`body ??= BodyPath(fileId)`,
  // `head ??= HeadPath(fileId)`, LookService.cs:166-168) but there is no such
  // fallback for hair or face: those two exist only as `Hair-1` / `Face-1` slots
  // or as `hair=` / `face=` query parameters. Omitting them produced a character
  // with a bare head -- measured, 13 parts and zero of them the face, against 16
  // with both slots set.
  //
  // Head and Body are deliberately NOT here even though the same fallback would
  // fill them in: they name the sheets the whole character is composed ON, and
  // leaving them to the server keeps one place that decides which skin is in use.
  'Hair-1': '00030000.img',
  'Face-1': '00020000.img',
};

/**
 * Slots the picker offers, in the reference's own avatar-editor order
 * (`AvatarForm.Designer.cs`: Head, Body, Weapon-1, Weapon-2, Cap-1, Cap-2, Coat,
 * Pants, Longcoat, Cape, Shield, Glove, Shoes, Hair-1, Hair-2, Face-1).
 *
 * `Head` and `Body` ARE pickable, and an earlier version of this file said
 * otherwise on the strength of `Body.cpp:31-32` -- that the two sheets are read
 * "as skin-type sheets straight out of Character.nx" and therefore have nothing
 * to pick from. That is half the story: it is true that they are addressed by
 * name out of the character archive and true that no `Character/Head/` folder
 * exists. It is NOT the whole story, because the reference's editor offers both
 * as grids. `AvatarForm.cs:496-499` gives them an empty `CharacterDir`, so the
 * enumeration walks the ARCHIVE ROOT instead of a folder (`AvatarForm.cs:538-547`),
 * and `:559-566` tells the two apart by leading digits -- Head drops `0000*`,
 * Body drops `0001*`, which inverts to Head=`0001*` and Body=`0000*`.
 * Measured on the open archive: 9 head sheets (00012000-00012011) and 9 body
 * sheets (00002000-00002011).
 *
 * Choosing one REPLACES the base the whole character is composed on rather than
 * layering over it, so the server routes these two to the `body`/`head`
 * arguments of `CharLookComposer.ComposeEquipped` instead of into `equipped`
 * (`LookService.Compose`). Passing them through as equipment produced a figure
 * with one body under a different head sheet, which is not a thing the client
 * can draw.
 */
export const PICKABLE_SLOTS = [
  'Head', 'Body', 'Cap-1', 'Coat', 'Pants', 'Longcoat', 'Cape', 'Shield',
  'Glove', 'Shoes', 'Weapon-1', 'Weapon-2', 'Hair-1', 'Face-1',
];
/**
 * What the character is doing, as this file maps it to a node.
 *
 * Only the poses the archive actually carries under these names are composed.
 * MapleCharacter.cs:142-143 names `stand1` and `walk1` as the player's resting
 * and travelling states; airborne and climbing are real stances in the archive
 * (Stance.cpp:24-46) and get their own names here.
 *
 * @param {object} tick one trace tick or one live frame
 * @returns {{state: string, animated: boolean}} the WZ action and whether its
 *   own timing should advance
 */
export function actionFor(tick) {
  // Prone is its own stance in the archive (Stance::PRONE, Stance.h) and the
  // server emits it when DOWN is held on the ground with no ladder to descend
  // (CharacterPhysics.StepCore, the branch after the two ladder early-returns).
  // It comes before the climb tests on purpose: `climb-top` is a ladder at its
  // end, and the archive's own name for that is not prone.
  const action = tick?.action;
  if (action === 'prone') {
    // The archive carries exactly one prone frame (`frameCount: 1` measured via
    // /api/simulate/look/stances), so this is a hold, not a cycle -- ticking it
    // would fold a single frame into itself forever.
    return { state: 'prone', animated: false };
  }

  // ---- swing pose -------------------------------------------------------
  // The stance name comes FROM THE SERVER, not from here. The reference reads the
  // equipped weapon's `info/attack` integer (WeaponData.cpp:35) and indexes a
  // table by it (CharLook.cpp:648, table at :682-696), then picks one of that
  // weapon's stances at random per attack (CharLook.cpp:706). Nothing about the
  // weapon is derivable from `action`, so guessing a group here would be guessing
  // about data this client cannot see.
  //
  // Animated, because inside a swing stance the archive advances frames itself:
  // every attack stance measured through /api/animation/audit is 3 frames
  // totalling 800ms (swingO1/2/3 and swingT1/2/3 alike), and the frame-to-frame
  // advance is each frame's own WZ delay (BodyDrawInfo.cpp:194), not a fixed
  // rate. `avatar.step` is what applies those delays.
  //
  // Animated is also why the server holds the pose for 48 ticks (= 800ms at
  // 60Hz, MapSimSession.SwingPoseTicks) instead of one: the attack key is an
  // edge, so a one-tick pose would be gone before frame 2.
  if (action === 'attack') {
    const s = tick?.attackStance;
    // An unarmed character has no swing group at all (CharLook.cpp:594 returns
    // before the table is indexed) and the server sends no stance. Falling through
    // leaves the body as it was, which is what the reference does -- NOT a
    // default swing.
    if (s) return { state: s, animated: true };
  }
  // Airborne is a STATE in the reference, not a modifier of walking: Char::State
  // FALL maps to Stance::JUMP (Stance.cpp:24-46). `rise` and `fall` are that
  // state here, so they get `jump` -- a body in the air drawn in walk1 is
  // visibly wrong, and the archive carries the jump pose for every body.
  if (action === 'rise' || action === 'fall') {
    return { state: 'jump', animated: false };
  }
  // A climb is its own stance in the archive (Stance.cpp:29 maps LADDER ->
  // LADDER), and it is the one pose where the clock keeps running while the
  // character is not making horizontal progress.
  if (action === 'climb' || action === 'climb-top') {
    return { state: 'ladder', animated: true };
  }
  // Everything else is grounded. The residual-speed term is kept as a floor so a
  // frame that arrives without an action word -- the first one off a fresh
  // session -- still draws the right pose rather than defaulting to walk.
  const moving = action === 'walk'
    || (action === null && (tick?.speed ?? 0) > 0.01)
    || (!tick?.onGround && !tick?.onLadder);
  // The clock advances ONLY while the character is travelling. This is the
  // part the reference does not spell out for us: MapleCharacter.cs:1224 gates
  // `Time += 17` on `Animate`, and every part sprite sets `Animate = true` at
  // MapleCharacter.cs:432 regardless of what the owner is doing -- so the
  // reference's own stand1 sways on its Zigzag forever. Requirement 3 here is
  // stricter than the reference, deliberately: "standing still is stand1" AND
  // "the animation must not run while the character is idle" cannot both hold
  // if stand1 keeps ticking, so a standing character holds its frame.
  return { state: moving ? 'walk1' : 'stand1', animated: moving };
}

/**
 * Fold a frame index into the range a pose actually has.
 *
 * @param {number} frame the index asked for
 * @param {number} frameCount how many frames the pose carries
 * @returns {number|null} a valid index, or null when the pose has none
 *
 * @description
 * Wrapping rather than clamping, because that is what the reference does:
 * `BodyDrawInfo::nextframe` (BodyDrawInfo.cpp:194-200) returns `frame + 1`
 * when the next frame exists and `0` when it does not, so an animation that
 * runs off the end of a pose restarts it instead of freezing on the last one.
 *
 * This is not theoretical. The measured sweep asked for frames 0,1,2,3,5
 * across 16 stance names; every frame index past a pose's end returned HTTP 200
 * carrying one lone face sprite and no character. Poses here carry 1 to 4
 * frames -- stand1/stand2/alert/swingT1 have 3, walk1 has 4, jump/sit/prone/dead
 * have 1, ladder has 2 -- so a hardcoded "0..5" is wrong for most of them.
 */
export function clampFrame(frame, frameCount) {
  if (!Number.isFinite(frameCount) || frameCount < 1) return null;
  return ((Math.trunc(frame) % frameCount) + frameCount) % frameCount;
}

/**
 * The avatar's own drawing state, held across frames.
 *
 * Frame selection is a clock, not a function of frame index: `Time` accumulates
 * the tick and advances only when it passes the current frame's own delay
 * (MapleCharacter.cs:1224-1226). Two things follow from keeping it here rather
 * than computing a frame from a timestamp. An idle character stops, because its
 * action is stand1 and stand1's frames are indexed by the same clock but the
 * caller does not advance it while standing -- see step(). And a walk that is
 * replayed twice from the same start produces the same frames, because the
 * clock is seeded from the frames themselves rather than from wall time.
 */
export class Avatar {
  /**
   * @param {object} [look] which hair, face and equipment to compose
   * @param {string} [look.hair] a Hair/....img name, e.g. '00030000.img'
   * @param {string} [look.face] a Face/....img name, e.g. '00020000.img'
   * @param {object} [look.equip] slot name to image name, e.g. {'Cap-1':'01000000.img'}
   */
  constructor(look = {}) {
    /** null | {look, frames, index, time, flip} */
    this.part = null;
    /** canvas session path -> decoded Image. Null means "asked, no pixels". */
    this.bitmaps = new Map();
    /** Why there is no avatar, in words, for the HUD. */
    this.reason = '';
    /** The session paths this look was composed from. */
    this.source = '';
    /** Paths the server listed as layers but would not hand over pixels for. */
    this.missingLayers = [];
    /** The anchors the server derived, kept for the HUD and for any caller that
     *  wants to reason about where a part landed rather than where it is. */
    this.anchors = null;
    this.hair = look.hair ?? DEFAULT_HAIR;
    this.face = look.face ?? DEFAULT_FACE;
    /**
     * What is worn, slot name -> image name.
     *
     * The hair and the face are NOT in here: they have their own fields because
     * they were the only two slots before equipment existed, and folding them in
     * would silently break every caller that sets `.hair` directly. They are
     * merged into the request at the bottom of load() instead, where both
     * spellings reach the same composer.
     */
    this.equip = { ...(look.equip ?? DEFAULT_EQUIP) };
    /** Slots the caller deliberately emptied, kept apart from `.hair`/`.face`. */
    this.stripped = [];
    /** The slots the server says it actually put on, from its own resolution. */
    this.equipped = [];
    /** Slots asked for and refused, one line each, named by the server. */
    this.unresolved = [];
    this.fileId = '';
    /** Frames the pose actually has, so the clock can walk off the end. */
    this.frameCount = 0;
  }

  /**
   * Replace what is worn.
   *
   * The composed look is dropped rather than patched: a cap is a different set
   * of layers, at different zmap ranks, and the pose's own frame clock is not
   * what changed -- so re-composing is the honest move and keeping the old
   * `part` would draw the previous outfit until the next pose change.
   *
   * Returns true when something actually changed, so a caller can skip the
   * re-compose a no-op select change would otherwise cause.
   *
   * @param {object} map slot name -> image name. An empty slot is dropped.
   * @returns {boolean} whether the worn set changed
   */
  setEquip(map = {}) {
    const next = {};
    for (const [slot, image] of Object.entries(map)) {
      if (image) next[slot] = image;
    }
    // Remember which slots the caller left out on purpose. Hair and face are
    // ALSO character fields (`.hair`, `.face`), so a map without them does not
    // mean "go bald" by itself -- slotMap() would fold the defaults back in.
    // `stripped` is how "I mean it" survives the round trip. Only the slots
    // this caller can actually control are tracked; a caller that passes a
    // partial map (just changing a cap) must not strip everything it omitted.
    this.stripped = Object.keys(this.equip)
      .filter((s) => !next[s] && !DEFAULT_EQUIP[s]);
    const before = JSON.stringify(Object.entries(this.equip).sort());
    const after = JSON.stringify(Object.entries(next).sort());
    if (before === after && !this.stripped.length) return false;

    this.equip = next;
    // The bitmaps are keyed by path, and the new outfit's paths overlap the old
    // one only for the body/head, so dropping the whole map is the cheap correct
    // move -- preload() refills it from the paths the server actually sent.
    for (const img of this.bitmaps.values()) {
      if (!img) continue;
      try { img.src = ''; } catch { /* already detached */ }
    }
    this.bitmaps = new Map();
    this.part = null;
    this.tried = null;
    this.missingLayers = [];
    this.frameCount = 0;
    return true;
  }

  /**
   * The slot map to send, with the hair and the face folded in.
   *
   * Both spellings reach the same composer: `Compose(fileId, slots, ...)`
   * resolves `Hair-1` and `Face-1` out of this map exactly as the GET's
   * `hair=`/`face=` query parameters did (LookService.cs:73-76), so a hair set
   * through `.hair` and a hair set through `.equip['Hair-1']` are the same
   * character. An explicit equip entry wins, because it is the more specific
   * statement.
   */
  slotMap() {
    const map = { ...this.equip };
    if (this.hair) map['Hair-1'] ??= this.hair;
    if (this.face) map['Face-1'] ??= this.face;
    // A slot the caller deliberately stripped stays stripped. Without this the
    // `??=` above would put the hair straight back on after the picker had
    // deleted it, because `.hair` is still set. `chooseEquip` also clears
    // `.hair`/`.face`; this line is the belt to that pair of braces, so a
    // direct `setEquip({...})` caller gets the same answer.
    // Sent as an empty string rather than deleted: `Compose` treats a slot the
    // caller did not NAME as "use the default sheet" (LookService.cs:166-168),
    // so an absent key is indistinguishable from never having been mentioned.
    // An explicit `''` is the only spelling that reaches the server as a strip.
    for (const slot of this.stripped ?? []) map[slot] = '';
    return map;
  }

  /** Drop every decoded bitmap and forget the composed look. */
  release() {
    // Nulled entries are the layers that failed to decode; there is no element to
    // detach, and asking for `.src` on them would be the one throw in an
    // otherwise total cleanup.
    for (const img of this.bitmaps.values()) {
      if (!img) continue;
      try { img.src = ''; } catch { /* already detached */ }
    }
    this.bitmaps = new Map();
    this.part = null;
    // The "already asked and it was not there" memo goes with it. A character
    // archive opened AFTER a map was first walked has to be findable, and a
    // memo that outlives the release would report the first, emptier answer
    // forever.
    this.tried = null;
    this.source = '';
    this.reason = '';
    this.fileId = '';
    this.frameCount = 0;
    // With the bitmaps. A stale list would name layers of a pose that is no
    // longer composed, and the HUD would keep reporting a hole in an avatar that
    // is not on screen.
    this.missingLayers = [];
    this.anchors = null;
    // What the server resolved goes with them. `equip` deliberately does NOT:
    // it is a property of the CHARACTER, not of the map that happened to be
    // loaded when it was chosen, and dropping it here would silently strip the
    // character on every map change -- which reads as "equipment broke" rather
    // than "the equipment outlived the map".
    this.equipped = [];
    this.unresolved = [];
  }

  /**
   * Compose the look for one action, once, and cache it.
   *
   * Returns false and sets `reason` when the archive has no character art, so
   * the caller can draw the fallback AND say that it did. A silent fallback
   * that looks like success is the failure this file exists to prevent.
   *
   * @param {string} state the WZ action, stand1 or walk1
   */
  async load(state) {
    if (this.part?.state === state) return true;
    if (this.tried?.state === state) return false;
    this.tried ??= {};
    this.tried[state] = true;

    const slots = this.slotMap();

    // POST whenever anything is worn, GET when it is not.
    //
    // The POST is not "nicer", it is the only route that can answer for a slot
    // map, and it reports `unresolved` -- a GET cannot carry a slot map and a
    // GET that quietly dropped an unknown slot would look exactly like a cap
    // that resolved and drew nothing (SimulatorEndpoints.cs:189-197). With
    // nothing worn the GET is kept, so the pre-equipment path is unchanged and
    // a failure there is still a failure there.
    const look = Object.keys(slots).length
      ? await api.lookEquipped({
        fileId: this.fileId || undefined,
        stance: state,
        frame: 0,
        equip: slots,
      }).catch(() => null)
      : await api.look({
        fileId: this.fileId || undefined,
        hair: this.hair,
        face: this.face,
        stance: state,
        frame: 0,
      }).catch(() => null);

    if (!look || !look.parts?.length) {
      // "The archive has a Character image and this state is not in it" is a
      // different statement from "there is no character art", and the difference
      // is the whole reason this is reported rather than drawn over.
      this.reason = t('archive has a Character image but no {0} frames under it')
        .replace('{0}', state);
      this.part = null;
      // Not memoized as "tried" for a missing archive: a character archive
      // opened later is exactly that case, and a permanent negative would keep
      // the placeholder on screen through a working lookup.
      this.tried = null;
      return false;
    }

    // What the SERVER resolved, not what was asked for. These two differ
    // whenever an id was not in its slot's folder, and reading the asked-for
    // map here would report a hat on a character the composer drew bare.
    this.equipped = look.equipped ?? Object.keys(slots);
    this.unresolved = look.unresolved ?? [];

    // Every frame of the pose, each with its OWN anchors and its OWN paint
    // order. BodyDrawInfo.cpp:59-100 indexes the anchor tables by stance AND
    // frame, so a pose whose frame 1 has a different navel from its frame 0 is
    // genuinely composed differently -- one set of parts would freeze the
    // character in frame 0's stance for the whole animation.
    const frames = (look.frames?.length ? look.frames : [look])
      .filter((f) => f.parts?.length)
      .sort((a, b) => a.frame - b.frame);
    if (!frames.length) {
      this.reason = t('archive has a Character image but no {0} frames under it')
        .replace('{0}', state);
      this.part = null;
      this.tried = null;
      return false;
    }

    this.anchors = {
      body: look.bodyAnchor,
      head: look.headAnchor,
      face: look.faceAnchor,
      hair: look.hairAnchor,
      arm: look.armAnchor,
      hand: look.handAnchor,
      bounds: look.bounds,
      hidden: look.hidden ?? [],
      zmap: look.zmapCount ?? 0,
    };

    this.part = {
      state,
      frames,
      index: 0,
      time: 0,
      flip: 1,
    };
    this.frameCount = frames.length;
    this.reason = '';
    this.source = look.source ?? '';
    await this.preload(this.part);
    return true;
  }

  /** Fetch every layer bitmap up front, so playback does not stutter. */
  async preload(part) {
    // Every frame's parts, deduped by path -- the body canvas of frame 0 and of
    // frame 1 are different paths but there is no reason to fetch twice over.
    const wanted = new Map();
    for (const frame of part.frames) {
      for (const p of frame.parts) {
        if (p.width > 0 && p.height > 0) wanted.set(p.path, p);
      }
    }

    // Names of layers the server listed but could not hand over pixels for. Kept
    // because a canvas element whose decode FAILED is still a truthy object, so
    // the draw loop's `if (!img) continue` cannot tell "no bitmap" from "no
    // pixels" -- it would blit a broken image (a silent no-op) and the avatar
    // would come out with a hole in it, looking like an archive quirk instead of
    // a load that did not finish. The others still draw; the HUD names what is
    // absent, because the scope rule is "draw the others and say which".
    const missing = [];
    await Promise.all([...wanted.values()].map(async (p) => {
      const img = new Image();
      img.src = api.canvasUrl(p.path);
      // decode() rejects on a 404 body (it is not an image), which is exactly the
      // signal wanted here -- onerror alone does not fire for a decoded-but-empty
      // response, and this is the only way to ask "did I actually get pixels".
      const ok = await img.decode().then(() => true, () => false);
      if (!ok) missing.push(p.path);
      // Stored either way: a failed entry has to stay addressable so the draw
      // loop skips it by path rather than fetching it again every frame.
      this.bitmaps.set(p.path, ok ? img : null);
    }));
    this.missingLayers = missing;
  }

  /**
   * Advance the frame clock, and only while the character is actually acting.
   *
   * @param {object} tick the tick being drawn
   * @returns {number|null} the frame index to draw, or null when no avatar is
   *   composed for this action
   */
  step(tick) {
    const { state, animated } = actionFor(tick);
    // null and not -1: -1 is a valid frame index into an empty list and would
    // be drawn as undefined rather than as "there is nothing to draw".
    if (this.part?.state !== state) return null;

    if (!animated) return this.part.index;

    this.part.time += TICK_MS;
    // Delay comes off the FRAME being shown. Animation.cpp:58-102 reads it per
    // frame and falls back to 100 when it is missing or zero -- and the numbers
    // differ per frame, which is the whole reason a pose does not tick at a
    // constant rate.
    const current = this.part.frames[this.part.index];
    const delay = current?.delay || DEFAULT_DELAY_MS;
    if (this.part.time > delay) {
      this.part.time = 0;
      this.part.index = (this.part.index + 1) % Math.max(1, this.frameCount);
    }
    return this.part.index;
  }

  /** Whether there is a composed look to draw at all. */
  get ready() { return !!this.part && this.part.frames.length > 0; }

  /** The composed parts of one frame, in the server's paint order. */
  partsAt(index) { return this.part?.frames[index]?.parts ?? []; }

  /** The anchors of one frame — the five the C++ derives per stance AND frame. */
  anchorsAt(index) { return this.part?.frames[index] ?? null; }

  /**
   * Where the composed look goes, in world units.
   *
   * The origin is the composed look's own, not a guess about the centre of a
   * box. MapleCharacter.cs:1352/:1355 is `origin.X = -wzOrigin.X` and
   * `origin.Y = -wzOrigin.Y`, with the mirrored case (:1348) being
   * `wzOrigin.X - ImageWidth`; drawImage's origin is top-left, so blitting at
   * `(X + origin.X, Y + origin.Y)` puts the frame's origin exactly on the
   * character's own position -- which is where its FEET are, because that is
   * what a foothold's Y is.
   *
   * Each part's own `screen` already carries the anchor the server applied, so
   * the whole look shares ONE placement rectangle: the union the server reported,
   * with the character's feet at its origin. Nothing here re-derives a per-part
   * offset, because that is the arithmetic that was wrong once already.
   *
   * @param {number} x the character's world X
   * @param {number} y the character's world Y
   * @param {number} dir 1 facing right, -1 facing left
   * @returns {{x: number, y: number, w: number, h: number, flip: boolean}}
   */
  placement(x, y, dir, index = this.part?.index ?? 0) {
    const parts = this.partsAt(index);
    if (!parts.length) {
      return { cx: x, cy: y, w: 0, h: 0, flip: dir > 0, originX: 0, originY: 0 };
    }

    // The origin of a composed look is the character's own position, and the
    // server has already placed every part relative to it, so the box is what
    // falls out of the parts: their union, measured from that origin. Nothing
    // here re-derives a per-part offset — that is the arithmetic that was wrong
    // once already.
    let minX = parts[0].screen.x;
    let minY = parts[0].screen.y;
    let maxX = parts[0].screen.x + parts[0].width;
    let maxY = parts[0].screen.y + parts[0].height;
    for (const p of parts) {
      if (p.screen.x < minX) minX = p.screen.x;
      if (p.screen.y < minY) minY = p.screen.y;
      if (p.screen.x + p.width > maxX) maxX = p.screen.x + p.width;
      if (p.screen.y + p.height > maxY) maxY = p.screen.y + p.height;
    }

    return {
      cx: x,
      cy: y,
      w: maxX - minX,
      h: maxY - minY,
      // Mirrored when the character faces RIGHT, and that is the opposite of the
      // obvious guess, so here is the whole chain it comes from.
      //
      // `MapleCharacter.cs:1649-1652` sets `FlipX = true` when Right is held, so
      // FlipX=true IS the right-facing body. `MapleCharacter.cs:1340-1353` then
      // picks the per-part draw flip from it:
      //
      //     case true:  this.Flip = -1;   // right-facing  -> mirrored
      //     case false: this.Flip =  1;   // left-facing   -> as stored
      //
      // The stored art therefore faces LEFT, and the right-facing form is its
      // mirror. Writing `dir < 0` here drew the right-facing figure from
      // left-facing pixels — the character walked the correct way while looking
      // the wrong way, which is the whole of "the art is backwards when walking".
      // Same inverted sign as the attack box's `facingLeft ? Mirror : Box`.
      flip: dir > 0,
      originX: minX,
      originY: minY,
    };
  }

  /**
   * One part's screen rectangle, in world units.
   *
   * Each part's `screen` is its own origin relative to the CHARACTER, already
   * shifted by the anchor the server applied — the head's by get_head_position,
   * the face's by getfacepos, a hair's by gethairpos minus its own brow.
   *
   * Facing is a mirror, not a second set of art (MapleCharacterEx.cs:130/134),
   * so the flip negates the horizontal offset and nothing else: a part `dx` to
   * the right of the character is `dx` to the left when mirrored. Anchoring the
   * mirror on the part's own left edge rather than on the composed box is what
   * keeps the character's own origin on the character.
   *
   * @param {object} at the value `placement()` returned for this draw
   * @param {object} part one entry of `partsAt(index)`
   */
  rectOf(at, part) {
    // screen.* is already relative to the character's own position -- the server
    // put the feet at screen.y = 0 -- so the part goes at that offset directly.
    //
    // Subtracting originX/originY here re-anchored the box on its own top-left
    // corner, which put at.cy on the character's HEAD instead of its feet: the
    // figure rendered a full body-height below the surface it was standing on,
    // and a mirrored figure at the left edge of the view went entirely
    // off-canvas.
    const dx = part.screen.x;
    const dy = part.screen.y;
    return {
      x: at.flip ? at.cx - dx - part.width : at.cx + dx,
      y: at.cy + dy,
      w: part.width,
      h: part.height,
    };
  }
}
