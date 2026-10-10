/**
 * Map editor section: open a map, see it drawn correctly, edit it by direct
 * manipulation, save through the round-trip model.
 *
 * The rules the server enforces are surfaced, not hidden, because they are the
 * point of the tool:
 *  - a map the model refuses is a banner with the model's reason, never a
 *    partial open;
 *  - the layer list comes from the document (749080500 has a layer 8);
 *  - the inspector shows keys verbatim — a trailing space is a different key
 *    and is shown as one;
 *  - a save reports what it verified against the saved-and-reopened archive,
 *    and how many unmodelled nodes it carried through untouched.
 *
 * The interaction layer (mapedit-view.js) owns the canvas; this controller
 * owns the keyboard, the palette (grid-first: every asset shows its picture
 * at rest, detail on hover, a live ghost when armed), the floating property
 * panel, and every commit — all through the typed server ops, each gesture
 * one named undo entry.
 */

import { q } from './api.js';
import { t } from './i18n.js';
import {
  el, clear, toast, toastError, fmt, debounce, modal, busyButton,
  motionIn, motionOut,
} from './ui.js';
import { emptyState } from './inspector.js';
import { icon } from './icons.js';
import { MapView } from './mapedit-view.js';

async function call(method, url, body) {
  const init = { method, headers: {} };
  if (body !== undefined) {
    init.headers['Content-Type'] = 'application/json';
    init.body = JSON.stringify(body);
  }
  const response = await fetch(url, init);
  if (!response.ok) {
    let message = `${response.status} ${response.statusText}`;
    try {
      const data = await response.json();
      if (data?.message) message = data.message;
    } catch { /* keep the status line */ }
    throw new Error(message);
  }
  if (response.status === 204) return null;
  return response.json();
}
const get = (url) => call('GET', url);
const post = (url, body) => call('POST', url, body ?? {});

const sameAddr = (a, b) => Array.isArray(a) && Array.isArray(b) && a.join() === b.join();

const KIND_LABEL = {
  tile: 'Tile', obj: 'Object', foothold: 'Foothold', portal: 'Portal',
  life: 'Life spawn', reactor: 'Reactor', ladder: 'Ladder / rope',
  rect: 'Zone', back: 'Background',
};

const RECENTS_KEY = 'mb-me-recents';
const RECENTS_MAX = 12;

/** Panel layout memory: which panels are open, how wide the palette drawer
 *  is — the editor comes back the way it was left. */
const UI_KEY = 'mb-me-ui';
const UI_DEFAULTS = {
  layers: true,      // left dock: compact layer list
  pal: false,        // palette drawer (slides OVER the canvas)
  inspector: false,  // right raw-node panel
  minimap: true,     // on-canvas minimap
  drawerW: 430,
};

export class MapEditSection {
  constructor({ host, app }) {
    this.host = host;
    this.app = app;
    this.doc = null;
    this.view = null;
    this.selection = [];            // array of { kind, entry, item?, vertex? }
    this.caps = null;
    this.portalIconsLoaded = false;
    this.built = false;

    try {
      this.ui = { ...UI_DEFAULTS, ...JSON.parse(localStorage.getItem(UI_KEY) || '{}') };
    } catch { this.ui = { ...UI_DEFAULTS }; }
    this.foldSnapshot = null;        // Tab fold-all remembers what to restore
    this.lastSpaceDown = 0;          // double-space = fit map
    this.floatManual = null;         // property panel dragged somewhere by hand

    // Per-kind palette memory: last set, last query — coming back to Tiles
    // finds it where it was left.
    this.palette = { kind: 'Tile', mem: {}, global: '' };
    this.placing = null;
    this.portalIconList = null;
    this.allSets = null;            // { Tile, Obj, Back } cached set lists
    this.previewCache = new Map();  // leaf path -> art dto (hover flyout)
    this.tileVariantCache = new Map(); // "setPath" -> entries
    this.clipboard = null;          // { items: [{addr, kind}], cx, cy }
    this.pendingNudge = null;       // accumulated arrow-key delta
    this.snapTiles = false;
    this.flyoutOpenTimer = null;
    this.flyoutCloseTimer = null;
    this.flyoutLastClosedAt = 0;

    try {
      this.recents = JSON.parse(localStorage.getItem(RECENTS_KEY) || '{}');
    } catch { this.recents = {}; }

    this.unsavedEdits = 0;
    this.unsavedDocs = 0;

    this.commitNudgeDebounced = debounce(() => this.commitNudge(), 350);
  }

  async refreshUnsaved() {
    try {
      const changes = await get('/api/mapedit/changes');
      this.unsavedEdits = changes.editCount;
      this.unsavedDocs = changes.dirtyDocs;
    } catch {
      this.unsavedEdits = this.doc?.dirty ? 1 : 0;
      this.unsavedDocs = this.doc?.dirty ? 1 : 0;
    }
    this.app.updateDirtyUi?.();
  }

  async open() {
    try {
      this.caps = await get('/api/mapedit/capabilities');
    } catch (err) {
      this.caps = null;
      toastError(err, t('Map editor'));
    }

    if (!this.caps?.available) {
      clear(this.host);
      this.host.className = 'stage-body';
      this.built = false;
      // Name the archives that are actually open, not the .wz family. An NX
      // client has Map.nx and no Map002.wz at all, and the old text told a user
      // holding a perfectly good Map.nx to go and open files that do not exist
      // in their client -- which is the whole reason the editor read as
      // "unusable" to anyone on NX.
      const open = this.app?.files?.map((f) => f.name) ?? [];
      const what = open.length
        ? t('None of the archives you have open hold maps. Open the Map archive — {archives} — or the whole client folder.',
            { archives: open.join(', ') })
        : t('The map editor reads maps out of Map002.wz and Map.wz, or out of an NX Map.nx. Open the client’s Map family (copies, not the live client) and come back.');
      this.host.append(emptyState(
        'layers', t('Open the Map archives first'),
        what,
        el('button', { class: 'btn btn-primary', onclick: () => this.app.openFilePicker() },
          icon('folderOpen', { size: 15 }), t('Open files'))));
      return;
    }

    if (!this.built) this.buildLayout();
    if (!this.doc) this.showIdle();
    else await this.refetchDoc();
  }

  /* ============================================================ layout */

  buildLayout() {
    clear(this.host);
    this.host.className = 'stage-body me-root';
    this.host.__section = this;   // reachable from devtools for live debugging
    this.built = true;

    this.toolbar = el('div', { class: 'me-toolbar' });

    // The canvas is the app. Everything else is a rail icon until asked for:
    // a thin rail, a collapsible layer dock, a palette drawer that slides
    // OVER the canvas, an inspector that only exists for a selection.
    this.rail = el('div', { class: 'me-rail' });
    this.dock = el('div', { class: 'me-dock' });
    this.canvasWrap = el('div', {
      class: 'me-canvas-wrap', tabindex: '0', 'aria-label': t('Map canvas'),
    });
    this.canvas = el('canvas', { class: 'me-canvas' });
    this.banner = el('div', { class: 'me-banner', hidden: true });
    this.float = el('div', { class: 'me-float', hidden: true });
    this.picker = el('div', { class: 'me-picker-pop', hidden: true });
    this.inspector = el('div', { class: 'me-inspector', hidden: true });

    // Palette drawer: header + scrollable body + a resize handle on its right
    // edge. this.side stays the name of the container the palette renders
    // into — the drawer body IS the old side panel, grown up.
    this.side = el('div', { class: 'me-drawer-body' });
    this.drawerTitle = el('strong', { text: t('Palette') });
    this.drawer = el('div', { class: 'me-drawer', hidden: true },
      el('div', { class: 'me-drawer-head' },
        this.drawerTitle,
        el('span', { class: 'me-toolbar-spacer' }),
        el('button', {
          class: 'btn btn-ghost btn-icon', 'data-tip': t('Close (P or Esc)'),
          'aria-label': t('Close palette'), onclick: () => this.togglePanel('pal', false),
        }, icon('close', { size: 14 }))),
      this.side);
    this.drawerGrip = el('div', { class: 'me-drawer-grip',
      'data-tip': t('Drag to resize — double-click closes') });
    this.drawer.append(this.drawerGrip);
    this.installDrawerResize();

    // On-canvas furniture: overlay strip (top), zoom cluster (bottom right),
    // minimap (bottom left), one-line status bar (bottom).
    this.overlayStrip = el('div', { class: 'me-ovstrip' });
    this.zoomCtl = el('div', { class: 'me-zoomctl' });
    this.minimapBox = el('div', { class: 'me-mmbox', hidden: true });
    this.statusText = el('span', { class: 'me-statusbar-text', text: '' });
    this.keyChip = el('button', {
      class: 'me-keychip',
      'data-tip': t('While the map has focus, every shortcut goes to the map — not the app. Click for the list.'),
      onclick: () => this.showKeysHelp(),
    }, t('map keys · ?'));
    this.statusBar = el('div', { class: 'me-statusbar' }, this.statusText,
      el('span', { class: 'me-toolbar-spacer' }), this.keyChip);

    this.canvasWrap.append(
      this.canvas, this.banner, this.overlayStrip, this.zoomCtl, this.minimapBox,
      this.statusBar, this.drawer, this.float, this.picker);
    this.canvas.addEventListener('pointerdown', () => this.canvasWrap.focus());

    this.host.append(
      this.toolbar,
      el('div', { class: 'me-body' }, this.rail, this.dock, this.canvasWrap, this.inspector));

    // The keyboard is scoped at the document, capture phase: while this
    // section is on screen and the user is not typing in a field, EVERY key
    // belongs to the editor and never reaches the app's global handlers —
    // Ctrl+Z here undoes map edits, not WZ tree edits underneath.
    if (!this._keysBound) {
      this._keysBound = true;
      document.addEventListener('keydown', (e) => this.captureKey(e), true);
      document.addEventListener('keyup', (e) => {
        if (e.code === 'Space' && this.editorActive()) this.view?.setSpaceHeld(false);
      }, true);
      window.addEventListener('blur', () => this.view?.setSpaceHeld(false));
    }

    this.view = new MapView(this.canvas, {
      onSelect: (sel) => {
        this.selection = sel ?? [];
        this.floatManual = null;
        this.closePicker();
        this.renderFloat();
        this.renderInspector();
      },
      onStatus: (text) => this.setStatus(text),
      onHover: () => { /* the status readout carries the hover info */ },
      onViewChanged: () => {
        this.positionFloat();
        this.updateZoomLabel();
        this.updateMinimapView();
      },
      onModeChanged: () => this.renderFloat(),
      onMoveMany: (items, dx, dy) => this.commitMoveMany(items, dx, dy),
      onMoveFoothold: (entry, vertex) => this.commitFootholdMove(entry, vertex),
      onMoveLadder: (entry) => this.commitLadderMove(entry),
      onRectResize: (entry) => this.commitRectResize(entry),
      onInsertVertex: (entry, x, y) => this.commitInsertVertex(entry, x, y),
      onExtendFoothold: (entry, vertex, x, y) => this.commitExtendFoothold(entry, vertex, x, y),
      onPlace: (x, y) => this.commitPlacement(x, y),
      onPlaceLadder: (x, y1, y2) => this.commitLadder(x, y1, y2),
      onFootholdChain: (layer, points) => this.commitFootholdChain(layer, points),
      onTilePicker: (sel, sx, sy) => this.openTilePicker(sel, sx, sy),
    });

    this.renderToolbar();
    this.renderSide();
  }

  /* ============================================================ keyboard */

  /** Whether the editor section is what the user is looking at — the focus
   *  boundary of every editor shortcut. */
  editorActive() {
    if (!this.built || !this.host.isConnected) return false;
    if (this.host.offsetParent === null) return false;   // another section is up
    if (document.querySelector('dialog[open]')) return false;
    const palette = document.getElementById('palette');
    if (palette && palette.dataset.open === 'true') return false;
    return true;
  }

  /**
   * Document capture-phase router. While the editor is on screen:
   *  - typing in a field keeps its keys (Esc blurs back to the canvas), and
   *    nothing leaks past the field to app-level handlers;
   *  - everything else is offered to handleKey; a consumed key stops dead —
   *    the app's Ctrl+Z (WZ tree undo) never sees it;
   *  - unclaimed keys are ALSO fenced off from the app's single-key global
   *    shortcuts, except an explicit pass list (section switching, history).
   */
  captureKey(e) {
    if (!this.editorActive()) return;
    const target = e.target;
    const typing = target instanceof Element
      && (/^(INPUT|TEXTAREA|SELECT)$/.test(target.tagName) || target.isContentEditable);

    if (typing) {
      if (e.key === 'Escape') {
        e.preventDefault();
        e.stopImmediatePropagation();
        target.blur();
        this.canvasWrap?.focus();
      } else {
        e.stopPropagation();   // the field owns its keys; the app gets none
      }
      return;
    }

    // The app keeps window-level navigation — nothing the editor uses.
    const mod = e.ctrlKey || e.metaKey;
    if ((mod && /^[1-5]$/.test(e.key)) || (e.altKey && /^Arrow/.test(e.key))) return;

    if (this.handleKey(e)) {
      e.preventDefault();
      e.stopImmediatePropagation();
      return;
    }
    // Not an editor key, but the editor is focused: single-key app shortcuts
    // ('/', '?', '*', '-', Delete, F2, Ctrl+S…) must not fire underneath.
    e.stopPropagation();
  }

