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
 */

import { t } from './i18n.js';
import { el, clear } from './ui.js';

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

/**
 * One walk, drawn on a canvas.
 */
export class WalkViewer {
  constructor() {
    this.root = el('div', { class: 'walkview' });
    this.canvas = el('canvas', {
      class: 'walkview-canvas',
      style: 'width:100%;display:block;background:#1a202c;border-radius:4px;image-rendering:pixelated',
    });
    this.hud = el('div', { class: 'walkview-hud' });
    this.controls = el('div', { class: 'walkview-controls' });
    this.root.append(this.canvas, this.controls, this.hud);

    this.geometry = null;
    this.ticks = [];
    this.frame = 0;
    this.playing = false;
    this.speed = 4;
    this.timer = null;
    this.scale = 1;
    this.ox = 0;
    this.oy = 0;
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
  async load(path) {
    this.path = path;

    const [geoRes, docRes] = await Promise.all([
      fetch(`/api/simulate/geometry?path=${encodeURIComponent(path)}`),
      fetch(`/api/mapedit/doc?path=${encodeURIComponent(path)}`),
    ]);
    if (!geoRes.ok) throw new Error(`${geoRes.status} ${geoRes.statusText}`);

    this.geometry = await geoRes.json();

    this.doc = docRes.ok ? await docRes.json() : null;
    this.images = new Map();
    if (this.doc) this.loadArt(this.doc);

    this.ticks = [];
    this.frame = 0;
    this.stop();
    this.draw();
  }

  /** Fetch every distinct picture the map's tiles and objects refer to. */
  loadArt(doc) {
    const art = doc.art ?? {};
    const pending = [];

    const want = (key) => {
      if (!key || this.images.has(key)) return;
      const meta = art[key];
      const slot = { img: null, ok: false, missing: !meta || meta.missing || !meta.path, meta };
      this.images.set(key, slot);
      if (slot.missing) return;
      pending.push(new Promise((resolve) => {
        const img = new Image();
        img.onload = () => { slot.img = img; slot.ok = true; resolve(); };
        img.onerror = () => { slot.missing = true; resolve(); };
        img.src = `/api/canvas?path=${encodeURIComponent(meta.path)}&v=me`;
      }));
    };

    for (const layer of doc.layers ?? []) {
      for (const tile of layer.tiles ?? []) want(tile.art);
      for (const obj of layer.objs ?? []) want(obj.art);
    }

    return Promise.all(pending).then(() => { this.drawn = (this.drawn ?? 0) + 1; this.draw(); });
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

  renderControls() {
    clear(this.controls);
    if (!this.ticks.length) return;

    const button = (label, onClick, tip) =>
      el('button', { class: 'btn btn-sm', onclick: onClick, 'data-tip': tip }, label);

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

  /** Everything the figure needs to draw itself, computed once per geometry load. */
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
    const pad = 40;
    minX -= pad; minY -= pad; maxX += pad; maxY += pad;

    const cssWidth = this.canvas.clientWidth || 900;
    const scale = Math.max(0.05, cssWidth / Math.max(1, maxX - minX));
    return { minX, minY, scale };
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

  draw() {
    const g = this.geometry;
    const ctx = this.canvas.getContext('2d');
    if (!ctx) return;
    this.ctx = ctx;

    const view = this.layout();
    const dpr = window.devicePixelRatio || 1;
    const cssW = this.canvas.clientWidth || 900;

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
      return;
    }

    // Height follows the map's own aspect ratio so the ground is not squashed.
    const worldW = Math.max(1, (Math.max(...g.Footholds.map((f) => Math.max(f.x1, f.x2)),
                                    ...g.Footholds.map((f) => Math.min(f.x1, f.x2))) - view.minX)
                          / view.scale);
    const cssH = Math.max(220, Math.min(520, worldW * (window.innerHeight < 900 ? 0.34 : 0.42)));

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

    // The map itself, drawn first so the walk reads as being ON it. Tiles and
    // objects come from the same document the editor draws, so what is on screen
    // is the map rather than an interpretation of it.
    if (this.doc) {
      // Back layers first, then tiles in layer order, so z composes the same way
      // the game stacks them.
      for (const layer of this.doc.layers ?? []) {
        for (const tile of layer.tiles ?? []) this.drawArt(tile, px, py, s);
      }
      for (const layer of this.doc.layers ?? []) {
        for (const obj of layer.objs ?? []) this.drawArt(obj, px, py, s);
      }
    }

    // The map's own boundary, which is what stops a walk at the edge.
    if (g.bounds) {
      ctx.strokeStyle = INK.bounds;
      ctx.setLineDash([4, 4]);
      ctx.lineWidth = 1;
      ctx.strokeRect(px(g.bounds.left), py(g.bounds.top),
                     (g.bounds.right - g.bounds.left) * s,
                     (g.bounds.bottom - g.bounds.top) * s);
      ctx.setLineDash([]);
    }

    // Ground. Walls are drawn apart because they are what the character can be
    // stopped by, and a map that looks uniformly walkable is exactly the
    // confusion this view exists to remove.
    ctx.lineWidth = Math.max(1, 2 * s);
    for (const f of g.Footholds) {
      ctx.strokeStyle = f.isWall ? INK.wall : INK.ground;
      ctx.beginPath();
      ctx.moveTo(px(f.x1), py(f.y1));
      ctx.lineTo(px(f.x2), py(f.y2));
      ctx.stroke();
    }

    ctx.lineWidth = Math.max(1, 3 * s);
    ctx.strokeStyle = INK.ladder;
    for (const l of g.ladders) {
      ctx.beginPath();
      ctx.moveTo(px(l.x), py(l.top));
      ctx.lineTo(px(l.x), py(l.bottom));
      ctx.stroke();
    }

    for (const p of g.portals) {
      ctx.fillStyle = p.isExit ? INK.portal : '#718096';
      const r = Math.max(3, 5 * s);
      ctx.beginPath();
      ctx.arc(px(p.x), py(p.y), r, 0, Math.PI * 2);
      ctx.fill();
    }

    // The trail, then the character on top of it.
    const upto = this.ticks.slice(0, this.frame + 1);
    if (upto.length > 1) {
      ctx.strokeStyle = INK.trail;
      ctx.lineWidth = Math.max(1, 1.5 * s);
      ctx.beginPath();
      ctx.moveTo(px(upto[0].x), py(upto[0].y));
      for (const k of upto) ctx.lineTo(px(k.x), py(k.y));
      ctx.stroke();
    }

    const cur = this.ticks[this.frame];
    if (cur) {
      const cx = px(cur.x), cy = py(cur.y);
      const r = Math.max(4, 6 * s);
      ctx.fillStyle = ACTIVE[cur.action] ?? '#e53e3e';
      ctx.strokeStyle = '#1a202c';
      ctx.lineWidth = 2;
      ctx.beginPath();
      ctx.arc(cx, cy, r, 0, Math.PI * 2);
      ctx.fill();
      ctx.stroke();

      // A ring where the character is standing right now, so a viewer can tell
      // "moving" from "frozen in place" at a glance.
      if (cur.onGround) {
        ctx.strokeStyle = INK.head;
        ctx.lineWidth = 1.5;
        ctx.beginPath();
        ctx.arc(cx, cy, r + 3, 0, Math.PI * 2);
        ctx.stroke();
      }
    }

    this.renderHud(cur);
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
    }

    if (this.error) {
      bits.push(this.error);
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