/**
 * Draws a walk on the map it happens on.
 *
 * This exists because the simulator's verdict is a sentence, and a sentence with
 * nothing behind it cannot be checked. Four bugs in a row passed every automated
 * check while the character stood still: 18,000 frames, a jump that peaked at
 * 0.3px, a handover that never happened. Nothing was wrong with the numbers —
 * the numbers were correct statements about a character that did not move.
 *
 * So the walk is drawn. The ground comes from /simulate/geometry as plain
 * segments, the path from /simulate/trace as one point per frame. If the figure
 * stands still while the trace says it moved, that is visible immediately and
 * cannot be argued away by a passing test suite.
 *
 * NOTE ON CITATIONS. The `.cs`/`.js` citations below name MapleNecrocer
 * (C:\MUWORK\GAME\_necrocer_src\MapleNecrocer) -- this port's own source,
 * which has no counterpart in the authoritative reference: clean-client
 * (C:\MUWORK\GAME\MPSH\clean-client\src) contains zero `.cs` files. Where a
 * MapleNecrocer citation disagrees with clean-client, clean-client wins -- see
 * pm/PARITY.md, "The C++ wins". These were not re-audited for content; some
 * record a port's deliberate divergence.
 */

import { t } from './i18n.js';
import { el, clear, toast, toastError, modal } from './ui.js';
import { api } from './api.js';
import { Avatar, actionFor, PICKABLE_SLOTS } from './avatar.js';
import { MobSpriteCache } from './mobsprite.js';
import { glyphsFor as damageGlyphs } from './damagenumber.js';

/** Colour per jump state, matching the states the backend reports. */
const ACTIVE = {
  walk:   '#5ec269',
  rise:   '#f0c674',
  fall:   '#e06c6c',
  climb:  '#6cb6f0',
  'climb-top': '#8a7fd0',
};

const INK = {
  ground:  '#4a5568',
  wall:    '#e07a5f',
  ladder:  '#5b8def',
  portal:  '#9ae6b4',
  bounds:  '#718096',
  trail:   '#3182ce',
  head:    '#ffd166',
  text:    '#a0aec0',
};

// Damage numbers, copied from Elem8100/MapleNecrocer Client/DamageNumber.cs
// (MIT). The frame clock is this project's: livewalk.js steps a frame every
// 1/60s, and the client steps one every 17ms (SpriteEngine's `FTime += 17`,
// ~58.8fps). The client's counters below are per ITS tick, so they are scaled
// by FRAME_RATIO rather than taken raw -- 50 client frames is 850ms there and
// 833ms here, and the difference shows as a number that lingers.
//
//   Create():  Len = digits;  Mid = (Len * 28) / 2;  X = AX - Mid / 2
//   DoDraw():  W = 20 per glyph, staggered OffY = -5 on odd indices
//              x = X + I*W - origin.x - Camera.X
//              y = Y - origin.y - Camera.Y + OffY
//   DoMove():  Y -= 0.5f per tick;  Counter > 50 -> Alpha -= 6;  Alpha < 10 -> dead
const DMG_GLYPH_STEP = 20;        // DoDraw: W = 20
const DMG_STAGGER = 5;            // DoDraw: OffY = -5 on odd indices
const DMG_CENTER_UNIT = 28;      // Create: Mid = (Len * 28) / 2
const DMG_RISE_PER_TICK = 0.5;   // DoMove: Y -= 0.5f

/**
 * How far above the reported point a damage number is drawn, in world pixels.
 *
 * The server places a number at the victim's own `head` node
 * (`MobFrameGeometry.HeadAt`), which on this archive lands INSIDE the mob's
 * body: reported as "too low" rather than as sitting on its head. The client's
 * `DamageNumber.Create` takes `Y` straight from the caller and applies no lift
 * of its own, so the offset belongs here rather than in the physics — but it
 * belongs HERE and not in `HeadAt`, because the head node is also what decides
 * a mob's hit box, and moving that would move the hit box with it.
 */
const DMG_HEAD_LIFT = 42;

/**
 * Whether the map's diagnostic geometry is painted: foothold lines, wall lines,
 * ladder ropes, portal markers and the dashed boundary box.
 *
 * OFF by default, and that is a change. The live view used to paint every one
 * of them, which put 114 foothold strokes on a single map (footholds are the
 * scene's own list, `buildDrawList`, so the count is the map's) under the art
 * and over the character, and read as a wireframe debugging view rather than
 * the game. Reported as "地圖會有很多邊界線之類的東西 那不用顯示".
 *
 * A toggle rather than a deletion because the lines are the only thing that
 * shows the walkable surface if a map's tile art fails to resolve, and because
 * the map editor's own view needs them. What is gone is the default, not the
 * capability -- the button sits in the simulator's control bar.
 */
const DMG_FADE_START = 50;       // DoMove: Counter > 50   (client ticks)
const DMG_FADE_STEP = 6;         // DoMove: Alpha -= 6
const DMG_DIE_BELOW = 10;        // DoMove: Alpha < 10 -> Dead()

// The client advances one DamageNumber tick every 17ms (SpriteEngine's
// `FTime += 17`). This project steps at 1/60s = 16.667ms, so an elapsed age in
// milliseconds is converted ONCE into the client's own tick count and every
// counter below is read straight off that. Doing it the other way round --
// rescaling each constant -- is what produced numbers that lingered a few
// percent too long.
const DMG_MS_PER_TICK = 17;

// How many ticks the number lives, from the client's own arithmetic:
//   50 ticks at full opacity, then Alpha drops 6 per tick from 255 and is
//   declared dead below 10:  (255 - 10) / 6 = 40.83 more ticks.
const DMG_FADE_TICKS = (255 - DMG_DIE_BELOW) / DMG_FADE_STEP;
const DMG_LIFE_TICKS = DMG_FADE_START + DMG_FADE_TICKS;    // ~90.83, i.e. 1544ms

/**
 * The only keys a skill hotkey may take, verbatim from the reference.
 *
 * SkillForm.Designer.cs:50 is a literal `Items.AddRange` of "A".."Z" -- twenty-six
 * letters, no digits and no function keys -- and `SkillForm.cs:173` parses the
 * chosen text with `Enum.Parse(typeof(Input), Char, true)`, which is what makes
 * the upper case cosmetic and the SET the real constraint. The server refuses
 * anything else and says so in `rejected`; offering exactly these is the client
 * half of that, and generating them from a range would be a second, driftable
 * copy of a list that is already written down.
 */
const SKILL_KEYS = [
  'A', 'B', 'C', 'D', 'E', 'F', 'G', 'H', 'I', 'J', 'K', 'L', 'M',
  'N', 'O', 'P', 'Q', 'R', 'S', 'T', 'U', 'V', 'W', 'X', 'Y', 'Z',
];

/**
 * One skill's icon, the third column of SkillForm.cs:51's grid.
 *
 * The reference reads the bitmap from `Skill/<job>.img/skill/<id>/icon`
 * (SkillForm.cs:107 and :128) and the server has already resolved that to a
 * `path` on the row, so it is a plain `/api/canvas` fetch rather than a search.
 *
 * Rendered at 24px and NOT through `lazySprite`. That loader exists to keep
 * thousands of thumbnails off the connection pool at once (media.js:88-95), and
 * this is 66 rows behind a dialog the user has to open deliberately -- the cap
 * would cost more in plumbing than it saves. `loading="lazy"` still does its own
 * job on the rows that are scrolled out of view.
 *
 * A missing icon is the reference's own empty cell: SkillForm.cs:108-110 adds
 * the row with a null bitmap when `String/Skill.img/<id>` is absent, so a
 * 404 here is a row the reference would also have shown without a picture, not a
 * failure worth reporting.
 */
const skillIcon = (skill) => {
  const path = skill.iconPath;
  if (!path) return el('span', { style: 'width:24px;height:24px;flex:0 0 24px' });
  const img = el('img', {
    src: `/api/canvas?path=${encodeURIComponent(path)}&v=me`,
    alt: '', loading: 'lazy', decoding: 'async', draggable: 'false',
    style: 'width:24px;height:24px;object-fit:contain;image-rendering:pixelated',
    onerror: (e) => { e.currentTarget.style.visibility = 'hidden'; },
  });
  return el('span', { style: 'width:24px;height:24px;flex:0 0 24px;display:flex' }, img);
};

/**
 * Which folder each pickable slot's art lives in, and which ids it takes.
 *
 * Both tables are the reference's, restated from EquipSlotTable.cs:92-113 --
 * `Cap` for both cap slots, `Weapon` for both weapon slots, the slot's own name
 * otherwise, with the id range that splits each pair. They are restated rather
 * than fetched because `/look/slots` returns a count and five samples, not the
 * filters, and a picker that offered ids the server will refuse would make
 * "equipment does not work" look like a bug in the equipment.
 *
 * The split: `Weapon-1` is everything in Weapon except the 0170 prefix, which
 * is a different client; `Weapon-2` is exactly the 0170 prefix. `Cap-1` is
 * 1000..1003 and `Cap-2` is 1004..1006, which is why a cap slot's count can be
 * zero while its folder has hundreds of images in it.
 */
const FOLDER_OF = {
  // Head and Body have NO folder -- the reference gives them an empty
  // CharacterDir (AvatarForm.cs:496-499) and enumerates the archive root. The
  // empty string here is what routes them there instead of into `/undefined`.
  Head: '', Body: '',
  'Weapon-1': 'Weapon', 'Weapon-2': 'Weapon',
  'Cap-1': 'Cap', 'Cap-2': 'Cap',
  Coat: 'Coat', Pants: 'Pants', Longcoat: 'Longcoat', Cape: 'Cape',
  Shield: 'Shield', Glove: 'Glove', Shoes: 'Shoes',
  'Hair-1': 'Hair', 'Hair-2': 'Hair',
  'Face-1': 'Face', 'Face-2': 'Face',
};

/**
 * Four-character id prefix a ROOT slot takes, or undefined for a folder slot.
 *
 * The inversion matters and is easy to get backwards. `AvatarForm.cs:559-562`
 * drops `0000*` from Head; `:563-566` drops `0001*` from Body. "Drops 0000" over a
 * set of eight-digit ids whose only prefixes are 0000 and 0001 means Head keeps
 * 0001* and Body keeps 0000*.
 */
const ROOT_PREFIX_OF = { Head: '0001', Body: '0000' };

/** Inclusive `imgId / 1000` range, or null for a slot that takes anything. */
const RANGE_OF = {
  'Cap-1': [1000, 1003], 'Cap-2': [1004, 1006],
  'Hair-1': [30, 56], 'Hair-2': [57, 85],
  'Face-1': [20, 23], 'Face-2': [24, 65],
};

/**
 * The thumbnail path for the slots that are NOT equipment items, relative to
 * `<Folder>/<id>.img/`. Everything else uses `info/icon`.
 *
 * MapleNecrocer splits its thumbnails exactly here (AvatarForm.cs:228-234) and
 * the split is not cosmetic. `Hair/` and `Face/` are CHARACTER ART, not item
 * folders: `f2/Hair/00030000.img/info` does not exist, so the generic
 * `info/icon` rule 404s on both and the picker would show an empty grid for the
 * two slots a reader is most likely to open first. What the benchmark shows
 * instead is the art itself:
 *
 *   Hair -> `default/hairOverHead`  (AvatarForm.cs:638-639)
 *   Face -> `default/face`          (AvatarForm.cs:633-634)
 *
 * `hairOverHead` and not `hair`: the over-head sheet is the one that includes
 * the long back strands, so a short bob and a long mane look different as
 * thumbnails even where `hair` would draw almost the same head.
 *
 * Head and Body sit at the archive root, so their prefix is no folder at all:
 *   Head -> `front/head`    (AvatarForm.cs:627)
 *   Body -> `stand1/0/body` (AvatarForm.cs:629)
 * Both are the character art itself, exactly like Hair and Face.
 */
const THUMB_PATH = {
  Head: 'front/head', Body: 'stand1/0/body',
  'Hair-1': 'default/hairOverHead', 'Hair-2': 'default/hairOverHead',
  'Face-1': 'default/face', 'Face-2': 'default/face',
};

/**
 * Second choice when THUMB_PATH misses, per slot.
 *
 * Measured over the open archive: 56 of the 1,518 sheets in `Hair/` (3.7%) have
 * no `default/hairOverHead` -- 00030030 through 00030085 range -- while every
 * one of the 1,518 has `default/hair`. Those sheets still carry `default`; they
 * just do not carry the over-head variant, so the fallback is a different node
 * in the same stance, not a different stance.
 *
 * MapleNecrocer does not need this: `WzUtils.GetBmp` returns null for a missing
 * node and `ImageListView.Items.Add(name, null)` quietly drops the entry. The
 * benchmark's grid therefore shows 56 fewer hairstyles than MapleBench would,
 * and the difference is invisible there because nothing is lost -- the tile just
 * is not there. A picker that silently omits 56 options is worse than one that
 * draws them, so the fallback is deliberate divergence, and `onerror` is where it
 * belongs: a second request for one image costs nothing next to pre-checking
 * 1,518 sheets on slot open.
 */
const THUMB_FALLBACK = {
  'Hair-1': 'default/hair', 'Hair-2': 'default/hair',
};

const ACCEPTED_BY = Object.fromEntries(PICKABLE_SLOTS.map((slot) => [slot, (name) => {
  const id = Number(name);
  if (!Number.isInteger(id)) return false;
  const root = ROOT_PREFIX_OF[slot];
  if (root) return String(id).padStart(8, '0').startsWith(root);
  if (slot === 'Weapon-1') return !String(id).padStart(8, '0').startsWith('0170');
  if (slot === 'Weapon-2') return String(id).padStart(8, '0').startsWith('0170');
  const range = RANGE_OF[slot];
  if (!range) return true;
  const bucket = Math.floor(id / 1000);
  return bucket >= range[0] && bucket <= range[1];
}]));

/**
 * Draw-order keys, copied from the client rather than designed here.
 *
 * MapleStory keeps one flat draw list sorted ascending by Z and paints it front
 * to back of the depth axis: the binary insert at SpriteEngine.cs:466-478 walks
 * right past every entry whose z is not greater than the newcomer, so a SMALLER
 * Z is painted FIRST and therefore sits BEHIND. Everything below is a copy of
 * the arithmetic that produces those Z values.
 *
 * The numbers are deliberately not tidied into a normalised 0..1 depth. The
 * *100000 stride is what gives a whole layer's art a private band while the
 * small offsets inside a band are what put an object under a tile at the same
 * depth, and a character under the next layer up. Collapsing them would change
 * which side of a wall the figure lands on.
 */
const Z_TILE = (layer, artZ) => layer * 100000 + artZ + 1000;   // MapTile.cs:33
const Z_OBJ = (layer, z) => layer * 100000 + z;                // MapObj.cs:82, :101
const Z_CHAR = (layer) => layer * 100000 + 60000;              // MapleCharacter.cs:561
// The mob's offset is 6000, not the character's 60000 — Mob.cs:191 sets
// `Z = FH.Z * 100000 + 6000`, and :548/:628/:705 recompute exactly that on
// every foothold change. Same layer stride, same formula, different constant:
// on the same layer a mob therefore draws BEHIND the character (6000 < 60000),
// which is what the client's single sorted draw list produces. An NPC uses
// 7000 (Npc.cs:117), so a mob draws behind an NPC there too. This is a fourth
// constant in the same scheme, not a second scheme: every one of them is
// `layer * 100000 + offset`.
const Z_MOB = (layer) => layer * 100000 + 6000;                // Mob.cs:191
const Z_LADDER = (page) => page * 100000 + 60000;              // MapleCharacter.cs:587
// MapPortal.cs:107 pins portals at a flat 1000000 -- above every map layer
// (nine layers at 100000 apart reach 900000) and below particles (10000000,
// MapParticle.cs:57). The map boundary is not a sprite the client draws at
// all; it is a marker like a portal and shares that depth.
const Z_MARKER = 1000000;

/**
 * A foothold's own depth: the number its layer node is named with.
 *
 * The reference reads it straight off the layer -- `FH.Z = Iter.Text.ToInt()`
 * where `Iter` walks the layer nodes (Foetholds.cs:246-247) -- and
 * /simulate/geometry carries that same layer name as the string MapSegmentDto
 * declares. A layer named something unparseable is 0 here, which is what
 * ToInt() hands the client too (FootholdPhysics.cs:181-184).
 */
function footholdLayerZ(f) {
  const n = parseInt(f.layer, 10);
  return Number.isFinite(n) ? n : 0;
}

/**
 * The FALLBACK figure, in WZ units, used only when the archive has no
 * character art at all. A character is not a dot: the whole complaint about this
 * view was that a walking character looked like a coloured circle, which cannot
 * be told apart from a marker. MapleStory's own player sprite is roughly 55px
 * wide and 79px tall (MapleCharacter.cs:133-134, Offset.Y = -79,
 * Offset.X = -40), so the box here matches that and layout() reserves room for
 * it.
 *
 * This box is a DRAWN shape, not a WZ sprite, and it is centred on the
 * character's own anchor -- which is the assumption TASK 7 exists to stop making
 * for the real thing. When avatar.js has composed a body, nothing here is drawn
 * and this is only used for headroom; when it has not, it is the figure and the
 * HUD says so by name.
 */
const FIGURE = { width: 55, height: 79, head: 13 };

/**
 * The FALLBACK mob, in WZ units, used only when no open archive draws the id.
 *
 * Roughly the character's own share of the screen but a little smaller, so a
 * mob reads as a separate creature rather than a second player: MapleStory's
 * ordinary mob sprites sit around 40x40 on a 55x79 player. This is the size of
 * the drawn slab and nothing else -- a real sprite is drawn at ITS OWN width
 * and height from the archive, which is why this constant only ever sizes the
 * fallback now.
 */
const MOB = { width: 40, height: 40 };

const INK_MOB = '#e05299';

/**
 * One walk, drawn on a canvas.
 */
export class WalkViewer {
  constructor() {
    this.root = el('div', { class: 'walkview' });
    // Middle button or space+drag pans the camera. Wired here, on the canvas, so
    // it works with no live session and before any control row is rendered.
    // `is-free`/`is-panning` are the only cursor states the stylesheet needs, and
    // they are driven from `cam.free` and the in-progress drag rather than from a
    // separate flag that could disagree with either.
    this.canvas = el('canvas', {
      class: 'walkview-canvas',
      style: 'width:100%;display:block;background:#1a202c;border-radius:4px;image-rendering:pixelated',
    });
    this.canvas.addEventListener('pointerdown', (event) => {
      if (event.button === 1 || this.spaceHeld) { event.preventDefault(); this.beginPan(event); }
    });
    this.canvas.addEventListener('pointermove', (event) => { this.movePan(event); });
    this.canvas.addEventListener('pointerup', (event) => { this.endPan(event); });
    this.canvas.addEventListener('pointercancel', (event) => { this.endPan(event); });
    this.canvas.addEventListener('contextmenu', (event) => {
      // Only while panning: suppressing it unconditionally would take the
      // browser's own menu away everywhere on the canvas.
      if (this.panPointer) { event.preventDefault(); this.endPan(event); }
    });
    this.hud = el('div', { class: 'walkview-hud' });
    this.controls = el('div', { class: 'walkview-controls' });
    // The equipment bar, its own row between the transport controls and the
    // HUD: it is wider than the controls, and folding twelve <select>s into
    // their flex-wrap row would push the play strip off the panel.
    this.equipHost = el('div', { class: 'walkview-equip' });
    // The character sheet, as its OWN row. Not folded into the HUD: that line is
    // a run-on of map and view facts, and a reader who wants to know what a
    // weapon did should not have to find it between "missing tile art" and a
    // foothold count. Everything in it is the server's — this draws, it decides
    // nothing.
    this.sheetRow = el('div', { class: 'walkview-sheet' });
    // Which slot the picker is showing. Cap-1 first: it is the slot whose
    // earlier dropdown default exposed the face-occlusion bug, so it is the one
    // worth being able to look at and change without scrolling. Persisted per
    // view rather than per session so reopening the tab comes back to the slot
    // you were choosing in, which is what the tabs are FOR.
    this.equipSlot = 'Cap-1';
    // The live driver, when one owns this viewer (LiveWalk registers itself in its
    // constructor). Null for a replayed trace, which has no session to tell about a
    // weapon. Declared HERE and not in load(): load() runs after the driver is
    // constructed and would clear the registration on every map change.
    this.live = null;
    this.godBox = null;
    this.root.append(this.canvas, this.controls, this.equipHost, this.sheetRow, this.hud);

    this.geometry = null;
    // The Z-ordered paint list, built once per map and reused by every frame.
    // See buildDrawList() for why this is a cache and not a per-frame sort.
    this.scene = null;

    // Diagnostic geometry (footholds, walls, ropes, portals, the boundary box) is
    // OFF by default. Undefined rather than false so that "never set" and
    // "switched off" are the same answer at every read site -- the checks are
    // `showOverlays === true`, never a truthiness test, so an uninitialised
    // field can never accidentally turn them on.
    // The NPC label's id line, ON by default.
    //
    // The reference has it behind `Map.ShowID`, a ViewForm checkbox
    // (ViewForm.cs / ViewForm.Designer.cs:47). There is no such checkbox here, so
    // the flag exists and defaults the way the reference's does -- MapleMap.cs:52
    // sets `ShowNpcName = true`, and nothing in the reference turns ShowID off
    // without being asked. Left as a flag so it can be turned off rather than
    // deleted, and because the name line's own gate (`hideName`) comes from the
    // archive, not from here.
    this.showNpcId = true;

    this.showOverlays = false;
    // Foothold id -> segment, so characterZ() reads a depth instead of scanning.
    this.footholdsById = new Map();
    this.ticks = [];
    this.frame = 0;
    // The mobs the server reported on the last frame. Written by whoever is
    // driving (LiveWalk) and read only by draw(), the same relationship the
    // ticks have — so a viewer with no live session draws no mobs rather than
    // inventing any.
    this.mobs = [];
    // Damage numbers waiting to be drawn: {x, y, value, miss, born}. Fed by
    // whoever is driving (LiveWalk) from each frame's `damage` array, and culled
    // by drawDamageNumbers once each has finished rising and fading. The same
    // relationship the mobs have -- no live session, no numbers.
    this.damageNumbers = [];
    // The chat balloon's art: the nine edges, the arrow and the text colour the
    // server named. `false` once asked and found absent — the NPC has nothing to
    // say on a map with no UI.nx open, which is a real state, not a fault.
    this.balloon = null;
    this.balloonLoading = false;
    // The live driver's character sheet, handed over whole: {level, strength,
    // maxHp, watk, minDamage, …}. Null for a replayed trace, which has no
    // session to report one — the same relationship the mobs have, and for the
    // same reason: no live session, no numbers.
    this.sheet = null;
    this.playing = false;
    this.speed = 4;
    this.timer = null;
    // The camera: the world point the canvas origin sits on. It used to be the
    // framed map's own corner, fixed for every map; it is now a transform the
    // character walks through. `placed` says the camera has been put anywhere
    // real yet -- the first follow() snaps instead of lerping in from 0,0.
    this.cam = { x: 0, y: 0, placed: false, free: false };
    // An in-progress camera drag: {id, x, y} in CSS pixels. Null when none.
    this.panPointer = null;
    // Space+drag, the second pan gesture. HELD rather than toggled, so it backs
    // out by letting go — the same reasoning pixeleditor.js:840-848 uses.
    this.spaceHeld = false;
    window.addEventListener('keydown', (event) => {
      if (event.code === 'Space') this.spaceHeld = true;
    });
    window.addEventListener('keyup', (event) => {
      if (event.code === 'Space') this.spaceHeld = false;
    });
    window.addEventListener('blur', () => { this.spaceHeld = false; });
    // Multiplied into the fit-to-width scale. 1 IS the whole map at the width of
    // the canvas, which is what this view has always drawn and what loadVisible
    // and the CLI checks assume; above 1 the frame is narrower than the map,
    // which is what gives the camera somewhere to travel.
    this.zoom = 1;
    // layout()'s last result. follow() runs once per physics frame and must not
    // re-scan every foothold to learn the viewport size; draw() refreshes it.
    this.viewBox = null;
    // The character, composed from the archive's own art. Held here rather
    // than built per frame because composition is a network round trip and the
    // frame clock inside it has to survive from one frame to the next.
    this.avatar = new Avatar();
    // The action the avatar last composed, so draw() only asks when it changes.
    this.avatarState = '';
    // The same shape for the mobs and NPCs: one sprite per id+kind, decoded
    // once, held across frames. Released with the map's art, for the same
    // reason the character's is -- every Image is a decoded surface and two
    // maps' worth at once is what pushed the server to 2.8 GB.
    this.mobSprites = new MobSpriteCache();
    this.mobSprites.onReady = () => this.draw();
  }

  /**
   * Load a map: its walkable geometry AND its picture.
   *
   * The picture is the point. This view started by drawing foothold lines on a
   * blank canvas, which is legible but is not the map -- someone looking at it
   * cannot tell whether they are looking at Henesys or a corridor in an
   * unreleased map, and a walk drawn on the wrong picture looks equally
   * authoritative. The editor already assembles every tile into a drawable
   * list (`/api/mapedit/doc`), so this asks for the same document rather than
   * inventing a second renderer.
   */
  /**
   * The open file id whose archive carries character art, or ''.
   *
   * Named on the file, never on its id: ids are per session and the same id is
   * a different archive on a different server.
   */
  async characterFileId() {
    try {
      const open = (await api.files()) ?? [];
      const rows = Array.isArray(open) ? open : (open?.files ?? []);
      return rows.find((f) => /Character\.nx$/i.test(f.name ?? ''))?.id ?? '';
    } catch {
      return '';
    }
  }