  /** Runs an editor shortcut. Returns true when the key was consumed. */
  handleKey(e) {
    if (!this.view) return false;
    const ctrl = e.ctrlKey || e.metaKey;
    const key = e.key.toLowerCase();

    if (e.code === 'Space') {
      if (!e.repeat) {
        const now = performance.now();
        if (now - this.lastSpaceDown < 350 && this.doc) {
          // Double-space: fit the whole map, like Home.
          this.view.fitToBounds();
          this.view.invalidate();
        }
        this.lastSpaceDown = now;
      }
      this.view.setSpaceHeld(true);
      return true;
    }

    if (key === 'escape') {
      if (!this.picker.hidden) { this.closePicker(); return true; }
      if (this.view.cancelMode()) { this.syncArmedUi(); return true; }
      if (this.placing) { this.cancelPlacement(); return true; }
      if (this.ui.pal) { this.togglePanel('pal', false); return true; }
      if (this.selection.length) {
        this.view.select([]);
        this.selection = [];
        this.renderFloat();
        this.renderInspector();
        return true;
      }
      return false;   // nothing of ours to close — the app may close things
    }

    if (key === 'enter') {
      if (this.view.fhDraw) { this.view.finishFootholdDraw(); return true; }
      if (this.view.fhExtend) { this.view.cancelExtend(); this.setStatus(t('Chain extension finished.')); return true; }
      return false;
    }

    if (key === 'tab' && !ctrl && !e.altKey) { this.foldAll(); return true; }

    if (ctrl && key === 's' && !e.shiftKey) {
      if (this.doc?.dirty) this.save();
      return true;   // claimed either way — never the app's WZ save
    }
    if (ctrl && key === 'z' && !e.shiftKey) { this.undo(false); return true; }
    if (ctrl && (key === 'y' || (e.shiftKey && key === 'z'))) { this.undo(true); return true; }
    if (ctrl && key === 'a') { this.view.selectAll(); return true; }
    if (ctrl && key === 'c') { this.copySelection(); return true; }
    if (ctrl && key === 'v') { this.pasteClipboard(); return true; }
    if (ctrl && key === 'd') { this.duplicateSelection(24, 0); return true; }
    if (ctrl) return false;

    if (key === 'delete' || key === 'backspace') {
      if (this.selection.length) this.deleteSelection();
      return true;
    }

    if (key === 'arrowleft' || key === 'arrowright' || key === 'arrowup' || key === 'arrowdown') {
      const step = e.shiftKey ? 10 : 1;
      const dx = key === 'arrowleft' ? -step : key === 'arrowright' ? step : 0;
      const dy = key === 'arrowup' ? -step : key === 'arrowdown' ? step : 0;
      if (this.selection.length) {
        this.view.nudgeSelection(dx, dy);
        if (!this.pendingNudge) this.pendingNudge = { dx: 0, dy: 0, items: this.view.selectionItems() };
        this.pendingNudge.dx += dx;
        this.pendingNudge.dy += dy;
        this.commitNudgeDebounced();
        this.renderFloat();
      } else {
        this.view.panBy(dx * 40, dy * 40);
      }
      return true;
    }

    if (key === '+' || key === '=') { this.view.zoomBy(1.15); return true; }
    if (key === '-' || key === '_') { this.view.zoomBy(1 / 1.15); return true; }
    if (key === '0' || key === 'home') { this.view.fitToBounds(); this.view.invalidate(); return true; }
    if (key === '1') { this.view.zoomTo(1); return true; }
    if (key === '?') { this.showKeysHelp(); return true; }
    if (key === 'p') { this.togglePanel('pal'); return true; }
    if (key === 'l') { this.togglePanel('layers'); return true; }
    if (key === 'm') { this.togglePanel('minimap'); return true; }
    if (key === 'i') { this.togglePanel('inspector'); return true; }

    if ((key === '[' || key === ']') && this.single('tile')) {
      this.cycleTileVariant(key === ']' ? 1 : -1);
      return true;
    }
    if (key === 'f') {
      const sel = this.selection.length === 1 ? this.selection[0] : null;
      if (sel && ['obj', 'life', 'reactor', 'back'].includes(sel.kind)) {
        const flip = Number(sel.entry.f ?? 0) === 1 ? '0' : '1';
        this.commitSetField(sel.entry.addr, 'f', flip);
        return true;
      }
      return false;
    }
    return false;
  }

  /* ==================================================== panels + folding */

  saveUi() {
    try { localStorage.setItem(UI_KEY, JSON.stringify(this.ui)); } catch { /* full */ }
  }

  /** Opens/closes one panel (or toggles it), remembers the choice. */
  togglePanel(name, force) {
    const next = force !== undefined ? !!force : !this.ui[name];
    if (this.ui[name] === next) return;
    this.ui[name] = next;
    this.saveUi();
    this.applyPanels();
  }

  /** Tab: everything folds away; Tab again brings back what was open —
   *  the art-tool gesture for "just let me see the map". */
  foldAll() {
    const anyOpen = this.ui.layers || this.ui.pal || this.ui.inspector;
    if (anyOpen) {
      this.foldSnapshot = { layers: this.ui.layers, pal: this.ui.pal, inspector: this.ui.inspector };
      this.ui.layers = false; this.ui.pal = false; this.ui.inspector = false;
    } else {
      const back = this.foldSnapshot ?? { layers: true, pal: false, inspector: false };
      this.ui.layers = back.layers; this.ui.pal = back.pal; this.ui.inspector = back.inspector;
    }
    this.saveUi();
    this.applyPanels();
  }

  /** Keeps a panel mounted through its exit so opening and closing feel like
   *  the same gesture. Direct canvas work stays immediate; only the chrome
   *  around it receives motion. */
  setPanelVisible(node, visible, { enter = 260, exit = 180 } = {}) {
    if (!node) return;
    if (visible) {
      if (node.hidden || node.dataset.motion === 'closing') motionIn(node, { timeout: enter });
      return;
    }
    if (!node.hidden && node.dataset.motion !== 'closing') {
      motionOut(node, { hide: true, timeout: exit }).then(() => this.view?.resize());
    }
  }

  /** Makes the DOM agree with this.ui, then re-renders what is visible. */
  applyPanels() {
    const showDock = Boolean(this.ui.layers && this.doc);
    const showDrawer = Boolean(this.ui.pal && this.doc);
    const showMinimap = Boolean(this.ui.minimap && this.doc);
    const showInspector = Boolean(this.ui.inspector && this.doc && this.selection.length);

    this.drawer.style.width = `${Math.max(280, this.ui.drawerW)}px`;
    this.setPanelVisible(this.dock, showDock, { enter: 240 });
    this.setPanelVisible(this.drawer, showDrawer, { enter: 280, exit: 200 });
    this.setPanelVisible(this.minimapBox, showMinimap);
    this.setPanelVisible(this.inspector, showInspector, { enter: 240 });
    this.renderRail();
    if (showDock) this.renderDock();
    if (showDrawer) this.renderPaletteBody();
    if (showMinimap) this.renderMinimapBox();
    if (showInspector) this.renderInspector();
    // The canvas may have resized under the drawerless layout.
    this.view?.resize();
  }

  installDrawerResize() {
    let start = null;
    this.drawerGrip.addEventListener('pointerdown', (e) => {
      start = { x: e.clientX, w: this.drawer.offsetWidth };
      this.drawerGrip.setPointerCapture(e.pointerId);
      e.preventDefault();
    });
    this.drawerGrip.addEventListener('pointermove', (e) => {
      if (!start) return;
      const max = Math.max(320, this.canvasWrap.clientWidth * 0.85);
      this.ui.drawerW = Math.round(Math.min(max, Math.max(280, start.w + (e.clientX - start.x))));
      this.drawer.style.width = `${this.ui.drawerW}px`;
    });
    this.drawerGrip.addEventListener('pointerup', () => { start = null; this.saveUi(); });
    this.drawerGrip.addEventListener('dblclick', () => this.togglePanel('pal', false));
  }

  setStatus(text) {
    this.statusText.textContent = text;
  }

  showKeysHelp() {
    const row = (keys, what) => el('div', { class: 'me-keys-row' },
      el('span', { class: 'me-keys-combo num', text: keys }),
      el('span', { text: what }));
    modal({
      title: t('Map editor keys'),
      subtitle: t('While the map editor is on screen these keys belong to it — the app underneath hears none of them.'),
      width: '520px',
      body: el('div', { class: 'me-keys-grid' },
        row('Space + drag', t('Pan — while Space is down nothing selects or moves')),
        row('Space ×2 / Home / 0', t('Fit the whole map')),
        row('Wheel', t('Zoom at the cursor · 1 = 100% · + / −')),
        row('Click / drag', t('Select / move (N selected move together)')),
        row('Shift-click · drag on empty', t('Add to selection · marquee')),
        row('Arrows (Shift = 10px)', t('Nudge selection, or pan when nothing is selected')),
        row('Ctrl+Z / Ctrl+Y', t('Undo / redo map edits')),
        row('Ctrl+C / V / D', t('Copy · paste at cursor · duplicate')),
        row('Delete', t('Remove selection (one undo entry)')),
        row('Ctrl+S', t('Save the map')),
        row('[ ]', t('Cycle a selected tile’s variant')),
        row('F', t('Flip the selected obj / life / back')),
        row('Double-click', t('Tile: variant picker · foothold: insert vertex · free end: extend')),
        row('Esc', t('Back out: gesture, placement, drawer, selection — innermost first')),
        row('Tab', t('Fold every panel away / bring them back')),
        row('P · L · I · M', t('Palette · Layers · Inspector · Minimap')),
        row('Enter', t('Finish a foothold chain'))),
      actions: [{ label: t('Close') }],
    });
  }

  single(kind) {
    return this.selection.length === 1 && this.selection[0].kind === kind
      ? this.selection[0] : null;
  }

  async commitNudge() {
    const pending = this.pendingNudge;
    this.pendingNudge = null;
    if (!pending || (!pending.dx && !pending.dy) || !this.doc) return;
    await this.commitMoveMany(pending.items, pending.dx, pending.dy, { alreadyApplied: true });
  }

  showIdle() {
    clear(this.banner);
    this.banner.append(
      el('div', { class: 'me-banner-card' },
        el('h3', { text: t('No map open') }),
        el('p', { text: t('{n} maps are available from {archives}.', {
          n: fmt.format(this.caps.mapCount), archives: this.caps.mapArchives.join(', '),
        }) }),
        el('p', { class: 'muted', text: t(
          'Once a map is open: hover shows what you are on, click selects, drag moves, ' +
          'space-drag pans, the wheel zooms at the cursor, and double-click edits in place.') }),
        el('button', { class: 'btn btn-primary', onclick: () => this.openPicker() },
          icon('search', { size: 15 }), t('Open a map…'))));
    motionIn(this.banner, { timeout: 240 });
  }

  renderToolbar() {
    clear(this.toolbar);
    const d = this.doc;
    this.toolbar.append(...[
      el('button', { class: 'btn', onclick: () => this.openPicker(), 'data-tip': t('Choose a map by id or name') },
        icon('search', { size: 14 }), t('Open map…')),
      el('button', { class: 'btn', onclick: () => this.newMapDialog(),
        'data-tip': t('Create a map from scratch: 9-digit id, the measured minimal shape, a String.wz row') },
        icon('plus', { size: 14 }), t('New map…')),
      d ? el('button', { class: 'btn', onclick: () => this.minimapDialog(),
          'data-tip': t('Regenerate the minimap from the map art — after a plan that names anyone sharing it') },
          t('Minimap…')) : null,
      this.animToggle(),
      d ? el('span', { class: 'me-title' },
          el('strong', { text: d.name ?? t('(no String.wz name)') }),
          el('span', { class: 'muted num', text: ` ${d.imageName}` }),
          d.dirty ? el('span', { class: 'me-dirty', 'data-tip': t('Unsaved map edits') }) : null)
        : el('span', { class: 'me-title muted', text: t('No map open') }),
      ...this.lifeArtActions(),
      el('span', { class: 'me-toolbar-spacer' }),
      d ? el('span', {
          class: 'me-chip', 'data-tip':
            t('Top-level nodes outside the editor’s vocabulary, carried through byte-for-byte: {names}',
              { names: d.unmodelledNames.length ? d.unmodelledNames.join(', ') : t('none') }),
          text: t('unmodelled nodes carried: {n}', { n: d.unmodelledCount }),
        }) : null,
      d ? el('button', { class: 'btn btn-icon btn-ghost', 'data-tip': t('Undo map edit (Ctrl+Z)'),
          'aria-label': t('Undo map edit'),
          disabled: !d.undoDepth, onclick: () => this.undo(false) }, icon('undo', { size: 15 })) : null,
      d ? el('button', { class: 'btn btn-icon btn-ghost', 'data-tip': t('Redo map edit (Ctrl+Y)'),
          'aria-label': t('Redo map edit'),
          disabled: !d.redoDepth, onclick: () => this.undo(true) }, icon('redo', { size: 15 })) : null,
      d ? el('button', { class: 'btn btn-ghost', 'data-tip': t('Every edit by name, newest first'),
          onclick: (e) => this.toggleHistory(e.currentTarget) }, t('History')) : null,
      d ? el('button', { class: 'btn btn-primary', disabled: !d.dirty,
          onclick: () => this.save() }, icon('save', { size: 15 }), t('Save map')) : null,
    ].filter(Boolean));
  }

  /** A map knows which NPCs/mobs stand on it, but their pixels live in the
   * separate Npc/Mob archives. Never leave a name-only fallback unexplained:
   * make the missing archive one click away, or name genuinely missing art. */
  lifeArtActions() {
    if (!this.doc) return [];
    const actions = [];
    for (const spec of [
      { type: 'n', label: t('NPC'), archive: 'Npc.wz', open: this.caps?.npcSprites },
      { type: 'm', label: t('mob'), archive: 'Mob.wz', open: this.caps?.mobSprites },
    ]) {
      const life = this.doc.life.filter((entry) => (entry.type || '').toLowerCase() === spec.type);
      if (!life.length) continue;
      const missing = life.filter((entry) => !entry.art || this.doc.art?.[entry.art]?.missing).length;
      if (!missing) continue;

      if (!spec.open) {
        actions.push(el('button', {
          class: 'btn',
          'data-tip': missing === 1
            ? t('{n} {label} sprite cannot be drawn because {archive} is not open. Names and positions come from the map; pixels come from {archive}.',
              { n: missing, label: spec.label, archive: spec.archive })
            : t('{n} {label} sprites cannot be drawn because {archive} is not open. Names and positions come from the map; pixels come from {archive}.',
              { n: missing, label: spec.label, archive: spec.archive }),
          onclick: () => this.app.openFilePicker(),
        }, icon('folderOpen', { size: 14 }), t('Open {archive} for sprites', { archive: spec.archive })));
      } else {
        actions.push(el('span', {
          class: 'me-chip',
          'data-tip': missing === 1
            ? t('{archive} is open, but {n} referenced {label} sprite did not contain a drawable stand/idle state.',
              { archive: spec.archive, n: missing, label: spec.label })
            : t('{archive} is open, but {n} referenced {label} sprites did not contain a drawable stand/idle state.',
              { archive: spec.archive, n: missing, label: spec.label }),
          text: missing === 1
            ? t('{n} {label} sprite unavailable', { n: missing, label: spec.label })
            : t('{n} {label} sprites unavailable', { n: missing, label: spec.label }),
        }));
      }
    }
    return actions;
  }

  animToggle() {
    const d = this.doc;
    if (!d || !this.view) return null;
    const animArt = this.view.animatedArtCount();
    const movingBacks = d.backs.filter((b) => b.type >= 4 && b.type <= 7).length;
    const spine = d.backs.filter((b) => b.spine).length
      + d.layers.reduce((n, l) => n + l.objs.filter((o) => o.spine).length, 0);
    if (!animArt && !movingBacks) return null;

    const playing = this.view.anim.playing;
    const parts = [];
    if (animArt) parts.push(t('{n} animated art at their own frame delays', { n: animArt }));
    if (movingBacks) {
      parts.push(movingBacks === 1
        ? t('{n} scrolling background', { n: movingBacks })
        : t('{n} scrolling backgrounds', { n: movingBacks }));
    }
    let tip = t('{label}: {parts}. Paused shows frame 0.', {
      label: playing ? t('Pause') : t('Play'), parts: parts.join(t(' and ')),
    });
    if (spine) {
      tip += spine === 1
        ? ` ${t('{n} Spine-rigged entry stays static with a badge — a rig is not a frame list.', { n: spine })}`
        : ` ${t('{n} Spine-rigged entries stay static with a badge — a rig is not a frame list.', { n: spine })}`;
    }

    return el('button', {
      class: `btn${playing ? ' btn-primary' : ''}`,
      'data-tip': tip,
      onclick: () => {
        this.view.setPlaying(!this.view.anim.playing);
        this.renderToolbar();
      },
    }, icon(playing ? 'pause' : 'play', { size: 14 }), playing ? t('Pause') : t('Play'));
  }

  /* ============================================================ history */

  async toggleHistory(button) {
    if (this.histPop) { this.closeHistory(); return; }
    let history;
    try {
      history = await get(`/api/mapedit/history?path=${q(this.doc.path)}`);
    } catch (err) { toastError(err, t('History')); return; }

    const pop = el('div', { class: 'me-hist-pop' });
    pop.append(el('h5', { text: t('Undo — {n}', { n: history.undo.length }) }));
    if (!history.undo.length) pop.append(el('p', { class: 'muted', text: t('Nothing to undo.') }));
    history.undo.slice(0, 30).forEach((label, i) => {
      pop.append(el('div', { class: 'me-hist-row', text: label,
        'data-tip': i === 0 ? t('Ctrl+Z takes this back next') : undefined }));
    });
    if (history.redo.length) {
      pop.append(el('h5', { text: t('Redo — {n}', { n: history.redo.length }) }));
      history.redo.slice(0, 10).forEach((label) => {
        pop.append(el('div', { class: 'me-hist-row me-hist-redo', text: label }));
      });
    }
    const rect = button.getBoundingClientRect();
    pop.style.top = `${rect.bottom + 4}px`;
    pop.style.right = `${Math.max(8, window.innerWidth - rect.right)}px`;
    document.body.append(pop);
    this.histPop = pop;
    motionIn(pop, { timeout: 220 });
    const close = (e) => {
      if (!pop.contains(e.target) && e.target !== button) this.closeHistory();
    };
    this._histClose = close;
    setTimeout(() => document.addEventListener('pointerdown', close), 0);
  }

  closeHistory() {
    if (this.histPop) {
      const pop = this.histPop;
      this.histPop = null;
      document.removeEventListener('pointerdown', this._histClose);
      motionOut(pop, { remove: true, timeout: 160 });
    }
  }

  /* ============================================================ picker */

  openPicker() {
    const list = el('div', { class: 'me-picker-list' });
    const note = el('p', { class: 'muted', text: '' });
    const input = el('input', {
      class: 'field-input', type: 'search', placeholder: t('Map id or name — e.g. 100000000 or Henesys'),
      autocomplete: 'off',
    });

    let closeModal = null;
    const load = async () => {
      const query = input.value.trim();
      try {
        const result = await get(`/api/mapedit/maps?q=${q(query)}&limit=200`);
        clear(list);
        note.textContent = result.truncated
          ? t('Showing {n} of {total} — narrow the search.',
            { n: result.maps.length, total: fmt.format(result.total) })
          : (result.total === 1
            ? t('{n} map.', { n: fmt.format(result.total) })
            : t('{n} maps.', { n: fmt.format(result.total) }));
        for (const row of result.maps) {
          list.append(el('button', { class: 'me-picker-row', onclick: () => {
            closeModal?.();
            this.openMap(row.path);
          } },
            el('span', { class: 'num me-picker-id', text: String(row.id).padStart(9, '0') }),
            el('span', { class: 'me-picker-name', text: row.name ?? t('(no name in String.wz)') }),
            el('span', { class: 'muted', text: row.source })));
        }
        if (!result.maps.length) list.append(el('p', { class: 'muted', text: t('Nothing matches.') }));
      } catch (err) {
        toastError(err, t('Map list'));
      }
    };
    input.addEventListener('input', debounce(load, 250));

    modal({
      title: t('Open a map'),
      subtitle: this.caps.names
        ? t('Names come from String.wz — the source that is actually right.')
        : t('String.wz is not open, so maps show ids only.'),
      body: el('div', { class: 'me-picker' }, input, note, list),
      actions: [{ label: t('Cancel') }],
      width: '560px',
      onOpen: (dialog) => {
        closeModal = () => dialog.close();
        input.focus();
        load();
      },
    });
  }

  /* ============================================================ document */

  async openMap(path) {
    this.hideBanner();
    try {
      const doc = await post('/api/mapedit/open', { path });
      this.adoptDoc(doc, { fit: true });
      if (doc.link) {
        this.showNotice(t('This map is a link stub — info/link points at {link}.',
          { link: doc.link }) + ' ' +
          t('What you see is the content the stub itself carries (portals, spawns, zones), which is real and preserved; the geometry lives in the target map.'));
      } else if (doc.boundsComputed) {
        this.showNotice(t('This map has no VR bound; the viewport was computed from its geometry.'));
      }
      this.canvasWrap.focus();
    } catch (err) {
      this.doc = null;
      this.renderToolbar();
      this.showRefusal(err.message);
    }
  }

  adoptDoc(doc, { fit = false } = {}) {
    this.doc = doc;
    if (!this.portalIconsLoaded) {
      this.portalIconsLoaded = true;
      get('/api/mapedit/portal-icons')
        .then((icons) => this.view.setPortalIcons(icons))
        .catch(() => { /* markers fall back to dots */ });
    }
    const cam = this.view.cam && !fit ? { ...this.view.cam } : null;
    this.floatManual = null;
    this.view.setDoc(doc);
    if (cam) this.view.cam = cam;
    this.view.invalidate();
    this.setStatus(t('Hover names what you are on · click selects · drag moves · Space-drag pans · ? for all keys'));
    this.reselect();
    this.renderToolbar();
    this.renderSide();
    this.renderFloat();
    this.renderInspector();
    this.refreshUnsaved();
  }

  async refetchDoc() {
    if (!this.doc) return;
    try {
      const doc = await get(`/api/mapedit/doc?path=${q(this.doc.path)}`);
      this.adoptDoc(doc);
    } catch (err) {
      toastError(err, t('Map document'));
    }
  }

  pools() {
    return {
      tile: this.doc.layers.flatMap((l) => l.tiles),
      obj: this.doc.layers.flatMap((l) => l.objs),
      foothold: this.doc.footholds,
      portal: this.doc.portals,
      life: this.doc.life,
      reactor: this.doc.reactors,
      ladder: this.doc.ladders,
      rect: this.doc.rects ?? [],
      back: this.doc.backs,
    };
  }

  /** Finds an entry (any kind) again after a re-fetch, by address. */
  findByAddr(addr) {
    const pools = this.pools();
    for (const [kind, pool] of Object.entries(pools)) {
      const entry = pool.find((e) => sameAddr(e.addr, addr));
      if (entry) return { kind, entry };
    }
    return null;
  }

  reselect() {
    if (!this.doc) { this.selection = []; return; }
    const kept = [];
    for (const sel of this.selection) {
      const addr = sel.entry?.addr;
      if (!addr) continue;
      const pools = this.pools();
      const entry = (pools[sel.kind] ?? []).find((e) => sameAddr(e.addr, addr));
      if (entry) kept.push({ ...sel, entry, item: undefined });
    }
    this.selection = kept;
    this.view.select(this.selection);
  }

  selectAddrs(addrs) {
    if (!this.doc || !addrs?.length) return;
    const picked = [];
    for (const addr of addrs) {
      const found = this.findByAddr(addr);
      if (found) picked.push(found);
    }
    this.selection = picked;
    this.view.select(this.selection);
    this.renderFloat();
    this.renderInspector();
  }

  /* ============================================================ banners */

  showRefusal(reason) {
    clear(this.banner);
    this.banner.append(el('div', { class: 'me-banner-card me-refusal' },
      el('h3', {}, icon('alert', { size: 16 }), t(' This map was refused, not opened')),
      el('p', { text: reason }),
      el('p', { class: 'muted', text: t(
        'The editor only opens maps it can write back byte-identically. Opening this one anyway ' +
        'would save a map that differs somewhere nobody asked to change.') }),
      el('button', { class: 'btn', onclick: () => this.openPicker() }, t('Open another map'))));
    motionIn(this.banner, { timeout: 240 });
  }

  showNotice(text) {
    clear(this.banner);
    const card = el('div', { class: 'me-banner-card me-notice' },
      el('p', { text }),
      el('button', { class: 'btn btn-ghost', onclick: () => this.hideBanner() }, t('Dismiss')));
    this.banner.append(card);
    motionIn(this.banner, { timeout: 240 });
  }

  hideBanner() {
    if (this.banner.hidden) return;
    motionOut(this.banner, { hide: true, timeout: 180 }).then((closed) => {
      if (closed && this.banner.hidden) clear(this.banner);
    });
  }

  /* ================================================ rail, dock, on-canvas */

  /** Everything panel-shaped, re-rendered to match this.ui and the doc. */
  renderSide() {
    this.renderOverlayStrip();
    this.renderZoomCtl();
    this.applyPanels();
  }

  renderRail() {
    clear(this.rail);
    const d = this.doc;
    const btn = (name, label, tip, key) => el('button', {
      class: `me-rail-btn${this.ui[name] ? ' active' : ''}`,
      'data-tip': `${tip} (${key})`,
      'aria-label': `${tip} (${key})`,
      'aria-pressed': this.ui[name] ? 'true' : 'false',
      disabled: d ? null : 'disabled',
      onclick: () => this.togglePanel(name),
    }, el('span', { class: 'me-rail-ic' }, label));
    this.rail.append(
      btn('pal', icon('image', { size: 17 }), t('Palette — place tiles, objects, mobs…'), 'P'),
      btn('layers', icon('layers', { size: 17 }), t('Layers'), 'L'),
      btn('inspector', icon('info', { size: 17 }), t('Raw inspector for the selection'), 'I'),
      btn('minimap', icon('search', { size: 17 }), t('Minimap'), 'M'),
      el('span', { class: 'me-rail-spacer' }),
      el('button', { class: 'me-rail-btn', 'data-tip': t('Keys — everything the editor listens for (?)'),
        'aria-label': t('Map Editor keyboard shortcuts'),
        onclick: () => this.showKeysHelp() }, el('span', { class: 'me-rail-ic', text: '?' })));
  }

  /** The layer dock: compact, eyes first, numbers kept quiet. */
  renderDock() {
    clear(this.dock);
    const d = this.doc;
    if (!d) return;
    this.dock.append(el('div', { class: 'me-dock-head' },
      el('h4', { text: t('Layers') }),
      el('button', {
        class: 'btn btn-ghost btn-icon me-dock-close', 'data-tip': t('Collapse (L)'),
        'aria-label': t('Collapse layers'), onclick: () => this.togglePanel('layers', false),
      }, icon('close', { size: 13 }))));
    const layers = el('div', { class: 'me-layer-list' });
    for (const layer of d.layers) {
      const visible = this.view.layerVisible.get(layer.index) !== false;
      layers.append(el('div', { class: 'me-layer-row' },
        el('button', {
          class: 'me-eye', 'aria-pressed': visible ? 'true' : 'false',
          'aria-label': t(visible ? 'Hide layer {n}' : 'Show layer {n}', { n: layer.index }),
          'data-tip': visible ? t('Hide layer') : t('Show layer'),
          onclick: (e) => {
            const on = e.currentTarget.getAttribute('aria-pressed') !== 'true';
            e.currentTarget.setAttribute('aria-pressed', on ? 'true' : 'false');
            e.currentTarget.setAttribute('aria-label', t(on ? 'Hide layer {n}' : 'Show layer {n}', { n: layer.index }));
            e.currentTarget.dataset.tip = on ? t('Hide layer') : t('Show layer');
            this.view.setLayerVisible(layer.index, on);
          },
        }, icon('eye', { size: 15 })),
        el('span', { class: 'me-layer-name', text: t('Layer {n}', { n: layer.index }) }),
        layer.ts ? el('span', {
          class: 'me-chip',
          'data-tip': t(
            'The tile set for this whole layer — tS is per-layer, so changing it ' +
            're-skins every tile on the layer.'),
          text: layer.ts + (layer.tsMag ? ` ×${layer.tsMag}` : ''),
        }) : null,
        el('span', { class: 'muted num me-layer-counts', text:
          `${layer.tiles.length ? `${fmt.format(layer.tiles.length)}t` : ''}` +
          `${layer.tiles.length && layer.objs.length ? ' · ' : ''}` +
          `${layer.objs.length ? `${fmt.format(layer.objs.length)}o` : ''}` }),
        layer.layerBackCount ? el('span', {
          class: 'me-chip', text: t('{n} back', { n: layer.layerBackCount }),
          'data-tip': t(
            'This layer holds a background list of its own — a measured anomaly ' +
            '(954090400). Rendered with the rest, carried on save.'),
        }) : null));
    }
    if (!d.layers.length)
      layers.append(el('p', { class: 'muted', text: d.link ? t('A link stub has no layers of its own.') : t('No layers.') }));
    this.dock.append(layers);
  }

  /** Overlay toggles as an icon strip on the canvas — progressive disclosure
   *  instead of a checkbox column. */
  renderOverlayStrip() {
    clear(this.overlayStrip);
    const d = this.doc;
    if (!d) return;
    const defs = [
      ['back', 'BG', t('Backgrounds ({n})', { n: d.backs.length })],
      ['foothold', 'FH', t('Footholds ({n})', { n: fmt.format(d.footholds.length) })],
      ['ladder', 'LD', t('Ladders & ropes ({n})', { n: d.ladders.length })],
      ['portal', 'PT', t('Portals ({n})', { n: d.portals.length })],
      ['life', 'NPC', t('Life spawns ({n})', { n: d.life.length })],
      ['reactor', 'RC', t('Reactors ({n})', { n: d.reactors.length })],
      ['rect', 'ZN', t('Zones ({n})', { n: (d.rects ?? []).length })],
      ['vr', 'VR', d.boundsComputed ? t('Bounds (computed)') : t('VR bound')],
    ];
    for (const [key, label, tip] of defs) {
      this.overlayStrip.append(el('button', {
        class: 'me-ov-btn', 'aria-pressed': this.view.overlays[key] ? 'true' : 'false',
        'data-tip': this.view.overlays[key]
          ? t('{tip} — click to hide', { tip })
          : t('{tip} — click to show', { tip }),
        onclick: (e) => {
          const on = e.currentTarget.getAttribute('aria-pressed') !== 'true';
          e.currentTarget.setAttribute('aria-pressed', on ? 'true' : 'false');
          this.view.setOverlay(key, on);
          this.renderOverlayStrip();
        },
      }, label));
    }
  }

  /** Zoom cluster in the canvas corner: −  %  +  ·  fit  1:1 */
  renderZoomCtl() {
    clear(this.zoomCtl);
    if (!this.doc) return;
    this.zoomLabel = el('button', { class: 'me-zoom-pct num', 'data-tip': t('Click = 100%'),
      'aria-label': t('Reset zoom to 100 percent'),
      onclick: () => this.view.zoomTo(1) }, '100%');
    this.zoomCtl.append(
      el('button', { class: 'me-zoom-btn', 'data-tip': t('Zoom out (−)'), 'aria-label': t('Zoom out'),
        onclick: () => this.view.zoomBy(1 / 1.25) }, icon('zoomOut', { size: 14 })),
      this.zoomLabel,
      el('button', { class: 'me-zoom-btn', 'data-tip': t('Zoom in (+)'), 'aria-label': t('Zoom in'),
        onclick: () => this.view.zoomBy(1.25) }, icon('zoomIn', { size: 14 })),
      el('button', { class: 'me-zoom-btn me-zoom-fit', 'data-tip': t('Fit the whole map (0, Home, double-Space)'),
        'aria-label': t('Fit the whole map'),
        onclick: () => { this.view.fitToBounds(); this.view.invalidate(); } }, '⛶'));
    this.updateZoomLabel();
  }

  updateZoomLabel() {
    if (this.zoomLabel && this.view) {
      const percent = Math.round(this.view.cam.scale * 100);
      this.zoomLabel.textContent = `${percent}%`;
      this.zoomLabel.setAttribute('aria-label', t('Reset zoom from {percent} to 100 percent', { percent }));
    }
  }

  /* ------------------------------------------------ minimap: click to jump */