  async load(path) {
    // Drop the previous map's decoded bitmaps before asking for the next one's.
    // Every Image holds a decoded surface, and the server caches them until its
    // own sweep runs -- which, at 2.8 GB, released nothing. Holding two maps'
    // art at once is what pushed it over.
    this.releaseArt();
    // The avatar's frames are decoded bitmaps of exactly the same kind, and it
    // is re-composed per action, so it is released on the same boundary.
    this.avatar?.release();
    this.avatarState = '';
    this.avatarPending = null;
    // Cleared WITH the release above, not left holding a frame from a map that no
    // longer exists -- a retained frame is only a stand-in while a NEW pose loads,
    // and across a map change there is no new pose coming.
    this.avatarDrawn = null;
    // Same boundary for the cast's sprites: their decoded bitmaps belong to the
    // archives this map was opened against, and a new map can resolve the same
    // container to a different image.
    this.skillFx?.clear();

    this.path = path;

    // Both of these belong to the map that was just dropped. `_visibleCheckAt`
    // is a wall-clock stamp, so carrying it over would let the NEW map's first
    // draw() be throttled away by the old map's scan having just finished --
    // and outside the live walk `draw()` only runs on user action, so the tiles
    // it skipped could stay unfetched until the user happened to do something.
    // `_visibleLoading` is still a live promise over the old map's document.
    this._visibleCheckAt = undefined;
    this._visibleLoading = false;
    // Bumped so any load still in flight over the previous map's document can
    // tell, when it resolves, that it no longer owns the flag.
    this._mapGen = (this._mapGen ?? 0) + 1;

    // The map opens below. The OTHER archives the walk needs do not: only
    // /api/files/open puts an archive into the session, and nothing in the walk
    // did that. So on a server where the user had opened a map but not its
    // Character, /api/simulate/look answered 404 ("no archive with character art
    // is open"), the avatar reported "no {0} frames under it", and the figure on
    // screen had no face and no hands -- not because the composition failed but
    // because there was nothing to compose FROM.
    //
    // These are opened by NAME, from the same folder the open map came out of,
    // and each one is skipped when the session already holds it. Base.nx is
    // needed too and not as an optional: CharLookComposer reads Base/zmap.img for
    // the paint order, and without it every layer comes back z=0 and the order
    // it sorts into is the order the composer happened to collect.
    // The archive that carries the bodies, by name. Id per compose is a
    // question asked 60 times a second that always has the same answer.
    this.avatar.fileId = await this.characterFileId();

    // The map must be open before /api/mapedit/doc will answer: documents do
    // not survive a restart or an eviction, and the endpoint refuses a path it
    // has never seen. Fetching doc without opening first returned 404, this.doc
    // stayed null, and every tile silently vanished -- the walk drew bare
    // geometry on an empty background and looked like a map that had no art.
    // Opening is idempotent, so doing it on every walk is safe and it also
    // re-opens after the server drops the document.
    try {
      const openRes = await fetch('/api/mapedit/open', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ path }),
      });
      if (!openRes.ok) {
        throw new Error(`${openRes.status} ${await openRes.text()}`);
      }
    } catch (err) {
      // Art is a bonus; the verdict above is the answer. A map that will not
      // open for drawing still walks, so this is recorded, not thrown.
      this.docError = err.message;
    }

    const [geoRes, docRes] = await Promise.all([
      fetch(`/api/simulate/geometry?path=${encodeURIComponent(path)}`),
      fetch(`/api/mapedit/doc?path=${encodeURIComponent(path)}`),
    ]);
    if (!geoRes.ok) throw new Error(`${geoRes.status} ${geoRes.statusText}`);

    // The API serializes camelCase (Program.cs sets JsonNamingPolicy.CamelCase
    // for every response), but the drawing code below was written against the
    // C# property names -- so `g.Footholds` was undefined and layout() threw
    // "g.Footholds is not iterable". Normalised once here, at the boundary,
    // rather than renaming every read site: one place to keep in sync with the
    // wire format, and the drawing code keeps the names the DTO declares.
    const geo = await geoRes.json();
    this.geometry = {
      ...geo,
      Footholds: geo.footholds ?? geo.Footholds ?? [],
      Ladders: geo.ladders ?? geo.Ladders ?? [],
      Portals: geo.portals ?? geo.Portals ?? [],
      Bounds: geo.bounds ?? geo.Bounds ?? null,
    };

    // Footholds indexed by id. A trace tick names the foothold it is standing
    // on, and the character's Z is that foothold's depth, so characterZ() reads
    // this instead of scanning every segment on every frame at 60Hz. Ids are
    // node names and a map is free to reuse one (MapFootholdDto says so), so a
    // duplicate keeps the FIRST -- which is the one the walk started on, and a
    // later entry with the same id would put the figure on the wrong band.
    this.footholdsById = new Map();
    for (const f of this.geometry.Footholds) {
      if (f.id && !this.footholdsById.has(f.id)) this.footholdsById.set(f.id, f);
    }

    // A missing doc used to be a silent blank map. Keep the reason: "no art"
    // and "the walk is broken" look identical from the outside.
    this.doc = docRes.ok ? await docRes.json() : null;
    if (!this.doc) {
      this.docError ||= `map document unavailable (${docRes.status} ${docRes.statusText})`;
    }
    this.images = new Map();
    if (this.doc) this.loadArt(this.doc);

    // The Z-ordered scene, built once per map. Rebuilt at the end of loadArt()
    // too, because a tile's depth comes from its art's z and the art index is
    // only complete once the document is in hand.
    this.scene = this.buildDrawList();

    this.ticks = [];
    this.frame = 0;
    // A new map is a new place, including its mobs: leaving the last map's
    // list on screen would draw Henesys' spawns standing on Orbis.
    this.mobs = [];
    // And their sprites. Every Image is a decoded surface; holding two maps'
    // worth of mob art at once is what pushed the server to 2.8 GB. Released
    // here rather than in releaseArt(), which runs before the document is
    // fetched, because a mob sprite belongs to the map's spawns rather than to
    // the map's tiles -- the two are released a moment apart and for different
    // reasons.
    this.mobSprites.release();
    // The camera from the last map is meaningless
    // here, so it is unplaced rather than somewhere arbitrary and follow()
    // snaps on the first frame. Zoom is NOT reset -- it is the user's choice and
    // keeping it is what stops every map lookup throwing the zoom away.
    this.cam.placed = false;
    this.viewBox = null;
    this.stop();
    this.draw();
  }

  /** Let go of every decoded bitmap this viewer is holding. */
  releaseArt() {
    if (!this.images) return;
    for (const slot of this.images.values()) {
      if (slot?.img) {
        // Clearing src is what actually frees the decoded surface; dropping the
        // reference alone can leave it on the element until the next GC.
        try { slot.img.src = ''; } catch { /* already detached */ }
        slot.img = null;
        slot.ok = false;
      }
    }
    this.images = new Map();
  }

  /** Fetch every distinct picture the map's tiles and objects refer to. */
  loadArt(doc) {
    const art = doc.art ?? {};
    const pending = [];

    // A map can name several hundred distinct pictures, and asking for all of
    // them at once is what killed the server: it answered every /api/canvas
    // request, the decoded bitmaps stayed in the image cache, and the process
    // went out at 2.8 GB. Measured from the crash log -- "released 0 images,
    // 2854 MB -> 2854 MB" -- so the sweep had nothing it could give back.
    //
    // Load the ones the viewport actually needs, a screenful at a time, and let
    // the rest arrive if the walk goes somewhere that needs them.
    const wanted = new Set();
    for (const layer of doc.layers ?? []) {
      for (const tile of layer.tiles ?? []) if (tile.art) wanted.add(tile.art);
      for (const obj of layer.objs ?? []) if (obj.art) wanted.add(obj.art);
    }
    this.artKeys = [...wanted];

    // Anything already cached is free; anything new is fetched in batches so no
    // single map asks for hundreds of pictures simultaneously.
    const fresh = this.artKeys.filter((key) => !this.images.has(key));
    for (const key of fresh) {
      const meta = art[key];
      this.images.set(key, {
        img: null, ok: false,
        missing: !meta || meta.missing || !meta.path,
        meta: meta ?? null,
      });
    }

    const loadKey = (key) => new Promise((resolve) => {
      const meta = art[key];
      if (!meta || meta.missing || !meta.path) { resolve(); return; }
      const slot = this.images.get(key);
      const img = new Image();
      img.onload = () => { slot.img = img; slot.ok = true; resolve(); };
      img.onerror = () => { slot.missing = true; resolve(); };
      img.src = `/api/canvas?path=${encodeURIComponent(meta.path)}&v=me`;
    });

    return this.loadVisible(() => { this.drawn = (this.drawn ?? 0) + 1; this.draw(); })
      .then(() => loadKey(this.artKeys[0]))
      // A tile's depth key reads its picture's z out of the art index, so the
      // scene is ordered against the index this call just filled in -- not
      // against an empty one.
      .then(() => { this.scene = this.buildDrawList(); })
      .then(() => pending);
  }

  /**
   * Fetch the pictures that fall inside the viewport, in batches.
   *
   * @param {(done: number, total: number) => void} [onBatch] progress callback
   */
  async loadVisible(onBatch) {
    // The camera may already have moved, so this must go through layout() and
    // not read this.cam directly -- otherwise the visible set is computed from a
    // position the frame is never drawn at.
    const view = this.layout();
    if (!view || !this.artKeys?.length) return;
    const cssW = view.cssWidth;
    const cssH = view.cssHeight;
    const s = view.scale;

    const inside = [];
        for (const layer of this.doc?.layers ?? []) {
          const groups = [layer.tiles, layer.objs];
          for (let g = 0; g < groups.length; g++) {
            const list = groups[g];
            if (!list) continue;
            for (let i = 0; i < list.length; i++) {
              const item = list[i];
              const px = (item.x - view.minX) * s;
              const py = (item.y - view.minY) * s;
              if (px > -120 && px < cssW + 120 && py > -120 && py < cssH + 120) {
                if (item.art) inside.push(item.art);
              }
            }
          }
        }

    const unique = [...new Set(inside)]
      .filter((key) => this.images?.get(key) && !this.images.get(key).ok && !this.images.get(key).missing);

    const BATCH = 16;
    const art = this.doc?.art ?? {};
    for (let i = 0; i < unique.length; i += BATCH) {
      const slice = unique.slice(i, i + BATCH);
      await Promise.all(slice.map((key) => new Promise((resolve) => {
        const meta = art[key];
        const slot = this.images.get(key);
        if (!meta || meta.missing || !meta.path) { resolve(); return; }
        const img = new Image();
        img.onload = () => { slot.img = img; slot.ok = true; resolve(); };
        img.onerror = () => { slot.missing = true; resolve(); };
        img.src = `/api/canvas?path=${encodeURIComponent(meta.path)}&v=me`;
      })));
      onBatch?.(Math.min(i + BATCH, unique.length), unique.length);
      // Yield so the browser can paint what arrived so far instead of sitting
      // blocked while several hundred requests finish.
      await new Promise((r) => setTimeout(r, 0));
    }
  }

  /** Load the per-frame path and stop at the end. */
  async loadWalk({ x = 0, y = 0, dir = 1, climb = 0, steps = 300, jumpEvery = 0 } = {}) {
    const q = new URLSearchParams({
      path: this.path, x, y, dir, climb, steps,
      jumpEvery: jumpEvery || 0, dropAtStep: 0,
    });
    const response = await fetch(`/api/simulate/trace?${q}`);
    if (!response.ok) throw new Error(`${response.status} ${response.statusText}`);
    const data = await response.json();
    this.ticks = data.ticks ?? [];
    this.error = data.error ?? '';
    this.startFoothold = data.startFoothold ?? '';
    this.frame = 0;
    this.draw();
    this.renderControls();
    return data;
  }

  /**
   * Stand the batch replay down and give the viewer to whoever comes next.
   *
   * loadWalk and the live body both write the SAME two fields -- ticks and
   * frame -- and each redraws the canvas as it goes. Loading a replay after a
   * body has been placed therefore overwrites the trail that body is painting,
   * and it leaves its play/seek strip on screen driving a frame counter the
   * body is moving at 60Hz. There is no merge and no priority rule that saves
   * this: the two answers disagree, so whichever ran last wins, and at 60Hz
   * "whichever ran last" is not a flicker, it is the wrong picture.
   *
   * The handover is therefore explicit rather than implied by call order --
   * whoever owns this viewer owns it alone until they hand it over.
   */
  releaseWalk() {
    this.stop();
    this.ticks = [];
    this.frame = 0;
    this.renderControls();
    this.draw();
  }

  play() {
    if (!this.ticks.length || this.playing) return;
    this.playing = true;
    const tick = () => {
      if (!this.playing) return;
      // 60Hz is the client's frame rate; `speed` frames per real tick.
      this.frame += this.speed;
      if (this.frame >= this.ticks.length) {
        this.frame = this.ticks.length - 1;
        this.stop();
      }
      this.draw();
      this.timer = setTimeout(tick, 1000 / 60);
    };
    tick();
  }

  pause() { this.stop(); }

  stop() {
    this.playing = false;
    if (this.timer) { clearTimeout(this.timer); this.timer = null; }
  }

  reset() { this.stop(); this.frame = 0; this.draw(); }

  /** Step the frame forward, for the single-step buttons. */
  seek(frame) {
    this.stop();
    this.frame = Math.max(0, Math.min(this.ticks.length - 1, frame));
    this.draw();
  }

  /**
   * What each pickable slot may be given, from the archive that is open.
   *
   * `/look/slots` samples five per slot (SimulatorEndpoints.cs:209-217), which
   * is enough to prove the wiring and not enough to be a picker, so each slot's
   * own folder is listed directly instead. One request per slot, all at once,
   * and cached -- the answer only changes when the open archives do.
   *
   * A slot whose folder will not list comes back empty and is dropped from the
   * picker rather than offered as a control that can only ever resolve to
   * nothing.
   *
   * @returns {Promise<object>} slot name -> image names, sorted
   */
  async loadEquipSlots() {
    // Only a NON-EMPTY listing is cached. The bar is first built from
    // renderControls(), which runs when the map loads -- before the avatar has
    // composed even once, so `avatar.source` is still '' and there is no folder
    // to list. Caching that empty answer would leave the bar permanently blank
    // on a perfectly good archive, which reads as "this feature is broken"
    // rather than as "we asked too early".
    if (this.equipSlots && Object.keys(this.equipSlots).length) return this.equipSlots;

    // The composer already worked out which archive carries character art and
    // reported it as `source` (LookService.cs:232), so reusing that is one round
    // trip fewer than asking /api/files and matching on a name.
    const fileId = this.avatar?.fileId || this.avatar?.source || '';
    if (!fileId) return {};

    const namesOf = async (slot) => {
      const folder = FOLDER_OF[slot];
      if (!fileId) return [];
      // An empty folder is Head or Body, whose images sit AT the archive root
      // (AvatarForm.cs:538-547), not inside a folder. Treating the empty string
      // as "no such slot" -- which the old `!folder` guard did -- made both
      // invisible while reporting nothing wrong.
      const rows = await api.children(folder ? `${fileId}/${folder}` : fileId).catch(() => []);
      return (rows || [])
        .map((r) => String(r.name || '').replace(/\.img$/i, ''))
        // The id rule is the SERVER's (EquipSlotTable.cs:53-64) and it is applied
        // there too when the slot is composed. Filtering here is only so the
        // picker does not offer an id the server will refuse; a slot whose
        // filter this table gets wrong shows up in `unresolved`, never silently.
        .filter((n) => /^\d+$/.test(n) && ACCEPTED_BY[slot]?.(n))
        .sort();
    };

    const entries = await Promise.all(PICKABLE_SLOTS.map(async (slot) => {
      const ids = await namesOf(slot);
      return ids.length ? [slot, ids] : null;
    }));

    this.equipSlots = Object.fromEntries(entries.filter(Boolean));
    return this.equipSlots;
  }

  /**
     * The equipment picker: slot tabs above a wall of thumbnails.
     *
     * It was twelve `<select>` dropdowns, and that was reported as unusable --
     * you cannot recognise a hat by reading "01000003.img". The benchmark's own
     * avatar editor is a grid of pictures for the same reason: MapleNecrocer's
     * `AvatarForm.cs:107` declares 21 `ImageListView` panels, one per slot, and
     * `AvatarForm.cs:642-643` fills them with WZ canvases rather than labels.
     *
     * WHERE THE THUMBNAILS COME FROM is the part worth copying exactly, because
     * the benchmark splits them in two (AvatarForm.cs:228-234):
     *
     *   - equipment slots use `Character/<Folder>/<id>.img/info/icon`, the item's
     *     own icon -- the same path its equipped-list column uses
     *     (`AvatarForm.cs:161`);
     *   - the four "造型" parts use the piece of the character art itself --
     *     Head -> `<id>.img/front/head`, Body -> `<id>.img/stand1/0/body`,
     *     Hair -> `<id>.img/default/hairOverHead`, Face -> `<id>.img/default/face`
     *     -- so you see the shape you are picking, not a generic box.
     *
     * A flat `info/icon` lookup for Hair/Face would 404: those are body folders,
     * not items, and they carry no `info` node at all. Verified against the open
     * archive: `f2/Face/00020000.img/default/face` is 26x16,
     * `f2/Hair/00030000.img/default/hairOverHead` is 38x22.
     *
     * NOTHING IS DOWNSCALED. AvatarForm.cs carries no Resize/Scale/Thumbnail
     * call anywhere -- `ImageListView.ThumbnailSize` does the fitting at paint
     * time, and the raw WZ canvas goes in. Same here: the `<img>` is the canvas
     * at native size and CSS fits it into the tile. Rescaling on the server would
     * make the picker slower than the thing it is fixing.
     *
     * One slot is listed at a time. `Cap/` alone carries 908 images and the
     * benchmark builds all 21 grids at open; doing that here is what turns a
     * picker into the "quick map switch hangs the app" bug all over again.
     */
    async renderEquipBar() {
      const host = this.equipHost;
      if (!host) return;
      const slots = await this.loadEquipSlots();
      // A re-render can land after the bar was torn down, and appending to a
      // detached node is the silent no-op that leaves an empty bar on screen.
      if (host !== this.equipHost) return;

      clear(host);
      const offered = PICKABLE_SLOTS.filter((s) => slots[s]?.length);
      if (!offered.length) {
        host.appendChild(el('span', { class: 'walkview-equip-none' },
          [t('no equipment folders in the open archives')]));
        return;
      }

      // `slotMap()`, not `.equip`. Hair and face are character fields (`.hair`,
      // `.face`) that the server also accepts as the `Hair-1` / `Face-1` slots,
      // so a character can be wearing both while `.equip` holds neither -- the
      // pickers then showed "Strip" on a head that had hair on it. slotMap() is
      // the one view that agrees with what was actually composed.
      const worn = this.avatar?.slotMap?.() ?? {};

      const grid = el('div', { class: 'walkview-equip-grid' });
          const tabs = el('div', { class: 'walkview-equip-tabs' });

          // `Cap-1` alone is 908 items and `Hair-1` 1,518. Rendering every tile at
          // once means 908 concurrent /api/canvas requests the moment the picker
          // opens, which is the same request storm that hangs the map view (see
          // `loadVisible`, walkview.js:438) -- the browser spends its connection pool
          // on icons and the frame loop starves. `loading="lazy"` does NOT save it
          // here: measured, all 908 came back `complete` immediately, because a
          // scroll container's children are all "near" the viewport by the spec's
          // margin rules.
          //
          // So the grid is built in pages of 120, appended when the scroller nears its
          // end. Deterministic, testable (tile count is observable) and no IntersectionObserver.
          const PAGE = 120;
          let shown = 0;

          const appendPage = () => {
            const ids = slots[this.equipSlot] ?? [];
            const wornHere = worn[this.equipSlot];
            const folder = FOLDER_OF[this.equipSlot];
            const fileId = this.avatar?.fileId || this.avatar?.source || '';
            // Head/Body: no folder segment, so `${fileId}/${folder}/${img}`
            // would request `f2//00012000.img/front/head` and 404 on the double
            // slash. This is the one place the root slots differ in SHAPE, not
            // just in path -- worth isolating to a single `base`.
            const base = folder ? `${fileId}/${folder}` : fileId;

            // Strip first, always: it is the only way back to "nothing here", and
            // keeping it at the head of its own slot is how you find it.
            if (shown === 0) {
              grid.appendChild(el('button', {
                class: 'walkview-equip-tile walkview-equip-tile-strip'
                  + (wornHere ? '' : ' is-worn'),
                type: 'button',
                'data-tip': `${t('Strip')} ${this.equipSlot}`,
                onclick: () => this.chooseEquip(this.equipSlot, ''),
              }, [el('span', { class: 'walkview-equip-strip' }, [t('Strip')])]));
            }

            const slice = ids.slice(shown, shown + PAGE);
            for (const id of slice) {
                        const img = `${id}.img`;
                        const primary = THUMB_PATH[this.equipSlot]
                          ? `${base}/${img}/${THUMB_PATH[this.equipSlot]}`
                          : `${base}/${img}/info/icon`;
                        const fallback = THUMB_FALLBACK[this.equipSlot]
                          ? `${base}/${img}/${THUMB_FALLBACK[this.equipSlot]}`
                          : null;
                        // Retry once on the fallback node, then give up and hide the <img>
                        // rather than leaving a broken-image glyph in the tile. `dataset.retried`
                        // is what stops the retry looping: an <img> that 404s fires onerror
                        // again for the fallback src, and again forever without a guard.
                        const thumb = el('img', {
                          src: `/api/canvas?path=${encodeURIComponent(primary)}&v=me`,
                          alt: '',
                          decoding: 'async',
                          draggable: 'false',
                          onerror: (e) => {
                            const node = e.currentTarget;
                            if (fallback && node.dataset.retried !== '1') {
                              node.dataset.retried = '1';
                              node.src = `/api/canvas?path=${encodeURIComponent(fallback)}&v=me`;
                              return;
                            }
                            node.style.visibility = 'hidden';
                          },
                        });
                        grid.appendChild(el('button', {
                          class: 'walkview-equip-tile' + (wornHere === img ? ' is-worn' : ''),
                          type: 'button',
                          'data-slot': this.equipSlot,
                          'data-tip': `${id} — ${t(this.equipSlot)}`,
                          'aria-label': `${t(this.equipSlot)} ${id}`,
                          onclick: () => this.chooseEquip(this.equipSlot, img),
                        }, [thumb, el('span', { class: 'walkview-equip-id' }, [id])]));
                      }
            shown += slice.length;
            grid.dataset.total = String(ids.length);
            grid.dataset.shown = String(shown);
            // The scroller is detached until it is appended, so this only starts
            // firing once the host is in the document -- which is the point.
            if (shown < ids.length && !grid.dataset.paged) {
              grid.dataset.paged = '1';
              grid.addEventListener('scroll', () => {
                if (grid.scrollTop + grid.clientHeight >= grid.scrollHeight - 48) appendPage();
              }, { passive: true });
            }
          };

          for (const slot of offered) {
            tabs.appendChild(el('button', {
              class: 'walkview-equip-tab' + (slot === this.equipSlot ? ' is-active' : ''),
              type: 'button',
              'data-tip': `${t(slot)} — ${slots[slot].length}`,
              // `this.` matters: a bare `renderEquipBar()` is not in scope inside this
              // arrow, so the click threw a ReferenceError and the tab did nothing --
              // which reads as "the tabs are dead", not "the handler is wrong".
              onclick: () => { this.equipSlot = slot; this.renderEquipBar(); },
              // W-081: the count IN the tab, not only in the tooltip.
              //
              // Every id in the slot is already listed -- `loadEquipSlots` asks
              // `api.children` for the whole folder and nothing is capped -- but a
              // picker that pages silently reads as one that stops early, which is
              // what it was reported as. "Cap (908)" says what is there before
              // anything is scrolled.
            }, [t(slot), el('span', { class: 'walkview-equip-count' }, String(slots[slot].length))]));
          }

          appendPage();
          host.appendChild(tabs);
          host.appendChild(grid);
        }

  /**
   * Wear one item, or strip one slot, and compose again.
   *
   * The composed look is dropped first (Avatar.setEquip) and the SAME pose asked
   * for again, because a cap is a different set of layers at different zmap
   * ranks -- there is no layer to add to a look that already exists. Not
   * awaited: the draw fires when the server answers, the same shape
   * stepAvatar() uses, so a slow compose never stalls the frame loop.
   */
  chooseEquip(slot, image) {
    // Read through slotMap() and write back through it, so stripping `Hair-1`
    // actually takes the hair off. Building `next` from `.equip` meant the
    // picker could not see the character was wearing hair (it lives in
    // `.hair`), and stripping wrote `delete next['Hair-1']` into a map that
    // never had the key -- the head stayed as it was.
    const next = { ...(this.avatar?.slotMap?.() ?? {}) };
    // An empty string, NOT a missing key.
    //
    // A missing key and a deliberate strip are different statements and the
    // server can only honour the second: `Compose` fills in `body ??=
    // BodyPath(fileId)` and `head ??= HeadPath(fileId)` for any slot the caller
    // did not name (LookService.cs:166-168), because that is right for a caller
    // who simply did not mention the slot. So deleting the key made the Strip
    // button a no-op -- the sheet came straight back. MEASURED before this fix:
    // Strip on Head put 00012000.img back on every frame.
    //
    // `stripped` is the client's record of the same fact, and `setEquip`'s own
    // inference cannot make it: that filter is `!next[s] && !DEFAULT_EQUIP[s]`,
    // and Head/Body are both IN DEFAULT_EQUIP, so those two slots were never
    // eligible to be stripped no matter what the user clicked.
    if (image) {
      next[slot] = image;
      this.avatar.stripped = (this.avatar.stripped ?? []).filter((s) => s !== slot);
    } else {
      next[slot] = '';
      this.avatar.stripped = [...new Set([...(this.avatar.stripped ?? []), slot])];
    }

    // An equip entry is the more specific statement, so it also becomes the
    // character's own hair/face: otherwise slotMap() would fold the old
    // `.hair` back in with `??=` and the change would silently do nothing.
    if (slot === 'Hair-1') this.avatar.hair = image || null;
    if (slot === 'Face-1') this.avatar.face = image || null;

    // The weapon is also a PHYSICS fact, not only a layer. Which stances a swing
    // may use comes from the weapon's own `info/attack` integer
    // (WeaponData.cpp:35 -> CharLook.cpp:648, table :682-696), which only the
    // server can read, so composing the look alone would leave the figure drawing
    // one weapon and swinging with another.
    //
    // `Weapon-1` is the one that swings; `Weapon-2` is the second-hand slot and the
    // reference reads the swing group from the weapon in hand, so stripping
    // Weapon-2 must not disarm the character.
    if (slot === 'Weapon-1') this.live?.setWeapon(image || null)?.catch(() => {});

    // And the WHOLE set is a STATS fact, not only a visual one: the reference's
    // sheet is base + every equipped item's inc* (Player.cpp:309-313), and 326 of
    // 840 sampled items carry incPDD, 134 incDEX, 123 incSTR. Posting the whole
    // slot map rather than one slot is the server's design: it cannot tell
    // "removed" from "never equipped" from a patch.
    if (this.live && slot !== 'Weapon-1') {
      this.live.setEquipment(next)?.catch(() => {});
    }

    if (!this.avatar.setEquip(next)) return;

    const cur = this.ticks[this.frame] ?? null;
    const state = this.avatarState || actionFor(cur ?? {}).state;
    this.avatarPending = state;
    this.avatar.load(state)
      .then(() => {
        // Only the newest request may publish, for the same reason as
        // stepAvatar(): two composes in flight, the slower one loses.
        if (this.avatarPending !== state) return;
        this.avatarPending = null;
        this.renderHud(cur);
        this.draw();
      })
      .catch(() => { this.avatarPending = null; });
  }

  /**
   * Draw the character sheet the server computed for this frame.
   *
   * @param {object|null} cur the current tick, which carries `status`
   *
   * @description
   * Every number here comes from `frame.status` — the `PlayerStatus` the
   * session rebuilt when a weapon was equipped (`MapSimSessionStore.
   * SetEquippedWeapon` -> `BuildStatus`). This method formats and shows; it
   * computes nothing, because a client that recomputes its own sheet would show
   * a character the server is not simulating.
   *
   * The rows are the reference's own groupings: pools first
   * (`Character.cs:14-17`), then the four attributes it derives damage from
   * (`:18-21`), then the derived figures — accuracy from
   * `CharStats.cpp:186-192`, the damage window from `:118` and `:177`.
   *
   * A field the archive did not supply arrives UNSET, because the API omits
   * nulls (`DefaultIgnoreCondition = WhenWritingNull`). That is printed as a
   * dash, never as 0: a zero there is a statement the archive did not make.
   */
  renderSheet(cur) {
    clear(this.sheetRow);
    // `cur.status` when the tick carries one (a replayed trace), else the live
    // driver's hand-over. LiveWalk paints `viewer.sheet = state.status` beside
    // the ticks, for the same reason it hands over `mobs`: the server owns the
    // numbers, and a second implementation here would be a second, drifting one.
    const s = cur?.status ?? this.sheet ?? null;
    if (!s) return;

    const num = (v) => (v === null || v === undefined ? '—' : Number(v).toLocaleString());

    const group = (label, value) => el('span', { class: 'walkview-sheet-group' },
      el('b', {}, t(label)), ' ', value);

    // HP/MP bars, coloured on the same threshold a game uses: this is the one
    // number on screen that decides whether the next hit matters.
    const bar = (value, max, kind) => {
      const pct = Math.max(0, Math.min(100, max > 0 ? (value / max) * 100 : 0));
      return el('span', { class: `walkview-sheet-pool is-${kind}` },
        el('i', { style: `width:${pct}%` }),
        el('u', {}, `${num(value)}/${num(max)}`));
    };

    // `.append()` stringifies a null argument into the literal text "null", so
    // the optional groups are FILTERED rather than passed through and relied on.
    const parts = [
      bar(s.hp, s.maxHp, s.hp > 0 && s.hp / Math.max(1, s.maxHp) < 0.3 ? 'low' : 'ok'),
      group('MP', num(s.mp) + '/' + num(s.maxMp)),
      group('STR', num(s.strength)),
      group('DEX', num(s.dexterity)),
      group('INT', num(s.intelligence)),
      group('LUK', num(s.luck)),
      // The weapon's own ATK is shown separately from the window it produces,
      // because they answer different questions: `ATK 17` says what is equipped,
      // `1-11` says what a swing can do with it.
      group('ATK', num(s.watk)),
      group('ACC', num(s.accuracy)),
      group(t('Damage'), s.minDamage === 0 && s.maxDamage === 0
        ? t('no weapon') : `${s.minDamage}-${num(s.maxDamage)}`),
      group(t('DEF'), `${num(s.physicalDefence)}/${num(s.magicalDefence)}`),
      s.level ? group('LV', num(s.level)) : null,
      // `MoveSpeed`, not `movementSpeed`: System.Text.Json camel-cases the C#
      // property name, and the C# property is MoveSpeed.
      s.moveSpeed !== undefined ? group(t('Speed'), num(s.moveSpeed)) : null,
      s.jumpPower !== undefined ? group(t('Jump'), num(s.jumpPower)) : null,
      s.exp !== undefined && s.exp !== null ? group('EXP', num(s.exp)) : null,
    ].filter(Boolean);
    this.sheetRow.append(...parts);
  }

  renderControls() {
    clear(this.controls);

    // A live body owns this viewer with an EMPTY tick list. releaseWalk() clears
    // `ticks` on the way in (:657) and the 60Hz loop owns `frame` from then on,
    // so the old `if (!this.ticks.length) return;` left the bar blank in exactly
    // the mode the summon controls exist for -- the one call that installed the
    // live body is the call that emptied the bar, and nothing rebuilt it.
    //
    // So the bar renders when there is EITHER a replay to drive OR a live body to
    // act on, and the playback half is gated on the replay because every one of
    // its controls indexes `this.ticks`: with an empty list `seek` clamps to
    // `ticks.length - 1 == -1` and `play` would spin a frame counter over
    // nothing, which is the conflict releaseWalk()'s own remarks describe.
    //
    // Zoom is NOT gated: setZoom/zoomBy read and write `this.zoom`, never `ticks`,
    // so the camera is exactly as usable against a live body as against a replay.
    const replaying = this.ticks.length > 0;
    if (!replaying && !this.live) return;

    const button = (label, onClick, tip) =>
      el('button', { class: 'btn btn-sm', onclick: onClick, 'data-tip': tip }, label);

    if (replaying) {
      this.playBtn = button(this.playing ? t('Pause') : t('Play'), () => {
        if (this.playing) { this.pause(); this.renderControls(); }
        else { this.play(); this.renderControls(); }
      }, t('Play the walk at the client\'s frame rate'));

      this.controls.append(
        button('⏮', () => this.seek(0), t('Back to the start')),
        this.playBtn,
        button('⏭', () => this.seek(this.ticks.length - 1), t('Jump to the end')),
        button('◀', () => this.seek(this.frame - 1), t('One frame back')),
        button('▶', () => this.seek(this.frame + 1), t('One frame forward')),
        el('label', { class: 'check-row', style: 'font-size:var(--fs-12)' },
          el('span', {}, t('Speed')),
          el('input', {
            type: 'range', min: '1', max: '16', value: String(this.speed),
            oninput: (e) => { this.speed = Number(e.target.value); },
          })),
      );
    }

    this.controls.append(
      // The camera controls, after the playback ones. The zoom cluster reuses
      // the four strings the map editor's own zoom cluster already ships, so
      // this adds no untranslated keys.
      el('label', { class: 'check-row', style: 'font-size:var(--fs-12)' },
        el('button', {
          class: 'btn btn-sm', 'data-tip': t('Zoom out'),
          'aria-label': t('Zoom out'), onclick: () => { this.zoomBy(1 / 1.25); this.renderControls(); },
        }, '−'),
        el('span', { class: 'num', style: 'min-width:44px;text-align:center' },
          `${Math.round((this.zoom || 1) * 100)}%`),
        el('button', {
          class: 'btn btn-sm', 'data-tip': t('Zoom in'),
          'aria-label': t('Zoom in'), onclick: () => { this.zoomBy(1.25); this.renderControls(); },
        }, '+'),
        button('⛶', () => { this.setZoom(1); this.renderControls(); },
               t('Fit the whole map'))),
    );

    // The summon cluster, MobForm.cs:107-143's two buttons, and only while a live
    // body owns this viewer. A replayed trace has no session to summon INTO --
    // the mobs belong to the server's MapSimSessionStore, not to the tick list
    // on screen -- so offering the buttons there would be offering a control that
    // can only silently do nothing. `this.live` exists only after the live body
    // has taken the viewer (livewalk.js:340), which is the same test
    // chooseEquip() already uses to decide whether a weapon change is worth
    // telling the server about (walkview.js:970).
    if (this.live) this.controls.append(this.summonCluster());

    // The geometry overlay's own switch. It is off by default, so this button
    // starts as "on" -- the one control whose label describes turning the noise
    // ON, not off, which is the honest label for a default of false.
    this.controls.append(el('button', {
      class: 'btn btn-sm',
      'data-tip': this.showOverlays
        ? t('Hide the foothold lines and markers')
        : t('Show the foothold lines and markers'),
      onclick: () => {
        this.showOverlays = !this.showOverlays;
        // The scene is a CACHE, so switching the overlays has to rebuild it --
        // otherwise the lines stay on screen until the next map.
        this.scene = this.buildDrawList();
        this.renderControls();
        this.draw();
      },
    }, t('Overlays')));

    // The skill hotkey button, in the same row and under the same `this.live`
    // gate, for the same reason. The bindings live on the session
    // (MapSimSessionStore's MapSimSession, posted through /skills), so a
    // replayed trace has nothing to bind them to -- exactly the argument the
    // summon cluster above makes about summoned mobs.
    //
    // One button, not the reference's whole form: SkillForm is a 871x640 window
    // (SkillForm.Designer.cs:87) holding two grids, and this is a control bar
    // over a canvas. The panel behind the button is the reference's content in
    // a sheet sized to what a control bar can carry.
    if (this.live) this.controls.append(el('button', {
      class: 'btn btn-sm',
      'data-tip': t('Bind a skill to a key while walking'),
      onclick: () => this.openSkillPanel(),
    }, t('Skills…')));

    // W-081. THE BOOKMARKS.
    //
    // Three things a person does in the simulator over and over -- change what the
    // character wears, put a mob in front of it, bind a skill -- each of which was
    // a button somewhere else in the bar or buried inside a panel. They are a
    // numbered row, so they can be REACHED as well as found: 1/2/3 do the same
    // thing the buttons do. The reference's own form row is a set of labelled
    // grids (AvatarForm.cs:107, SkillForm.cs:41-137, MobForm.cs:107-124) and this
    // is the one control bar that can carry all three.
    //
    // Number keys are a VIEWER binding, not a global shortcut: `livewalk.js`
    // treats a keydown whose target is not an input as a walk key, and 1/2/3 are
    // not walk keys, so they reach here untouched. A digit while typing in the
    // search box above the mob grid still types a digit -- the guard is the same
    // `skillFor()` uses.
    if (this.live) this.controls.append(el('span', {
      class: 'walkview-bookmarks',
      style: 'display:inline-flex;gap:4px;margin:0 0 0 8px;align-items:center',
      role: 'group',
      'aria-label': t('Simulator bookmarks'),
    }, [
      el('span', { style: 'font-size:var(--fs-12);opacity:.6' }, t('Go to')),
      this.bookmarkButton(1, t('Equipment'), () => this.openEquipBookmark()),
      this.bookmarkButton(2, t('Mobs'), () => this.openMobPicker(() => {})),
      this.bookmarkButton(3, t('Skills'), () => this.openSkillPanel()),
    ]));

    // The camera row. NOT gated on `this.live`: a replayed trace has a camera too,
    // and being unable to look at a walk because nobody is driving it is the same
    // complaint as not being able to zoom one. `cam.free` suspends `follow()`,
    // which is where the character drives the camera (MapleCharacter.cs:749-755).
    const cameraRow = el('span', { class: 'walkview-camera-row' });
    this.renderCameraControls(cameraRow);
    this.controls.append(cameraRow);

    // THE INVULNERABILITY TOGGLE.
    //
    // `player.is_invincible()` — Stage.cpp:794 for the damage path,
    // Player.cpp:1252 for the attack one. It was a session-creation parameter the
    // browser never sent, and MEASURED the consequence: a level-1 character (50 HP)
    // walking into `f4/Map/Map1/100000004.img`, whose mobs carry `info/PADamage`
    // 90 to 150, is dead within seconds — and every mechanic worth watching on that
    // map happens after the first touch. A simulator you die in cannot be used to
    // study a mob's attacks.
    const godRow = el('span', { class: 'walkview-god-row' });
    const godBox = el('label', { class: 'walkview-god' }, [
      el('input', {
        type: 'checkbox',
        onchange: (e) => {
          this.live?.setInvulnerable(!!e.target.checked)?.catch(() => {});
        },
      }),
      el('span', {}, [t('take no damage')]),
    ]);
    godRow.append(godBox);
    this.controls.append(godRow);
    this.godBox = godBox;
    // Reflect whatever the live session already has, so a reconnect does not show
    // a checkbox that disagrees with the server.
    this.godBox.querySelector('input').checked = !!this.live?.opts?.invulnerable;

    // The equipment bar, fired and not awaited for the same reason composition
    // is: this runs inside an await chain that draw()'s callers sit in, and it
    // must not hold the paint. It repaints itself into its own host element.
    this.renderEquipBar().catch(() => { /* the bar's own error text */ });
  }

  /**
   * The reference's MobForm: an id box, a count box, and two buttons.
   *
   * MobForm.cs has exactly these controls and no others -- `button1_Click`
   * (:107-124) reads the selected mob and `textBox1`, `button2_Click` (:126-143)
   * removes everything summoned. Nothing here is invented: there is no kind
   * picker, because the reference has none, and no per-mob delete, because the
   * reference's Remove is all-or-nothing over `Mob.SummonedList` (:132-139).
   *
   * BOTH RESULTS ARE REPORTED, which is the one deliberate divergence and it is
   * declared as such on the server side too (MapSimSessionStore.cs:749-755):
   * MobForm.cs:117 drops a mob whose random x fell outside the map with no `else`
   * and no message, so a user asking for five mobs could see three and have no
   * way to learn why. The server puts those reasons in `rejected`; they are shown
   * here rather than swallowed, because a control that reports "spawned 5" for
   * three mobs is worse than one that reports nothing.
   *
   * The handlers are async and NOT awaited by anything on the 60Hz path. There is
   * deliberately no busy flag and no button disable here: `this.busy` belongs to
   * LiveWalk's frame loop (livewalk.js:700) and is what stops a second step being
   * issued mid-batch. Borrowing it for a click would park the physics for the
   * length of a round trip, so a summon would cost the walk a visible frame
   * instead of costing it nothing.
   *
   * @returns {DocumentFragment|Element} the cluster, for the controls row
   */
  summonCluster() {
    const cluster = el('span', {
      class: 'check-row',
      style: 'font-size:var(--fs-12);margin:0 0 0 8px',
    });

    // The id box. Trimmed because the user pastes it out of a grid cell or the
    // Explorer, and MobForm.cs:109 concatenates it as `"Mob/" + MobID + ".img"`
    // -- a leading space produces a node that does not exist, and the
    // reference's answer to that is a silent `return` (:110). An empty box is
    // NOT posted for the same reason: the server would answer `rejected:
    // ["no mob id given"]` (MapSimSessionStore.cs:805-808), which is a true
    // statement about a request nobody meant to make.
    const idBox = el('input', {
      class: 'field-input',
      style: 'width:8em;height:26px;padding:0 8px;font-size:var(--fs-12)',
      placeholder: '100100',
      'aria-label': t('Mob ID'),
      onkeydown: (e) => { if (e.key === 'Enter') { e.preventDefault(); summon(); } },
    });

    // The browse button. The id box above is MobForm.cs's own control, but a
    // field you have to already know the answer to is not a list: the reference's
    // form is a GRID of mobs (MobForm.cs:107-124) and the grid is the feature.
    // What is NOT in the reference is the picture -- the archive carries no
    // `info/icon` on any of the 2,053 mobs (measured), so the tiles show each mob's
    // own `stand/0`, which is the pose it spends its life in.
    const browseBtn = el('button', {
      class: 'btn btn-sm',
      'data-tip': t('Browse the mobs this archive holds'),
      onclick: () => this.openMobPicker((id) => { idBox.value = id; }),
    }, t('Browse…'));

    // The count box, MobForm.cs:112-114. `min=1` is the server's own clamp
    // (SimulatorEndpoints.cs:300, Math.Clamp(count, 1, 64)); the reference reads
    // the box with `Int32.Parse` (Extension.cs:41-44), so a non-numeric value
    // throws there rather than spawning nothing. Here Number() yields NaN and the
    // POST is simply not made -- the outcome is the same (zero mobs) without an
    // exception nobody could catch.
    const countBox = el('input', {
      class: 'field-input',
      type: 'number',
      min: '1',
      max: '64',
      value: '1',
      style: 'width:4.5em;height:26px;padding:0 8px;font-size:var(--fs-12)',
      'aria-label': t('Count'),
    });

    const summon = async () => {
      // Nothing to ask for: no session means the request would 404, and this
      // control only exists when `live` does -- but `live.sessionId` is null
      // between a lost body and its re-placement, and that is a state the user
      // can reach by walking off the edge of the lease.
      if (!this.live?.sessionId) return;

      const mobId = idBox.value.trim();
      if (!mobId) return;

      // A non-numeric count summons ZERO mobs in the reference
      // (MobForm.cs:112, `Char.IsNumber(textBox1.Text[0])`), so this reports the
      // refusal rather than sending a value the server would clamp away.
      const count = Number(countBox.value);
      if (!Number.isFinite(count) || count < 1) {
        toast(t('The count must be a whole number'), 'warning');
        return;
      }

      try {
        const r = await this.live.summon(mobId, Math.floor(count));
        if (!r) return;
        const rejected = r.rejected ?? [];
        // The headline is what actually appeared; the reasons follow, because
        // MobForm.cs:117's drop is invisible and this is the only place it is
        // not. `warning` rather than `error`: the mobs that landed are real.
        const message = t('Summoned {spawned} of {requested}', {
          spawned: r.spawned ?? 0, requested: r.requested ?? 0,
        }) + (rejected.length ? `\n${rejected.join('\n')}` : '');
        toast(message, rejected.length ? 'warning' : 'success');
      } catch (err) {
        toastError(err, t('Could not summon that mob'));
      }
    };

    const despawn = async () => {
      if (!this.live?.sessionId) return;
      try {
        const r = await this.live.despawn();
        if (!r) return;
        // MobForm.cs:142 sweeps the dead sprites and says nothing; the count is
        // what tells a user the button did something, and on a map whose own
        // `life` shares ids with the summons it is also the only honest number.
        toast(t('Removed {n} summoned', { n: r.removed ?? 0 }), 'success');
      } catch (err) {
        toastError(err, t('Could not remove the summoned mobs'));
      }
    };

    cluster.append(
      el('span', {}, t('Mob ID')), idBox, browseBtn,
      el('span', {}, t('Count')), countBox,
      el('button', {
        class: 'btn btn-sm', onclick: summon,
        'data-tip': t('Spawn these mobs around the character'),
      }, t('Summon')),
      el('button', {
        class: 'btn btn-sm', onclick: despawn,
        'data-tip': t('Remove every mob this control summoned'),
      }, t('Remove summoned')),
    );
  return cluster;
  }

  /**
   * The reference's MobForm grid, as something you can look at.
   *
   * @param {(id: string) => void} onPick called with the chosen mob id
   *
   * @description
   * `MobForm.cs:107-124` opens on a grid of mobs, not on a text field, and that
   * grid is the reason a person can use the thing at all: the open Mob.nx holds
   * **2,053** mobs with ids nobody can recall (`2220100` is 蓝蘑菇, `0100100` is
   * 蜗牛). A field asks you to already know that.
   *
   * <p>The tiles show each mob's own `stand/0`. There is no icon to show: the
   * archive carries `info/icon` on **0 of 2,053** (measured over every image in
   * the open Mob.nx), so a stand-in that looks like the creature is the honest
   * substitute and an invented icon sheet would not be.
   *
   * <p>Nothing is loaded until this is opened, and only the tiles the browser can
   * actually see get an <img>: 1,543 of the 2,053 have a `stand/0` at all, and
   * fetching 2,053 canvases to show 60 of them would be the slow way to learn
   * nothing.
   *
   * <p><b>The right-hand pane is `MobTooltipRenderer.cs`.</b> Clicking a tile no
   * longer closes the dialog — it SELECTS, and the pane fills with that mob's
   * level, HP, EXP, trait tags, elemental profile and monster-book entry; the
   * footer's "Use this mob" is what commits the id to the box. That two-step is
   * OWN DESIGN and it is a deliberate departure: the reference commits on the
   * grid's own click, but a bestiary page you cannot read without dismissing it
   * is not a bestiary page. The committed value and the outcome are unchanged —
   * `onPick` still fills the caller's id box and the caller still spawns from
   * there — and typing an id into the search box then pressing Enter selects the
   * exact mob asked for, so the fast path for someone who already knows the
   * number is one keystroke longer than it was, not several.
   */
  /** One bookmark: a numbered button that also answers to its digit. */
  bookmarkButton(n, label, run) {
    const fire = () => {
      try { run(); } catch (e) { toastError(e, t('That bookmark could not open')); }
    };
    return el('button', {
      class: 'btn btn-sm walkview-bookmark',
      'data-key': String(n),
      'data-tip': `${n} — ${label}`,
      onclick: fire,
    }, [`${n} ${label}`]);
  }

  /**
   * W-081. The keyboard half of the bookmarks: 1/2/3 open Equipment / Mobs /
   * Skills, and this returns true when it consumed the key.
   *
   * `LiveWalk.onKeyDown` calls it FIRST, so a digit never reaches the hotkey
   * lookup or the walk keys. The field test is the same one `skillFor()` uses --
   * a digit typed into the mob picker's search box is a digit, and a search box
   * that cannot take a digit is not a search box. `data-skill-panel` is honoured
   * for the same reason the hotkey panel is exempted (W-071).
   */
  viewerBookmarkKey(e) {
    if (e.ctrlKey || e.metaKey || e.altKey || e.repeat) return false;
    if (typeof e.key !== 'string' || !/^[123]$/.test(e.key)) return false;
    const target = e.target;
    const inPanel = typeof target?.closest === 'function'
      && target.closest('[data-skill-panel]');
    if (target && !inPanel
        && (target.tagName === 'INPUT' || target.tagName === 'TEXTAREA'
            || target.tagName === 'SELECT' || target.isContentEditable)) {
      return false;
    }
    const n = Number(e.key);
    const button = this.controls?.querySelector(`.walkview-bookmark[data-key="${n}"]`);
    button?.click();
    return !!button;
  }

  /**
   * Bookmark 1: the equipment bar, opened and brought into view.
   *
   * The bar is already IN the control row (`renderEquipBar`), so this does not
   * open a panel -- it renders the bar, selects the first slot that has anything
   * in it, and scrolls the bar into view. Without the scroll a bookmark reads as
   * "nothing happened", which is the same complaint the number keys answer.
   */
  async openEquipBookmark() {
    const slots = await this.loadEquipSlots();
    const first = PICKABLE_SLOTS.find((s) => slots[s]?.length);
    if (first) this.equipSlot = first;
    await this.renderEquipBar();
    const host = this.equipHost;
    if (host?.scrollIntoView) {
      host.scrollIntoView({ block: 'nearest', behavior: 'smooth' });
    }
    return first ?? null;
  }

  openMobPicker(onPick) {
    const grid = el('div', {
      style: 'display:grid;grid-template-columns:repeat(auto-fill,minmax(96px,1fr));' +
             'gap:8px;max-height:58vh;overflow:auto;padding:2px;min-width:0',
    });
    const search = el('input', {
      class: 'field-input',
      style: 'width:100%;height:30px;padding:0 10px;margin-bottom:10px',
      placeholder: t('Search by name or id'),
      'aria-label': t('Search mobs'),
    });
    const status = el('div', { style: 'font-size:var(--fs-12);opacity:.7;margin-bottom:8px' }, t('Loading mobs…'));

    // The bestiary pane. Everything in it is the archive's, read per mob on
    // selection -- there is no second copy of the 2,053 rows client-side.
    const info = el('div', {
      class: 'walkview-mob-info',
      style: 'width:288px;flex:0 0 288px;border-left:1px solid var(--line,#2a3140);' +
             'padding-left:12px;max-height:64vh;overflow:auto;font-size:var(--fs-12)',
    });

    const dialog = modal({
      title: t('Summon a mob'),
      subtitle: t('Pick one to read its bestiary page'),
      width: '1080px',
      body: el('div', { style: 'display:flex;gap:12px;align-items:flex-start' },
        el('div', { style: 'flex:1 1 auto;min-width:0' }, search, status, grid),
        info),
      actions: [
        {
          label: t('Use this mob'),
          class: 'btn-primary',
          run: (close) => {
            if (!selected) {
              toast(t('Pick a mob first'), 'warning');
              return false;
            }
            onPick(selected);
          },
        },
        { label: t('Close'), class: 'btn-ghost' },
      ],
    });

    let all = [];
    // The chosen mob's bare id. `onPick` is still what commits it, so the id box
    // the caller owns is written by the caller and not from here.
    let selected = null;

    const draw = () => {
      const q = search.value.trim().toLowerCase();
      const rows = q
        ? all.filter((m) => m.name.toLowerCase().includes(q) || m.id.includes(q))
        : all;
      status.textContent = t('{shown} of {total} mobs', { shown: 0, total: rows.length });
      clear(grid);
      // W-081. PAGES, NOT A CAP.
      //
      // This sliced to the first 240 and stopped, with no next page: 2,053 mobs in
      // the archive, 1,813 of them reachable only by typing an id into the search
      // box. The equip grid solves the same problem by appending a page when the
      // scroller nears its end -- 2,053 tiles at once is a frozen tab, and a
      // TRUNCATED list is worse than a slow one because it does not say what it
      // left out. `status` reports rendered-of-matched throughout, so the count is
      // never a lie.
      const PAGE = 120;
      let shown = 0;

      const appendPage = () => {
        const slice = rows.slice(shown, shown + PAGE);
        for (const m of slice) {
          const tile = el('button', {
            class: `btn${m.id === selected ? ' btn-primary' : ''}`,
            style: 'display:flex;flex-direction:column;align-items:center;gap:4px;' +
                   'padding:6px 4px;height:auto;min-height:74px',
            'data-tip': m.name ? `${m.name} (${m.id})` : m.id,
            onclick: () => select(m),
          });
          if (m.thumb) {
            const img = el('img', { alt: '', style: 'image-rendering:pixelated;max-width:56px;max-height:40px' });
            img.loading = 'lazy';
            img.decoding = 'async';
            img.src = `/api/canvas?path=${encodeURIComponent(m.thumb)}`;
            tile.append(img);
          } else {
            // No stand pose: the id alone, rather than a broken-image tile that
            // looks like a rendering fault.
            tile.append(el('span', { style: 'font-size:var(--fs-12);opacity:.6' }, '—'));
          }
          tile.append(el('span', { style: 'font-size:var(--fs-12);line-height:1.2' },
            m.name || m.id));
          grid.append(tile);
        }
        shown += slice.length;
        status.textContent = t('{shown} of {total} mobs', { shown, total: rows.length });
        if (shown >= rows.length && rows.length) {
          status.append(el('span', { style: 'opacity:.6' },
            ' — ' + t('all shown')));
        }
        return slice.length > 0;
      };

      appendPage();
      // The modal BODY scrolls (ui.js:618 puts the content in `.modal-body`),
      // so the listener goes on the element that actually scrolls.
      const scroller = dialog.dialog?.querySelector('.modal-body')
        || grid.closest('[style*="overflow"]') || grid.parentElement || grid;
      scroller.addEventListener('scroll', () => {
        if (shown >= rows.length) return;
        if (scroller.scrollHeight - scroller.scrollTop - scroller.clientHeight < 600) appendPage();
      }, { passive: true });
      if (shown < rows.length) appendPage();
    };

    // A dash for a number the archive does not carry. Zero and absent are
    // different facts here: `exp` is missing on roughly 1 mob in 45 and
    // `category` on 96% of them, so printing 0 for either would invent a value
    // no v83 player could have read off the page.
    const num = (v) => (v === null || v === undefined ? '—' : Number(v).toLocaleString());

    const line = (label, value) => el('div', { style: 'display:flex;gap:6px;line-height:1.5' },
      el('span', { style: 'flex:0 0 42%;opacity:.75' }, label),
      el('span', { style: 'flex:1 1 auto' }, value));

    const tag = (text) => el('span', {
      style: 'background:#3d5a2a;color:#e6ff8a;padding:0 5px;margin:0 4px 3px 0;' +
             'border-radius:3px;display:inline-block;font-size:var(--fs-11)',
    }, text);

    const showPlaceHolder = (text) => {
      clear(info);
      info.append(el('div', { style: 'opacity:.6;padding:24px 8px;text-align:center' }, text));
    };

    const renderBook = (book, root) => {
      root.append(el('div', { style: 'margin-top:10px;border-top:1px solid var(--line,#2a3140);padding-top:8px' },
        el('div', { style: 'opacity:.75;margin-bottom:4px' }, t('Monster book'))));
      if (book.episode) {
        root.append(el('div', { style: 'white-space:pre-wrap;line-height:1.55;margin-bottom:8px' },
          book.episode));
      }
      // Ids are printed raw. Turning a map id into a name is String.nx/Map.img's
      // job and an item id's into a name is String.nx/Eqp.img's; labelling them
      // here would put a possibly-wrong name next to a right number.
      for (const [heading, ids] of [[t('Maps'), book.maps], [t('Rewards'), book.rewards]]) {
        if (!ids?.length) continue;
        root.append(el('div', { style: 'opacity:.75;margin-top:6px' }, `${heading} (${ids.length})`));
        root.append(el('div', { style: 'word-break:break-all;line-height:1.5' }, ids.join(', ')));
      }
    };

    const renderInfo = (data, thumb, mobId) => {
      clear(info);

      const head = el('div', { style: 'display:flex;gap:10px;align-items:flex-start;margin-bottom:8px' });
      if (thumb) {
        const img = el('img', {
          alt: '',
          style: 'image-rendering:pixelated;width:72px;height:72px;object-fit:contain;flex:0 0 72px',
        });
        img.src = `/api/canvas?path=${encodeURIComponent(thumb)}`;
        head.append(img);
      }
      head.append(el('div', { style: 'min-width:0' },
        el('div', { style: 'font-size:var(--fs-14);font-weight:600' }, data.name || data.id),
        el('div', { style: 'opacity:.7' }, `ID:${data.id}`)));
      info.append(head);

      if (!data.hasInfo) {
        info.append(el('div', { style: 'opacity:.7;line-height:1.5' },
          t('No open Mob archive holds this mob, so it has no page to show.')));
        return;
      }

      // Trait tags first and in their own colour, the way MobTooltipRenderer
      // does (:128-133). The server already ordered them; it also already
      // decided that a boss is either [Mini-Boss] or [Boss] and never both.
      if (data.traits?.length) {
        const tags = el('div', { style: 'margin-bottom:6px' });
        for (const trait of data.traits) tags.append(tag(trait));
        info.append(tags);
      }
      if (data.disappearAfterSeconds > 0) {
        info.append(el('div', { style: 'margin-bottom:6px' },
          tag(t('[Disappears after {n} seconds]', { n: data.disappearAfterSeconds }))));
      }

      const rows = el('div');
      // The reference's own line order, MobTooltipRenderer.cs:141-197, with its
      // conditional lines kept conditional (:147-154, :165-192).
      rows.append(line(t('Type'), data.categoryName ?? '—'));
      rows.append(line(t('Level'), num(data.level)));
      rows.append(line(t('HP'), num(data.maxHp)));
      rows.append(line(t('MP'), num(data.maxMp)));
      if (data.hpRecovery > 0) rows.append(line(t('HP Recovery'), num(data.hpRecovery)));
      if (data.mpRecovery > 0) rows.append(line(t('MP Recovery'), num(data.mpRecovery)));
      rows.append(line(t('Physical Damage'), num(data.paDamage)));
      rows.append(line(t('Magic Damage'), num(data.maDamage)));
      rows.append(line(t('Physical DEF Rate'), data.pdRate === null || data.pdRate === undefined
        ? '—' : `${num(data.pdRate)}%`));
      rows.append(line(t('Magic DEF Rate'), data.mdRate === null || data.mdRate === undefined
        ? '—' : `${num(data.mdRate)}%`));
      rows.append(line(t('Knockback'), num(data.pushed)));
      rows.append(line(t('EXP'), num(data.exp)));
      // The five attribute EXPs and Weapon Points. The open Mob.nx carries none
      // of them (measured over 343 mobs, 2026-10-16), so these lines never
      // appear here -- they are kept because the reference has them and a future
      // archive that does carry them should show them without another change.
      for (const [label, value] of [
        [t('Ambition EXP'), data.charismaExp], [t('Empathy EXP'), data.senseExp],
        [t('Insight EXP'), data.insightExp], [t('Willpower EXP'), data.willExp],
        [t('Diligence EXP'), data.craftExp], [t('Charm EXP'), data.charmExp],
      ]) {
        if (value > 0) rows.append(line(label, num(value)));
      }
      if (data.weaponPoints > 0) {
        rows.append(line(t('Weapon Points (for Zero)'), num(data.weaponPoints)));
      }
      info.append(rows);

      if (data.elements?.length) {
        info.append(el('div', { style: 'margin-top:6px' },
          el('span', { style: 'opacity:.75;margin-right:6px' }, t('Elements')),
          data.elements.map((e) => t(e.label)).join(', ')));
      }
      // Whether this mob can ATTACK at all, and with what.
      //
      // Not decoration. Measured 2026-10-16: 41% of Mob.nx carries an `attack*`
      // node, and `f4/Map/Map1/100000004.img` has 0 of its 2 mob ids carrying
      // one — so on that map no mob can play an attack animation and a viewer
      // cannot tell that apart from the animation being broken. The reference
      // agrees it is right to be silent: `MobAttack.cpp:88` only enters the
      // animation when `available` is non-empty, and a mob with no attacks never
      // fills it.
      const attackRow = el('div', { style: 'margin-top:6px;opacity:.8' });
      info.append(attackRow);
      this.renderMobAttacks(mobId, attackRow);
      if (data.mobType) {
        info.append(line(t('mobType'), data.mobType));
      }
      if (data.revivesInto?.length) {
        info.append(el('div', { style: 'margin-top:6px;color:#e6ff8a' },
          `${t('Revives into')}: ${data.revivesInto.join(', ')}`));
      }

      if (data.book) renderBook(data.book, info);
    };

    const select = async (m) => {
      selected = m.id;
      draw();
      showPlaceHolder(t('Reading {id}…', { id: m.id }));
      try {
        const res = await fetch(`/api/simulate/mob-info?mobId=${encodeURIComponent(m.id)}`);
        if (!res.ok) throw new Error(String(res.status));
        renderInfo(await res.json(), m.thumb, m.id);
      } catch (err) {
        showPlaceHolder(t('Could not read that mob: {msg}',
          { msg: String(err.message ?? err) }));
      }
    };

    search.addEventListener('input', draw);
    search.addEventListener('keydown', (event) => {
      // Enter takes the first hit, which is the whole point of typing an id into
      // this box: you already know the number, you want its page.
      if (event.key !== 'Enter' || !all.length) return;
      const q = search.value.trim().toLowerCase();
      const first = (q
        ? all.filter((m) => m.name.toLowerCase().includes(q) || m.id.includes(q))
        : all)[0];
      if (first) { event.preventDefault(); select(first); }
    });

    const load = async () => {
      try {
        const res = await fetch('/api/simulate/mob-catalog');
        if (!res.ok) throw new Error(String(res.status));
        const data = await res.json();
        all = data.mobs ?? [];
        draw();
        showPlaceHolder(t('Pick a mob to read its level, HP, EXP and traits.'));
      } catch (err) {
        status.textContent = t('Could not list the mobs: {msg}', { msg: String(err.message ?? err) });
      }
    };
    load();

    return dialog;
  }

  /**
   * The reference's SkillForm, as a sheet: SkillForm.cs:51's list of castable
   * skills beside SkillForm.cs:156-174's list of the ones bound to a key.
   *
   * THE PANEL DOES NOT DECIDE ANYTHING. Every change posts the WHOLE list and
   * then repaints from the server's answer, because the two exclusions that
   * govern this list are the reference's and are applied there:
   * SkillForm.cs:156-162 drops a row whose skill is already bound and a row
   * whose key is already taken, so a client that repainted from its own
   * pending list would show two skills on one key for as long as the round
   * trip was in the air. The server's `rejected` is reported through
   * `toast()` -- the same helper summonCluster() uses -- because a binding that
   * silently did not take is the one failure a settings panel cannot have.
   *
   * Nothing is persisted. SkillForm.cs:184-187 closes into `Hide()`
   * (`SkillForm.cs:44`) and `Skill.HotKeyList` is a static that survives the
   * form but is not a saved setting; adding localStorage here would invent a
   * lifetime the reference does not give the bindings.
   *
   * The hotkey combo is INLINE rather than revealed per row. The reference
   * shows it only for the clicked row (SkillForm.cs:30-38, `CellClick` positions
   * the combo over the grid and scrolls it away on `Scroll`, :74-77) -- a layout
   * choice for a 40px-row DataGridView, not a behaviour: the combo is hardcoded
   * to the same 26 letters either way (SkillForm.Designer.cs:50), and this
   * server rejects anything else with its own reason.
   *
   * @returns {void}
   */
  openSkillPanel() {
    // A castable list is worth nothing without a session to bind it to: the
    // POST would 404 (the route is /session/{id}/skills) and the keys would
    // silently never fire. `sessionId` is null between a lost body and its
    // re-placement, which is a state the user can reach, so this is checked
    // rather than assumed.
    if (!this.live?.sessionId) return;

    // Cached on the instance, and only asked for HERE. The reference builds its
    // grid inside `SkillForm_Shown` (SkillForm.cs:41-137), i.e. when the form
    // opens -- not at startup, and not per keystroke. Fetching this during the
    // map load would spend a round trip and 66 rows of sprite work on a panel
    // the user may never open.
    //
    // Held as a promise so two clicks during the same round trip ask once, and
    // so a reopen after a failure asks again: the catch below leaves `skillList`
    // empty and replaces the promise, rather than caching the failure for the
    // life of the session.
    const rows = el('div', {
      class: 'walkview-skill-rows',
      style: 'max-height:16rem;overflow-y:auto;display:flex;flex-direction:column;gap:2px',
    });
    const bound = el('div', {
      class: 'walkview-skill-bound',
      style: 'display:flex;flex-direction:column;gap:2px',
    });
    const status = el('div', { class: 'walkview-skill-status', style: 'font-size:var(--fs-12)' });

    // What the server last said, in the server's own direction (key -> id).
    // Seeded from the live body so a panel opened before any change shows the
    // bindings that are actually in effect, rather than an empty list.
    let bindings = Object.fromEntries(this.live.skillKeys ?? new Map());

    const redraw = () => {
      clear(rows);
      const taken = new Set(Object.keys(bindings));
      for (const skill of this.skillList ?? []) {
        const boundKey = Object.entries(bindings)
          .find(([, id]) => id === skill.skillId)?.[0];
        rows.append(skillRow(skill, boundKey, taken));
      }
      if (!(this.skillList ?? []).length) {
        rows.append(el('div', { style: 'color:#a0aec0;font-size:var(--fs-12)' },
          t('No skills can be cast from the open archives')));
      }

      clear(bound);
      const entries = Object.entries(bindings);
      for (const [key, id] of entries) {
        bound.append(el('div', { class: 'check-row', style: 'font-size:var(--fs-12)' },
          el('span', {}, id),
          el('button', {
            class: 'btn btn-sm', 'aria-label': t('Remove this binding'),
            'data-tip': t('Remove this binding'),
            onclick: () => submit(Object.entries(bindings)
              .filter(([k]) => k !== key)
              .map(([k, id]) => ({ skillId: id, key: k }))),
          }, '✕')));
      }
      if (!entries.length) {
        bound.append(el('div', { style: 'color:#a0aec0;font-size:var(--fs-12)' },
          t('No skill is bound to a key')));
      }
      status.textContent = t('{n} bound', { n: entries.length });
    };

    // The single write path. `next` is the WHOLE list, in the request shape
    // {skillId, key} -- SkillForm.cs:164-173 rebuilds `Skill.HotKeyList` from
    // the Use List grid, so every change is the entire grid, never a delta.
    const submit = async (next) => {
      if (!this.live?.sessionId) return;
      try {
        const r = await this.live.setSkills(next);
        if (!r) return;
        // From the RESPONSE, not from `next` -- the server's exclusions
        // (SkillForm.cs:156-162) can leave fewer rows than were posted.
        bindings = Object.fromEntries(this.live.skillKeys ?? new Map());
        redraw();
        const rejected = r.rejected ?? [];
        const message = t('{n} bound', { n: r.count ?? Object.keys(bindings).length })
          + (rejected.length ? `\n${rejected.join('\n')}` : '')
          + (rejected.length ? '' : `\n${t('Close this panel, then press the key.')}`);
        toast(message, rejected.length ? 'warning' : 'success');
        // W-072. TAKE THE FOCUS BACK OFF THE DROPDOWN.
        //
        // This is the whole reason a bound skill could be unusable in practice
        // while every server-side test was green. The binding is made from a
        // <select>, and `livewalk.skillFor` deliberately refuses a keydown whose
        // target is a SELECT (livewalk.js:999 -- the guard has its own passing
        // test, "picking in a select does not", and it is right to exist: typing
        // into a field must not fire a skill).
        //
        // So immediately after a successful bind the caret is still sitting in
        // that dropdown, the panel is still open, and the very next keypress --
        // the one the user just bound -- is swallowed. Nothing errors. The panel
        // says "1 bound" and the skill never casts.
        //
        // The fix is to end the interaction, not to weaken the guard: focus goes
        // back to the dialog itself, so the next keypress has a target that is
        // neither INPUT nor SELECT and reaches the walk.
        const active = document.activeElement;
        // `closest('dialog')` rather than a reference to the dialog: it is created
        // inside modal(), so it is not in this scope, and asking the DOM whether
        // the focused element is inside one needs no handle at all.
        const host = active?.closest?.('dialog');
        if (host && typeof active.blur === 'function') {
          active.blur();
        }
      } catch (err) {
        toastError(err, t('Could not bind that skill'));
      }
    };

    const skillRow = (skill, boundKey, taken) => {
      const pick = el('select', {
        class: 'field-input no-icon',
        style: 'font-size:var(--fs-12);height:26px;padding:0 4px',
        'aria-label': t('Key'),
      });
      // The 26 letters, verbatim from SkillForm.Designer.cs:50. NOT generated
      // from a range: the reference's list is a literal, and a literal is what
      // the server's rejection message quotes when it refuses anything else.
      for (const key of SKILL_KEYS) {
        // A key already held is still offered, and still says so. Hiding it
        // would make the reference's second exclusion (a key at most one skill)
        // invisible instead of enforced: the user would learn the rule only by
        // the option going missing between one castable skill and the next.
        //
        // A key this project's ATTACK owns is offered the same way, for the
        // same reason. WASD was removed (the reference's movement is the four
        // arrow keys, Keyboard.cpp:78-81); Z stays because Ctrl cannot reach the
        // handler. `livewalk.skillFor` checks `keyFor()` BEFORE `skillKeys` — so a
        // skill on Z is accepted, reported as bound, and can never fire. The
        // server now refuses it with a reason; this says so before the user picks.
        const owned = ['Z'].includes(key);
        const label = owned
          ? `${key} — ${t('used to move or attack')}`
          : key + (taken.has(key) && boundKey !== key ? ` — ${t('in use')}` : '');
        const opt = el('option', { value: key }, label);
        opt.disabled = owned;
        pick.append(opt);
      }
      pick.value = boundKey ?? '';
      pick.addEventListener('change', () => {
        const key = pick.value;
        if (!key) return;
        // Both exclusions, applied client-side so the POST is already the
        // reference's answer: a skill may hold at most one key, a key at most
        // one skill (SkillForm.cs:156-162). Anything else would be a round trip
        // spent on a row the server is going to name in `rejected`.
        const rest = Object.entries(bindings).filter(([, id]) => id !== skill.skillId);
        submit([...rest.map(([k, id]) => ({ skillId: id, key: k })), { skillId: skill.skillId, key }]);
      });

      return el('div', { class: 'check-row', style: 'font-size:var(--fs-12);gap:6px' },
        skillIcon(skill),
        el('span', { style: 'min-width:9.5em' }, skill.name || t('Unnamed skill')),
        el('span', { style: 'color:#a0aec0;font-variant-numeric:tabular-nums' }, skill.skillId),
        pick);
    };

    modal({
      title: t('Hotkey Settings'),
      subtitle: t('Pick a key for a skill. Each key holds one skill, and each skill one key.'),
      width: '520px',
      body: el('div', {
        // W-071. Marks this panel so `livewalk.skillFor` knows a keydown aimed at
        // one of ITS OWN dropdowns is a cast, not a keystroke swallowed by a
        // control. The blanket "target is a SELECT -> ignore" rule made the key
        // you just bind get eaten by the dropdown you bound it with.
        'data-skill-panel': '1',
        style: 'display:flex;flex-direction:column;gap:10px' },
        el('h3', { style: 'margin:0;font-size:var(--fs-13)' }, t('Use List')),
        bound,
        el('h3', { style: 'margin:0;font-size:var(--fs-13)' }, t('Skills')),
        rows,
        status),
      actions: [
        { label: t('Clear all'), run: async () => {
          if (!this.live?.sessionId) return;
          try {
            const r = await this.live.clearSkills();
            if (!r) return;
            bindings = {};
            redraw();
            toast(t('Cleared {n} bindings', { n: r.cleared ?? 0 }), 'success');
          } catch (err) {
            toastError(err, t('Could not clear the bindings'));
          }
        } },
      ],
      onOpen: () => {
        redraw();
        // Lazy, once, on first open -- never during the map load. A failure is
        // reported in the panel rather than as a toast: the panel is what the
        // user is looking at, and the reason it is empty belongs inside it.
        this.skillCache = this.live.castable()
          .then((j) => {
            if (!j) throw new Error('no answer');
            this.skillList = j.skills ?? [];
            redraw();
          })
          .catch((err) => {
            clear(rows);
            rows.append(el('div', { style: 'color:#e07a5f;font-size:var(--fs-12)' },
              t('Could not list the castable skills: {msg}', { msg: err.message })));
          });
      },
    });
  }

  /**
   * Everything the figure needs to draw itself: the map's extent, the pixels
   * per world unit, and where the canvas origin sits.
   *
   * The scale is still fit-to-width -- cssWidth over the framed width -- because
   * the whole map at canvas width is what this view has always drawn and what
   * loadVisible and the CLI checks measure against. What changed is `minX`:
   * it is the CAMERA's world position now, not the map's left edge. At zoom 1
   * the camera is clamped to sit exactly where the old fixed framing did, so the
   * picture is unchanged; above 1 the viewport is narrower than the map and the
   * camera has room to travel.
   */
  layout() {
    const g = this.geometry;
    if (!g) return null;

    // Everything the walk can touch: the ground, plus a margin so a character
    // mid-fall stays on screen instead of clipping to nothing.
    const pts = [];
    for (const f of g.Footholds) {
      pts.push([f.x1, f.y1], [f.x2, f.y2]);
    }
    for (const l of g.Ladders) pts.push([l.x, l.top], [l.x, l.bottom]);
    const tk = this.ticks;
    if (tk.length) pts.push([tk[0].x, tk[0].y], [tk[tk.length - 1].x, tk[tk.length - 1].y]);

    if (!pts.length) return null;

    let minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity;
    for (const [x, y] of pts) {
      if (x < minX) minX = x;
      if (y < minY) minY = y;
      if (x > maxX) maxX = x;
      if (y > maxY) maxY = y;
    }

    // The character stands ON a foothold, so the foothold's lowest point is
    // its feet. Without headroom above, every frame where it stands near the
    // top of the framed area draws its head outside the canvas -- the art was
    // there and the figure was clipped off it. Same reason for the map's own
    // vertical bounds when they exist: the ground the walk uses is not always
    // where the picture is.
    const b = g.Bounds;
    if (b && Number.isFinite(b.top) && Number.isFinite(b.bottom)) {
      minY = Math.min(minY, b.top);
      maxY = Math.max(maxY, b.bottom);
    }
    minY -= FIGURE.height + 24;

    const pad = 40;
    minX -= pad; maxX += pad; maxY += pad;

    const cssWidth = this.canvas.clientWidth || 900;
        // The framed extent, kept before the camera is folded in: the camera is
        // clamped against these, and a view that clamped against its own camera
        // position could never leave.
        const world = { left: minX, top: minY, right: maxX, bottom: maxY };

        // The scale is the player's eye, not a fit-to-width of the map.
        //
        // This line used to be `cssWidth / (maxX - minX)`, which pins the map's
        // WIDTH to the canvas width on every map. On f1/Map/Map0/000000002.img that
        // map is 5378 world units wide, so one world unit became 0.167 canvas
        // pixels and the 79-unit-tall character drew 13 pixels tall — a speck on a
        // postcard. The camera logic underneath was already correct (follow(),
        // rectify(), the clamp), it just had nothing to travel inside because the
        // viewport was the whole map.
        //
        // The reference client draws at scale 1: one world unit is one pixel, and
            // the camera moves over a viewport of the display's own size
            // (MapleCharacter.cs:749-767). So the default is scale 1.
            //
            // `fit` is the OTHER direction: how much you would shrink a map wider than
            // the canvas so it all fits. It is below 1 exactly when the map is wider
            // than the canvas — and that is the case zoom-out should reach, not the
            // case the default should be dragged down to. Writing
            // `min(fit, 1)` therefore reproduced the bug on every wide map: it took
            // the fit value whenever fit happened to be small, which is always.
            // Measured on f1/Map/Map0/000000002.img: fit = 900/5378 = 0.167, and the
            // character drew 13 pixels tall.
            //
            // Zoom multiplies on top, so `zoomBy(0.167)` still reaches the old
            // whole-map framing for anyone who wants to see the level at once.
            const fit = Math.max(0.05, cssWidth / Math.max(1, maxX - minX));
                const scale = Math.max(0.05, (this.zoom ?? 1));
                // Kept live, not vestigial: `fit` is the zoom that shows the whole map, so
                // the zoom-out control can reach exactly that framing instead of an
                // arbitrary constant.
                this.wholeMapZoom = fit;

    // The canvas height is derived HERE, not read off the element, because the
        // camera's vertical clamp needs the viewport it is about to be drawn into
        // and the element still holds the PREVIOUS frame's height. Reading
        // clientHeight made the clamp lag a frame and, on the first frame, guess.
        // It is a pure function of the scale, so there is no feedback loop.
        //
        // At the player's own scale the window's aspect decides the viewport, not
        // the map's: a 900px-wide canvas at scale 1 sees 900 world units across
        // and `window.innerHeight` tall. Deriving the height from the map's width
        // instead is what pinned the camera to a whole-map postcard.
        const worldW = Math.max(1, cssWidth / scale);
            // A 900px-wide viewport at the player's scale sees 900 world units across.
            // The height is the WINDOW's height, not the map's width times a ratio —
            // that formula existed only to give a sensible aspect for a
            // whole-map-in-one-view picture, and it produced a 306px letterbox once
            // the scale stopped fitting the map to the canvas.
            //
            // The budget is MEASURED, not the old flat `- 120`. That number
            // assumed the rows above and below the canvas were worth about 120px
            // in total; adding the equipment bar (52px) pushed the controls, the
            // bar and the HUD past the bottom of the window, so the reader had
            // to scroll to reach the thing that dresses the character. What is
            // left after the header above and the rows below is what the canvas
            // gets, and it is asked of the DOM rather than guessed.
            const chrome = this.chromeHeight();
            const cssHeight = Math.max(
                220,
                Math.min(window.innerHeight - chrome, Math.round(worldW * 0.62)));

    // The camera's own clamp rectangle, which is NOT the framing rectangle
    // above. `world` is the drawn extent -- footholds, ladders, ticks, padded,
    // with figure headroom -- and it is what fit-to-width measures. The camera
    // clamps against the MAP's bounds: VRLeft/VRTop/VRRight/VRBottom when the
    // map declares VR, otherwise the foothold X range with
    // -centerY+50 .. -centerY+MapHeight-55 (MapleMap.cs:175-194, computed
    // server-side by FootholdPhysics.ComputeBounds and delivered as
    // /api/simulate/geometry -> bounds).
    //
    // Clamping against `world` instead is what made the camera "have no
    // bounds": `world` was derived from the same geometry the walk touches,
    // padded by 40 and pushed up by the figure height, so on any map whose VR
    // is wider than its footholds the camera could sit well outside the
    // rectangle the client would never let it leave.
    // the same map bounds already read above as `b`.
    const camBounds = b
      && Number.isFinite(b.left) && Number.isFinite(b.top)
      && Number.isFinite(b.right) && Number.isFinite(b.bottom)
      && b.right > b.left && b.bottom > b.top
      ? { left: b.left, top: b.top, right: b.right, bottom: b.bottom }
      : null;

    const view = { ...world, scale, world, camBounds, cssWidth, cssHeight };
    // Until something has placed the camera it IS the framed map corner, so a
    // viewer nobody is following draws the transform it always drew. Starting at
    // (0,0) instead would frame world origin, which on most maps is off to one
    // side and empty.
    view.minX = this.cam.placed ? this.cam.x : world.left;
    view.minY = this.cam.placed ? this.cam.y : world.top;
    return view;
  }

  /**
   * The viewport's half-extent in world units -- how much the camera may see
   * past its own position. Rectify() needs it to clamp; follow() needs it to
   * know how big a step the character has to stay centred within.
   */
  viewHalf() {
    const v = this.viewBox ?? this.layout();
    if (!v) return null;
    return { x: v.cssWidth / (2 * v.scale), y: v.cssHeight / (2 * v.scale) };
  }

  /**
   * Move the camera toward (x, y) so the character ends up centred, then hold
   * it inside the map. This is MapleCharacter.cs:749-767, in order.
   *
   * The two axes do NOT share a rate, and neither is 1/60. Horizontal is
   * 12f/800f = 0.015 per frame; vertical is a flat 0.01. They are copied, not
   * derived, because deriving them would have produced a different camera.
   *
   * The reference's own deadbands are kept: nothing moves while the character
   * is already within 1 world unit of centre, so a standing character does not
   * drift. Note the asymmetry -- the horizontal test looks at the DESTINATION
   * offset alone, the vertical test at the destination offset plus the current
   * camera. That is what the source does (MapleCharacter.cs:750 vs 754) and it
   * is preserved rather than tidied.
   *
   * The frame rate matters: both coefficients are per 1/60s tick, the same tick
   * the body is stepped at, so callers must call this once per stepped frame and
   * not once per painted one.
   *
   * @param {number} x the character's world X
   * @param {number} y the character's world Y
   */
  follow(x, y) {
    // A camera the reader has moved STAYS MOVED. The reference has no such state:
    // MapleCharacter.cs:749-755 drives the camera from the character's position
    // every frame, unconditionally. Free panning is therefore an OWN-DESIGN
    // departure, declared here rather than smuggled in — without this guard the
    // character walks off the edge of the picture within a second of every pan,
    // which is what "the camera cannot be moved" feels like from the other side.
    if (this.cam.free) { this.rectify(); return; }
    const v = this.viewBox ?? this.layout();
    if (!v) return;

    // The reference centres on a fixed 1024x768 screen (MapleMap.cs:42). The
    // canvas is neither of those, so the centred point comes from the canvas's
    // own half-extent in world units: the same computation, sized to this
    // viewport instead of the reference's.
    const half = this.viewHalf() ?? { x: 0, y: 0 };
    const cam = this.cam;

    // Not placed yet: snap. Lerping in from the default (0,0) slides the map
    // across the screen on every map load, which reads as the camera being
    // broken rather than settling.
    //
    // rectify() runs on the snap too, and it is not cosmetic: the character
    // stands near an edge of a small map, so centring on it puts the camera
    // past the boundary. The four clamps below would pull it back anyway on the
    // NEXT frame, but only after the character was drawn somewhere the map does
    // not cover.
    if (!cam.placed) {
      cam.x = x - half.x;
      cam.y = y - half.y;
      cam.placed = true;
      this.rectify();
      return;
    }

    // MapleCharacter.cs:749-751 -- horizontal, coefficient 12f/800f.
    const destX = half.x - x + cam.x;
    if (Math.abs(destX) > 1) cam.x -= destX * (12 / 800);

    // MapleCharacter.cs:753-755:
    //   DestY = (DisplaySize.Y / 2) + (Map.OffsetY + 60) - Y;
    // `half.y` is this viewport's `(DisplaySize.Y / 2)`. The `+ (Map.OffsetY +
    // 60)` half was the part that got dropped -- it read `half.y + 60 - y`,
    // keeping the constant and losing the offset beside it -- which left the
    // vertical camera solved for a 600px-tall display on a display that is not
    // 600px tall.
    //
    // MapleMap.cs:279 computes the missing half:
    //   Map.OffsetY = (Map.DisplaySize.Y - 600) / 2;
    //
    // That is a SCREEN PIXEL quantity, because the reference's world unit IS a
    // screen pixel -- it draws 1:1 with no zoom. Ours is not: `scale` pixels
    // cover one world unit. So the whole pixel constant is converted once, at
    // the point of use, instead of being added as if it were world units --
    // which is also why the old `60` was silently the wrong size at any zoom
    // other than 1. At scale 1 this is the reference's number exactly.
    const offsetY = ((v.cssHeight - 600) / 2 + 60) / v.scale;
    const destY = half.y + offsetY - y;
    if (Math.abs(destY + cam.y) > 1) cam.y -= 0.01 * (destY + cam.y);

    this.rectify();
  }

  /**
   * Hold the camera inside the map, MapleCharacter.cs:757-767.
   *
   * Four clamps first, then the smaller-than-viewport case: an axis whose map
   * is narrower than the view centres instead of pinning to an edge, because
   * "clamp to Left" on a narrow map would park the camera off the map and put
   * blank space where the picture is. The reference writes this as
   * Map.Left - ((DisplaySize.X - MapWidth) / 2) -- a negative offset -- which is
   * the same as centring and is reproduced literally here.
   */
  /**
   * Move the camera by a screen-space delta, in CSS pixels.
   *
   * @param {number} dxPixels rightward movement of the POINTER
   * @param {number} dyPixels downward movement of the pointer
   *
   * @description
   * Divides by the scale rather than multiplying, because `layout()` maps one
   * world unit to `scale` pixels: a pointer that moves right drags the MAP right
   * under it, so the camera origin moves LEFT by the same world distance.
   *
   * Goes through `rectify()`, so a pan cannot park the camera off the map — the
   * same four clamps the follow-cam uses (`MapleCharacter.cs:757-767`), which is
   * why panning to an edge stops at the edge instead of showing blank space.
   */
  panBy(dxPixels, dyPixels) {
    const scale = this.viewBox?.scale || 1;
    this.cam.free = true;
    this.canvas.classList.add('is-free');
    this.cam.x -= dxPixels / scale;
    this.cam.y -= dyPixels / scale;
    this.cam.placed = true;
    this.rectify();
    this.draw();
  }

  /**
   * Hand the camera back to the character.
   *
   * @description
   * Clears `cam.free`, and the next `follow()` snaps rather than lerping — the
   * same `placed` reset `load()` uses, because a lerp from across the map slides
   * the picture and reads as a broken camera.
   */
  recentre() {
    this.cam.free = false;
    this.cam.placed = false;
    this.canvas.classList.remove('is-free');
    this.draw();
  }

  rectify() {
    const v = this.viewBox ?? this.layout();
    if (!v) return;
    // The MAP's rectangle, not the drawn extent. See layout(): `world` is
    // padded framing for the picture, and clamping a camera to it is how the
    // camera ends up outside the map it is walking. Falls back to the drawn
    // extent only when the map carried no bounds at all, so a map with no VR
    // and no geometry still has something to clamp against instead of
    // dividing by the camera's own position.
    const w = v.camBounds ?? v.world;
    const half = this.viewHalf() ?? { x: 0, y: 0 };
    const cam = this.cam;

    // MapleCharacter.cs:757-761 -- four clamps, then the smaller-than-viewport
    // case at :766-767.
    if (cam.x < w.left) cam.x = w.left;
    if (cam.x > w.right - half.x * 2) cam.x = w.right - half.x * 2;
    if (cam.y < w.top) cam.y = w.top;
    if (cam.y > w.bottom - half.y * 2) cam.y = w.bottom - half.y * 2;

    if (w.right - w.left < half.x * 2) cam.x = w.left - ((half.x * 2 - (w.right - w.left)) / 2);
    if (w.bottom - w.top < half.y * 2) cam.y = w.top - ((half.y * 2 - (w.bottom - w.top)) / 2);

    // Camera.AdjustRectEnabled: snap the camera to whole SCREEN pixels before
    // it is used to draw. The reference has a flag for this and leaves it on,
    // because a camera sitting on a half-pixel makes every sprite land between
    // two device pixels and shimmer as the character walks. Ours scales instead
    // of drawing 1:1, so the snap is in device pixels -- the same intent, at
    // this viewer's own unit.
    //
    // It is applied AFTER the clamps on purpose: snapping first would push the
    // camera back outside by up to half a pixel, and the next frame would clamp
    // it again, and the figure would sit on the boundary jittering forever.
    if (this.adjustRect !== false) {
      const s = v.scale * this.dprScale();
      cam.x = Math.round(cam.x * s) / s;
      cam.y = Math.round(cam.y * s) / s;
    }
  }

  /**
   * The canvas's device-pixel ratio, for AdjustRect.
   *
   * Read fresh rather than cached: the value changes when the window moves
   * between displays or the browser zoom changes, and a stale one would leave
   * the camera snapped to the WRONG grid -- which is the shimmer this is
   * meant to remove, kept in a different form.
   */
  dprScale() {
    return window.devicePixelRatio || 1;
  }

  /**
   * Zoom the camera. 1 is the whole map at canvas width -- the historical
   * framing; above 1 is a closer frame that the camera can travel inside.
   * @param {number} factor multiplier applied to the current zoom
   */
  zoomBy(factor) {
    this.setZoom((this.zoom || 1) * factor);
  }

  /**
   * Drag the camera. Middle button, or space held — the same gesture
   * pixeleditor.js:690-717 already uses in this application, because it is what
   * every other canvas tool does and a reader who has learned it here has
   * learned it here.
   *
   * @returns {boolean} true when the event was a pan and must not be used for
   *   anything else
   */
  beginPan(event) {
    if (this.panPointer !== null) return false;
    this.panPointer = { id: event.pointerId, x: event.clientX, y: event.clientY };
    this.cam.free = true;
    this.canvas.classList.add('is-free');
    this.canvas.classList.add('is-panning');
    try { this.canvas.setPointerCapture(event.pointerId); } catch { /* not fatal */ }
    return true;
  }

  /**
   * Keep panning. Returns true while a pan is in progress, so the caller can skip
   * everything else.
   */
  movePan(event) {
    const pan = this.panPointer;
    if (!pan || pan.id !== event.pointerId) return false;
    this.panBy(event.clientX - pan.x, event.clientY - pan.y);
    pan.x = event.clientX;
    pan.y = event.clientY;
    return true;
  }

  endPan(event) {
    if (!this.panPointer || this.panPointer.id !== event.pointerId) return false;
    this.panPointer = null;
    this.canvas.classList.remove('is-panning');
    try { this.canvas.releasePointerCapture(event.pointerId); } catch { /* already gone */ }
    return true;
  }

  /**
   * The camera controls: nudge each way, and hand the camera back to the
   * character.
   *
   * @description
   * OWN-DESIGN. The reference has no camera controls at all —
   * `MapleCharacter.cs:749-755` drives the camera from the character every frame
   * and there is no code path that moves it otherwise. So the nudge buttons and
   * the drag are additions, and the only thing kept from the reference is the
   * behaviour they suspend: `cam.free` makes `follow()` stop, which is the same
   * thing `rectify()` (MapleCharacter.cs:757-767) already does for the
   * follow-cam's own clamping.
   */
  renderCameraControls(host) {
    const nudge = (dx, dy) => el('button', {
      class: 'btn btn-sm',
      'data-tip': t('Move the camera {dir}', { dir: dx && dy ? t('diagonally') : (dx ? t('right') : (dy ? t('down') : t('up'))) }),
      onclick: () => this.panBy(dx * 64, dy * 64),
    }, dx && dy ? '↖' : (dx > 0 ? '→' : (dx < 0 ? '←' : (dy > 0 ? '↓' : '↑'))));

    host.append(el('span', { style: 'color:#a0aec0' }, t('Camera')));
    host.append(nudge(-1, 0), nudge(0, -1), nudge(1, 0), nudge(0, 1));
    host.append(el('button', {
      class: 'btn btn-sm',
      'data-tip': t('Follow the character again'),
      onclick: () => this.recentre(),
    }, t('Recentre')));
    if (this.cam.free) {
      host.append(el('span', { style: 'color:#e6ff8a;font-size:var(--fs-11)' },
        t('free — drag the map or press Recentre')));
    }
  }

  /**
   * Set the zoom level. Never goes below 1: fit-to-width is the floor, because
   * that is the framing this view has always drawn and what the CLI checks and
   * loadVisible measure against.
   *
   * The ceiling is a drawing affordance, NOT a reference rule -- the reference
   * draws 1:1 and never zooms at all. It used to be `mapWidth / cssWidth`,
   * which pins zoom to exactly 1 on every map narrower than the canvas, and
   * that is most maps once the picture has the whole window: the map was
   * rendered fit-to-width into a wider canvas, so there was never any room to
   * zoom into. That ceiling encoded an assumption that the viewport can only
   * grow until it equals the map, which is false -- on a map narrower than the
   * view, the reference CAMERA centres (MapleCharacter.cs:766-767, which
   * rectify() already implements), and zooming simply crops into the middle of
   * it.
   *
   * The cap that remains is about pixels, not about the map: past it one world
   * unit is more than a few device pixels and the picture is a blur. It is
   * generous on purpose so that "the same spot at two zoom levels" is always
   * expressible.
   *
   * @param {number} z
   */
  setZoom(z) {
          // The floor used to be 1, which meant "the whole map at canvas width"
          // because layout() scaled the map to fit. layout() no longer does that
          // — scale is 1 (the player's own units) and the camera travels inside a
          // viewport — so the floor has to drop with it, or zooming out can
          // never get back to a whole-map overview.
          const zoom = Math.min(8, Math.max(0.15, z));
          this.zoom = zoom;
    // The cached box was built at the old scale, and it is what viewHalf()
    // measures the viewport from. Dropping it makes the next read recompute;
    // keeping it would clamp against a stale frame's height.
    this.viewBox = null;
    this.draw();
  }

  /**
   * The paint order for one frame: every drawable in the scene, in the order
   * the client's single Z-sorted draw list would produce it.
   *
   * Why a key and not a sort (requirement 5). The set of things that can be
   * drawn is fixed the moment a map loads -- its tiles, objects, footholds,
   * ladders and portals do not move and do not appear mid-walk. Only the
   * figure and its trail change from frame to frame. So the expensive half is
   * computed once per map (`this.scene`, built when geometry or art arrive)
   * and every frame only does the O(n) walk that pulls the two moving items
   * out to the right depth. A per-frame Array#sort over a whole map's geometry
   * at 60 Hz would be re-deciding a fact that changed zero times since the
   * map loaded.
   *
   * The scene is rebuilt whenever the document changes (loadArt) as well as
   * when geometry changes, because the tile list comes from the document: art
   * arriving is not a re-order, but the first art load is when the tile list
   * exists at all.
   *
   * Order is a plain insertion walk over a Z-ascending list, matching
   * SpriteEngine.cs:466-478 -- it does not call Array#sort at all, and neither
   * does this. Ascending Z is BACK to front; the paint loop walks it in order.
   */
  buildDrawList() {
    const g = this.geometry;
    const doc = this.doc;
    if (!g) { this.scene = []; return this.scene; }

    const scene = [];

    // Map art. Tiles and objects first, and both use the same per-layer band,
    // so an object at the same depth as a tile sorts between them by its own z
    // rather than being painted wholesale after the layer's tiles.
    for (const layer of doc?.layers ?? []) {
      const li = Number.isFinite(layer.index) ? layer.index : 0;
      for (const tile of layer.tiles ?? []) {
        // MapTile.cs:33 -- the tile's art z, not its entry's `z` node.
        scene.push({ z: Z_TILE(li, this.artZ(tile.art)), tile });
      }
      for (const obj of layer.objs ?? []) {
        scene.push({ z: Z_OBJ(li, obj.z ?? 0), obj });
      }
    }

    // The geometry below is diagnostic, and only enters the scene when the
    // overlay toggle is on. Art is the map; these are the scaffolding around it,
    // and one line per foothold is what turned the live view into a wireframe --
    // f4/Map/Map1/100000004.img alone carries 114 footholds.
    //
    // Leaving them out of the LIST rather than skipping them at paint time is
    // deliberate: that also keeps them out of the per-frame z walk, so the loop
    // stops comparing against 114 entries it was never going to draw.
    if (this.showOverlays !== true) {
      scene.sort((a, b) => a.z - b.z);
      this.scene = scene;
      return scene;
    }

    // Ground. Foetholds.cs:247 gives a foothold the number its layer node is
    // named with, so footholds and art of the same layer share one band and a
    // foothold on layer 2 is occluded by art on layer 3 -- which is the whole
    // point: standing behind a wall has to put the wall in front.
    for (const f of g.Footholds) {
      scene.push({ z: footholdLayerZ(f), f });
    }

    // Ladders. The client draws no sprite for a ladder rope, so there is no
    // ladder Z in the reference to copy -- only the CHARACTER's Z while it is
    // on one, `LadderRope.Page * 100000 + 60000` (MapleCharacter.cs:587). The
    // rope itself is given the same band the character gets while climbing, so
    // a climbing figure and the rope it hangs from are never separated by a
    // wall that the figure is in front of. /simulate/geometry does not carry
    // `page` (MapGeometryLadderDto has only x/top/bottom/upperFoothold), so
    // page is not reachable here and every rope takes page 0.
    for (const l of g.Ladders) {
      scene.push({ z: Z_LADDER(0), ladder: l });
    }

    // Markers: portals, and the map boundary. Both are overlays, not terrain,
    // and the client pins portals at a flat 1000000 (MapPortal.cs:107).
    for (const p of g.Portals) scene.push({ z: Z_MARKER, portal: p });

    scene.sort((a, b) => a.z - b.z);
    this.scene = scene;
    return scene;
  }

  /** The art's own z, 0 when the picture is unknown. Same read as mapedit-view.js:552. */
  artZ(key) {
    const meta = key ? this.doc?.art?.[key] : null;
    return meta?.z ?? 0;
  }

  /**
   * How many of this map's distinct pictures have no pixels.
   *
   * A slot counts as missing when the document had no path for it OR when the
   * fetch came back as something the browser could not decode (loadArt's and
   * loadVisible's onerror). Counted here rather than kept as a running total
   * because both loaders already write the same flag and a second counter would
   * be a second thing that can disagree with them.
   */
  get missingArt() {
    let n = 0;
    for (const slot of this.images?.values() ?? []) if (slot?.missing) n++;
    return n;
  }

  /** Blit one tile or object, anchoring it the way WZ art expects. */
  drawArt(item, px, py, scale) {
    const slot = this.images?.get(item.art);
    if (!slot?.ok || !slot.img || !slot.meta) return;

    const m = slot.meta;
    // WZ art is cropped to its own bounds and positioned by an origin vector.
    // Without the offset every frame lands shifted by its own crop.
    const w = m.w ?? slot.img.width;
    const h = m.h ?? slot.img.height;
    const ox = m.ox ?? 0;
    const oy = m.oy ?? 0;

    // `x`,`y` is the art's origin on the map. Draw so the origin lands there.
    const dx = px(item.x - ox);
    const dy = py(item.y - oy);
    const dw = w * scale;
    const dh = h * scale;
    if (dw < 0.5 || dh < 0.5) return;          // sub-pixel at this zoom
    this.ctx.drawImage(slot.img, 0, 0, m.w ?? slot.img.width, m.h ?? slot.img.height,
                       dx, dy, dw, dh);
  }

  /**
   * How much of the window height is NOT the canvas.
   *
   * Measured from the DOM: the rows above the canvas (section header,
   * breadcrumbs) and the rows below it (controls, equipment bar, HUD), plus the
   * gaps between them. The flat `120` this replaces was tuned before the
   * equipment bar existed, so adding the bar's 52px pushed the controls and the
   * HUD below the fold and the reader had to scroll to reach the very thing
   * that dresses the character.
   *
   * Measured rather than hardcoded because every one of these rows is
   * conditional -- the bar is absent until the archive lists slots -- and the
   * HUD changes height with its content.
   *
   * @returns {number} pixels to reserve. Never more than half the window, so a
   *   short window cannot squeeze the map down to nothing.
   */
  chromeHeight() {
    const vh = window.innerHeight || 800;
    // Rows above the canvas: from the top of the document to the canvas.
    const rect = this.canvas.getBoundingClientRect();
    const above = Math.max(0, Math.round(rect.top + window.scrollY));
    // Rows below: each optional row's own height, skipping ones that are not
    // laid out at all. offsetHeight of a display:none row is 0 anyway, but
    // offsetParent === null is the explicit test and reads as intent.
    const below = [this.controls, this.equipHost, this.hud]
      .reduce((sum, el) => sum + (el && el.offsetParent !== null ? el.offsetHeight : 0), 0);
    const gaps = 24;
    return Math.min(vh - 120, Math.max(120, above + below + gaps));
  }

  draw() {
    const g = this.geometry;
    const ctx = this.canvas.getContext('2d');
    if (!ctx) return;
    this.ctx = ctx;

    const view = this.layout();
    const dpr = window.devicePixelRatio || 1;
    const cssW = this.canvas.clientWidth || 900;
    // Published so follow() can clamp without re-scanning every foothold at 60Hz.
    // It is set before anything below can return, so it is never stale for the
    // frame actually drawn.
    this.viewBox = view;

    if (!view) {
      this.canvas.width = cssW * dpr;
      this.canvas.height = 260 * dpr;
      ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
      ctx.fillStyle = '#1a202c';
      ctx.fillRect(0, 0, cssW, 260);
      ctx.fillStyle = INK.text;
      ctx.font = '13px var(--font-ui, sans-serif)';
      ctx.fillText(t('No geometry loaded'), 12, 24);
      this.renderHud();
      this.renderSheet(null);
      return;
    }

    // Fetch whatever just walked into view and has no bitmap yet.
    //
    // `loadVisible` used to be called from `load()` and nowhere else, so it ran
    // once with whatever the camera happened to be looking at. Walking moved the
    // camera, new scenery came into frame, and none of it was ever requested:
    // measured over one right-walk, `drawSceneItem` was entered 33,274 times and
    // 11,303 of those found a cache slot whose `img` was still null. The slot is
    // created up front for every art key (`img: null, ok: false`), so "slot
    // exists" is not evidence the picture arrived — the picture is the only
    // evidence, which is why a whole-map view came out with chunks missing.
    //
    // Guarded by a single in-flight flag: `draw()` runs at frame rate, and 60
        // overlapping batch loops would ask for the same hundreds of PNGs again.
        if (!this._visibleLoading) {
              // Throttled to 5Hz. This scan walks every tile and object of every layer
              // just to find the ones that walked into view without a bitmap, and it was
              // running on every one of the 60 frames a second the live walk spends in
              // draw(). Frame-rate map items do not need to be looked for 60 times a
              // second, and the walk's 60Hz draw loop was: this is what wedged the
              // renderer on a map switch. 200ms is far below the ~300ms a tile batch
              // takes to come back, so a tile that lands is still picked up on the next
              // pass -- the difference is invisible and the frame loop stops drowning.
              const nowMs = (typeof performance !== 'undefined' ? performance.now() : 0);
              if (nowMs - (this._visibleCheckAt ?? -1e9) >= 200) {
                this._visibleCheckAt = nowMs;
                const s = view.scale;
                let wanted = 0;
                // Walk tiles then objs without building the `[...tiles, ...objs]` concat
                // array this loop used to allocate per layer per call.
                for (const layer of this.doc?.layers ?? []) {
                  const groups = [layer.tiles, layer.objs];
                  for (let g = 0; g < groups.length; g++) {
                    const list = groups[g];
                    if (!list) continue;
                    for (let i = 0; i < list.length; i++) {
                      const item = list[i];
                      if (!item.art) continue;
                      const slot = this.images?.get(item.art);
                      if (slot && (slot.img || slot.missing)) continue;
                      const cx = (item.x - view.minX) * s;
                      const cy = (item.y - view.minY) * s;
                      if (cx > -120 && cx < view.cssWidth + 120 && cy > -120 && cy < view.cssHeight + 120) {
                        wanted++;
                      }
                    }
                  }
                }
                if (wanted > 0) {
                  this._visibleLoading = true;
                  // `load()` stamps a new generation per map. Without the check below,
                  // the OLD map's still-in-flight batch resolves after the new map has
                  // already started its own, and clears `_visibleLoading` while that
                  // one is still running -- which re-opens the concurrent-batch
                  // condition the flag exists to prevent.
                  const gen = this._mapGen;
                  this.loadVisible()
                    .then(() => {
                      if (this._mapGen === gen) { this._visibleLoading = false; this.draw(); }
                    })
                    .catch(() => {
                      if (this._mapGen === gen) this._visibleLoading = false;
                    });
                }
              }
            }

    // Height follows the visible area's own aspect ratio so the ground is not
    // squashed, and layout() has already worked it out from the scale --
    // the camera needs that number to clamp vertically before this frame is drawn.
    // At zoom 1 it is the height this view has always drawn.
    const cssH = view.cssHeight;

    this.canvas.style.height = `${cssH}px`;
    this.canvas.width = Math.round(cssW * dpr);
    this.canvas.height = Math.round(cssH * dpr);

    const ox = view.minX;
    const oy = view.minY;
    const s = view.scale;
    // WZ Y grows downward, canvas Y grows downward: no flip needed.
    const px = (x) => (x - ox) * s;
    const py = (y) => (y - oy) * s;

    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.clearRect(0, 0, cssW, cssH);
    ctx.fillStyle = '#1a202c';
    ctx.fillRect(0, 0, cssW, cssH);

    // The trail and the character, and where in the depth order they go.
    //
    // The character's Z comes from the FOOTHOLD IT STANDS ON, not from a
    // constant: `Z = FH.Z * 100000 + 60000` (MapleCharacter.cs:561), recomputed
    // on every handover. That is why a figure whose foothold changes layer
    // visibly moves from behind the scenery to in front of it, and it is why
    // climbing takes a different formula again -- the ladder's page, not the
    // foothold's layer (MapleCharacter.cs:587). A trace tick names the foothold
    // it is on, so the foothold list is indexed by id once per map and the
    // lookup is a map read, not a scan.
    const cur = this.ticks[this.frame];
    const charZ = this.characterZ(cur);

    // The transform THIS paint used, kept on the view so a measurement taken from
    // outside the paint loop can map a recorded `drawImage` argument back to a
    // world coordinate.
    //
    // `view` is a fresh object built per paint, so its `minX` does not survive the
    // frame; the walkview itself never had one, and a reader asking for it got
    // `undefined`, which then read as 0 and put a ~1700 px error into every offset
    // the judge computed. It is written here, next to `cur` and the px/py that
    // consumed `ox`/`oy` above, because that is the only point where all of them
    // describe the same frame.
    this.lastTransform = { ox, oy, s, dpr, charX: cur?.x ?? 0, charY: cur?.y ?? 0 };

    // The trail follows the character at the same depth. It is not a separate
    // layer in the client -- it has no counterpart there at all -- so it takes
    // the figure's Z: a trail drawn over a wall the figure is behind would
    // state the opposite of what the figure states.
    //
    // Under the scenery it launched from rather than a band per frame of fall;
    // characterZ() is what holds that last foothold's depth across a jump.
    const upto = this.ticks.slice(0, this.frame + 1);

    // The mobs, each with the depth its own foothold gives it, sorted. The sort
    // is over the mob list only — a handful of entries, not the map's geometry,
    // so the O(n) rule below still holds: the map's own paint order stays the
    // precomputed one. They are kept in a `sorted` copy rather than sorted in
    // place because this order is the frame's order, not the session's.
    const mobs = this.mobs ?? [];
    const charZNow = this.characterZ(cur);
    // A mob whose foothold is not on this map has no depth at all. Sorting it to
    // Infinity pinned it to the very end of the frame -- in front of the
    // character, every mob, and every tile -- which is one of the two ways the
    // player ended up buried. `charZNow` is the honest substitute: same band as
    // the character, so it joins the same tie-break as a mob that does have a
    // foothold, instead of being exempted from it. A mob that cannot be placed
    // is drawn, not privileged.
    const mobsSorted = mobs
      .map((m) => ({ m, z: this.mobZ(m) ?? charZNow ?? 0 }))
      .sort((a, b) => a.z - b.z);

    // The character, from the archive when the archive has it.
    //
    // Composition is a network round trip (AnimationService.Describe plus one
    // /api/canvas per frame), so it is asked for only when the ACTION changes.
    // The result -- the frames, their origins, their delays -- is then kept and
    // advanced by the sprite's own clock, which is what makes the figure move
    // at the WZ rate rather than at whatever rate this happens to redraw.
    const avatarFrame = this.stepAvatar(cur);

    // The paint order is the precomputed Z-ascending scene (back to front) with
    // the moving pair spliced in at its depth. This is the O(n) per-frame walk
    // requirement 5 asks for: no sort happens here, only a comparison against
    // the one Z that can have changed.
    const scene = this.scene ?? this.buildDrawList();
    let drawn = false;
    let mobI = 0;
    for (const item of scene) {
      // Strictly greater, which is the reference's tie-break and not an
      // oversight: AddDrawList's test is `DrawList[I].z - sprite.z - 1 < 0`, so
      // it steps past every entry whose z is LESS THAN OR EQUAL to the
      // newcomer and inserts behind it. Equal depth therefore puts the figure
      // in front -- which is what a character on a layer-0 foothold (Z 60000)
      // against a rope on page 0 (Z 60000) has to come out as, or the figure
      // vanishes behind the ladder it is climbing.
      if (!drawn && charZ !== null && item.z > charZ) {
        // Mobs at or behind the character's depth go FIRST, so the tie-break the
        // comment below states -- equal depth puts the figure in front -- holds
        // for mobs as well as for tiles. It did not: the character was painted
        // here and every mob with z <= item.z (which includes every mob sharing
        // the character's own layer) was painted after it, on top of it.
        while (mobI < mobsSorted.length && mobsSorted[mobI].z <= charZ) {
          this.drawMob(ctx, px, py, s, mobsSorted[mobI].m);
          mobI++;
        }
        this.drawWalk(ctx, px, py, s, upto, cur, upto.length, avatarFrame);
        // W-076: the effects the reference paints before the avatar (z < 0).
        this.drawSkillEffectPass(ctx, px, py, s, cur, true);
        drawn = true;
      }
      // Same comparison, same tie-break, same pass: a mob behind this tile is
      // painted before it and one in front after it, with the character left
      // where the client's single draw list would have put all three.
      while (mobI < mobsSorted.length && (mobsSorted[mobI].z ?? Infinity) <= item.z) {
        this.drawMob(ctx, px, py, s, mobsSorted[mobI].m);
        mobI++;
      }
      this.drawSceneItem(item, ctx, px, py, s);
    }
    // Past everything -- the figure is in front of the whole map. Happens when
    // it is standing on the topmost layer, which is ordinary.
    if (!drawn && cur) {
      this.drawWalk(ctx, px, py, s, upto, cur, upto.length, avatarFrame);
      this.drawSkillEffectPass(ctx, px, py, s, cur, true);
    }
    // Whatever mobs are still behind the last tile, for the same reason.
    for (; mobI < mobsSorted.length; mobI++) {
      this.drawMob(ctx, px, py, s, mobsSorted[mobI].m);
    }

    // The map's own boundary is a marker rather than terrain (see Z_MARKER):
    // it is the thing that stops a walk at the edge and it belongs on top of
    // the map, so it is painted after the sorted scene rather than inside it.
    // load() normalises the wire format to the DTO's own PascalCase names once, at
    // the boundary. Every read below follows those names: `g.ladders` is not a
    // second spelling, it is undefined, and the ladder loop it guarded threw on
    // every frame of every loaded map.
    //
    // Gated with the rest of the geometry: the box is one more diagnostic line
    // around a map the user is playing, not part of it.
    if (g.Bounds && this.showOverlays === true) {
      ctx.strokeStyle = INK.bounds;
      ctx.setLineDash([4, 4]);
      ctx.lineWidth = 1;
      ctx.strokeRect(px(g.Bounds.left), py(g.Bounds.top),
                     (g.Bounds.right - g.Bounds.left) * s,
                     (g.Bounds.bottom - g.Bounds.top) * s);
      ctx.setLineDash([]);
    }

    this.drawDamageNumbers(ctx, px, py, s);
    this.drawNpcLabels(ctx, px, py, s, this.mobs ?? []);
    this.drawSkillEffects(ctx, px, py, s, cur);   // z >= 0: in front, as Char.cpp:240 does
    this.renderHud(cur);
    this.renderSheet(cur);
  }

  /**
   * The label an NPC carries above itself, as `NpcText.DoDraw` draws it.
   *
   * @description
   * <b>`MapleNecrocer/Client/Npc.cs:261-298`</b>, row for row:
   *
   * <pre>
   *   id    cyan    box (x-2, WY+36, w+5, 15)  text WY+37
   *   name  yellow  box (x-2, WY+3,  w+5, 16)  text WY+4
   *   func  yellow  box (x-2, WY+21, w+5, 16)  text WY+22
   *   background: FillRoundRect(..., Color(0,0,0,150))
   *   centred: X = WX - width/2 ;  GMS OffsetY = 0
   * </pre>
   *
   * The two gates are the reference's, not taste: the id line is NOT hidden by
   * `hideName` (`:279-280` returns early only for the NAME, after the id is
   * already drawn), and the function line needs `HasFunc`, which the server
   * derives from the STRING being non-empty rather than the node existing
   * (`:174-175`).
   *
   * <p>Drawn last, over everything, because `Npc.cs:178` puts the label at
   * `Z = 1000000 + 100000` — above the whole map, not at the NPC's own depth.
   */
  drawNpcLabels(ctx, px, py, s, mobs) {
    ctx.save();
    ctx.textAlign = 'center';
    ctx.textBaseline = 'top';
    for (const mob of mobs) {
      // Mob nameplate: 【LVxx】Name, BELOW the body.
      //
      // Below, not above: `py = (y - oy) * s` (walkview.js:2250) so a POSITIVE
      // offset is lower on screen, and the existing NPC label already puts its
      // name at `WY + 3` (Npc.cs:273-296). The mob label follows the same
      // convention rather than inventing one.
      //
      // Not gated on a toggle: the HP bar above the body is already unconditional
      // (drawMobHp, :2927), and a mob whose level you cannot read is exactly the
      // one worth naming.
      if (mob?.kind === 'm') {
        const name = mob.mobName || '';
        const lv = Number.isFinite(mob.level) ? mob.level : 0;
        const text = lv > 0 ? `【LV${lv}】${name}` : (name || `ID:${mob.mobId}`);
        if (text) {
          ctx.font = '12px system-ui, sans-serif';
          const w = ctx.measureText(text).width;
          const x = px(mob.x) - w / 2;
          const y = py(mob.y) + 4 * s;
          ctx.fillStyle = 'rgba(0,0,0,0.588)';
          ctx.fillRect(x - 2 * s, y, w + 5 * s, 16 * s);
          ctx.fillStyle = '#ffff00';
          ctx.fillText(text, px(mob.x), y + s);
        }
        continue;
      }
      if (mob?.kind !== 'n') continue;
      const label = (text, colour, yBox, yText, height) => {
        if (!text) return;
        ctx.font = '12px system-ui, sans-serif';
        const w = ctx.measureText(text).width;
        const x = px(mob.x) - w / 2;
        const y = py(mob.y) + yBox * s;
        ctx.fillStyle = 'rgba(0,0,0,0.588)';   // Color(0,0,0,150) over the map
        ctx.fillRect(x - 2 * s, y, w + 5 * s, height * s);
        ctx.fillStyle = colour;
        ctx.fillText(text, px(mob.x), y + s);
      };
      // Order matters: the id is drawn BEFORE the hideName check, exactly as
      // Npc.cs:270-280 does it, so an NPC that hides its name still shows its id.
      if (this.showNpcId) label(`ID:${mob.mobId}`, '#00ffff', 36, 37, 15);
      if (!mob.npcHideName) {
        label(mob.npcName, '#ffff00', 3, 4, 16);
        if (mob.npcFunc) label(mob.npcFunc, '#ffff00', 21, 22, 16);
      }
      // The balloon regardless of `hideName`, and ABOVE the name plate: the
      // reference hides a name but not the NPC's speech (Npc.cpp draws the two
      // from different origins), and a talking NPC with no name is a state the
      // archives really do produce.
      this.drawBalloon(ctx, px, py, s, mob);
    }
    ctx.restore();
  }

  /**
   * Resolve the balloon's nine edges, its arrow and its text colour from the
   * server, and load them.
   *
   * @returns {Promise<object|null>} null when the archive has no `npc` balloon,
   *   which is a real state (UI.nx not open) and not something to paper over.
   */
  async balloonArt() {
    // The balloon is the same for every NPC, so one NPC on the map is enough to
    // ask about it and the answer applies to all of them.
    const npc = (this.mobs ?? []).find((m) => m?.kind === 'n');
    const id = npc?.mobId ?? '10000';
    const res = await fetch(`/api/simulate/npc-chat?npcId=${encodeURIComponent(id)}`);
    if (!res.ok) return null;
    const body = await res.json();
    const balloon = body?.balloon;
    if (!balloon?.found) return null;

    const load = (path) => new Promise((done) => {
      if (!path) { done(null); return; }
      const img = new Image();
      img.onload = () => done(img);
      img.onerror = () => done(null);
      img.src = `/api/canvas?path=${encodeURIComponent(path)}`;
    });

    const edges = {};
    const [nw, n, ne, w, c, e, sw, s, se, arrow] = await Promise.all([
      load(balloon.edges.nw), load(balloon.edges.n), load(balloon.edges.ne),
      load(balloon.edges.w), load(balloon.edges.c), load(balloon.edges.e),
      load(balloon.edges.sw), load(balloon.edges.s), load(balloon.edges.se),
      load(balloon.arrow),
    ]);
    if (!nw || !c) return null;

    // ChatBalloon.cpp:71-77 keeps the low three bytes of `clr` and forces alpha
    // to 255. 0xFF800000 measured, which is the maroon the reference names.
    const rgb = (balloon.textColor ?? 0x800000) & 0xffffff;
    const textColour = `rgb(${(rgb >> 16) & 255},${(rgb >> 8) & 255},${rgb & 255})`;

    return {
      ready: true,
      nw, n, ne, w, c, e, sw, s, se, arrowImg: arrow,
      arrow: { width: arrow?.width ?? 0, height: arrow?.height ?? 0 },
      textColour,
    };
  }

  /**
   * The attacks one mob has, into a row — or the reason it has none.
   *
   * @description
   * Own-design, and the reason is the report it answers: "the mob does not
   * attack" is not answerable from the screen. A mob with no `attack*` node in
   * Mob.nx can never play an attack animation, and it looks exactly like a mob
   * whose animation is broken. This row names which of the two it is.
   *
   * An EMPTY list is stated, not left blank: 41% of Mob.nx carries an attack
   * node (measured 2026-10-16, 50 of 120 sampled), so "no attacks" is the common
   * case and needs saying. On `f4/Map/Map1/100000004.img` it is 0 of 2.
   */
  async renderMobAttacks(mobId, host) {
    if (!mobId) return;
    clear(host);
    host.append(el('span', { style: 'opacity:.75;margin-right:6px' }, t('Attacks')),
      el('span', {}, t('reading…')));
    try {
      const res = await fetch(`/api/simulate/mob-attacks?mobId=${encodeURIComponent(mobId)}`);
      if (!res.ok) throw new Error(String(res.status));
      const body = await res.json();
      const list = body?.attacks ?? [];
      clear(host);
      host.append(el('span', { style: 'opacity:.75;margin-right:6px' }, t('Attacks')));
      if (!list.length) {
        host.append(el('span', {},
          t('none in the archive — this mob only deals contact damage')));
        return;
      }
      host.append(el('span', {}, list.map((a) => {
        const reach = a.radius
          ? t('radius {n}', { n: a.radius })
          : (a.range && (a.range.right - a.range.left)
            ? t('{n}px', { n: a.range.right - a.range.left }) : '—');
        return `${a.action} · ${t('power')} ${a.power} · ${reach}`;
      }).join('   ')));
    } catch (err) {
      clear(host);
      host.append(el('span', { style: 'opacity:.75;margin-right:6px' }, t('Attacks')),
        el('span', {}, t('could not be read: {msg}', { msg: String(err.message ?? err) })));
    }
  }

  /**
   * What an NPC is saying, in the balloon the archive supplies.
   *
   * @description
   * `ChatBalloon.cpp:61-82` + `Npc.h:127`: `UI.nx["ChatBalloon.img"]["npc"]` is a
   * 9-slice — `nw n ne w c e sw s se` — plus an `arrow`, plus a `clr` the text is
   * tinted with. Measured on this archive: all nine edges and the arrow are
   * present, and `clr` is `-8388608` = `0xFF800000`, the maroon the reference's own
   * comment names for NPCs.
   *
   * The text comes from the SERVER (`mob.speech`), never from here: which line an
   * NPC says and when is `Npc::speak`'s pool arithmetic over states and lines
   * (Npc.cpp:478-484), and a client that picked its own line would be a second,
   * drifting implementation of it.
   *
   * Own-design in one respect: the reference scales the frame to the measured
   * text width, and so does this, but it also WRAPS at a width no archive
   * specifies. Without a wrap, one long Chinese line runs off the canvas; the
   * cap is this project's, and it is said here rather than left to be found.
   */
  drawBalloon(ctx, px, py, s, mob) {
    if (!mob.speech) return;
    const art = this.balloon;
    if (!art?.ready) { this.loadBalloon(); return; }

    ctx.font = '12px system-ui, sans-serif';
    const maxWidth = 26 * 8 * s;
    const words = String(mob.speech).split('');
    const lines = [];
    let line = '';
    for (const ch of words) {
      const probe = line + ch;
      if (ctx.measureText(probe).width > maxWidth && line) { lines.push(line); line = ch; }
      else line = probe;
    }
    if (line) lines.push(line);
    if (!lines.length) return;

    const widest = Math.max(...lines.map((l) => ctx.measureText(l).width));
    const lineHeight = 14 * s;
    const padX = 6 * s;
    const w = Math.max(widest + padX * 2, art.nw.width * s + art.ne.width * s);
    const h = lines.length * lineHeight + art.nw.height * s + art.ne.height * s;
    // Above the NPC, which is what the reference does: ChatBalloon.cpp:100-110
    // draws at the character's position with the text lifted by its own height.
    const x = px(mob.x) - w / 2;
    const y = py(mob.y) - art.arrow.height * s - h;

    ctx.save();
    this.nineSlice(ctx, art, x, y, w, h, s);
    if (art.arrowImg) {
      ctx.drawImage(art.arrowImg, px(mob.x) - (art.arrow.width * s) / 2,
        py(mob.y) - art.arrow.height * s, art.arrow.width * s, art.arrow.height * s);
    }
    ctx.textAlign = 'center';
    ctx.textBaseline = 'top';
    ctx.fillStyle = art.textColour;
    const top = y + art.nw.height * s;
    lines.forEach((l, i) => {
      ctx.fillText(l, px(mob.x), top + i * lineHeight + s);
    });
    ctx.restore();
  }

  /** The nine edges in the order that tiles a rectangle. */
  nineSlice(ctx, art, x, y, w, h, s) {
    const put = (img, dx, dy, dw, dh) => {
      if (img && dw > 0 && dh > 0) {
        ctx.drawImage(img, x + dx * s, y + dy * s, dw * s, dh * s);
      }
    };
    const cw = art.nw.width * s, ch = art.nw.height * s;
    const ew = art.ne.width * s, eh = art.se.height * s;
    const midW = Math.max(0, w - cw - ew);
    const midH = Math.max(0, h - ch - eh);
    put(art.nw, 0, 0, cw, ch);
    put(art.ne, cw + midW, 0, ew, ch);
    put(art.sw, 0, ch + midH, cw, eh);
    put(art.se, cw + midW, ch + midH, ew, eh);
    put(art.n, cw, 0, midW, ch);
    put(art.e, cw + midW, ch, ew, midH);
    put(art.w, 0, ch, cw, midH);
    put(art.s, cw, ch + midH, midW, eh);
    put(art.c, cw, ch, midW, midH);
  }

  /**
   * Fetch the balloon's nine edges and its arrow, once.
   *
   * Asks the SERVER which nodes and which colour, rather than hard-coding
   * `f16/ChatBalloon.img/npc`: the file id depends on open order, and a hard-coded
   * one is exactly the sort of thing that works until a restart.
   */
  async loadBalloon() {
    if (this.balloonLoading || this.balloon === false) return;
    this.balloonLoading = true;
    try {
      const art = await this.balloonArt();
      if (!art) { this.balloon = false; return; }
      this.balloon = art;
    } catch {
      this.balloon = false;
    } finally {
      this.balloonLoading = false;
    }
  }

  /**
   * The sprites a running cast draws.
   *
   * @param {object|null} cur the frame being painted
   *
   * @description
   * The server sends the cast's own `SkillCastSprite`s with the frame
   * (MapSimSessionStore.CastSkill), so this is a drawing job, not a lookup: the
   * archive each sprite lives in, its frame count and its first frame all arrive
   * resolved.
   *
   * <p>Frames come from the sprite's own container, which is the image path minus
   * its last segment -- `.../effect/0/0` lives in `.../effect/0`, whose children
   * are `0..n`. That is the same shape `SkillCastService` reads, and deriving the
   * siblings here rather than shipping a second list per sprite keeps the wire
   * small.
   *
   * <p>Placed on the character and drawn UNDER it, because the reference spawns
   * the cast's sprites into the same list the body is in and the body's own z
   * decides which is in front (CharLook.cpp:161-204 is the same ordering rule for
   * a character that carries many parts).
   */
  drawSkillEffects(ctx, px, py, s, cur) {
    this.drawSkillEffectPass(ctx, px, py, s, cur, false);
  }

  /**
   * W-076. The effects with a negative z go BEHIND the character.
   *
   *   SkillUseEffect.h:50    z = src["z"];
   *   EffectLayer.cpp:24      drawbelow walks up to and including index -1
   *   EffectLayer.cpp:35      drawabove starts there
   *   Char.cpp:95             effects.drawbelow(absp)   BEFORE the avatar
   *   Char.cpp:160            the body
   *   Char.cpp:240            effects.drawabove(absp)   AFTER it
   *
   * We painted every effect in one pass after the avatar, so an effect authored
   * with a negative z covered the character instead of sitting behind it.
   * 1121008 carries no z at all, which is why this looked right there and wrong
   * elsewhere.
   *
   * `behind` selects the half; both halves run the same code so the two cannot
   * drift apart.
   */
  drawSkillEffectPass(ctx, px, py, s, cur, behind) {
    const list = cur?.skillEffects ?? [];
    const tiles = cur?.tileEffects ?? [];
    if ((!list.length && !tiles.length) || !cur) return;

    const cache = this.skillFx ??= new Map();
    // W-076. THE ARCHIVE'S OWN CLOCK, not one invented here.
    //
    //   Animation.cpp:64-67   delay = src["delay"]; if (delay == 0) delay = 100;
    //   EffectLayer.h:59-61   sprite.update(TIMESTEP * speed)   per 8ms tick
    //   Char.cpp:607-612      get_real_attackspeed() = 1.7f - speed/10
    //
    // so a frame lasts `delay / speed` ms of real time. MEASURED on 1121008: nine
    // frames, delay 120 each. This block used to advance on a hardcoded 90 ms per
    // frame, which is 75% of the reference's rate and wraps on `% n` instead of
    // ending -- the effect visibly ran slow.
    // W-089. THE CLOCK DOES NOT RUN UNTIL THE ART IS HERE.
    //
    // The comment on `if (!img?.naturalWidth) continue;` below has always said
    // "so the cast simply starts when its own art is ready". Nothing implemented
    // that: `born` was the cast's own start and the art was requested at the same
    // moment, so on a map that is still loading its own background the two raced,
    // and the art lost. MEASURED 2026-10-18 on f4/Map/Map1/100000004.img,
    // 1121008: the nine frame images reported `complete === false` for the whole
    // ~1.2 s pose and only finished at about 12 s, and because the clock had run
    // on without them the one frame that finally drew was frame 8 -- the LAST
    // one. A nine-frame slash appeared as its final image.
    //
    // So the clock is armed by the art, not by the cast: it starts on the frame
    // that first sees every entry's frame 0 decoded, and until then nothing is
    // drawn and no time passes. The delay arithmetic below is unchanged; only the
    // instant it is measured from moved.
    //
    // It is keyed on `cur.skillCastSeq` rather than read from the `skillFxBorn`
    // livewalk.js:1464 publishes, which is the same cast identity for the same
    // reason: a second cast of the same skill is a different clock either way,
    // and a clock this file owns is one it can also refuse to start.
    const now = performance.now();
    // `cur.skillCastSeq` DOES NOT EXIST. The tick livewalk.js:1401 builds carries
    // `skillStance`, `dir`, `step`, `skillEffects` and `tileEffects` and not the
    // cast's sequence number, so reading it off the tick is `undefined ?? 0` --
    // one clock for the whole session, armed by the first skill ever cast and
    // then only ever growing. Every later cast then resolved to its LAST frame
    // (`found < 0 -> found = n - 1`) and no skill after the first animated at all.
    // The sequence is on the live state, one level up, and that is where it is
    // read from.
    // W-094. The clock is PER CAST, and the cast is the SPRITE'S OWN.
    //
    // `live.state.skillCastSeq` is the sequence number of the LAST FRAME THE
    // SERVER SENT, and the frame that carries a new cast is itself a frame late:
    // until it lands the client holds the old number, so the first paint of a
    // new cast ran the previous cast's clock -- already at its last frame.
    // MEASURED 2026-10-18: effect frames [8, 0, 0, 0, 0, 1, ...] on a nine-frame
    // animation, i.e. the tail of the previous effect first, which is the pop
    // that reads as jitter.
    //
    // The sprite carries its own cast (`SkillCastSprite.CastSeq`), and the clock
    // is looked up INSIDE the loop, because `fx` is the loop's variable -- reading
    // it here was a ReferenceError that killed the whole paint.
    const clocks = this.skillFxClock ??= new Map();

    // W-088. The two lists differ in exactly two things — where the rectangle is
    // anchored, and who decides the mirror — so they are NORMALISED into one
    // entry shape and the rectangle below is computed once.
    //
    //   cur.skillEffects   a cast sprite: anchored AT the character, mirrored by
    //                      the character's own facing (`DrawArgument(facing_right)`,
    //                      Char.cpp:645) and running at `speed` = the character's
    //                      real attack speed.
    //   cur.tileEffects    a ground decal: anchored at the character PLUS a shift
    //                      (`DrawArgument(position, facing_right)`, Char.cpp:657,
    //                      whose `position` is the decal's `rel_pos`), mirrored by
    //                      the character's facing too, but running at the speed the
    //                      server resolved.
    //
    // `sprite` is on the tile entry rather than splatted onto it because the hit
    // effect's own pass reads the same field, and one name for the payload keeps
    // the two passes from drifting.
    // A fresh array, never `list` itself: `list` is `cur.skillEffects`, the very
    // object the server's frame carries, and pushing tiles into it would both
    // grow it on every draw (60 times a second) and hand the next frame a list
    // that already contains them.
    const entries = list.length ? list.slice() : [];
    for (const fx of tiles) {
      if (!fx?.sprite?.imagePath) continue;
      entries.push({
        imagePath: fx.sprite.imagePath,
        frames: fx.sprite.frames,
        delays: fx.sprite.delays,
        origins: fx.sprite.origins,
        z: fx.z,
        speed: fx.speed,
        shiftX: fx.shiftX ?? 0,
        shiftY: fx.shiftY ?? 0,
        // W-089. A ground decal has no archive node of its own -- it is a
        // `tile/<index>` the server already resolved -- so it is named here
        // rather than published as `null`. A measurement that separates the
        // caster's cast sprites from its decals needs a word to separate them
        // by, and "the entry has no node" is not the same as "this is a decal".
        node: 'tile',
        // The CASTER's facing, sent by the server for the reason
        // `drawMobHitEffectPass` receives `flipped` on the wire rather than
        // reading `cur.dir` here: `SkillUseEffect.cpp:257` passes
        // `facing_right` into `DrawArgument(position, facing_right)`, and the
        // decal mirrors with the character that threw it.
        flipped: fx.flipped === true,
      });
    }

    // Every entry's frames, requested before the clock is consulted so that
    // "is the art here" is a question this function can answer rather than one it
    // discovers halfway through the draw.
    const art = entries.map((fx) => {
      if (!fx?.imagePath) return null;
      const container = fx.imagePath.replace(/\/[^/]+$/, '');
      let imgs = cache.get(container);
      if (!imgs) {
        imgs = [];
        const n = Math.max(1, fx.frames | 0);
        for (let i = 0; i < n; i++) {
          const img = new Image();
          img.src = `/api/canvas?path=${encodeURIComponent(`${container}/${i}`)}`;
          imgs.push(img);
        }
        cache.set(container, imgs);
      }
      return imgs;
    });

    for (let order = 0; order < entries.length; order++) {
      const fx = entries[order];
      if (!fx?.imagePath) continue;
      // z < 0 is behind the avatar (EffectLayer.cpp:24); 0 and up is in front.
      // A tile is z = 4 on both of its paths (SkillUseEffect.cpp:211,257), so it
      // lands in the same half a cast sprite with z = 0 does.
      if (((fx.z ?? 0) < 0) !== behind) continue;
      const imgs = art[order];

      // This entry's own cast, and the clock that goes with it.
      const castSeq = fx.castSeq ?? (this.live?.state?.skillCastSeq ?? 0);
      let clock = clocks.get(castSeq);
      if (!clock) {
        // Bounded for the same reason the hit effect's map is (W-083): an old
        // cast's clock is dead the moment its animation ends, and a session is not
        // endless.
        if (clocks.size >= 16) clocks.delete(clocks.keys().next().value);
        clock = { born: null };
        clocks.set(castSeq, clock);
      }
      if (clock.born == null) {
        // The clock starts when the ART is ready, not when the cast is announced:
        // "the cast simply starts when its own art is ready". Until then this
        // entry draws nothing and no time passes.
        if (!(imgs && imgs[0] && imgs[0].naturalWidth > 0)) continue;
        clock.born = now;
      }
      const age = now - clock.born;
      const n = Math.max(1, fx.frames | 0);
      const speed = fx.speed > 0 ? fx.speed : 1;
      const delays = fx.delays ?? [];

      // Walk the frames by their own delays, the way Frame::update does, and stop
      // on the last frame once the animation is out rather than wrapping.
      let acc = 0;
      let found = -1;
      for (let i = 0; i < n; i++) {
        const d = (delays[i] || 100) / speed;
        if (age < acc + d) { found = i; break; }
        acc += d;
      }
      if (found < 0) found = n - 1;
      const img = imgs[found];
      // An <img> that has not decoded yet has naturalWidth 0. Drawing it would be
      // a silent no-op for a frame and then a pop, so the cast simply starts when
      // its own art is ready. W-089 moved that start: see the clock block above --
      // the effect is not drawn at all until frame 0 of every entry has decoded,
      // so the animation cannot start without the art it is going to show.
      if (!img?.naturalWidth) continue;

      // W-083. A LITERAL TRANSCRIPTION of the reference's rectangle, not a
      // paraphrase of it.
      //
      //   Sprite.cpp:27-31        absargs = stateargs + parentpos
      //   Char.cpp:645            DrawArgument(facing_right) -> pos (0,0), center (0,0),
      //                            xscale = flip ? -1 : +1      (DrawArgument.h:40)
      //   DrawArgument.h:131-157  rlt  = pos - center - origin
      //                           left  = cx + xscale * rlt.x
      //                           right = cx + xscale * (rlt.x + w)
      //                           top   = cy + yscale * rlt.y
      //                           bot   = cy + yscale * (rlt.y + h)
      //
      // `pos == center == the character's position`, so rlt = -origin and:
      //
      //   facing RIGHT (xscale +1): left = charX - originX, right = left + w
      //   facing LEFT  (xscale -1): left = charX + originX, right = left - w
      //
      // W-088. A GROUND DECAL is the same rectangle with a non-zero `pos`:
      // `DrawArgument(position, facing_right)` (Char.cpp:657) sets BOTH `pos` and
      // `center` to the decal's `rel_pos`, so `rlt = rel_pos - origin` and `cx`/
      // `cy` are the shifted anchor:
      //
      //   facing RIGHT (xscale +1): left = charX + dx - originX, right = left + w
      //   facing LEFT  (xscale -1): left = charX + dx + originX, right = left - w
      //
      // which is the `drawMobHitEffectPass` form with `mob.x/y` replaced by the
      // character's and `fx.shiftX/Y` read off the decal. One anchor expression,
      // used by both halves, so the two cannot disagree about where a box is.
      //
      // The shift travels RELATIVE because the reference's `DrawArgument` takes
      // it as a child of the character's own `absp` and adds it there
      // (`Sprite.cpp:27-31`, `absargs = stateargs + parentpos`); a world position
      // would freeze the decal on the map while the character walked away.
      const org = fx.origins?.[found] ?? { x: 0, y: 0 };
      // W-093. `mirrored` means xscale = -1, and for the CASTER's own cast
      // sprite that is TRUE when the caster faces RIGHT.
      //
      //   DrawArgument.h:34-40   xscale = flip ? -1 : +1
      //   Char.cpp:645            DrawArgument(facing_right)  -> flip = facing_right
      //   avatar.js               flip: dir > 0              -> the same
      //
      // The fallback used to be `(cur.dir ?? 1) < 0`, i.e. mirrored when facing
      // LEFT, which is the INVERSE of the reference -- so the character's own
      // pose pointed one way and the skill art pointed the other.
      // MEASURED 2026-10-18: facing right, avatar flipped = true and the cast
      // effect mirrored = false.
      const mirrored = fx.flipped ?? ((cur.dir ?? 1) > 0);
      const iw = img.naturalWidth;
      const ih = img.naturalHeight;
      const anchorX = cur.x + (fx.shiftX ?? 0);
      const anchorY = cur.y + (fx.shiftY ?? 0);
      // MapleNecrocer Client/Skill.cs:397-406, verbatim:
      //
      //     switch (FlipX)
      //     {
      //         case true:  Offset.X = origin.X - ImageWidth; break;
      //         case false: Offset.X = -origin.X;            break;
      //     }
      //     Offset.Y = -origin.Y;
      //
      // and the same rule for the body, Client/MapleCharacter.cs:1340-1355:
      //
      //     case true:   this.Flip = -1;  origin.X = src.X - this.ImageWidth; break;
      //     case false:  this.Flip =  1;  origin.X = -src.X;               break;
      //
      // The `- ImageWidth` in the mirrored branch is the whole point. Mirroring a
      // sprite whose anchor sits inside it puts the FAR edge at `origin.x` and the
      // near edge one image-width back. W-093 read the two branches as the same
      // expression with a sign change and wrote `anchorX + org.x`, so facing right
      // the effect drew a full image-width too far right.
      //
      // That is the shape of the complaint: correct one way, a whole image off the
      // other, so it reads as "the direction is wrong" rather than "the offset is
      // wrong".
      const left = mirrored ? anchorX + org.x - iw : anchorX - org.x;
      const right = mirrored ? anchorX + org.x : anchorX - org.x + iw;
      const top = anchorY - org.y;
      const bottom = top + ih;

      // The rectangle this frame is being drawn into, in WORLD coordinates and
      // with the frame it came from, so a measurement can check it without
      // reverse-engineering the two blit branches.
      //
      // The branches do not report alike: unmirrored it is
      // `drawImage(img, px(left), py(top), ...)`, mirrored it is
      // `translate(px(right),0); scale(-1,1); drawImage(img, 0, py(top), ...)`,
      // whose recorded x is literally 0 and whose real position lives only in
      // the transform. So a judge reading drawImage arguments can measure only
      // ONE of the two, and picking a facing that makes it measurable moves the
      // body around the very walk it is trying to observe. Recording the
      // rectangle closes that gap.
      //
      // W-089. `pass`, `order` and `node` are the PAINT ORDER, and they are here
      // because a rectangle cannot say when it was drawn. A skill with five
      // effect nodes draws five of these per frame, and a measurement that kept
      // only the last one — which is all this field ever held — could not answer
      // "did all five reach the canvas, and in which order", only "did the last
      // one". `pass` is the half (`behind` runs before the avatar, `front`
      // after it, draw() above), `order` is the entry's own index in the
      // server's list, and `node` is the archive node it came from. Together they
      // are enough to re-derive the draw order a measurement saw.
      this.lastEffectRect = {
        imagePath: fx.imagePath, frame: found, origin: org, mirrored,
        left, top, right, bottom, iw, ih,
        anchorX, anchorY, charX: cur.x, charY: cur.y,
        pass: behind ? 'behind' : 'front',
        order, node: fx.node ?? null, z: fx.z ?? 0,
      };

      ctx.save();
      ctx.imageSmoothingEnabled = false;
      if (mirrored) {
        // The source is flipped, so the blit starts at the rectangle's RIGHT edge
        // and runs left, which is exactly the rectangle above.
        ctx.translate(px(right), 0);
        ctx.scale(-1, 1);
        ctx.drawImage(img, 0, py(top), iw * s, ih * s);
      } else {
        ctx.drawImage(img, px(left), py(top), iw * s, ih * s);
      }
      ctx.restore();
    }
  }

  /**
   * The damage numbers currently rising off whatever was just hit.
   *
   * The glyphs are the archive's own -- Effect/BasicEff.img/NoRed1 for the
   * leading digit and NoRed0 for the rest, drawn as sprites with each glyph's
   * `origin` subtracted, staggered 5px on alternating digits. That is
   * DamageNumber.Create/DoDraw in Elem8100/MapleNecrocer Client/DamageNumber.cs
   * (MIT), and it is why this is not ctx.fillText: the archive carries the art
   * and it is what the client shows.
   *
   * The motion is DoMove from the same file: 0.5px up per tick, full opacity
   * for 50 ticks, then -6/255 per tick until it is gone. Those counters are the
   //  client's own, on a 17ms tick (SpriteEngine's `FTime += 17`); this project
   //  steps at 1/60s, so an age in ms is converted to that tick axis once and
   //  every counter is read off it -- see DMG_MS_PER_TICK.
   *
   * Positions arrive in world coordinates from the server's `damage` array,
   * already raised to the victim's head, so nothing here guesses where the
   * number belongs.
   *
   * @param {(x:number)=>number} px world x -> screen x
   * @param {(y:number)=>number} py world y -> screen y
   * @param {number} s world -> screen scale
   */
  drawDamageNumbers(ctx, px, py, s) {
    const list = this.damageNumbers ?? [];
    if (!list.length) return;

    const now = performance.now();
    const lifeMs = DMG_LIFE_TICKS * DMG_MS_PER_TICK;   // ~1544ms
    // Cull what has finished so a long fight does not grow the array without
    // bound. The client's own life: 50 + (255-10)/6 ticks.
    this.damageNumbers = list.filter((d) => now - d.born < lifeMs);

    // The glyphs are the archive's own. The table resolves Effect's file id
    // once (this server keys canvas paths on an open-file id, not an archive
    // name) and then answers `large(d)` for the leading digit, `small(d)` for
    // the rest; a digit still in flight reads as null and is skipped rather
    // than drawn wrong. Null table means the id has not landed yet -- nothing
    // is drawn that frame and the next one tries again.
    const glyphs = damageGlyphs(api, list.map((d) => d.value));
    if (!glyphs) return;

    for (const d of this.damageNumbers) {
      // The client's tick count for this number's age. Uncapped, so it runs
      // slightly past DMG_LIFE_TICKS for anything culled on the same frame it
      // should have died -- which is what pushes alpha under DMG_DIE_BELOW.
      const tick = (now - d.born) / DMG_MS_PER_TICK;

      // DoMove: Y -= 0.5f every tick, for the whole life. The client does not
      // stop the climb at full-opacity time, so neither does this: over
      // DMG_LIFE_TICKS the number travels 0.5 * 90.83 = ~45px.
      const rise = tick * DMG_RISE_PER_TICK;

      // DoMove: Counter > 50 -> Alpha -= 6 per tick; Alpha < 10 -> Dead()
      const alpha = tick > DMG_FADE_START
        ? 255 - (tick - DMG_FADE_START) * DMG_FADE_STEP
        : 255;
      if (alpha < DMG_DIE_BELOW) continue;

      // DamageNumber.cs draws only digits -- it has no MISS branch, so a miss
      // would vanish and read as "the hit did not register". The server does
      // report misses (MapSimDamageNumber.Miss), so they are drawn: "Miss" as
      // text in the same place and on the same timeline, since the archive's
      // own Miss glyph sits under a different effect node that this port does
      // not claim to reproduce.
      const text = d.miss ? 'Miss' : String(Math.max(0, Math.trunc(d.value)));

      // Create(): X = AX - Mid / 2, Mid = (Len * 28) / 2
      const len = text.length;
      const x = px(d.x) - ((len * DMG_CENTER_UNIT) / 2) / 2 * s;
      const y = py(d.y - DMG_HEAD_LIFT) - rise * s;

      const a = Math.max(0, Math.min(255, alpha)) / 255;

      if (d.miss) {
        // Not a glyph: 'Miss' has no digit sprite. Same anchor, same timeline.
        ctx.save();
        ctx.globalAlpha = a;
        ctx.font = `${Math.round(13 * s)}px var(--font-ui, sans-serif)`;
        ctx.textAlign = 'center';
        ctx.textBaseline = 'middle';
        ctx.lineWidth = Math.max(1, 3 * s);
        ctx.strokeStyle = 'rgba(10, 12, 18, 0.85)';
        ctx.strokeText(text, px(d.x), py(d.y) - rise * s);
        ctx.fillStyle = '#9aa6bd';
        ctx.fillText(text, px(d.x), py(d.y) - rise * s);
        ctx.restore();
        continue;
      }

      ctx.save();
      ctx.globalAlpha = a;
      for (let i = 0; i < len; i++) {
        // I == 0 ? LargeNumber : SmallNumber  --  NoRed1 then NoRed0
        const img = i === 0 ? glyphs.large(text.charAt(i))
                            : glyphs.small(text.charAt(i));
        if (!img) continue;                    // still in flight, or no such glyph
        // DoDraw: OffY = (I % 2) == 0 ? 0 : -5
        const offY = (i % 2) === 0 ? 0 : -DMG_STAGGER;
        const gx = x + i * DMG_GLYPH_STEP * s;
        const gy = y + offY * s;
        // DrawColor(..., 255, 255, 255, Alpha): the archive's own colours, and
        // origin comes out of the PNG itself, so the number sits on the mob.
        ctx.drawImage(img, Math.round(gx), Math.round(gy),
                      Math.round(img.width * s), Math.round(img.height * s));
      }
      ctx.restore();
    }
    ctx.globalAlpha = 1;
  }

  /**
   * The figure's depth key for the tick it is drawing.
   *
   * @returns {number|null} null when there is no figure, which sorts nowhere
   */
  characterZ(cur) {
    if (!cur) return null;
    const g = this.geometry;
    if (!g) return null;

    // On a ladder the client uses the ladder's page (MapleCharacter.cs:587) and
    // not the foothold's layer. The trace says whether this tick is on one
    // (`onLadder`) and which ladder (`ladderX`), so the ladder's own page is
    // used when it is reachable and 0 otherwise -- see the note in
    // buildDrawList() on why page is not in /simulate/geometry.
    if (cur.onLadder) {
      const l = g.Ladders.find((q) => q.x === cur.ladderX);
      return Z_LADDER(l?.page ?? 0);
    }

    // The foothold under the feet decides everything else. The tick names it,
    // so this is a map read rather than a scan.
    const byId = this.footholdsById;
    const fh = cur.foothold ? byId?.get(cur.foothold) : null;
    if (fh) return Z_CHAR(footholdLayerZ(fh));

    // No foothold named on this tick: either it is airborne, or the walk never
    // reached one. An index into the foothold list is NOT used as a fallback --
    // the client's Z is a depth and an index is not one, and a wrong band is a
    // wrong picture.
    //
    // Airborne FIRST carries the previous foothold forward, and that is the
    // client's own behaviour: a jump leaves Z alone and only a handover changes
    // it, so a character stays under the scenery it launched from for the whole
    // arc instead of changing band on the frame it leaves the ground.
    for (let i = Math.min(this.frame, this.ticks.length - 1); i >= 0; i--) {
      const id = this.ticks[i]?.foothold;
      if (id && byId?.get(id)) return Z_CHAR(footholdLayerZ(byId.get(id)));
    }

    // Airborne from the very first frame -- no foothold has ever been named, so
    // there is nothing to carry. Find the one beneath the feet the way the
    // client finds the one it will land on (the FindBelow calls in
    // MapleCharacter.cs). This is a linear scan, which is the price of having
    // no spatial index in the payload; it runs only while no foothold is named,
    // and a walk that reaches one never pays it again.
    let below = null;
    let bestY = Infinity;
    for (const f of g.Footholds) {
      const lo = Math.min(f.x1, f.x2);
      const hi = Math.max(f.x1, f.x2);
      if (cur.x < lo || cur.x > hi) continue;
      const span = f.x2 - f.x1;
      const y = span ? f.y1 + ((cur.x - f.x1) * (f.y2 - f.y1)) / span : Math.min(f.y1, f.y2);
      if (y >= cur.y && y < bestY) { bestY = y; below = f; }
    }
    return Z_CHAR(below ? footholdLayerZ(below) : 0);
  }

  /**
   * A mob's depth key, from the foothold it stands on.
   *
   * `Z_MOB`, not `Z_CHAR`: the reference reuses the character's formula but
   * with its own offset. `Mob.cs:191` is `Z = FH.Z * 100000 + 6000` where
   * `MapleCharacter.cs:561` is `Z = FH.Z * 100000 + 60000`, and both recompute
   * on every foothold change (Mob.cs:548, :628, :705). So this is the same
   * scheme and the same layer stride — not a parallel one — and it produces the
   * one relationship that matters on screen: on the same layer the mob's 6000
   * puts it behind the character's 60000, so the character walks in front of a
   * mob standing on the same foothold.
   *
   * @returns {number|null} null when the foothold is not on this map
   */
  mobZ(mob) {
    const byId = this.footholdsById;
    const fh = mob?.foothold ? byId?.get(mob.foothold) : null;
    return fh ? Z_MOB(footholdLayerZ(fh)) : null;
  }

  /** Paint one entry of the precomputed Z-ordered scene. */
  drawSceneItem(item, ctx, px, py, s) {
    if (item.tile) { this.drawArt(item.tile, px, py, s); return; }
    if (item.obj) { this.drawArt(item.obj, px, py, s); return; }

    if (item.f) {
      // Ground. Walls are drawn apart because they are what the character can
      // be stopped by, and a map that looks uniformly walkable is exactly the
      // confusion this view exists to remove.
      ctx.lineWidth = Math.max(1, 2 * s);
      ctx.strokeStyle = item.f.isWall ? INK.wall : INK.ground;
      ctx.beginPath();
      ctx.moveTo(px(item.f.x1), py(item.f.y1));
      ctx.lineTo(px(item.f.x2), py(item.f.y2));
      ctx.stroke();
      return;
    }

    if (item.ladder) {
      ctx.lineWidth = Math.max(1, 3 * s);
      ctx.strokeStyle = INK.ladder;
      ctx.beginPath();
      ctx.moveTo(px(item.ladder.x), py(item.ladder.top));
      ctx.lineTo(px(item.ladder.x), py(item.ladder.bottom));
      ctx.stroke();
      return;
    }

    if (item.portal) {
      ctx.fillStyle = item.portal.isExit ? INK.portal : '#718096';
      const r = Math.max(3, 5 * s);
      ctx.beginPath();
      ctx.arc(px(item.portal.x), py(item.portal.y), r, 0, Math.PI * 2);
      ctx.fill();
    }
  }

  /**
   * The trail, then the character.
   *
   * The character is the archive's own sprite when the archive has one, and the
   * stroke figure only when it does not. The two are not alternatives between
   * which this file chooses on looks: `avatarFrame` is null exactly when no
   * character art composed, and the HUD says which of the two is on screen by
   * name, so "it rendered" and "it rendered the real thing" stay different
   * claims.
   *
   * @param {object|null} avatarFrame the composed frame index, or null
   */
  drawWalk(ctx, px, py, s, upto, cur, uptoLen, avatarFrame) {
    if (uptoLen > 1) {
      ctx.strokeStyle = INK.trail;
      ctx.lineWidth = Math.max(1, 1.5 * s);
      ctx.beginPath();
      ctx.moveTo(px(upto[0].x), py(upto[0].y));
      for (const k of upto) ctx.lineTo(px(k.x), py(k.y));
      ctx.stroke();
    }

    if (cur) {
      // (x, y) is where the character's FEET are: it stands on the foothold.
      // Everything above is drawn upward from there, which is also why layout()
      // reserves FIGURE.height of headroom -- without it the figure is clipped.
      const fx = px(cur.x);
      const fy = py(cur.y);

      if (avatarFrame !== null && avatarFrame !== undefined) {
        this.drawAvatar(ctx, px, py, s, cur, avatarFrame);
        // The ring marks where the character is standing so a viewer can tell
        // "moving" from "frozen in place"; the real sprite shows the pose, not
        // the position, so the mark still has a job.
        if (cur.onGround) {
          ctx.strokeStyle = INK.head;
          ctx.lineWidth = 1.5;
          ctx.beginPath();
          ctx.arc(fx, fy, Math.max(3, 5 * s) + 3, 0, Math.PI * 2);
          ctx.stroke();
        }
        return;
      }

      const w = FIGURE.width * s;
      const h = FIGURE.height * s;
      const headR = FIGURE.head * s;
      const colour = ACTIVE[cur.action] ?? '#e53e3e';

      ctx.save();
      ctx.lineJoin = 'round';
      // Outline first, so the figure reads against any map art underneath.
      ctx.strokeStyle = '#141a24';
      ctx.lineWidth = Math.max(2, 3 * s);

      // Legs and torso: a simple standing pose, leaning the way it travels.
      const lean = cur.action === 'climb' ? 0 : (cur.dir ?? 1) * 0.06 * h;
      ctx.beginPath();
      ctx.moveTo(fx - w * 0.18 + lean, fy);
      ctx.lineTo(fx - w * 0.10 + lean, fy - h * 0.42);
      ctx.moveTo(fx + w * 0.18 + lean, fy);
      ctx.lineTo(fx + w * 0.10 + lean, fy - h * 0.42);
      ctx.moveTo(fx - w * 0.10 + lean, fy - h * 0.42);
      ctx.lineTo(fx + w * 0.10 + lean, fy - h * 0.42);
      ctx.lineTo(fx + w * 0.13 + lean, fy - h * 0.74);
      ctx.stroke();

      // Head, filled, so it is unmistakably a person and not a ring.
      ctx.beginPath();
      ctx.arc(fx + lean, fy - h * 0.74 - headR * 0.75, headR, 0, Math.PI * 2);
      ctx.fillStyle = colour;
      ctx.fill();
      ctx.stroke();
      ctx.restore();

      // The action colour, kept as a mark at the feet: the pose above is
      // deliberately simple, and this is what says walk vs rise vs fall.
      const r = Math.max(3, 5 * s);
      ctx.fillStyle = colour;
      ctx.strokeStyle = '#141a24';
      ctx.lineWidth = 2;
      ctx.beginPath();
      ctx.arc(fx, fy, r, 0, Math.PI * 2);
      ctx.fill();
      ctx.stroke();

      // A ring where the character is standing right now, so a viewer can tell
      // "moving" from "frozen in place" at a glance.
      if (cur.onGround) {
        ctx.strokeStyle = INK.head;
        ctx.lineWidth = 1.5;
        ctx.beginPath();
        ctx.arc(fx, fy, r + 3, 0, Math.PI * 2);
        ctx.stroke();
      }
    }
  }

  /**
   * One mob or NPC, drawn at its own foothold's depth, from the Mob/Npc
   * archive's own pixels.
   *
   * This used to draw a pink slab with a circle on it, on the reasoning that
   * "a mob's animation set is in the Mob archive, not the map file, and this
   * surface has to work on an archive with no mob art at all". The first half of
   * that was true and the conclusion was wrong: /simulate/mob-sprite resolves
   * the id to its actions and their frames, so the art was reachable all along
   * and 2,054 open mob images produced no pixels at all. The second half is
   * still true -- so the slab REMAINS, as the fallback for an id nothing holds,
   * and the HUD says which of the two is on screen.
   *
   * (x, y) is where the feet are, same as the character, and the frame's own
   * `origin` is what puts those feet there: Mob.cpp draws at
   * `origin.X = -wzOrigin.X` exactly as MapleCharacter.cs:1352 does, so the
   * blit is top-left at `(x - originX, y - originY)`. Facing is a mirror, not a
   * second set of art (MapleCharacterEx.cs:130/134).
   */
  drawMob(ctx, px, py, s, mob) {
    const sprite = this.mobSprites.get(mob);
    const drawn = sprite ? sprite.step(mob) : null;

    // W-087. The effects a cast put ON this mob, split by z around the body.
    //
    //   Mob.cpp:972   effects.drawbelow(absp)   BEFORE the mob
    //   Mob.cpp:1127  effects.drawabove(absp)   AFTER it
    //   EffectLayer.cpp:22-42  drawbelow walks up to and including index -1;
    //                            drawabove starts there
    //
    // so z <= -1 is behind the mob and z >= 0 is in front -- the same split
    // drawSkillEffectPass makes for the character, on the same two halves, for
    // the same reason. Drawn HERE and not in a pass of its own because the
    // reference's layer belongs to the MOB: `show_effect` adds to `Mob::effects`
    // and `Mob::draw` draws it around the mob's own body, so the effect is
    // anchored at the mob and z-sorted with it.
    this.drawMobHitEffectPass(ctx, px, py, s, mob, true);

    if (drawn) {
      const { frame, img } = drawn;
      const w = frame.width * s;
      const h = frame.height * s;
      // Feet on the foothold's surface: the frame's own origin, negated the
      // same way the character places its parts.
      const ox = (frame.originX ?? 0) * s;
      const oy = (frame.originY ?? 0) * s;
      const x = px(mob.x) - ox;
      const y = py(mob.y) - oy;

      ctx.save();
      // Facing RIGHT is the mirrored case, not the left one. The art faces left by
      // default, which the reference states three separate times:
      //   Mob.cs:356-362      case 50 -> FlipX=false, Left  /  case 100 -> FlipX=true, Right
      //   Morph.cs:106-109    KeyDown(Left) -> FlipX=false  /  KeyDown(Right) -> FlipX=true
      //   Familiar.cs:124-131 same
      // and `GameCanvas.Draw(texture, x, y, flipX, ...)` mirrors when FlipX is set.
      // So facing > 0 means mirror. This tested `< 0`, which drew every mob walking
      // backwards -- and disagreed with the character, whose avatar.js uses
      // `flip: dir > 0` for exactly this reason.
      if ((mob.facing ?? -1) > 0) {
        // Mirrored about px(mob.x) -- the point the sprite's originNode sits on
        // -- NOT about the image's top-left.
        //
        // drawImage(img, x, ...) puts the image's top-left at x, and the sprite's
        // own origin is at x + ox inside it, so x + ox == px(mob.x). The three
        // calls below compose to a reflection about one vertical line; that line
        // has to pass through the ORIGIN, not the corner, or the origin moves.
        //
        // Two wrong pivots, both measured on this client's own frames:
        //   top-left (x)      -> origin lands at x - ox  = px(mob.x) - 2*ox
        //   image right edge  -> origin lands at x + w - 2*ox = px(mob.x) + w - 2*ox
        // For 2220000/move (originX 51, width 60) that is 102px off, 1.7x the
        // body's own width -- and die1 uses a different originX than move, so
        // the error changes as the creature changes stance and it jitters while
        // standing still. Mirroring about the feet is the third wrong answer
        // (off by w - 2*ox) and reads as "left and right are swapped".
        ctx.translate(x + ox, 0);
        ctx.scale(-1, 1);
        ctx.translate(-(x + ox), 0);
      }
      ctx.imageSmoothingEnabled = false;
      ctx.drawImage(img, x, y, w, h);
      ctx.restore();
      this.drawMobAttackBall(ctx, px, py, s, mob, sprite);
      this.drawMobHp(ctx, px, py, s, mob, drawn.frame);
      // The other half of the same split: z >= 0 draws over the mob.
      this.drawMobHitEffectPass(ctx, px, py, s, mob, false);
      return;
    }

    this.drawMobSlab(ctx, px, py, s, mob);
    this.drawMobAttackBall(ctx, px, py, s, mob, this.mobSprites.get(mob));
    this.drawMobHp(ctx, px, py, s, mob, null);
    this.drawMobHitEffectPass(ctx, px, py, s, mob, false);
  }

  /**
   * The hit effects one mob is showing, on one side of its body.
   *
   * @param {object|null} mob the mob's own frame entry
   * @param {boolean} behind true for z <= -1, false for z >= 0
   *
   * @description
   * The counterpart of `drawSkillEffectPass`, and the same code with two things
   * changed, because those two are the whole difference between the caster's
   * effect and the struck mob's:
   *
   * <p><b>The anchor is the MOB, not the caster.</b> `Mob::show_effect`
   * (`Mob.cpp:1536-1559`) computes `shift = get_head_position(...)` when
   * `pos == 0` and adds it to the mob's own position — `Sprite.cpp:27-31`,
   * `absargs = stateargs + parentpos`, where `parentpos` is the mob's `absp`.
   * The server sends that shift already resolved (it computed the same head for
   * the damage number), so this only adds `mob.x + shiftX`.
   *
   * <p><b>`xscale` is the ATTACKER's facing.</b> `DrawArgument(shift, f)` builds
   * `xscale = f ? -1 : +1` from the flip `SkillHitEffect.h:47` receives, and every
   * caller passes `user.flip` (`SkillHitEffect.cpp:28`) — the caster's own
   * facing. So the flash mirrors with the character that threw it, not with the
   * mob, which is why `flipped` arrives on the wire instead of being read off
   * `mob.facing`.
   */
  drawMobHitEffectPass(ctx, px, py, s, mob, behind) {
    const list = mob?.hitEffects;
    if (!Array.isArray(list) || !list.length) return;

    const cache = this.mobHitFx ??= new Map();
    const now = performance.now();

    for (let order = 0; order < list.length; order++) {
      const fx = list[order];
      const sprite = fx?.sprite;
      if (!sprite?.imagePath) continue;
      // z < 0 is behind the mob (EffectLayer.cpp:22-42); 0 and up is in front.
      if (((fx.z ?? 0) < 0) !== behind) continue;

      const container = sprite.imagePath.replace(/\/[^/]+$/, '');
      const n = Math.max(1, sprite.frames | 0);
      const delays = sprite.delays ?? [];

      let imgs = cache.get(container);
      if (!imgs) {
        imgs = [];
        for (let i = 0; i < n; i++) {
          const img = new Image();
          img.src = `/api/canvas?path=${encodeURIComponent(`${container}/${i}`)}`;
          imgs.push(img);
        }
        cache.set(container, imgs);
      }

      // The animation starts when the HIT is published, keyed on the cast's own
      // sequence for the reason `skillFxCastSeq` is: keying on the skill id
      // leaves the second cast of the same skill animating from the first
      // cast's stale clock (W-075/W-083). The server publishes the effect for
      // the frame the hit lands on, so "first frame seen" IS "the hit landed".
      const born = this.mobHitFxBorn ??= new Map();
      const seq = fx.castSeq ?? 0;
      if (!born.has(seq)) {
        // Keep the map to the casts still on screen: an old cast's entry is dead
        // the moment its animation ends, and a session is not endless.
        if (born.size >= 16) born.delete(born.keys().next().value);
        born.set(seq, now);
      }
      const age = now - born.get(seq);

      // Walk the frames by their own delays, the way Frame::update does, and
      // stop on the last frame once the animation is out rather than wrapping.
      //
      //   Animation.cpp:64-67  delay = src["delay"]; if (delay == 0) delay = 100;
      //   EffectLayer.cpp:70   add(animation, args, z) -> speed 1.0f
      //
      // so a hit effect runs at real time: `Mob::show_effect` calls the
      // three-argument `add`, whose speed is 1.0 (EffectLayer.cpp:68-71), not
      // the caster's attack speed the cast sprite runs at.
      let acc = 0;
      let found = -1;
      for (let i = 0; i < n; i++) {
        const d = delays[i] || 100;
        if (age < acc + d) { found = i; break; }
        acc += d;
      }
      if (found < 0) found = n - 1;
      const img = imgs[found];
      if (!img?.naturalWidth) continue;

      // W-087. A LITERAL TRANSCRIPTION of the reference's rectangle, the same one
      // drawSkillEffectPass writes out and the same one it cites.
      //
      //   Sprite.cpp:27-31        absargs = stateargs + parentpos
      //   Mob.cpp:1558            effects.add(animation, DrawArgument(shift, f), z)
      //   DrawArgument.h:34       DrawArgument(position, flip)
      //                            -> pos = center = shift, xscale = flip ? -1 : +1
      //   DrawArgument.h:131-157  rlt  = pos - center - origin
      //                           left  = cx + xscale * rlt.x
      //                           right = cx + xscale * (rlt.x + w)
      //                           top   = cy + yscale * rlt.y
      //                           bot   = cy + yscale * (rlt.y + h)
      //
      // pos == center == the shift, so rlt = -origin and cx = mobX + shift.x,
      // cy = mobY + shift.y. Which is:
      //
      //   facing RIGHT (xscale +1): left = mobX + shift.x - origin.x
      //   facing LEFT  (xscale -1): left = mobX + shift.x + origin.x
      //
      // Both are written out rather than left as a `ctx.scale(-1,1)` sign, for
      // the reason drawSkillEffectPass gives: a rectangle that can be read
      // against DrawArgument.h line by line is one that can be checked.
      const org = sprite.origins?.[found] ?? { x: 0, y: 0 };
      const mirrored = fx.flipped === true;
      const iw = img.naturalWidth;
      const ih = img.naturalHeight;
      const anchorX = mob.x + (fx.shiftX ?? 0);
      const anchorY = mob.y + (fx.shiftY ?? 0);
      // MapleNecrocer Client/Skill.cs:397-406, verbatim:
      //
      //     switch (FlipX)
      //     {
      //         case true:  Offset.X = origin.X - ImageWidth; break;
      //         case false: Offset.X = -origin.X;            break;
      //     }
      //     Offset.Y = -origin.Y;
      //
      // and the same rule for the body, Client/MapleCharacter.cs:1340-1355:
      //
      //     case true:   this.Flip = -1;  origin.X = src.X - this.ImageWidth; break;
      //     case false:  this.Flip =  1;  origin.X = -src.X;               break;
      //
      // The `- ImageWidth` in the mirrored branch is the whole point. Mirroring a
      // sprite whose anchor sits inside it puts the FAR edge at `origin.x` and the
      // near edge one image-width back. W-093 read the two branches as the same
      // expression with a sign change and wrote `anchorX + org.x`, so facing right
      // the effect drew a full image-width too far right.
      //
      // That is the shape of the complaint: correct one way, a whole image off the
      // other, so it reads as "the direction is wrong" rather than "the offset is
      // wrong".
      const left = mirrored ? anchorX + org.x - iw : anchorX - org.x;
      const right = mirrored ? anchorX + org.x : anchorX - org.x + iw;
      const top = anchorY - org.y;
      const bottom = top + ih;

      // W-089. The SAME publication `drawSkillEffectPass` makes, for the same
      // reason and with the same shape, and it is published here rather than
      // there for one specific reason: nothing about this pass was observable
      // before. The caster's rectangles were readable, which is what the
      // per-frame offset judge was built on; a mob's were not, so a question
      // about a struck mob — "the hit effect draws at the mob's head", or the
      // one this closes, "do N hit effects on one mob land on N places or on
      // one" — had no instrument at all and was answered by reading the code.
      //
      // `mobId`, `order` and `pass` are carried for the same reason `pass` and
      // `order` are on the caster's: one field overwritten by the next effect
      // answers "where did the LAST one go" and not "how many were drawn, and
      // where each of them". A mob struck by a skill that publishes four hit
      // sprites (1311001) runs this loop four times per frame, and only the
      // fourth one's rectangle survives a single-valued field.
      this.lastMobHitRect = {
        mobId: mob.mobId ?? null, imagePath: sprite.imagePath, frame: found,
        origin: org, mirrored, left, top, right, bottom, iw, ih,
        anchorX, anchorY, mobX: mob.x, mobY: mob.y,
        castSeq: fx.castSeq ?? null, z: fx.z ?? 0,
        shiftX: fx.shiftX ?? 0, shiftY: fx.shiftY ?? 0,
        pass: behind ? 'behind' : 'front', order,
      };

      ctx.save();
      ctx.imageSmoothingEnabled = false;
      if (mirrored) {
        // The source is flipped, so the blit starts at the rectangle's RIGHT
        // edge and runs left, which is exactly the rectangle above.
        ctx.translate(px(right), 0);
        ctx.scale(-1, 1);
        ctx.drawImage(img, 0, py(top), iw * s, ih * s);
      } else {
        ctx.drawImage(img, px(left), py(top), iw * s, ih * s);
      }
      ctx.restore();
    }
  }

  /**
   * The projectile a mob has in the air, drawn where the entry is.
   *
   * `Mob.cpp:1118-1126`, verbatim in shape:
   *
   *   for (const auto& entry : attack_entries) {
   *     const auto& data = attacks.at(entry.index);
   *     if (data.geometry.type != 2 || !data.has_ball || entry.elapsed < entry.delay) continue;
   *     double t = std::min(1.0, double(entry.elapsed - entry.delay) / entry.lifetime);
   *     Point<int16_t> point{ viewx + entry.start.x() + (entry.finish.x() - entry.start.x()) * t,
   *                           viewy + entry.start.y() + (entry.finish.y() - entry.start.y()) * t };
   *     data.ball.draw(DrawArgument(point, entry.flipped), alpha);
   *   }
   *
   * The server sends the position (`attackEntryX/Y`), the art name
   * (`attackEntryBallAction`, empty until the entry is past its delay), the frame
   * index, and the facing — so every branch of the C++ `continue` is already
   * resolved and this is only the blit.
   *
   * The facing is the ATTACK's, not the mob's: a mob that turns mid-shot still
   * fires its ball the way it aimed it, which is what `entry.flipped` carries.
   */
  drawMobAttackBall(ctx, px, py, s, mob, sprite) {
    if (!sprite) return;
    // W-070. EVERY entry in flight, not just the first.
    //
    // The frame's scalar `attackEntry*` fields describe entry 0, and entry 0 is not
    // always the one carrying art. MEASURED on 4220000: it has a FLYING `attack1`
    // (ball, delay 1320 ms) and a STATIC `attack2` (no ball). While it used
    // attack2 the scalars named an entry with no ball and the art never appeared,
    // though it was in the archive the whole time. `Mob.cpp:1118-1126` draws one
    // ball per entry past its delay; the list is what that needs.
    //
    // The scalars are still read when the list is absent, so an older frame keeps
    // drawing rather than going blank.
    const list = mob?.attackEntries;
    if (Array.isArray(list) && list.length) {
      for (const e of list) this.#blitBall(ctx, px, py, s, sprite, e.x, e.y,
        e.ballAction, e.ballFrameIndex, e.flipped);
      return;
    }
    this.#blitBall(ctx, px, py, s, sprite, mob?.attackEntryX, mob?.attackEntryY,
      mob?.attackEntryBallAction, mob?.attackEntryBallFrameIndex, mob?.attackEntryFlipped);
  }

  /** One ball, at the point and frame the server resolved. */
  #blitBall(ctx, px, py, s, sprite, ex, ey, action, frameIndex, flipped) {
    if (!action || !sprite) return;
    const drawn = sprite.stepNamed({}, action, frameIndex);
    if (!drawn) return;

    const { frame, img } = drawn;
    const w = frame.width * s;
    const h = frame.height * s;
    const ox = (frame.originX ?? 0) * s;
    const oy = (frame.originY ?? 0) * s;
    const x = px(ex) - ox;
    const y = py(ey) - oy;

    ctx.save();
    if (flipped) {
      // Mirrored about the ball's own origin, for the same reason the body is:
      // the pivot has to pass through the point the art hangs from, or the
      // origin moves when it flips.
      ctx.translate(x + ox, 0);
      ctx.scale(-1, 1);
      ctx.translate(-(x + ox), 0);
    }
    ctx.imageSmoothingEnabled = false;
    ctx.drawImage(img, x, y, w, h);
    ctx.restore();
  }

  /**
   * A mob's health, as a bar over its head.
   *
   * @param {object|null} frame the frame actually drawn, for the mob's width
   *
   * @description
   * <b>Declared own-design.</b> The reference has no HP bar: its mobs carry `HP`
   * (Mob.cs:158) but nothing paints it, and the only place a number is shown is
   * a hover panel. This is here because it was asked for, not because anything
   * in the benchmark draws one, and it is the only drawing in this file with no
   * reference line behind it.
   *
   * <p>The numbers are not invented: `hp` and `maxHp` are already on every mob in
   * the frame the server sends (measured: `hp=350 maxHp=350` on a fresh mob), so
   * this reads state that already exists rather than adding a model.
   *
   * <p>Not drawn for a corpse (`dying`/`dead`) — a mob playing `die1` is past
   * caring, and the bar would sit on the animation's last frames.
   */
  drawMobHp(ctx, px, py, s, mob, frame) {
    const max = mob?.maxHp ?? 0;
    if (!(max > 0)) return;
    if (mob?.dying || mob?.dead) return;

    const hp = Math.max(0, mob.hp ?? 0);
    const frac = Math.max(0, Math.min(1, hp / max));

    // Width tracks the creature so the bar reads as belonging to it. The slab
    // fallback passes no frame, and gets the fallback body's own width.
    const w = Math.max(14, (frame?.width ?? MOB.width) * s * 0.7);
    const h = Math.max(2, 3 * s);
    const x = px(mob.x) - w / 2;
    const y = py(mob.y) - (frame ? (frame.height ?? 0) * s : MOB.height * s) - h * 2.2;

    ctx.save();
    ctx.fillStyle = 'rgba(10,14,20,0.75)';
    ctx.fillRect(x - 1, y - 1, w + 2, h + 2);
    // Green through amber to red, so the state reads without a number. The
    // client's own damage numbers are the archive's NoRed glyphs; a bar is not
    // the same thing and borrowing that palette would be a different claim.
    ctx.fillStyle = frac > 0.5 ? '#4ade80' : frac > 0.25 ? '#fbbf24' : '#ef4444';
    ctx.fillRect(x, y, w * frac, h);
    ctx.restore();
  }

  /**
   * The fallback creature, for an id no open archive draws.
   *
   * A slab and a head rather than a person. Kept from before the sprites were
   * real, and still the honest answer for a map opened without Mob.nx -- but now
   * it is a FALLBACK and not the mob. `walkReasons()` says so by name, the same
   * way the HUD says whether the character on screen is a composed avatar or the
   * drawn box.
   */
  drawMobSlab(ctx, px, py, s, mob) {
    // (x, y) is where the feet are, same as the character: MobPatrol starts at
    // the foothold's own endpoint and walks its surface.
    const mx = px(mob.x);
    const my = py(mob.y);
    const w = MOB.width * s;
    const h = MOB.height * s;

    ctx.save();
    ctx.lineJoin = 'round';
    ctx.strokeStyle = '#141a24';
    ctx.lineWidth = Math.max(2, 3 * s);

    // Body, leaning the way it travels. A mob standing still (Direction === 0)
    // is a real state the roll produces, so it stands square rather than
    // leaning at nothing.
    const lean = (mob.direction ?? 0) * 0.10 * h;
    ctx.fillStyle = INK_MOB;
    ctx.beginPath();
    ctx.moveTo(mx - w * 0.5 + lean, my);
    ctx.lineTo(mx - w * 0.35 + lean, my - h * 0.45);
    ctx.lineTo(mx + w * 0.35 + lean, my - h * 0.45);
    ctx.lineTo(mx + w * 0.5 + lean, my);
    ctx.closePath();
    ctx.fill();
    ctx.stroke();

    // Head, filled and outlined like the character's, so the two are visibly
    // the same species of drawn thing and clearly not the same size.
    const headR = h * 0.22;
    ctx.beginPath();
    ctx.arc(mx + lean, my - h * 0.62, headR, 0, Math.PI * 2);
    ctx.fill();
    ctx.stroke();
    ctx.restore();
  }

  /**
   * Advance the avatar's frame clock for the tick being drawn, composing one
   * first if the archive has not been asked yet.
   *
   * Composition is fired and not awaited: draw() is called from a
   * requestAnimationFrame callback and from the middle of loadWalk's await
   * chain, and neither can block on a network round trip without either
   * stalling the paint or discarding the frame it is drawing. Until the
   * promise settles, stepAvatar returns null and the fallback figure is drawn
   * with the HUD saying why -- so the first frame after a map loads is honest
   * rather than blank.
   *
   * @param {object} cur the tick being drawn
   * @returns {number|null} the frame index, or null when there is no avatar
   */
  stepAvatar(cur) {
    const avatar = this.avatar;
    if (!cur || !avatar) return null;

    // A cast's pose is the SKILL's own, named by the archive's `<id>/action`
    // string (MapleCharacter.cs:1591-1600; Skill.cs:46-47 substitutes "alert"
    // for a skill with no `action` node, so there is no unknown case to
    // invent). That name arrives on the frame as `skillStance`.
    //
    // It is applied by REWRITING THE TICK rather than by branching here, and
    // that is the only thing that works: `actionFor` is the single place that
    // decides which pose a tick is in, and `Avatar.step` calls it AGAIN to
    // return a frame (avatar.js:564-565). Loading the stance here while leaving
    // the tick's `action` alone would therefore compose one pose and then ask
    // for a different one, get `null` back from the `part.state` guard, and
    // draw nothing at all for the length of the cast.
    //
    // `actionFor` already resolves a server-named pose this way for the swing
    // (avatar.js:195-202, `action: 'attack'` + `attackStance`), so handing it
    // `action: 'attack'` with the cast's stance is the SAME contract, not a
    // new one -- and the animation clock is the swing's too, which is right:
    // both advance at each frame's own WZ delay. What is deliberately not
    // copied is the swing's semantics: `attack` here means "a server-named
    // pose is in effect", and both actions name mutually exclusive states.
    const pose = this.skillPoseTick(cur);
    if (pose !== cur) cur = pose;

    const { state } = actionFor(cur);
    if (state !== this.avatarState) {
      this.avatarState = state;
      this.avatarPending = state;
      avatar.load(state)
        .then((ok) => {
          // Only the newest request may publish. A map swap between stand and
          // walk can resolve out of order, and a slow stand1 answering after a
          // fast walk1 would leave the figure in the wrong action.
          if (this.avatarPending !== state) return;
          this.avatarPending = null;
          // The equipment bar needs `avatar.source` -- the session file id that
          // carries the art -- and the first compose is what produces it. The
          // bar was built from renderControls() before that, so it rebuilt
          // itself from an empty listing; this is where it gets a real one.
          if (!this.equipSlots) this.renderEquipBar().catch(() => {});
          if (ok) this.draw();
        })
        .catch(() => { this.avatarPending = null; });
    }

    // The LAST frame that was actually drawn, kept so a pose change does not blank
    // the character.
    //
    // `avatar.step` returns null in two very different situations and they were
    // being drawn the same way:
    //
    //   1. nothing has EVER composed -- no character archive, or the archive has
    //      no frames for the stance. The stick figure is the honest answer and the
    //      HUD names it (`placeholder figure`, walkReasons()).
    //   2. a new pose is on its way. `avatar.load(state)` is fired above and NOT
    //      awaited, so for the length of one network round trip `step()` has
    //      nothing for the new stance and returns null.
    //
    // Case 2 is what "玩家攻擊的時候底部會有一個骨架人跑出來" was: every swing and
    // every cast changes the stance, so every swing flashed a stick figure
    // standing where the character was. Holding the previous frame costs nothing
    // and is what a viewer expects -- the client does not drop the sprite while
    // the next one loads either, it keeps drawing what it has.
    const frame = avatar.step(cur);
    if (frame) this.avatarDrawn = frame;
    return frame ?? this.avatarDrawn ?? null;
  }

  /**
   * The tick to pose the avatar from when a cast is running, or `cur` itself.
   *
   * A copy rather than a mutation: `cur` is one entry of `this.ticks`, and
   * `action` is read by the trail colour and the HUD, which look it up in the
   * `ACTIVE` table (:2437, :2840). `skill` is not in that table — and neither is
   * `stand` or `attack`, so those already fall through to the same
   * "on ground / airborne" wording. Relabelling the entry would not improve the
   * HUD; it would just make a cast claim to be a swing.
   *
   * Returns `cur` unchanged unless the server named a stance. A cast with no
   * reported pose is not a cast this client can draw, and inventing one would be
   * the same guess `actionFor` refuses to make for the swing.
   *
   * @param {object} cur the tick being drawn
   * @returns {object} the tick to pose from
   */
  skillPoseTick(cur) {
    if (cur?.action !== 'skill' || !cur.skillStance) return cur;
    return { ...cur, action: 'attack', attackStance: cur.skillStance };
  }

  /**
   * Blit one composed avatar frame.
   *
   * The parts arrive already anchored and already in paint order. The server
   * read each part's `map` points out of the archive and applied the anchor its
   * OWNER uses -- the body off the navel, the head off get_head_position, the
   * hair off gethairpos minus its brow, the face off getfacepos (Body.cpp,
   * BodyDrawInfo.cpp, Hair.cpp, Face.cpp) -- and sorted them by zmap rank with
   * a stable tie-break (CharLook.cpp:175-179).
   *
   * That is deliberate: every one of those numbers is per-part and per-frame,
   * and re-deriving any of them here would be a second implementation that can
   * disagree with the first. What is left for this function is to blit
   * rectangles in the order they arrive.
   *
   * A part's `screen` is its own origin relative to the CHARACTER, so the
   * character's feet -- which is where a foothold's Y is -- are the common
   * point every part is measured from. Blitting at the centre of a box instead
   * is what put the head in the floor once already.
   */
  drawAvatar(ctx, px, py, s, cur, index) {
    const avatar = this.avatar;
    const parts = avatar.partsAt(index);
    if (!parts.length) return;

    const dir = cur.dir ?? (cur.facing >= 0 ? 1 : -1);
    const at = avatar.placement(cur.x, cur.y, dir, index);
    if (at.w * s < 0.5 || at.h * s < 0.5) return;   // sub-pixel at this zoom

    // W-089. Which POSE this is, which frame of it, and which way it faces,
    // published for the same reason `lastEffectRect` is published one screen up:
    // nothing else on the page says. `stepAvatar` composes the pose and
    // `actionFor` picks it, both of them internal to avatar.js, so a viewer
    // asking "which of this skill's two actions actually played, and how many of
    // its frames reached the canvas" had nothing to read and had to infer it
    // from pixels. `state` is the composed state's own name (`skillStance` for
    // a cast, `attackStance` for a swing, `stand1` and so on otherwise), which
    // is the answer to the first half of that question exactly.
    this.lastAvatarPose = {
      state: this.avatarState ?? null,
      // `skillStance` is what the SERVER named; `attackStance` is what it was
      // rewritten to by `skillPoseTick`, and it is the same word. Both are here
      // so a measurement can read the name off the frame it already has instead
      // of reaching back into the composed state.
      stance: cur.skillStance || cur.attackStance || null,
      frame: index, flipped: !!at.flip, dir,
      skillStance: cur.skillStance || null,
      parts: parts.length,
      x: cur.x, y: cur.y,
    };

    for (const part of parts) {
      const img = avatar.bitmaps.get(part.path);
      if (!img || part.width <= 0 || part.height <= 0) continue;

      const r = avatar.rectOf(at, part);
      const dw = part.width * s;
      const dh = part.height * s;
      if (!at.flip) {
        ctx.drawImage(img, px(r.x), py(r.y), dw, dh);
        continue;
      }
      // The image has to be mirrored too, not just its slot.
      //
      // `rectOf` already puts a flipped part in its mirrored slot (`at.cx - dx -
      // width`), and mobs have been mirroring with `ctx.scale(-1,1)` since the start,
      // but drawAvatar drew every part upright. So facing left moved the face, hair
      // and hands to the other side while the artwork still pointed right -- which
      // reads as "walking left but the swing goes right" and as the face and hands
      // being displaced. Position was mirrored; the pixels were not.
      //
      // MapleCharacter.cs:1337-1356 is the reference: it sets `this.Flip = -1` per
      // part on FlipX, so the benchmark mirrors each part's drawing as well as its
      // origin. Mirroring about the destination rect's right edge leaves that rect
      // where rectOf put it, so the two halves cannot disagree.
      ctx.save();
      ctx.translate(px(r.x) + dw, py(r.y));
      ctx.scale(-1, 1);
      ctx.drawImage(img, 0, 0, dw, dh);
      ctx.restore();
    }
  }

  renderHud(cur) {
    clear(this.hud);
    const g = this.geometry;
    const bits = [];

    if (!g) {
      bits.push(t('No map loaded'));
    } else {
      bits.push(`${g.Footholds.length} ${t('footholds')}`);
      bits.push(`${g.Ladders.length} ${t('ladders')}`);
      bits.push(`${g.Portals.length} ${t('portals')}`);
      // W-072. WHICH KEYS, ALWAYS ON SCREEN.
      //
      // A bound skill that is hard to reach is indistinguishable from one that
      // is not bound at all, and the only place the list existed was a modal
      // that had to be opened. Stating it on the HUD every frame means the user
      // can see the key they are supposed to press without opening anything --
      // and when nothing casts, the first question "which key?" is answered by
      // looking, not by reading source.
      {
        const keys = [...(this.live?.skillKeys ?? new Map()).entries()];
        if (keys.length) {
          bits.push(`${t('skills')}: `
            + keys.map(([k, id]) => `${k}=${id}`).join('  '));
        } else {
          bits.push(t('no skill is bound to a key'));
        }
      }
      // Only when there are any: a map with no spawns has nothing to say here,
      // and a persistent "0 mobs" on every map would be noise that trains the
      // reader to stop reading this line.
      if (this.mobs?.length) {
        const npcs = this.mobs.filter((m) => m.kind === 'n').length;
        const mobCount = this.mobs.length - npcs;
        // Both counts, because a map whose spawns are all NPCs reporting "6
        // mobs" is a different claim from the one the client makes. The kind
        // comes off each life entry, and it is the only thing that says which
        // archive the id resolves against.
        const parts = [];
        if (mobCount) parts.push(`${mobCount} ${mobCount === 1 ? 'mob' : 'mobs'}`);
        if (npcs) parts.push(`${npcs} ${npcs === 1 ? 'NPC' : 'NPCs'}`);
        bits.push(parts.join(' + '));
      }
      // The same rule as the avatar's, for the same reason: a real sprite and a
      // drawn slab look equally like "a mob" from across the room, and the
      // difference is the difference between this view working and this view
      // having quietly fallen back. Said every frame, in the same line.
      {
        const sprites = this.mobs
          .map((m) => this.mobSprites.get(m))
          .filter(Boolean);
        const real = sprites.filter((s) => s.ready).length;
        const missing = sprites.filter((s) => s.reason).map((s) => s.reason);
        if (real) bits.push(t('real mob art') + ` (${real})`);
        if (missing.length) {
          bits.push(`${t('placeholder mobs')} — ${[...new Set(missing)][0]}`);
        }
      }
      // Which figure is on screen, named. A real sprite and a drawn box look
      // equally like "a character" from across the room, and the difference is
      // the difference between this view working and this view having quietly
      // fallen back -- so it is said, every frame, in the same line as the rest.
      if (this.avatar?.ready) {
        bits.push(t('real avatar') + ` (${this.avatar.part.state}/${this.avatar.part.index})`);
      } else if (this.avatar?.reason) {
        bits.push(`${t('placeholder figure')} — ${this.avatar.reason}`);
      }
      // What is worn, named. The equipment bar shows what was CHOSEN; this
      // shows what the server RESOLVED, and the two differ whenever an id was
      // not in its slot's folder -- which is precisely the case where a dressed
      // picker and a bare character otherwise look identical.
      if (this.avatar?.equipped?.length) {
        bits.push(t('wearing') + ` ${this.avatar.equipped.join(', ')}`);
      }
      // And what could not be worn, by the server's own words. Silently
      // dropping it is the one outcome that would make equipment look broken
      // with no way to tell which slot to fix.
      if (this.avatar?.unresolved?.length) {
        bits.push(`${t('not equipped')}: ${this.avatar.unresolved.join('; ')}`);
      }
      // Partial art is reported by NAME. The scope rule for a stack that cannot
      // fully load is "draw the others and say which" -- a body with its arm
      // missing draws as a plausible figure, and nothing about the picture says
      // a layer failed, so the HUD is the only place that can.
      if (this.avatar?.missingLayers?.length) {
        bits.push(t('missing avatar layers') + `: ${this.avatar.missingLayers.length}`);
      }
      // Same rule for the ground. A tile whose art did not resolve leaves a gap
      // that reads as "that part of the map is empty" rather than "the editor
      // could not fetch it", which is the difference between a real finding and
      // an honest failure to show up in one.
      if (this.missingArt > 0) {
        bits.push(t('missing tile art') + `: ${this.missingArt}`);
      }
    }

    if (this.error) {
      bits.push(this.error);
    } else if (this.live?.state?.playerDead) {
      // THE DEAD BANNER, and the only thing on screen that explains it.
      //
      // A corpse that neither swings nor casts and will not walk reads as a broken
      // build, and that is exactly how it was reported. The reference answers it
      // with a revive prompt (UIStateGame.cpp:548-556 -> UIRevive); this port has
      // the server side (POST /session/{id}/revive, MapSimSessionStore.Revive,
      // after Stage::respawn / Player::respawn) and no prompt, so the prompt's one
      // useful button lives in the HUD.
      const dead = el('span', { class: 'walkview-dead' }, [
        el('span', { class: 'walkview-dead-text' },
          [t('the character is dead — attack and skills do nothing until you revive')]),
        el('button', {
          class: 'btn btn-primary walkview-revive',
          disabled: !!this.live.reviving,
          onclick: () => this.live.revive(),
        }, [this.live.reviving ? t('reviving') : t('revive at the portal')]),
      ]);
      this.hud.replaceChildren(dead);
      return;
    } else if (this.ticks.length) {
      bits.push(`${t('frame')} ${this.frame + 1} / ${this.ticks.length}`);
      if (cur) {
        bits.push(`x=${cur.x} y=${cur.y}`);
        bits.push(t('foothold') + ` ${cur.foothold || '—'}`);
        bits.push(ACTIVE[cur.action] ? cur.action : (cur.onGround ? t('on ground') : t('airborne')));
      }
      const segments = new Set(this.ticks.map((k) => k.foothold).filter(Boolean));
      bits.push(`${segments.size} ${t('footholds crossed')}`);
    } else {
      bits.push(t('Pick a map and run a walk'));
    }

    this.hud.textContent = bits.join('  ·  ');
  }
}