  renderMinimapBox() {
    clear(this.minimapBox);
    const d = this.doc;
    if (!d) return;
    this.mmCanvas = el('canvas', { class: 'me-mm-canvas' });
    this.minimapBox.append(this.mmCanvas);
    this.mmImage = null;
    this.mmMap = null;   // world<->mini transform, set by drawMinimap

    if (d.miniMap?.canvasPath) {
      const img = new Image();
      img.onload = () => { this.mmImage = img; this.drawMinimap(); };
      img.onerror = () => this.drawMinimap();
      img.src = `/api/canvas?path=${q(d.miniMap.canvasPath)}&v=me`;
    }

    const jump = (e) => {
      const m = this.mmMap;
      if (!m) return;
      const rect = this.mmCanvas.getBoundingClientRect();
      const px = (e.clientX - rect.left) * (this.mmCanvas.width / rect.width);
      const py = (e.clientY - rect.top) * (this.mmCanvas.height / rect.height);
      this.view.centerOn(m.wx0 + (px - m.ox) / m.k, m.wy0 + (py - m.oy) / m.k);
    };
    let down = false;
    this.mmCanvas.addEventListener('pointerdown', (e) => {
      down = true;
      this.mmCanvas.setPointerCapture(e.pointerId);
      jump(e);
    });
    this.mmCanvas.addEventListener('pointermove', (e) => { if (down) jump(e); });
    this.mmCanvas.addEventListener('pointerup', () => { down = false; });
    this.drawMinimap();
  }

  /** The minimap picture + the viewport rectangle. Redrawn on every camera
   *  change — it is a canvas blit, cheap. */
  drawMinimap() {
    const c = this.mmCanvas;
    const d = this.doc;
    if (!c || !d) return;
    const W = 208; const H = 140;
    c.width = W; c.height = H;
    const g = c.getContext('2d');
    g.fillStyle = '#141a24';
    g.fillRect(0, 0, W, H);

    // World rect the minimap stands for.
    let wx0; let wy0; let ww; let wh;
    const mm = d.miniMap;
    if (this.mmImage && mm?.width && mm?.height) {
      wx0 = -(mm.centerX ?? 0);
      wy0 = -(mm.centerY ?? 0);
      ww = Number(mm.width);
      wh = Number(mm.height);
    } else if (d.bounds) {
      wx0 = d.bounds.left; wy0 = d.bounds.top;
      ww = Math.max(1, d.bounds.right - d.bounds.left);
      wh = Math.max(1, d.bounds.bottom - d.bounds.top);
    } else {
      this.mmMap = null;
      g.fillStyle = 'rgba(255,255,255,0.4)';
      g.font = '11px system-ui';
      g.fillText(t('no minimap'), 8, 20);
      return;
    }
    const k = Math.min((W - 8) / ww, (H - 8) / wh);
    const ox = (W - ww * k) / 2;
    const oy = (H - wh * k) / 2;
    this.mmMap = { wx0, wy0, k, ox, oy };

    if (this.mmImage) {
      g.imageSmoothingEnabled = true;
      g.drawImage(this.mmImage, ox, oy, ww * k, wh * k);
    } else {
      // No minimap picture: a foothold sketch is the honest layout view.
      g.strokeStyle = '#61d872';
      g.lineWidth = 1;
      g.beginPath();
      for (const fh of d.footholds) {
        g.moveTo(ox + (fh.x1 - wx0) * k, oy + (fh.y1 - wy0) * k);
        g.lineTo(ox + (fh.x2 - wx0) * k, oy + (fh.y2 - wy0) * k);
      }
      g.stroke();
    }

    // The viewport, as a draggable-feeling rectangle.
    const vp = this.view.viewportWorld();
    const rx = ox + (vp.x0 - wx0) * k;
    const ry = oy + (vp.y0 - wy0) * k;
    g.strokeStyle = '#7fd0ff';
    g.lineWidth = 1.5;
    g.strokeRect(rx, ry, (vp.x1 - vp.x0) * k, (vp.y1 - vp.y0) * k);
  }

  updateMinimapView() {
    if (!this.minimapBox.hidden) this.drawMinimap();
  }

  /* ============================================================ palette */

  mem() {
    const p = this.palette;
    if (!p.mem[p.kind]) p.mem[p.kind] = { set: null, entries: null, query: '', layer: 0 };
    return p.mem[p.kind];
  }

  /** One set in the set grid: its representative picture with its name under
   *  it. A set with no resolvable art says so on its face. */
  setCell(set, before) {
    const face = el('div', { class: 'me-pal-setface' });
    if (set.thumbPath) {
      const img = el('img', { src: `/api/mapedit/thumb?path=${q(set.thumbPath)}`, loading: 'lazy', alt: '' });
      img.addEventListener('error', () => {
        img.remove();
        face.append(el('span', { class: 'me-pal-brokenmark', text: '✕',
          'data-tip': t('{name} — its representative art did not decode.', { name: set.name }) }));
      });
      face.append(img);
    } else {
      face.append(el('span', { class: 'me-pal-brokenmark muted', text: '∅',
        'data-tip': t('{name} — nothing in this set resolved to drawable art.', { name: set.name }) }));
    }
    return el('button', {
      class: 'me-pal-setcell', 'data-tip': t('{name} — {source}', { name: set.name, source: set.source }),
      onclick: () => { before?.(); this.openPaletteSet(set); },
    }, face, el('span', { class: 'me-pal-setname', text: set.name }));
  }

  /** Clears and re-renders the palette drawer's body. */
  renderPaletteBody() {
    if (!this.doc) return;
    const scroll = this.side.scrollTop;
    clear(this.side);
    this.renderPalette();
    this.side.scrollTop = scroll;
  }

  renderPalette() {
    const p = this.palette;

    // Search everything: one box across every kind at once. The input stays
    // mounted while the content below it changes. Rebuilding the whole drawer
    // here used to destroy focus after the first debounced keystroke; the next
    // letter then became a map shortcut (P opened/closed the palette, arrows
    // panned, and so on) instead of part of the query.
    const global = el('input', {
      class: 'field-input me-pal-global', type: 'search',
      'aria-label': t('Search the palette catalog'),
      placeholder: t('Search everything — sets, mobs, NPCs, reactors…'), value: p.global,
    });
    const refresh = debounce(() => {
      if (!global.isConnected || !this.ui.pal) return;
      while (global.nextSibling) global.nextSibling.remove();
      const query = p.global.trim();
      if (query.length >= 2) this.renderGlobalSearch(query);
      else this.renderPaletteCatalog();
    }, 250);
    global.addEventListener('input', () => {
      p.global = global.value;
      refresh();
    });
    this.side.append(global);

    if (p.global.trim().length >= 2) {
      this.renderGlobalSearch(p.global.trim());
      return;
    }

    this.renderPaletteCatalog();
  }

  /** The ordinary kind browser below the persistent global-search field. */
  renderPaletteCatalog() {
    const p = this.palette;

    const kinds = [
      ['Tile', 'Tiles'], ['Obj', 'Objects'], ['Back', 'Backs'],
      ['mob', 'Mobs'], ['npc', 'NPCs'], ['reactor', 'Reactors'], ['portal', 'Portals'],
      ['fh', 'Footholds'], ['ladder', 'Ladders'],
    ];
    const chips = el('div', { class: 'me-pal-kinds' });
    for (const [key, label] of kinds) {
      chips.append(el('button', {
        class: `me-chip me-pal-kind${p.kind === key ? ' active' : ''}`, text: t(label),
        'aria-pressed': p.kind === key ? 'true' : 'false',
        onclick: () => { p.kind = key; this.renderPaletteBody(); },
      }));
    }
    this.side.append(chips);

    if (this.placing) {
      this.side.append(el('div', { class: 'me-pal-armed' },
        el('span', { text: t('Placing: {label}', { label: this.placing.label }) }),
        el('button', { class: 'btn btn-ghost', text: 'Esc', onclick: () => this.cancelPlacement() })));
    }

    this.renderRecents();

    if (p.kind === 'Tile' || p.kind === 'Obj' || p.kind === 'Back') this.renderArtPalette();
    else if (p.kind === 'mob' || p.kind === 'npc') this.renderLifePalette();
    else if (p.kind === 'reactor') this.renderReactorPalette();
    else if (p.kind === 'portal') this.renderPortalPalette();
    else if (p.kind === 'fh') this.renderFootholdTools();
    else if (p.kind === 'ladder') this.renderLadderTools();
  }

  /* -------- recents: the last assets used, pictures first */

  renderRecents() {
    const rows = this.recents[this.palette.kind];
    if (!rows?.length) return;
    this.side.append(el('h5', { class: 'me-pal-group', text: 'Recent' }));
    const grid = el('div', { class: 'me-pal-grid' });
    for (const row of rows) {
      const cell = this.paletteCell({
        thumbPath: row.thumb, label: row.label, frames: row.frames,
        onclick: () => this.armRecent(row),
        previewLeaf: row.previewLeaf,
      });
      grid.append(cell);
    }
    this.side.append(grid);
  }

  pushRecent(kind, row) {
    const list = this.recents[kind] ?? [];
    const without = list.filter((r) => r.label !== row.label);
    without.unshift(row);
    this.recents[kind] = without.slice(0, RECENTS_MAX);
    try { localStorage.setItem(RECENTS_KEY, JSON.stringify(this.recents)); } catch { /* full */ }
  }

  armRecent(row) {
    if (row.arm.kind === 'life') {
      this.armLife(row.arm.lifeType, row.arm.id, row.label, row.thumb, { silent: true });
    } else if (row.arm.kind === 'reactor') {
      this.armReactor(row.arm.id, row.thumb, { silent: true });
    } else {
      this.armArt(row.arm.paletteKind, row.arm.set, row.arm.setPath, row.arm.entry, { silent: true });
    }
  }

  /* -------- one palette cell: picture at rest, badge for animation,
     placeholder while loading, broken marker on failure, flyout on hover */

  paletteCell({ thumbPath, label, caption, frames, hasFoothold, onclick, previewLeaf, w, h }) {
    const cell = el('button', {
      class: `me-pal-cell${caption ? ' me-pal-cell-tall' : ''}`,
      onclick, 'data-tip': label, 'aria-label': label,
    });
    if (thumbPath) {
      const img = el('img', {
        src: `/api/mapedit/thumb?path=${q(thumbPath)}`, loading: 'lazy', alt: '',
      });
      img.addEventListener('error', () => {
        img.remove();
        cell.classList.add('me-pal-broken');
        cell.append(el('span', { class: 'me-pal-brokenmark', text: '✕',
          'data-tip': t('{label} — the art did not decode; the reference is real but its pixels did not render.', { label }) }));
      });
      cell.append(img);
    } else {
      cell.classList.add('me-pal-broken');
      cell.append(el('span', { class: 'muted', text: '∅',
        'data-tip': t('{label} — no drawable art resolves here.', { label }) }));
    }
    if (frames > 1) cell.append(el('span', { class: 'me-pal-frames', text: `${frames}f` }));
    if (hasFoothold) cell.append(el('span', { class: 'me-pal-fh', text: 'fh' }));
    if (caption) cell.append(el('span', { class: 'me-pal-caption', text: caption }));
    this.attachFlyout(cell, { label, thumbPath, frames, previewLeaf, w, h });
    return cell;
  }

  /* -------- hover flyout: bigger, animated where animated */

  attachFlyout(cell, info) {
    cell.addEventListener('mouseenter', () => {
      clearTimeout(this.flyoutCloseTimer);
      clearTimeout(this.flyoutOpenTimer);
      const warm = Date.now() - this.flyoutLastClosedAt < 500;
      this.flyoutOpenTimer = setTimeout(() => this.showFlyout(cell, info), warm ? 75 : 180);
    });
    cell.addEventListener('mouseleave', () => {
      clearTimeout(this.flyoutOpenTimer);
      clearTimeout(this.flyoutCloseTimer);
      this.flyoutCloseTimer = setTimeout(() => this.hideFlyout(), 100);
    });
  }

  hideFlyout({ immediate = false } = {}) {
    clearTimeout(this.flyoutOpenTimer);
    clearTimeout(this.flyoutCloseTimer);
    if (this.flyout) {
      const fly = this.flyout;
      fly.stop?.();
      this.flyout = null;
      this.flyoutLastClosedAt = Date.now();
      if (immediate) fly.remove();
      else motionOut(fly, { remove: true, timeout: 150 });
    }
  }

  async showFlyout(cell, info) {
    this.hideFlyout({ immediate: true });
    if (!info.thumbPath && !info.previewLeaf) return;
    const fly = el('div', { class: 'me-flyout' });
    fly.append(el('div', { class: 'me-flyout-title', text: info.label ?? '' }));
    const stage = el('div', { class: 'me-flyout-stage' });
    fly.append(stage);
    const meta = el('div', { class: 'me-flyout-meta muted' });
    fly.append(meta);

    const rect = cell.getBoundingClientRect();
    fly.style.left = `${Math.min(rect.right + 8, window.innerWidth - 280)}px`;
    fly.style.top = `${Math.min(rect.top, window.innerHeight - 300)}px`;
    document.body.append(fly);
    this.flyout = fly;
    motionIn(fly, { timeout: 200 });

    // Animated: fetch the composed frame list once, play it on the flyout's
    // own clock. Still: the full-size picture. Either way the canvas thread
    // never blocks — everything here is async.
    let art = null;
    if (info.previewLeaf && (info.frames ?? 1) > 1) {
      art = this.previewCache.get(info.previewLeaf);
      if (!art) {
        try {
          art = await get(`/api/mapedit/palette/preview?path=${q(info.previewLeaf)}`);
          this.previewCache.set(info.previewLeaf, art);
        } catch { art = null; }
      }
    }
    if (this.flyout !== fly) return; // pointer moved on

    if (art?.frames?.length > 1) {
      const canvas = el('canvas', { class: 'me-flyout-canvas' });
      const scale = Math.min(1, 220 / Math.max(art.w, 1), 220 / Math.max(art.h, 1));
      canvas.width = Math.max(1, Math.round(art.w * scale));
      canvas.height = Math.max(1, Math.round(art.h * scale));
      stage.append(canvas);
      meta.textContent = t('{w}×{h} · {frames} frames · {ms} ms loop',
        { w: art.w, h: art.h, frames: art.frames.length, ms: art.totalMs });

      const images = art.frames.map((f) => {
        const img = new Image();
        img.src = `/api/canvas?path=${q(f.path)}&v=me`;
        return img;
      });
      const g = canvas.getContext('2d');
      const start = performance.now();
      let raf = 0;
      const tick = () => {
        let t = (performance.now() - start) % Math.max(1, art.totalMs);
        let index = 0;
        for (let i = 0; i < art.frames.length; i++) {
          t -= art.frames[i].delay;
          if (t < 0) { index = i; break; }
        }
        const frame = art.frames[index];
        const img = images[index];
        g.clearRect(0, 0, canvas.width, canvas.height);
        if (img.complete && img.naturalWidth) {
          g.drawImage(img, frame.dx * scale, frame.dy * scale,
            frame.w * scale, frame.h * scale);
        }
        raf = requestAnimationFrame(tick);
      };
      raf = requestAnimationFrame(tick);
      fly.stop = () => cancelAnimationFrame(raf);
    } else if (info.thumbPath) {
      const img = el('img', { src: `/api/mapedit/thumb?path=${q(info.thumbPath)}`, alt: '' });
      stage.append(img);
      img.addEventListener('load', () => {
        meta.textContent = `${img.naturalWidth}×${img.naturalHeight}`;
      });
    }
  }

  /* -------- cross-kind instant search */

  async ensureAllSets() {
    if (this.allSets) return this.allSets;
    const [tile, obj, back] = await Promise.all([
      get('/api/mapedit/palette/sets?kind=Tile').catch(() => []),
      get('/api/mapedit/palette/sets?kind=Obj').catch(() => []),
      get('/api/mapedit/palette/sets?kind=Back').catch(() => []),
    ]);
    this.allSets = { Tile: tile, Obj: obj, Back: back };
    return this.allSets;
  }

  renderGlobalSearch(query) {
    const host = el('div', { class: 'me-pal-globalresults' });
    this.side.append(host);
    host.append(el('p', { class: 'muted', text: t('Searching…') }));
    const stamp = query;
    (async () => {
      const [sets, mobs, npcs, reactors] = await Promise.all([
        this.ensureAllSets(),
        get(`/api/mapedit/palette/life?q=${q(query)}&type=m&limit=12`).catch(() => null),
        get(`/api/mapedit/palette/life?q=${q(query)}&type=n&limit=12`).catch(() => null),
        get(`/api/mapedit/palette/reactors?q=${q(query)}&limit=12`).catch(() => null),
      ]);
      if (this.palette.global.trim() !== stamp || !this.ui.pal) return;
      clear(host);
      const lower = query.toLowerCase();
      let any = false;

      for (const kind of ['Tile', 'Obj', 'Back']) {
        const matches = (sets[kind] ?? []).filter((s) => s.name.toLowerCase().includes(lower)).slice(0, 12);
        if (!matches.length) continue;
        any = true;
        host.append(el('h5', { class: 'me-pal-group', text: t('{kind} sets', { kind }) }));
        const setGrid = el('div', { class: 'me-pal-setgrid' });
        for (const set of matches) {
          setGrid.append(this.setCell(set, () => {
            this.palette.global = '';
            this.palette.kind = kind;
          }));
        }
        host.append(setGrid);
      }

      for (const [label, result, type] of [['Mobs', mobs, 'm'], ['NPCs', npcs, 'n']]) {
        if (!result?.rows?.length) continue;
        any = true;
        host.append(el('h5', { class: 'me-pal-group', text: t(label) }));
        const grid = el('div', { class: 'me-pal-lifegrid' });
        for (const row of result.rows) grid.append(this.lifeCell(row, type));
        host.append(grid);
      }

      if (reactors?.rows?.length) {
        any = true;
        host.append(el('h5', { class: 'me-pal-group', text: t('Reactors') }));
        const grid = el('div', { class: 'me-pal-grid' });
        for (const row of reactors.rows) {
          grid.append(this.paletteCell({
            thumbPath: row.iconPath, label: `reactor ${row.id}`, caption: String(row.id),
            previewLeaf: row.iconPath?.replace(/\/0$/, ''),
            frames: 2, // reactor state 0 often animates; the flyout will know
            onclick: () => this.armReactor(row.id, row.iconPath),
          }));
        }
        host.append(grid);
      }

      if (!any) host.append(el('p', { class: 'muted', text: t('Nothing matches anywhere.') }));
    })();
  }

  /* -------- art palettes (Tile / Obj / Back) */

  layerPicker() {
    const mem = this.mem();
    const select = el('select', { class: 'field-input me-pal-layer' });
    for (const layer of this.doc.layers) {
      select.append(el('option', {
        value: String(layer.index),
        text: layer.ts
          ? t('Layer {n} — {ts}', { n: layer.index, ts: layer.ts })
          : t('Layer {n} — no tS', { n: layer.index }),
        selected: layer.index === mem.layer ? 'selected' : null,
      }));
    }
    select.addEventListener('change', () => { mem.layer = Number(select.value); });
    return select;
  }

  targetLayer() { return this.mem().layer ?? 0; }

  renderArtPalette() {
    const p = this.palette;
    const mem = this.mem();
    if ((p.kind === 'Tile' || p.kind === 'Obj') && this.doc.layers.length) {
      this.side.append(el('label', { class: 'me-pal-row' },
        el('span', { class: 'muted', text: t('Layer') }), this.layerPicker()));
    }
    if (p.kind === 'Tile') {
      this.side.append(el('label', { class: 'me-overlay-row' },
        el('input', {
          type: 'checkbox', checked: this.snapTiles ? 'checked' : null,
          onchange: (e) => { this.snapTiles = e.currentTarget.checked; },
        }),
        el('span', {
          text: t('Snap to tile grid'),
          'data-tip': t(
            'Snaps placement to the armed tile’s own art size, aligned to an ' +
            'existing tile of the same variant on the target layer when one exists. A placement ' +
            'aid — tiles are modular at their art size — not a measured client grid.'),
        })));
    }

    if (!mem.sets || mem.sets.kind !== p.kind) {
      mem.sets = { kind: p.kind, list: null };
      get(`/api/mapedit/palette/sets?kind=${q(p.kind)}`)
        .then((list) => { mem.sets.list = list; if (this.ui.pal) this.renderPaletteBody(); })
        .catch((err) => toastError(err, t('Palette')));
      this.side.append(el('p', { class: 'muted', text: t('Loading sets…') }));
      return;
    }
    if (!mem.sets.list) { this.side.append(el('p', { class: 'muted', text: t('Loading sets…') })); return; }

    if (!mem.set) {
      const input = el('input', {
        class: 'field-input', type: 'search', placeholder: t('Search {n} sets…', { n: mem.sets.list.length }),
        value: mem.query,
      });
      // Sets are pictures too: each cell shows the set's first drawable, so
      // browsing reads as art, not as a list of file names.
      const list = el('div', { class: 'me-pal-setgrid' });
      const renderSets = () => {
        if (!input.isConnected || !list.isConnected) return;
        clear(list);
        const query = mem.query.trim().toLowerCase();
        let shown = 0;
        for (const set of mem.sets.list) {
          if (query && !set.name.toLowerCase().includes(query)) continue;
          if (++shown > 200) {
            list.append(el('p', { class: 'muted', text: t('More — narrow the search.') }));
            break;
          }
          list.append(this.setCell(set));
        }
        if (!shown) list.append(el('p', { class: 'muted', text: t('No set matches.') }));
      };
      const refresh = debounce(renderSets, 120);
      input.addEventListener('input', () => {
        // Keep the search field mounted while its results change. Rebuilding
        // the whole palette here destroyed focus after every letter, sending
        // the next keystroke to the map editor's shortcuts instead.
        mem.query = input.value;
        refresh();
      });
      this.side.append(input, list);
      renderSets();
      return;
    }

    this.side.append(el('div', { class: 'me-pal-row' },
      el('button', { class: 'btn btn-ghost', text: t('‹ sets'), onclick: () => { mem.set = null; mem.entries = null; this.renderPaletteBody(); } }),
      el('strong', { text: mem.set.name })));

    if (!mem.entries) { this.side.append(el('p', { class: 'muted', text: t('Loading entries…') })); return; }
    if (mem.entries.truncated) {
      this.side.append(el('p', { class: 'muted', text:
        t('Showing {n} of {total} entries.',
          { n: mem.entries.entries.length, total: fmt.format(mem.entries.total) }) }));
    }

    const groups = new Map();
    for (const entry of mem.entries.entries) {
      const key = p.kind === 'Tile' ? entry.u
        : p.kind === 'Obj' ? `${entry.l0}/${entry.l1}`
        : entry.ani === 0 ? 'still' : entry.ani === 1 ? 'animated' : 'spine';
      if (!groups.has(key)) groups.set(key, []);
      groups.get(key).push(entry);
    }
    for (const [group, entries] of groups) {
      this.side.append(el('h5', { class: 'me-pal-group', text: t(group) }));
      const grid = el('div', { class: 'me-pal-grid' });
      for (const entry of entries) {
        grid.append(this.paletteCell({
          thumbPath: entry.thumbPath,
          label: this.entryTip(entry),
          frames: entry.frames,
          hasFoothold: entry.hasFoothold,
          previewLeaf: this.entryLeafPath(mem.set, entry),
          onclick: () => this.armArt(p.kind, mem.set.name, mem.set.path, entry),
        }));
      }
      this.side.append(grid);
    }
  }

  entryLeafPath(set, entry) {
    const p = this.palette;
    if (p.kind === 'Obj' || (entry.l0 != null)) {
      const segs = [entry.l0, entry.l1, entry.l2, entry.l3].filter((s) => s != null && s !== '');
      return `${set.path}/${segs.join('/')}`;
    }
    if (p.kind === 'Back' || entry.ani != null) {
      if (entry.u == null && entry.l0 == null) {
        const branch = entry.ani === 1 ? 'ani' : entry.ani === 2 ? 'spine' : 'back';
        return `${set.path}/${branch}/${entry.no}`;
      }
    }
    return `${set.path}/${entry.u}/${entry.no}`;
  }

  entryTip(entry) {
    const p = this.palette;
    const mem = this.mem();
    if (p.kind === 'Tile') return `${mem.set.name}/${entry.u}/${entry.no}${entry.hasFoothold ? t(' — carries foothold geometry') : ''}`;
    if (p.kind === 'Obj') {
      return `${mem.set.name}/${entry.l0}/${entry.l1}/${entry.l2}${entry.l3 ? `/${entry.l3}` : ''}` +
        `${entry.frames > 1 ? t(' — {n} frames', { n: entry.frames }) : ''}` +
        `${entry.hasFoothold ? t(' — carries foothold geometry') : ''}`;
    }
    return `${mem.set.name}/${entry.ani ? 'ani' : 'back'}/${entry.no}`;
  }

  openPaletteSet(set) {
    const mem = this.mem();
    mem.set = set;
    mem.entries = null;
    if (!this.ui.pal) { this.ui.pal = true; this.saveUi(); this.applyPanels(); }
    this.renderPaletteBody();
    get(`/api/mapedit/palette/entries?kind=${q(this.palette.kind)}&path=${q(set.path)}`)
      .then((entries) => { mem.entries = entries; if (this.ui.pal) this.renderPaletteBody(); })
      .catch((err) => { toastError(err, t('Palette')); mem.set = null; this.renderPaletteBody(); });
  }

  armArt(paletteKind, setName, setPath, entry, { silent } = {}) {
    if (paletteKind === 'Tile') {
      this.placing = {
        kind: 'tile', set: setName, u: entry.u, no: entry.no,
        label: `tile ${setName}/${entry.u}/${entry.no}`,
        art: entry,
        getSnap: () => {
          if (!this.snapTiles) return null;
          const layer = this.doc.layers.find((l) => l.index === this.targetLayer());
          const mag = Number(layer?.tsMag ?? 1) || 1;
          const pw = entry.w * mag; const ph = entry.h * mag;
          if (!(pw > 0) || !(ph > 0)) return null;
          const anchor = layer?.tiles.find((t) => t.u === entry.u) ?? layer?.tiles[0];
          return { pw, ph, ax: anchor?.x ?? 0, ay: anchor?.y ?? 0 };
        },
      };
    } else if (paletteKind === 'Obj') {
      this.placing = {
        kind: 'obj', set: setName, l0: entry.l0, l1: entry.l1, l2: entry.l2, l3: entry.l3,
        label: `obj ${setName}/${entry.l0}/${entry.l1}/${entry.l2}`,
        art: entry,
      };
    } else {
      this.placing = {
        kind: 'back', set: setName, no: entry.no, ani: entry.ani,
        label: `back ${setName}/${entry.no}`,
        art: entry, backType: 0, front: 0,
      };
    }
    this.pushRecent(paletteKind, {
      label: this.placing.label,
      thumb: entry.thumbPath,
      frames: entry.frames,
      previewLeaf: setPath ? this.entryLeafPath({ path: setPath, name: setName }, entry) : null,
      arm: { paletteKind, set: setName, setPath, entry },
    });
    this.view.setPlacing(this.placing);
    this.setStatus(t('Click the map to place {label}; Esc cancels. The ghost shows exactly where it lands.',
      { label: this.placing.label }));
    if (!silent && this.ui.pal) this.renderPaletteBody();
  }

  /* -------- life palette: a grid of sprites, names under them */

  lifeCell(row, type) {
    return this.paletteCell({
      thumbPath: row.iconPath,
      caption: row.name ?? String(row.id),
      label: `${row.name ?? t('(no name)')} · ${row.id}`,
      frames: 2, // stand animations play in the flyout when they exist
      previewLeaf: row.iconPath?.replace(/\/0$/, ''),
      onclick: () => this.armLife(type, String(row.id), `${type === 'n' ? t('NPC') : t('mob')} ${row.name ?? row.id}`, row.iconPath),
    });
  }

  armLife(type, id, label, iconPath, { silent } = {}) {
    this.placing = { kind: 'life', lifeType: type, id, label };
    this.pushRecent(type === 'n' ? 'npc' : 'mob', {
      label, thumb: iconPath, previewLeaf: iconPath?.replace(/\/0$/, ''), frames: 2,
      arm: { kind: 'life', lifeType: type, id },
    });
    this.view.setPlacing(this.placing);
    this.setStatus(t('Click the map to place {label} — it anchors to the foothold below the click.',
      { label }));
    if (!silent && this.ui.pal) this.renderPaletteBody();
  }

  renderLifePalette() {
    const p = this.palette;
    const mem = this.mem();
    const type = p.kind === 'npc' ? 'n' : 'm';
    const input = el('input', {
      class: 'field-input', type: 'search',
      placeholder: t('Search {kind} by name or id…', { kind: p.kind === 'npc' ? t('NPCs') : t('mobs') }), value: mem.query ?? '',
    });
    const list = el('div', { class: 'me-pal-lifegrid' });
    const load = async () => {
      mem.query = input.value;
      try {
        const result = await get(`/api/mapedit/palette/life?q=${q(input.value)}&type=${type}&limit=60`);
        clear(list);
        // Missing prerequisites are said out loud, on the palette's face —
        // a silent fallback to a bare name list is how this reads as broken.
        if (!result.namesAvailable) {
          list.append(el('div', { class: 'me-pal-warn', text: t(
            'String.wz is not open, so there are no names to search here — open the client’s ' +
            'String.wz (a copy) and this becomes a searchable picture grid.') }));
        }
        if (!result.iconsAvailable) {
          list.append(el('div', { class: 'me-pal-warn', text: t(
            '{archive} is not open, so these rows have no pictures — ' +
            'open it (a copy) to see every sprite here.', { archive: type === 'n' ? 'Npc.wz' : 'Mob.wz' }) }));
        }
        for (const row of result.rows) list.append(this.lifeCell(row, type));
        if (!result.rows.length && result.namesAvailable) {
          list.append(el('p', { class: 'muted', text: input.value.trim()
            ? t('Nothing matches.')
            : t('Type a name or id — e.g. {example} — and the matches appear as sprites.',
              { example: type === 'n' ? t('“Maple Administrator” or 9010000') : t('“snail” or 100100') }) }));
        }
      } catch (err) { toastError(err, t('Life palette')); }
    };
    input.addEventListener('input', debounce(load, 250));
    this.side.append(input, list);
    load();
  }

  armReactor(id, iconPath, { silent } = {}) {
    this.placing = { kind: 'reactor', id, label: `reactor ${id}` };
    this.pushRecent('reactor', {
      label: `reactor ${id}`, thumb: iconPath, previewLeaf: iconPath?.replace(/\/0$/, ''), frames: 2,
      arm: { kind: 'reactor', id },
    });
    this.view.setPlacing(this.placing);
    this.setStatus(t('Click the map to place reactor {id}; Esc cancels.', { id }));
    if (!silent && this.ui.pal) this.renderPaletteBody();
  }

  renderReactorPalette() {
    const grid = el('div', { class: 'me-pal-grid' });
    this.side.append(grid);
    get('/api/mapedit/palette/reactors?limit=200').then((result) => {
      if (!result.available) {
        grid.replaceWith(el('div', {},
          el('p', { class: 'muted', text: t(result.reason ?? 'Reactor.wz is not open.') }),
          (() => {
            const manual = el('input', { class: 'field-input', placeholder: t('Reactor id (typed)… Enter arms it') });
            manual.addEventListener('keydown', (e) => {
              if (e.key === 'Enter' && manual.value.trim()) this.armReactor(manual.value.trim(), null);
            });
            return manual;
          })()));
        return;
      }
      for (const row of result.rows) {
        grid.append(this.paletteCell({
          thumbPath: row.iconPath, label: `reactor ${row.id}`, caption: String(row.id), frames: 2,
          previewLeaf: row.iconPath?.replace(/\/0$/, ''),
          onclick: () => this.armReactor(row.id, row.iconPath),
        }));
      }
    }).catch((err) => toastError(err, t('Reactor palette')));
  }

  renderPortalPalette() {
    const pnInput = el('input', { class: 'field-input', placeholder: t('pn — portal name (sp/tp may repeat)') });
    const tnInput = el('input', { class: 'field-input', placeholder: t('tn — target portal name') });
    const tmInput = el('input', { class: 'field-input num', placeholder: t('tm — target map id (999999999 = none)') });
    this.side.append(
      el('p', { class: 'muted me-pal-note', text: t(
        'Pick an icon to arm placement. pt comes from the MapHelper editor table — the measured ' +
        'order, not alphabetical. Two linked portals: place one with pn=a, tn=b, then one with pn=b, tn=a.') }),
      pnInput, tnInput, tmInput);

    const grid = el('div', { class: 'me-pal-grid' });
    this.side.append(grid);
    const fill = (icons) => {
      for (const row of icons) {
        grid.append(this.paletteCell({
          thumbPath: row.path, label: `pt ${row.pt} — ${row.name}`, caption: row.name,
          onclick: () => {
            this.placing = {
              kind: 'portal', pt: row.pt,
              pn: () => pnInput.value.trim(), tn: () => tnInput.value.trim(),
              tm: () => tmInput.value.trim(),
              label: `portal ${row.name} (pt ${row.pt})`,
            };
            this.view.setPlacing(this.placing);
            this.setStatus(t('Click the map to place {label}.', { label: this.placing.label }));
            this.renderPaletteBody();
          },
        }));
      }
    };
    if (this.portalIconList) fill(this.portalIconList);
    else {
      get('/api/mapedit/portal-icons').then((icons) => {
        this.portalIconList = icons;
        fill(icons);
      }).catch(() => grid.append(el('p', { class: 'muted', text: t('MapHelper.img is not open.') })));
    }
  }

  renderFootholdTools() {
    this.side.append(el('label', { class: 'me-pal-row' },
      el('span', { class: 'muted', text: t('Layer') }), this.layerPicker()));
    this.side.append(
      el('button', { class: 'btn btn-primary me-pal-draw', onclick: () => {
        this.cancelPlacement();
        this.view.beginFootholdDraw(this.targetLayer());
        this.setStatus(t('Click to drop vertices; Enter or double-click finishes the chain; Esc cancels.'));
      } }, t('Draw foothold chain')),
      el('p', { class: 'muted me-pal-note', text: t(
        'Vertices become prev/next-linked footholds with coincident endpoints, a fresh group, and ' +
        'ids unique in this map. On the canvas: double-click a segment to insert a vertex, ' +
        'double-click a free chain end to keep drawing from it, drag vertices to reshape — ' +
        'linked endpoints move together. Existing footholds are never rewired; forks stay forks.') }));
  }

  renderLadderTools() {
    const lSelect = el('select', { class: 'field-input me-pal-layer' },
      el('option', { value: '1', text: t('Ladder (l = 1)') }),
      el('option', { value: '0', text: t('Rope (l = 0)') }));
    const uf = el('input', { type: 'checkbox', checked: 'checked' });
    this.side.append(
      el('label', { class: 'me-pal-row' },
        el('span', { class: 'muted', text: t('Kind') }), lSelect),
      el('label', { class: 'me-overlay-row' }, uf,
        el('span', { text: t('uf — climbable off the top'), 'data-tip':
          t('uf = 1 lets the character climb off at the top; 0 does not.') })),
      el('label', { class: 'me-pal-row' },
        el('span', { class: 'muted', text: t('Page (layer)') }), this.layerPicker()),
      el('button', { class: 'btn btn-primary me-pal-draw', onclick: () => {
        this.view.cancelFootholdDraw();
        this.placing = {
          kind: 'ladder',
          get l() { return Number(lSelect.value); },
          get uf() { return uf.checked ? 1 : 0; },
          label: t('ladder / rope'),
        };
        this.view.setPlacing(this.placing);
        this.setStatus(t('Click the top of the ladder, then its bottom — two clicks, one x. Esc cancels.'));
        this.renderPaletteBody();
      } }, t('Place ladder / rope')),
      el('p', { class: 'muted me-pal-note', text: t(
        'Written in the sampled shipping shape and order — l, uf, x, y1, y2, page, all Int. ' +
        'Placed ladders drag whole or by endpoint on the canvas.') }));
  }

  /* ============================================================ placement */

  syncArmedUi() {
    if (this.ui.pal) this.renderPaletteBody();
  }

  cancelPlacement() {
    if (this.placing) {
      this.placing = null;
      this.view.setPlacing(null);
      if (this.ui.pal) this.renderPaletteBody();
    }
    this.view.cancelFootholdDraw();
    this.view.cancelExtend();
    this.renderFloat();
  }

  async commitPlacement(x, y) {
    const arm = this.placing;
    if (!arm || !this.doc) return;
    const body = { path: this.doc.path, kind: arm.kind, x, y };

    if (arm.kind === 'tile' || arm.kind === 'obj') {
      body.layer = this.targetLayer();
      body.set = arm.set;
      if (arm.kind === 'tile') { body.u = arm.u; body.no = arm.no; }
      else { body.l0 = arm.l0; body.l1 = arm.l1; body.l2 = arm.l2; body.l3 = arm.l3; }

      if (arm.kind === 'tile') {
        const layer = this.doc.layers.find((l) => l.index === this.targetLayer());
        if (layer && !layer.ts) {
          const ok = await this.confirmDialog(t('Adopt this tile set?'),
            t("Layer {n} has no tile set yet. Placing this tile writes tS = '{set}' " +
              'on the layer — every future tile on it will come from that set.',
              { n: layer.index, set: arm.set }));
          if (!ok) return;
          body.adoptLayerTs = true;
        } else if (layer && layer.ts !== arm.set) {
          this.offerTsChoices(layer, arm, x, y);
          return;
        }
      }
    } else if (arm.kind === 'back') {
      body.set = arm.set; body.no = arm.no; body.ani = arm.ani;
      body.backType = arm.backType ?? 0; body.front = arm.front ?? 0;
    } else if (arm.kind === 'portal') {
      body.pn = arm.pn(); body.tn = arm.tn(); body.pt = arm.pt;
      const tm = arm.tm();
      if (tm) body.tm = Number(tm);
    } else if (arm.kind === 'life') {
      body.lifeType = arm.lifeType; body.id = arm.id;
    } else if (arm.kind === 'reactor') {
      body.id = arm.id;
    }

    try {
      const result = await post('/api/mapedit/place', body);
      for (const note of result.notes ?? []) toast(note);
      this.applyPlaceResult(result);
      await this.refetchDoc();     // structural: addresses and art may be new
      // Forgiveness: the just-placed thing is selected — a mis-drop drags
      // again immediately. The armed placement stays armed.
      if (result.placed?.length) this.selectAddrs([result.placed]);
    } catch (err) {
      toastError(err, t('Place'));
    }
  }

  /** Two-click ladder placement: the server writes the sampled shipping
   *  shape (l, uf, x, y1, y2, page) and swaps the ys if the bottom came first. */
  async commitLadder(x, y1, y2) {
    const arm = this.placing;
    if (!arm || arm.kind !== 'ladder' || !this.doc) return;
    try {
      const result = await post('/api/mapedit/place', {
        path: this.doc.path, kind: 'ladderRope',
        x, y: y1, y2, l: arm.l, uf: arm.uf, layer: this.targetLayer(),
      });
      for (const note of result.notes ?? []) toast(note);
      this.applyPlaceResult(result);
      await this.refetchDoc();
      if (result.placed?.length) this.selectAddrs([result.placed]);
    } catch (err) {
      toastError(err, t('Ladder'));
    }
  }

  applyPlaceResult(result) {
    if (this.doc) {
      this.doc.undoDepth = result.undoDepth;
      this.doc.redoDepth = result.redoDepth;
      this.doc.dirty = result.dirty;
    }
    this.renderToolbar();
    this.refreshUnsaved();
  }

  offerTsChoices(layer, arm, x, y) {
    const carriers = this.doc.layers.filter((l) => l.ts === arm.set);
    const body = el('div', {},
      el('p', { text: t(
        "Layer {n} draws its tiles from '{ts}', and a layer has exactly one tile set. " +
        "This tile is from '{set}'.", { n: layer.index, ts: layer.ts, set: arm.set }) }),
      carriers.length
        ? el('p', { text: t("Layers carrying '{set}': {list}.",
          { set: arm.set, list: carriers.map((l) => l.index).join(', ') }) })
        : el('p', { class: 'muted', text: t("No layer carries '{set}' yet — an empty layer can adopt it.",
          { set: arm.set }) }),
      el('p', { class: 'me-refusal-text', text: t(
        "Re-skinning changes ALL {n} tiles already on layer {layer} to '{set}' pictures — " +
        'same u/no, different art.',
        { n: fmt.format(layer.tiles.length), layer: layer.index, set: arm.set }) }));

    const actions = [{ label: t('Cancel') }];
    if (carriers.length) {
      actions.push({
        label: t('Place on layer {n}', { n: carriers[0].index }),
        run: () => {
          this.mem().layer = carriers[0].index;
          this.commitPlacement(x, y);
        },
      });
    }
    actions.push({
      label: t('Re-skin layer {n}', { n: layer.index }), class: 'btn-danger',
      run: async () => {
        try {
          const result = await post('/api/mapedit/set-layer-ts', {
            path: this.doc.path, layer: layer.index, ts: arm.set, confirmReskin: true,
          });
          for (const note of result.notes ?? []) toast(note);
          await this.refetchDoc();
          this.commitPlacement(x, y);
        } catch (err) { toastError(err, t('Re-skin')); }
      },
    });
    modal({ title: t('Tile set conflict'), body, width: '460px', actions });
  }

  confirmDialog(title, text) {
    return new Promise((resolve) => {
      modal({
        title,
        body: el('p', { text }),
        width: '420px',
        actions: [
          { label: t('Cancel'), run: () => resolve(false) },
          { label: t('Proceed'), class: 'btn-primary', run: () => resolve(true) },
        ],
        onOpen: (dialog) => dialog.addEventListener('close', () => resolve(false), { once: true }),
      });
    });
  }

  async commitFootholdChain(layer, points) {
    if (!this.doc || points.length < 2) return;
    try {
      const result = await post('/api/mapedit/foothold-chain', {
        path: this.doc.path, layer, points,
      });
      for (const note of result.notes ?? []) toast(note);
      await this.refetchDoc();
    } catch (err) {
      toastError(err, t('Foothold chain'));
    }
  }

  async autoFoothold() {
    const sel = this.selection[0];
    if (!sel?.entry?.addr) return;
    try {
      const result = await post('/api/mapedit/auto-foothold', {
        path: this.doc.path, addr: sel.entry.addr,
      });
      for (const note of result.notes ?? []) toast(note);
      await this.refetchDoc();
    } catch (err) {
      toastError(err, t('Auto-foothold'));
    }
  }

  /* ============================================================ minimap */

  async minimapDialog() {
    if (!this.doc) return;
    const body = el('div', {}, el('p', { class: 'muted', text:
      t('Planning… the sharer scan walks every map image in the session, so this can take a few seconds.') }));
    const handle = modal({ title: t('Regenerate minimap'), body, width: '520px', actions: [{ label: t('Close') }] });

    let plan;
    try {
      plan = await get(`/api/mapedit/minimap/plan?path=${q(this.doc.path)}`);
    } catch (err) {
      toastError(err, t('Minimap plan'));
      return;
    }

    clear(body);
    body.append(el('p', { text: t(
      'Will write: width {w}, height {h}, centerX {cx}, centerY {cy}, mag {mag}, ' +
      'canvas {cw}×{ch} px (the measured floor(dim/16)).',
      { w: fmt.format(plan.width), h: fmt.format(plan.height), cx: plan.centerX, cy: plan.centerY,
        mag: plan.mag, cw: plan.canvasW, ch: plan.canvasH }) }));
    body.append(el('p', { class: 'muted', text: t(
      'The render is the map art (tiles + objects, frame 0) at minimap scale — a faithful layout ' +
      'picture, not a pixel-copy of the client’s styling. Backgrounds are not drawn.') }));
    if (!plan.hasMiniMap) {
      body.append(el('p', { text: t('This map has no minimap yet (939 geometry maps ship without one).') }));
    }

    const detach = el('input', { type: 'checkbox' });
    const shared = el('input', { type: 'checkbox' });
    if (plan.canvasIsLink) {
      body.append(el('label', { class: 'me-minimap-confirm' }, detach,
        el('span', { text: t(
          'This minimap is a LINK to {target}. Regenerating detaches it and gives this ' +
          'map its own picture. I understand.', { target: plan.linkTarget }) })));
    }
    if (plan.sharerCount > 0) {
      body.append(el('label', { class: 'me-minimap-confirm' }, shared,
        el('span', { class: 'me-refusal-text', text: t(
          "{n} other map(s) draw THIS map's minimap through an _outlink: {list}. " +
          'Overwriting changes what every one of them shows. I understand.',
          { n: plan.sharerCount,
            list: plan.sharers.join(', ') + (plan.sharerCount > plan.sharers.length ? ' …' : '') }) })));
    }

    body.append(el('div', { class: 'me-insp-actions' },
      el('button', { class: 'btn btn-primary', onclick: async (e) => {
        try {
          busyButton(e.currentTarget, true);
          const result = await post('/api/mapedit/minimap/regenerate', {
            path: this.doc.path,
            confirmDetachLink: detach.checked,
            confirmSharedOverwrite: shared.checked,
          });
          for (const note of result.notes ?? []) toast(note);
          toast(t('Minimap regenerated: {w}×{h}. Save the map to make it real.',
            { w: result.canvasW, h: result.canvasH }));
          handle.close();
          await this.refetchDoc();
        } catch (err) {
          busyButton(e.currentTarget, false);
          toastError(err, t('Minimap'));
        }
      } }, t('Regenerate'))));
  }

  /* ============================================================ new map */

  newMapDialog() {
    const idInput = el('input', { class: 'field-input num', placeholder: t('9-digit id, e.g. 900000001') });
    const nameInput = el('input', { class: 'field-input', placeholder: t('Map name (String.wz row)') });
    const streetInput = el('input', { class: 'field-input', placeholder: t('Street name') });
    const body = el('div', { class: 'me-newmap' },
      el('p', { class: 'muted', text: t(
        'The id is collision-checked across every open Map archive. The map is scaffolded with ' +
        'the measured minimal shape (layers 0-7, the empty containers real maps ship, sane info ' +
        'defaults), saved to the archive that keeps the client’s maps, and named in String.wz.') }),
      idInput, nameInput, streetInput);

    modal({
      title: t('Create a map from scratch'),
      body,
      width: '460px',
      actions: [
        { label: t('Cancel') },
        {
          label: t('Create and open'), class: 'btn-primary',
          run: async () => {
            const id = Number(idInput.value.trim());
            try {
              const result = await post('/api/mapedit/create', {
                id, name: nameInput.value.trim(), streetName: streetInput.value.trim(),
              });
              for (const note of result.notes ?? []) toast(note);
              toast(t('Created {path} in {archive}', { path: result.path, archive: result.archive }) +
                (result.stringRowWritten
                  ? t('; String row in {region}.', { region: result.stringRegion })
                  : '.'));
              await this.openMap(result.path);
            } catch (err) {
              toastError(err, t('Create map'));
            }
          },
        },
      ],
      onOpen: () => idInput.focus(),
    });
  }

  /* ============================================================ edits */

  editOp(op) {
    return post('/api/mapedit/edit', { path: this.doc.path, op });
  }

  /** Applies the server's authoritative geometry to the local model. */
  applyMoved(result) {
    const pools = this.pools();
    for (const moved of result.moved ?? []) {
      const found = this.findByAddrIn(pools, moved.addr);
      if (!found) continue;
      const e = found.entry;
      if (found.kind === 'foothold') {
        e.x1 = moved.x1; e.y1 = moved.y1; e.x2 = moved.x2; e.y2 = moved.y2;
      } else if (found.kind === 'ladder') {
        e.x = moved.x1; e.y1 = moved.y1; e.y2 = moved.y2;
      } else if (found.kind === 'rect') {
        e.x1 = moved.x1; e.y1 = moved.y1; e.x2 = moved.x2; e.y2 = moved.y2;
      } else if (found.kind === 'life') {
        e.x = moved.x1; e.y = moved.y1; e.cy = moved.y1;
      } else {
        e.x = moved.x1; e.y = moved.y1;
      }
    }
  }

  findByAddrIn(pools, addr) {
    for (const [kind, pool] of Object.entries(pools)) {
      const entry = pool.find((e) => sameAddr(e.addr, addr));
      if (entry) return { kind, entry };
    }
    return null;
  }

  async commitMoveMany(items, dx, dy) {
    if (!this.doc || !items.length || (dx === 0 && dy === 0)) {
      this.view.refreshStatic();
      return;
    }
    try {
      const result = await this.editOp({
        kind: 'moveMany',
        dx, dy,
        items: items.map((i) => ({ addr: i.entry.addr, kind: this.opKind(i.kind) })),
      });
      this.applyMoved(result);
      for (const note of result.notes ?? []) toast(note);
      this.applyEditResult(result);
      this.view.refreshStatic();
      this.renderFloat();
      this.renderInspector();
    } catch (err) {
      toastError(err, t('Move'));
      this.refetchDoc();
    }
  }

  /** Selection kinds → server batch kinds. */
  opKind(kind) {
    if (kind === 'tile' || kind === 'obj' || kind === 'portal' || kind === 'reactor' || kind === 'back')
      return kind;
    return kind; // life, foothold, ladder, rect map 1:1
  }

  async commitFootholdMove(entry, vertex) {
    try {
      const result = await this.editOp({
        kind: 'moveFoothold', addr: entry.addr, vertex,
        x: vertex === 1 ? entry.x1 : entry.x2,
        y: vertex === 1 ? entry.y1 : entry.y2,
      });
      this.applyMoved(result);
      if ((result.moved?.length ?? 0) > 1)
        toast(t('{n} footholds share this vertex; all moved together.', { n: result.moved.length }));
      this.applyEditResult(result);
      this.view.refreshStatic();
      this.renderFloat();
      this.renderInspector();
    } catch (err) {
      toastError(err, t('Foothold'));
      this.refetchDoc();
    }
  }

  async commitLadderMove(entry) {
    try {
      const result = await this.editOp({
        kind: 'moveLadder', addr: entry.addr, x: entry.x, y1: entry.y1, y2: entry.y2,
      });
      this.applyMoved(result);
      this.applyEditResult(result);
      this.view.refreshStatic();
      this.renderFloat();
    } catch (err) {
      toastError(err, t('Ladder'));
      this.refetchDoc();
    }
  }

  async commitRectResize(entry) {
    try {
      const result = await this.editOp({
        kind: 'setRect', addr: entry.addr,
        x1: entry.x1, y1: entry.y1, x2: entry.x2, y2: entry.y2,
      });
      this.applyMoved(result);
      this.applyEditResult(result);
      this.view.refreshStatic();
      this.renderFloat();
    } catch (err) {
      toastError(err, t('Zone'));
      this.refetchDoc();
    }
  }

  async commitInsertVertex(entry, x, y) {
    try {
      const result = await this.editOp({
        kind: 'insertFootholdVertex', addr: entry.addr, x, y,
      });
      for (const note of result.notes ?? []) toast(note);
      this.applyEditResult(result, { skipRefetch: true });
      await this.refetchDoc();
      if (result.placed?.length) this.selectAddrs(result.placed);
      toast(t('Vertex inserted — drag it into place.'));
    } catch (err) {
      toastError(err, t('Insert vertex'));
    }
  }

  async commitExtendFoothold(entry, vertex, x, y) {
    try {
      const result = await this.editOp({
        kind: 'extendFoothold', addr: entry.addr, vertex, x, y,
      });
      this.applyEditResult(result, { skipRefetch: true });
      await this.refetchDoc();
      // Keep drawing: re-arm extension from the NEW segment's free end.
      const placedAddr = result.placed?.[0];
      const found = placedAddr ? this.findByAddr(placedAddr) : null;
      if (found?.kind === 'foothold') {
        this.selection = [{ kind: 'foothold', entry: found.entry }];
        this.view.select(this.selection);
        this.view.beginExtend(found.entry, vertex);
        this.renderFloat();
        this.setStatus(t('Chain extended — click to keep drawing, Enter/Esc to stop.'));
      }
    } catch (err) {
      toastError(err, t('Extend chain'));
      this.view.cancelExtend();
    }
  }

  async deleteSelection() {
    const items = this.selection.filter((s) => s.entry?.addr);
    if (!items.length) return;
    try {
      const result = await this.editOp({
        kind: 'deleteMany',
        items: items.map((s) => ({ addr: s.entry.addr, kind: s.kind })),
      });
      for (const note of result.notes ?? []) toast(note);
      toast(items.length === 1
        ? t('Deleted {kind} — Ctrl+Z puts it back.',
          { kind: t(KIND_LABEL[items[0].kind] ?? 'node') })
        : t('Deleted {n} items — one Ctrl+Z puts them all back.', { n: items.length }));
      this.selection = [];
      this.view.select([]);
      this.renderFloat();
      this.applyEditResult(result);
    } catch (err) {
      toastError(err, t('Delete'));
    }
  }

  copySelection() {
    const items = this.selection.filter((s) => s.entry?.addr && s.kind !== 'foothold');
    if (!items.length) {
      if (this.selection.length) toast(t('Footholds copy as chains — use extend/draw instead.'));
      return;
    }
    let cx = 0; let cy = 0;
    for (const s of items) {
      cx += s.entry.x ?? s.entry.x1 ?? 0;
      cy += s.entry.y ?? s.entry.y1 ?? 0;
    }
    this.clipboard = {
      items: items.map((s) => ({ addr: s.entry.addr, kind: s.kind })),
      cx: Math.round(cx / items.length),
      cy: Math.round(cy / items.length),
    };
    toast(items.length === 1
      ? t('Copied {n} item — Ctrl+V pastes at the cursor.', { n: items.length })
      : t('Copied {n} items — Ctrl+V pastes at the cursor.', { n: items.length }));
  }

  pasteClipboard() {
    if (!this.clipboard) return;
    const mouse = this.view.mouse;
    const dx = mouse ? Math.round(mouse[0]) - this.clipboard.cx : 24;
    const dy = mouse ? Math.round(mouse[1]) - this.clipboard.cy : 24;
    this.duplicateItems(this.clipboard.items, dx, dy);
  }

  duplicateSelection(dx, dy) {
    const items = this.selection
      .filter((s) => s.entry?.addr)
      .map((s) => ({ addr: s.entry.addr, kind: s.kind }));
    if (!items.length) return;
    this.duplicateItems(items, dx, dy);
  }

  async duplicateItems(items, dx, dy) {
    try {
      const result = await this.editOp({ kind: 'duplicate', items, dx, dy });
      for (const note of result.notes ?? []) toast(note);
      this.applyEditResult(result, { skipRefetch: true });
      await this.refetchDoc();
      if (result.placed?.length) {
        this.selectAddrs(result.placed);
        toast(result.placed.length === 1
          ? t('{n} duplicate selected — drag into place.', { n: result.placed.length })
          : t('{n} duplicates selected — drag into place.', { n: result.placed.length }));
      }
    } catch (err) {
      toastError(err, t('Duplicate'));
    }
  }

  async commitSetField(addr, name, value) {
    try {
      const result = await this.editOp({ kind: 'setField', addr, name, value: String(value) });
      this.applyEditResult(result);
      if (!result.structural) {
        // Update the local copy so the panel and canvas agree immediately.
        const found = this.findByAddr(addr);
        if (found && name in found.entry) {
          const n = Number(value);
          found.entry[name] = Number.isNaN(n) ? value : n;
        }
        this.view.refreshStatic();
        this.renderFloat();
      }
      return true;
    } catch (err) {
      toastError(err, t('Edit'));
      return false;
    }
  }

  async setNodeValue(addr, value) {
    const result = await this.editOp({ kind: 'setValue', addr, value });
    this.applyEditResult(result, { refetchAlways: true });
  }

  applyEditResult(result, { refetchAlways = false, skipRefetch = false } = {}) {
    if (this.doc) {
      this.doc.undoDepth = result.undoDepth;
      this.doc.redoDepth = result.redoDepth;
      this.doc.dirty = result.dirty;
    }
    this.renderToolbar();
    this.refreshUnsaved();
    if (!skipRefetch && (result.structural || refetchAlways)) this.refetchDoc();
  }

  async undo(redo) {
    if (!this.doc) return;
    try {
      const result = await post(`/api/mapedit/${redo ? 'redo' : 'undo'}`, { path: this.doc.path });
      if (result.applied) {
        toast(redo
          ? t('Redid: {what}', { what: result.applied })
          : t('Undid: {what}', { what: result.applied }));
      }
      await this.refetchDoc();
    } catch (err) {
      toastError(err, redo ? t('Redo') : t('Undo'));
    }
  }

  /* ==================================================== tile variant picker */

  async tileVariants(sel) {
    const layer = this.doc.layers.find((l) => l.index === sel.item?.layer)
      ?? this.doc.layers.find((l) => l.tiles.includes(sel.entry));
    const ts = layer?.ts;
    if (!ts) return null;
    let entries = this.tileVariantCache.get(ts);
    if (!entries) {
      const sets = await this.ensureAllSets();
      const set = (sets.Tile ?? []).find((s) => s.name.toLowerCase() === ts.toLowerCase());
      if (!set) return null;
      const result = await get(`/api/mapedit/palette/entries?kind=Tile&path=${q(set.path)}`);
      entries = result.entries;
      this.tileVariantCache.set(ts, entries);
    }
    return entries.filter((e) => e.u === sel.entry.u);
  }

  async openTilePicker(sel, sx, sy) {
    this.closePicker();
    let variants;
    try {
      variants = await this.tileVariants(sel);
    } catch (err) { toastError(err, t('Tile variants')); return; }
    if (!variants?.length) { toast(t('No variant list resolves for this tile’s set.')); return; }

    const pop = this.picker;
    clear(pop);
    pop.append(el('h5', { text: t('{u} — pick a variant (or [ ] keys)', { u: sel.entry.u }) }));
    const grid = el('div', { class: 'me-pal-grid' });
    for (const v of variants) {
      const cell = el('button', {
        class: `me-pal-cell${Number(v.no) === Number(sel.entry.no) ? ' me-pal-current' : ''}`,
        'data-tip': `no ${v.no}`,
        onclick: async () => {
          this.closePicker();
          if (Number(v.no) !== Number(sel.entry.no))
            await this.commitSetField(sel.entry.addr, 'no', v.no);
        },
      });
      if (v.thumbPath) cell.append(el('img', { src: `/api/mapedit/thumb?path=${q(v.thumbPath)}`, loading: 'lazy', alt: '' }));
      grid.append(cell);
    }
    pop.append(grid);

    const wrap = this.canvasWrap.getBoundingClientRect();
    pop.style.left = `${Math.min(sx, wrap.width - 280)}px`;
    pop.style.top = `${Math.min(sy + 12, wrap.height - 240)}px`;
    motionIn(pop, { timeout: 220 });
  }

  closePicker() {
    if (this.picker.hidden) return;
    motionOut(this.picker, { hide: true, timeout: 160 }).then((closed) => {
      if (closed && this.picker.hidden) clear(this.picker);
    });
  }

  async cycleTileVariant(step) {
    const sel = this.single('tile');
    if (!sel) return;
    let variants;
    try { variants = await this.tileVariants(sel); } catch { return; }
    if (!variants?.length) return;
    const index = variants.findIndex((v) => Number(v.no) === Number(sel.entry.no));
    const next = variants[(index + step + variants.length) % variants.length];
    if (next && Number(next.no) !== Number(sel.entry.no)) {
      await this.commitSetField(sel.entry.addr, 'no', next.no);
    }
  }

  /* ==================================================== floating panel */

  positionFloat() {
    if (this.float.hidden) return;
    const wrap = this.canvasWrap.getBoundingClientRect();
    const width = this.float.offsetWidth || 260;
    const height = this.float.offsetHeight || 40;
    if (this.floatManual) {
      // Dragged by hand: it stays where it was put, clamped on screen.
      this.float.style.left = `${Math.max(6, Math.min(this.floatManual.x, wrap.width - width - 6))}px`;
      this.float.style.top = `${Math.max(6, Math.min(this.floatManual.y, wrap.height - height - 6))}px`;
      return;
    }
    const box = this.view.selectionScreenBox();
    if (!box) { this.float.hidden = true; return; }
    let x = (box.x0 + box.x1) / 2 - width / 2;
    let y = box.y0 - height - 10;
    if (y < 6) y = Math.min(box.y1 + 10, wrap.height - height - 6);
    x = Math.max(6, Math.min(x, wrap.width - width - 6));
    this.float.style.left = `${x}px`;
    this.float.style.top = `${y}px`;
  }

  /** The property panel drags by its title bar — put it where it does not
   *  cover the work and it stays there for this selection. */
  makeFloatDraggable(title) {
    title.classList.add('me-float-grab');
    title.addEventListener('pointerdown', (e) => {
      e.preventDefault();
      const startX = e.clientX; const startY = e.clientY;
      const baseX = this.float.offsetLeft; const baseY = this.float.offsetTop;
      title.setPointerCapture(e.pointerId);
      const move = (ev) => {
        this.floatManual = { x: baseX + (ev.clientX - startX), y: baseY + (ev.clientY - startY) };
        this.positionFloat();
      };
      const up = () => {
        title.removeEventListener('pointermove', move);
        title.removeEventListener('pointerup', up);
      };
      title.addEventListener('pointermove', move);
      title.addEventListener('pointerup', up);
    });
  }

  renderFloat() {
    const panel = this.float;
    clear(panel);
    const sel = this.selection;
    // Authoring modes own the canvas — the panel must never sit between the
    // cursor and the next click of a chain draw or an armed placement.
    const authoring = this.placing || this.view?.fhDraw || this.view?.fhExtend;
    if (!sel.length || !this.doc || authoring) { panel.hidden = true; return; }
    panel.hidden = false;

    if (sel.length > 1) {
      const byKind = new Map();
      for (const s of sel) byKind.set(s.kind, (byKind.get(s.kind) ?? 0) + 1);
      const multiTitle = el('div', { class: 'me-float-title', text:
        t('{n} selected — {kinds}',
          { n: sel.length,
            kinds: [...byKind].map(([k, n]) => t('{n} {label}',
              { n, label: t(KIND_LABEL[k] ?? k) })).join('、') }) });
      this.makeFloatDraggable(multiTitle);
      panel.append(multiTitle);
      panel.append(el('div', { class: 'me-float-actions' },
        el('button', { class: 'btn btn-ghost', text: t('Duplicate'), 'data-tip': 'Ctrl+D',
          onclick: () => this.duplicateSelection(24, 0) }),
        el('button', { class: 'btn btn-danger-ghost', text: t('Delete'), 'data-tip': t('One undo entry'),
          onclick: () => this.deleteSelection() })));
      this.positionFloat();
      return;
    }

    const s = sel[0];
    const e = s.entry;
    const title = {
      tile: () => `${t('Tile')} ${e.u}/${e.no}`,
      obj: () => `${t('Obj')} ${e.l0 ?? ''}/${e.l1 ?? ''}/${e.l2 ?? ''}`,
      foothold: () => `${t('Foothold')} ${e.id} · prev ${e.prev} next ${e.next}`,
      ladder: () => (e.l === 1 ? t('Ladder') : t('Rope')),
      portal: () => `${t('Portal')} ${e.pn || t('(unnamed)')}`,
      life: () => `${(e.type || '').toLowerCase() === 'n' ? 'NPC' : 'mob'} ${e.name ?? e.id}`,
      reactor: () => `${t('Reactor')} ${e.id ?? ''}`,
      rect: () => `${e.kind}/${e.name}`,
      back: () => `${t('Back')} ${e.no}`,
    }[s.kind]?.() ?? s.kind;
    const titleBar = el('div', { class: 'me-float-title', text: title });
    this.makeFloatDraggable(titleBar);
    panel.append(titleBar);

    const fields = el('div', { class: 'me-float-fields' });
    panel.append(fields);

    const num = (label, value, commit, tip) => {
      const input = el('input', { class: 'me-float-num num', value: String(value), 'data-tip': tip });
      const fire = async () => {
        const n = Number(input.value.trim());
        if (Number.isNaN(n) || n === Number(value)) { input.value = String(value); return; }
        await commit(n);
      };
      input.addEventListener('keydown', (ev) => { if (ev.key === 'Enter') input.blur(); ev.stopPropagation(); });
      input.addEventListener('blur', fire);
      fields.append(el('label', { class: 'me-float-field' },
        el('span', { text: label }), input));
    };
    const text = (label, value, name) => {
      const input = el('input', { class: 'me-float-num', value: value ?? '' });
      input.addEventListener('keydown', (ev) => { if (ev.key === 'Enter') input.blur(); ev.stopPropagation(); });
      input.addEventListener('blur', async () => {
        if (input.value !== (value ?? '')) await this.commitSetField(e.addr, name, input.value);
      });
      fields.append(el('label', { class: 'me-float-field' },
        el('span', { text: label }), input));
    };
    const field = (label, name, tip) => num(label, e[name] ?? e[name.toLowerCase()] ?? 0,
      (n) => this.commitSetField(e.addr, name, n), tip);
    const flip = () => {
      const box = el('input', { type: 'checkbox', checked: Number(e.f ?? 0) === 1 ? 'checked' : null });
      box.addEventListener('change', () => this.commitSetField(e.addr, 'f', box.checked ? '1' : '0'));
      fields.append(el('label', { class: 'me-float-field me-float-check' },
        el('span', { text: t('flip') }), box));
    };
    const moveXY = () => {
      num('x', e.x, (n) => this.commitMoveMany([{ kind: s.kind, entry: e }], n - e.x, 0));
      num('y', e.y, (n) => this.commitMoveMany([{ kind: s.kind, entry: e }], 0, n - e.y));
    };

    if (s.kind === 'tile') {
      moveXY();
      field('zM', 'zM');
      fields.append(el('button', { class: 'btn btn-ghost me-float-btn', text: t('variant…'),
        'data-tip': t('Swap which picture this tile shows — same u, different no. Also: double-click the tile, or [ ] keys.'),
        onclick: () => {
          const box = this.view.selectionScreenBox();
          this.openTilePicker(s, box ? (box.x0 + box.x1) / 2 : 40, box ? box.y1 : 40);
        } }));
    } else if (s.kind === 'obj') {
      moveXY();
      field('z', 'z');
      field('zM', 'zM');
      flip();
    } else if (s.kind === 'foothold') {
      num('x1', e.x1, async (n) => { e.x1 = n; await this.commitFootholdMove(e, 1); });
      num('y1', e.y1, async (n) => { e.y1 = n; await this.commitFootholdMove(e, 1); });
      num('x2', e.x2, async (n) => { e.x2 = n; await this.commitFootholdMove(e, 2); });
      num('y2', e.y2, async (n) => { e.y2 = n; await this.commitFootholdMove(e, 2); });
    } else if (s.kind === 'ladder') {
      num('x', e.x, async (n) => { e.x = n; await this.commitLadderMove(e); });
      num('y1', e.y1, async (n) => { e.y1 = n; await this.commitLadderMove(e); });
      num('y2', e.y2, async (n) => { e.y2 = n; await this.commitLadderMove(e); });
      field('l', 'l', t('1 ladder, 0 rope'));
      field('uf', 'uf', t('1 = climbable off the top'));
    } else if (s.kind === 'portal') {
      moveXY();
      text('pn', e.pn, 'pn');
      text('tn', e.tn, 'tn');
      field('tm', 'tm', t('999999999 = no target map'));
      field('pt', 'pt');
    } else if (s.kind === 'life') {
      num('x', e.x, (n) => this.commitMoveMany([{ kind: 'life', entry: e }], n - e.x, 0));
      num('cy', e.cy || e.y, (n) => this.commitMoveMany([{ kind: 'life', entry: e }], 0, n - (e.cy || e.y)),
        t('Moves re-anchor: the spawn lands ON the foothold, y/cy kept paired.'));
      field('mobTime', 'mobTime', t('-1 = never respawns'));
      field('hide', 'hide');
      flip();
    } else if (s.kind === 'reactor') {
      moveXY();
      text('id', e.id, 'id');
      text('name', e.reactorName, 'name');
      flip();
    } else if (s.kind === 'rect') {
      num('x1', e.x1, async (n) => { e.x1 = n; await this.commitRectResize(e); });
      num('y1', e.y1, async (n) => { e.y1 = n; await this.commitRectResize(e); });
      num('x2', e.x2, async (n) => { e.x2 = n; await this.commitRectResize(e); });
      num('y2', e.y2, async (n) => { e.y2 = n; await this.commitRectResize(e); });
    } else if (s.kind === 'back') {
      moveXY();
      field('rx', 'rx', t('Parallax; on types 4-7, scroll speed'));
      field('ry', 'ry');
      field('type', 'type', t('0-7: still, tiled, scrolling'));
      field('front', 'front');
      field('a', 'a', t('Opacity 0-255'));
      flip();
    }

    panel.append(el('div', { class: 'me-float-actions' },
      el('button', { class: 'btn btn-ghost', text: t('Duplicate'), 'data-tip': 'Ctrl+D',
        onclick: () => this.duplicateSelection(24, 0) }),
      el('button', { class: 'btn btn-danger-ghost', text: t('Delete'), 'data-tip': t('Delete key — undoable'),
        onclick: () => this.deleteSelection() })));

    this.positionFloat();
  }

  /* ============================================================ save */

  save() {
    const d = this.doc;
    if (!d) return;
    const body = el('div', {},
      el('p', { text: t('{name} will be rebuilt from the model and written into its ' +
        'archive. The write is then read back from the saved file and byte-compared against ' +
        'the model — the same check the round-trip harness runs.', { name: d.imageName }) }),
      el('p', { class: 'muted', text: t(
        'Carried through untouched: {n} unmodelled top-level nodes ({list}).',
        { n: d.unmodelledCount,
          list: d.unmodelledNames.length ? d.unmodelledNames.join(', ') : '' }) }),
      el('p', { class: 'muted', text: t('Undo history survives the save: the saved file becomes ' +
        'the new clean baseline, and undoing past it simply makes the map dirty again.') }));

    modal({
      title: t('Save this map?'),
      body,
      width: '480px',
      actions: [
        { label: t('Cancel') },
        {
          label: t('Save and verify'), class: 'btn-primary',
          run: () => { this.runSave(); },
        },
      ],
    });
  }

  async runSave() {
    try {
      const result = await post('/api/mapedit/save', { path: this.doc.path });
      await this.refetchDoc();
      const lines = [
        el('p', {}, result.verified
          ? el('strong', { text: t('Verified: the saved image’s bytes are exactly the model’s bytes.') })
          : el('strong', { class: 'me-refusal-text', text: t('NOT VERIFIED — the saved image differs from the model:') })),
        ...(result.differences ?? []).map((line) => el('p', { class: 'muted', text: line })),
        el('p', { class: 'muted', text: t('Written to {path} ({bytes} bytes, {s}s).',
          { path: result.savedTo, bytes: fmt.format(result.archiveBytes), s: result.seconds.toFixed(1) }) }),
        result.backupPath ? el('p', { class: 'muted', text: t('Backup: {path}', { path: result.backupPath }) }) : null,
        el('p', { class: 'muted', text:
          t('Unmodelled nodes carried: {n}.', { n: result.unmodelledCarried }) }),
        result.historyKept
          ? null
          : el('p', { class: 'me-refusal-text', text:
              result.historyNote ?? t('Undo history was cleared by this save.') }),
      ];
      modal({ title: result.verified ? t('Map saved') : t('Saved, but verification failed'),
        body: el('div', {}, ...lines), width: '520px', actions: [{ label: t('Close') }] });
    } catch (err) {
      toastError(err, t('Save map'));
    }
  }

  /* ============================================================ inspector */

  renderInspector() {
    const sel = this.selection;
    const show = Boolean(this.ui.inspector && this.doc && sel.length);
    this.setPanelVisible(this.inspector, show, { enter: 240 });
    if (!show) return;
    clear(this.inspector);

    if (sel.length > 1) {
      this.inspector.append(el('h4', { text: t('{n} items selected', { n: sel.length }) }));
      this.inspector.append(el('p', { class: 'muted', text: t(
        'They drag together, arrows nudge them, Delete removes them — each gesture one undo entry. ' +
        'The raw inspector shows a single selection.') }));
      return;
    }

    const one = sel[0];
    const entry = one.entry;
    this.inspector.append(el('h4', {},
      el('span', { text: t(KIND_LABEL[one.kind] ?? one.kind) }),
      el('span', { class: 'muted num', text: ` @ ${entry.addr.join('/')}` })));

    if (one.kind === 'life' && entry.name)
      this.inspector.append(el('p', { class: 'me-insp-name', text: entry.name }));
    if (one.kind === 'foothold')
      this.inspector.append(el('p', { class: 'muted', text: t(
        'id {id} · layer {layer} · group {group} · prev {prev} · next {next} — ' +
        'prev/next are never rewritten; forks are legal and preserved. ' +
        'Double-click the line to insert a vertex; double-click a free end to extend the chain.',
        { id: entry.id, layer: entry.layer, group: entry.group, prev: entry.prev, next: entry.next }) }));
    if (one.kind === 'tile' || one.kind === 'obj') {
      this.inspector.append(el('button', {
        class: 'btn me-auto-fh',
        'data-tip': t('Reads the art’s own foothold Convex (Tile/<set>/<u>/<no>/foothold; obj ' +
          'frames carry them too) and writes a linked chain at this placement. Refused when the ' +
          'art carries none — nothing is invented.'),
        onclick: () => this.autoFoothold(),
      }, t('Generate footholds from art')));
    }

    const table = el('div', { class: 'me-node-table' });
    this.inspector.append(table);
    this.loadNode(table, entry.addr);
  }

  async loadNode(table, addr) {
    let node;
    try {
      node = await get(`/api/mapedit/node?path=${q(this.doc.path)}&addr=${addr.join(',')}`);
    } catch (err) {
      table.append(el('p', { class: 'muted', text: err.message }));
      return;
    }
    clear(table);

    for (const child of node.children) {
      const editable = ['Short', 'Int', 'Long', 'Float', 'Double', 'String', 'UOL']
        .includes(child.type);
      const row = el('div', { class: 'me-node-row' },
        nameLabel(child.name),
        el('span', { class: 'me-node-type muted', text: child.type }));

      if (editable) {
        const input = el('input', {
          class: 'field-input me-node-value', value: child.value ?? '',
          'data-tip': t('Stored as {type}; the type is preserved on write.', { type: child.type }),
        });
        const commit = async () => {
          if (input.value === (child.value ?? '')) return;
          try {
            await this.setNodeValue(child.addr, input.value);
            child.value = input.value;
            toast(`${child.name} = ${input.value}`);
          } catch (err) {
            input.value = child.value ?? '';
            toastError(err, t('Edit value'));
          }
        };
        input.addEventListener('keydown', (e) => { if (e.key === 'Enter') input.blur(); });
        input.addEventListener('blur', commit);
        row.append(input);
      } else if (child.hasChildren) {
        row.append(el('button', {
          class: 'btn btn-ghost me-node-open', text: `${child.childCount} ▸`,
          onclick: () => this.loadNode(table, child.addr),
        }));
      } else {
        row.append(el('span', { class: 'me-node-value muted', text: child.value ?? '—' }));
      }
      table.append(row);
    }

    if (addr.length >= 2) {
      table.append(el('div', { class: 'me-insp-actions' },
        el('button', { class: 'btn btn-danger-ghost', onclick: () => this.deleteSelection() },
          icon('trash', { size: 14 }), t('Delete (undoable)'))));
    }
  }
}

/**
 * A node name, shown faithfully. A trailing space is a DIFFERENT KEY from its
 * trimmed namesake — rendered visibly instead of being trimmed by HTML
 * collapsing.
 */
function nameLabel(name) {
  const match = /^(.*?)( +)$/.exec(name);
  const label = el('span', { class: 'me-node-name' });
  if (!match) {
    label.textContent = name;
    return label;
  }
  label.append(
    el('span', { text: match[1] }),
    el('span', {
      class: 'me-trailing-space',
      text: '·'.repeat(match[2].length),
      'data-tip': t(
        match[2].length === 1
          ? 'This key ends with {n} space — a distinct key from the trimmed one, preserved exactly.'
          : 'This key ends with {n} spaces — a distinct key from the trimmed one, preserved exactly.',
        { n: match[2].length }),
    }));
  return label;
}
