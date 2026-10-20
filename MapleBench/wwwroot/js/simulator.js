/**
 * Simulator section: answer "could a character actually cross this map?"
 *
 * This is deliberately NOT part of the map editor. The editor owns editing:
 * documents, dirty state, undo. The simulator owns one read-only question, and
 * it must work on a map the user has never opened in an editing session — which
 * is exactly the situation when an agent has just generated a map and nobody
 * knows whether a character can walk it.
 *
 * The editor cannot answer that question even when it tries: a severed chain
 * and a complete one look identical on screen. /validate checks the prev/next
 * links, which is necessary and not sufficient. This walks the chain and says
 * where it stops.
 *
 * Every claim on screen is the server's own words. A walk that ends early is
 * reported as inconclusive rather than as a broken map, because multi-level maps
 * legitimately connect by ladders and portals that this does not model.
 */

import { el, clear, toastError } from './ui.js';
// emptyState lives in inspector.js, not ui.js -- importing it from ui.js is a
// hard module error, and it only shows up when this section is first opened.
import { emptyState } from './inspector.js';
import { icon } from './icons.js';
import { WalkViewer } from './walkview.js';
import { LiveWalk } from './livewalk.js';

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
  return response.json();
}
const get = (url) => call('GET', url);
const post = (url, body) => call('POST', url, body ?? {});

export class SimulatorSection {
  constructor({ host, app }) {
    this.host = host;
    this.app = app;
    this.root = el('div', { class: 'sim' });
    host.appendChild(this.root);
    this.maps = [];
    this.selected = null;
    this.result = null;
    this.filter = '';
    this.loading = false;
    // 1 walks left-to-right, -1 the other way. The client supports both and the
    // two are not symmetric — a chain that runs out on one side can continue on
    // the other — so the control is on the page rather than implied.
    this.direction = 1;

    // The walk, drawn on the map. The verdict below is a sentence; this is the
    // picture that makes it checkable. Four bugs in a row passed every automated
    // check while the character stood still, because no check could see the
    // difference between walking and not walking.
    this.viewer = new WalkViewer();
    // The live body. It owns the arrow keys and asks the server for one frame
    // per tick; the viewer only draws. Without it this page could only play
    // back a precomputed walk, which is not a simulator.
    this.live = new LiveWalk(this.viewer);

    // The map picker is an overlay inside the simulator, not a column beside
    // it. It is closed on open: the canvas is the thing this section is for,
    // and a 288px list of 5,366 paths beside it is a permanent cost for a
    // control that is used once. Opened on demand from the header.
    this.pickerOpen = false;

    /** Where "exit" goes. Null means the Editors list. */
    this.onExit = null;

  }

  /**
   * Take the window: no command bar, no rail, no file tree, no inspector.
   *
   * The flag lives on <body> because two of the four things being removed are
   * not inside #workspace (app.css). It is set on open and cleared by exit(),
   * never by a timer or a scroll: if the user is looking at a full-screen
   * simulator with no way back, that is a dead end, and an exit path that
   * depends on the flag being cleaned up somewhere else is not one.
   */
  enterFocus() {
    document.body.dataset.focus = 'simulator';

    // Escape leaves. A full-screen view with one button as the only exit is
    // not full-screen, it is a modal with extra steps. Bound here and removed
    // in exitFocus() so two visits cannot leave two listeners, and bound on
    // the document because the focus may be anywhere inside the section --
    // the canvas, the picker's search box, the button itself.
    //
    // capture:true, so it runs before the section's own key handling and the
    // arrow keys / Space that drive the character still work when focused.
    if (!this._esc) {
      this._esc = (e) => {
        if (e.key === 'Escape') { e.preventDefault(); this.exit(); }
      };
      document.addEventListener('keydown', this._esc, true);
    }
  }

  /** Give the window back to the editor. Safe to call twice. */
  exitFocus() {
    delete document.body.dataset.focus;
    this.pickerOpen = false;
    if (this._esc) {
      document.removeEventListener('keydown', this._esc, true);
      this._esc = null;
    }
    // The canvas was sized against the pre-collapse width. Without this the
    // stretched-back editor shows the map at the old scale until something
    // else forces a resize.
    requestAnimationFrame(() => this.viewer.draw?.());
  }

  /** Leave the simulator and go back to the editor. */
  exit() {
    this.live.stop?.();
    this.exitFocus();
    const go = this.onExit;
    this.onExit = null;
    if (go) go();
    else if (this.app?.sections) this.app.sections.open('mapedit').catch(() => {});
  }

  async open() {
    this.enterFocus();
    this.render();
    await this.refresh();
    await this.restoreLastMap();
  }

  /**
   * Reload whatever map was last walked here, without being asked.
   *
   * Opening the simulator onto an empty canvas and then making the user pick
   * from a 5,366-entry list every single time is a step that buys nothing: the
   * picker is one button away for anyone who wants a different map, but a
   * blank canvas is what you get if you do not touch it. The same session had
   * to re-pick 000000002.img after every reload, which is the friction this
   * removes.
   *
   * Remembered per archive path, not globally: the simulator is opened from a
   * file that is already open, so the remembered map has to belong to the same
   * archive or it silently walks the wrong one.
   */
  async restoreLastMap() {
    if (this.selected || this.loading) return;
    const key = this.mapMemoryKey();
    if (!key) return;

    let path;
    try {
      path = localStorage.getItem(key);
    } catch {
      // Private browsing / storage disabled. A simulator that cannot remember
      // is still a working simulator, so this is not worth failing over.
      return;
    }
    if (!path) return;

    const row = this.maps.find((m) => m.path === path);
    // Not re-announced when it is gone: the archive may have been swapped under
    // us, and a remembered path that no longer resolves should fall back to the
    // picker quietly rather than nag on every open.
    if (row) await this.walk(row);
  }

  /** localStorage key scoped to the archive currently open. */
  mapMemoryKey() {
    // app.currentPath holds the open archive's file id (e.g. "f16"), which is
    // what the simulator's map paths are prefixed with, so it is also what
    // scopes a remembered map to the archive it belongs to.
    const id = this.app?.currentPath ?? null;
    return id ? `maplebench.simulator.lastMap.${id}` : null;
  }

  async refresh() {
    this.loading = true;
    this.render();
    try {
      const caps = await get('/api/simulate/capabilities');
      this.caps = caps;
      if (caps.available) {
        // The search box filters this array in the browser, so whatever is not
        // fetched here is unreachable from the screen -- silently, with no
        // error. It used to fetch 2,000 of 5,366 and the remaining 3,366 could
        // not be picked even by exact id.
        const data = await get('/api/simulate/maps?limit=100000');
        this.maps = data.maps ?? [];
        // Loaded vs total is reported rather than inferred: "the picker is
        // short" and "the archive is short" look identical from the outside,
        // and the first one is a bug worth seeing.
        this.mapsLoaded = this.maps.length;
        this.mapsTotal = data.total ?? caps.mapCount ?? this.maps.length;
      } else {
        this.maps = [];
        this.mapsLoaded = 0;
        this.mapsTotal = 0;
      }
    } catch (err) {
      this.error = err.message;
    } finally {
      this.loading = false;
      this.render();
    }
  }

  /** Switch walking direction, re-running the current map so the change is visible. */
  async setDirection(dir) {
    if (this.direction === dir) return;
    this.direction = dir;
    this.render();
    if (this.selected) await this.walk(this.selected);
  }

  matches(map) {
    if (!this.filter) return true;
    const q = this.filter.toLowerCase();
    if (map.path.toLowerCase().includes(q)) return true;
    if ((map.name ?? '').toLowerCase().includes(q)) return true;
    if (String(map.id ?? '').includes(this.filter.trim())) return true;
    return false;
  }

  async walk(map) {
    this.selected = map;
    this.result = null;
    this.advice = null;
    this.replayError = null;
    this.render();

    // Remembered here rather than in the picker's onchange, because setDirection
    // re-walks the same map and this is the one place that means "this map is
    // the one being shown".
    const key = this.mapMemoryKey();
    if (key) {
      try {
        localStorage.setItem(key, map.path);
      } catch {
        // Same reasoning as restoreLastMap: not being able to remember is not a
        // reason the walk should not happen.
      }
    }

    try {
      // The CHARACTER, not the chain. The chain walk answers "are the links
      // intact"; this answers "can a character actually cross it", and the
      // second is the question that matters — /validate already answers the
      // first.
      this.result = await post('/api/simulate/character', {
        path: map.path,
        direction: this.direction,
      });

      // When the walk stops, ask what to change. "Blocked" is a fact an agent
      // cannot act on; "extend foothold 38 to x=-1250" is a task.
      if (this.result.fellOut) {
        try {
          this.advice = await get(
            `/api/simulate/advice?path=${encodeURIComponent(map.path)}`
            + `&x=${this.result.finalX}&y=${this.result.finalY}&dir=${this.direction}`);
        } catch { /* advice is a bonus, not the verdict */ }
      } else {
        this.advice = null;
      }

      // Now draw it. The verdict above is what the walk concluded; this is what
      // the walk did, frame by frame, on the ground it did it over. A mismatch
      // between the two is the exact failure this project kept shipping.
      try {
        await this.viewer.load(map.path);

        // The batch walk answers a different question -- where an unattended
        // walk ends -- and it is what the advice above is built on. It is also
        // a still frame, and the live body below is not, so the body wins the
        // viewer: loadWalk runs FIRST here and start() stands it down
        // (WalkViewer.releaseWalk) before the first 60Hz tick. Order alone is
        // not the whole fix -- if /trace throws, the body still has to start,
        // because the character is the point of the section and the replay is
        // not. So the replay is optional here and its failure is reported, not
        // swallowed, and it is stopped from ever loading after the body runs.
        try {
          await this.viewer.loadWalk({
            x: this.result.startX ?? 0,
            y: this.result.startY ?? 0,
            dir: this.direction,
            climb: -1,
            steps: Math.min(300, Math.max(30, Number(this.result.ticks) || 300)),
          });
        } catch (err) {
          // Stands down rather than retries: past this point the body owns the
          // viewer and a late replay would draw over the character.
          this.viewer.releaseWalk();
          this.replayError = err.message;
        }

        // Put a live character on it, at the spawn the CLIENT would use
        // (no coordinates passed: the server takes the pt==0 portal). From here
        // the arrow keys drive a body that keeps its speed between frames, which
        // is what a trace viewer cannot do.
        await this.live.start({ path: map.path, direction: this.direction });
        this.viewError = '';
      } catch (err) {
        this.viewError = err.message;
      }
    } catch (err) {
      this.error = err.message;
      toastError(err);
    }
    this.render();
  }

  // ---------------------------------------------------------------- render

  render() {
    clear(this.root);

    this.root.appendChild(el('header', { class: 'sim-head' }, [
      el('div', { class: 'sim-head-main' }, [
        el('h2', {}, ['Simulator']),
        el('p', { class: 'sim-blurb' }, [
          'Walk a map\'s foothold chain and see whether a character can cross it. ',
          'Nothing here writes — this surface never saves, edits or marks a map dirty.',
        ]),
      ]),
      el('div', { class: 'sim-dir' }, [
        el('button', {
          class: `sim-dir-btn${this.direction > 0 ? ' on' : ''}`,
          title: 'Walk from the left edge to the right',
          onclick: () => this.setDirection(1),
        }, ['→']),
        el('button', {
          class: `sim-dir-btn${this.direction < 0 ? ' on' : ''}`,
          title: 'Walk from the right edge to the left',
          onclick: () => this.setDirection(-1),
        }, ['←']),
      ]),
      // The picker is an overlay now, so it gets a button that says so. It
      // toggles rather than opens: once the map is on screen the list is
      // covering it, and a control that can only close would be a trap.
      el('button', {
        class: `btn ghost sim-map-btn${this.pickerOpen ? ' on' : ''}`,
        title: this.pickerOpen ? 'Close the map list' : 'Choose a map',
        'aria-expanded': this.pickerOpen ? 'true' : 'false',
        onclick: () => { this.pickerOpen = !this.pickerOpen; this.renderBodyInto(); },
      }, [icon('folderOpen', { size: 15 }), this.pickerOpen ? 'Close map list' : 'Maps']),
      el('button', {
        class: 'btn ghost', onclick: () => this.refresh(), title: 'Reload',
      }, [icon('refresh'), 'Refresh']),
      // The way back. Prominent and last in reading order, which is where a
      // person looks for it after pressing Escape.
      el('button', {
        class: 'btn sim-exit', title: 'Leave the simulator (Esc)',
        onclick: () => this.exit(),
      }, [icon('close', { size: 15 }), 'Exit simulator']),
    ]));

    if (this.error) {
      this.root.appendChild(el('p', { class: 'sim-error' }, [this.error]));
    }

    if (this.loading) {
      this.root.appendChild(el('p', { class: 'sim-note' }, ['Loading…']));
      return;
    }

    if (!this.caps?.available) {
      this.root.appendChild(emptyState(
        'info',
        'No map archive is open',
        this.caps?.note
          ?? 'The simulator reads maps the backend already has loaded. Open a Map archive first.',
      ));
      return;
    }

    this.bodyHost = el('div', { class: 'sim-body' });
    this.root.appendChild(this.bodyHost);
    this.renderBody(this.bodyHost);
  }

  /**
 * Build the body into `target`.
 *
 * Takes the host rather than returning a fresh root because the picker toggle
 * repaints the body WITHOUT the header: the exit button must not be rebuilt
 * and the canvas must not be detached on every open/close of an overlay. A
 * returned element would have to be either re-parented out of a throwaway
 * wrapper or diffed against the old one; both are worse than being handed the
 * element to fill.
 */
renderBody(target) {
    const right = el('div', { class: 'sim-right' });

    // ---- map picker, as an OVERLAY over the canvas
    //
    // A <select>, not a scrolling list of 5,366 paths. The editor opens a map
    // through a search box with the map's Chinese name beside its id
    // (/api/mapedit/maps), and that is what a reader actually recognises --
    // "彩虹岛 : 蘑菇村西入口" rather than "f1/Map/Map0/000000000.img".
    //
    // It is positioned over the stage, not beside it. It was a grid column
    // taking ~288px of the width permanently, for a control used once per
    // session; on a full-screen canvas that is a quarter of the picture gone
    // for a list the reader has to dismiss to see the thing they asked for.
    const search = el('input', {
      class: 'sim-search', type: 'search', value: this.filter,
      placeholder: `${(this.caps?.mapCount ?? 0).toLocaleString()} 張地圖 — 以名稱或編號搜尋`,
      oninput: (e) => { this.filter = e.target.value; this.renderPicker(); },
    });

    const select = el('select', {
      class: 'sim-select', size: 18,
      onchange: (e) => {
        const row = this.visibleMaps().find((m) => m.path === e.target.value);
        // Picking a map closes the overlay. Leaving it open would cover the
        // map that was just loaded with the list it came from.
        if (row) { this.pickerOpen = false; this.walk(row); }
      },
    });

    const picker = el('div', { class: `sim-picker${this.pickerOpen ? ' open' : ''}`, hidden: !this.pickerOpen },
      [search, select]);

    this.pickerHost = select;

    if (this.viewError) {
      right.appendChild(el('p', { class: 'sim-error' }, [`walk view: ${this.viewError}`]));
    }

    // The replay is optional and the character is not, so a failed /trace must
    // not be silent: the canvas below is showing the live body and would look
    // complete. Said where the other view failure is said, in the same words.
    if (this.replayError) {
      right.appendChild(el('p', { class: 'sim-note' },
        [`batch replay unavailable (${this.replayError}); the live character is unaffected`]));
    }

    // The canvas first and as large as the window allows. It is the thing the
    // section exists for, and it comes before the verdict so the verdict is
    // read against the picture rather than instead of it.
    right.appendChild(this.result
      ? el('div', { class: 'sim-viewer' }, [this.viewer.root])
      : emptyState(
          'play',
          'Pick a map',
          'Choose one from the list to walk it. A map is not proven walkable '
          + 'by looking at it — the chain has to be walked.',
        ));

    if (this.result) right.appendChild(this.renderResult());

    this.renderPicker();

    // One column now. The picker overlays it rather than sitting beside it,
    // so the stage is the full width at every window size and there is no
    // breakpoint at which the list has to be given up to keep the picture.
    target.appendChild(el('div', { class: 'sim-grid' }, [right, picker]));
  }

  /**
   * Repaint just the body, so toggling the picker does not rebuild the header
   * (and with it the exit button) or drop the canvas the reader is looking at.
   */
  renderBodyInto() {
    const host = this.bodyHost;
    if (!host || !host.parentElement) { this.render(); return; }
    clear(host);
    this.renderBody(host);
  }

  /** Maps matching the filter, with the Chinese name leading. */
  visibleMaps() {
    return this.maps.filter((m) => this.matches(m));
  }

  /** What the reader sees: the name when the archive has one, else the id. */
  mapLabel(m) {
    const id = String(m.id ?? '').padStart(9, '0');
    return m.name ? `${m.name}  (${id})` : id;
  }

  renderPicker() {
    if (!this.pickerHost) return;
    clear(this.pickerHost);

    const shown = this.visibleMaps();
    // A <select> holds its options as text, so 5,366 of them is a few hundred
    // kilobytes rather than 5,366 DOM nodes -- but only after a filter narrows
    // it. Empty filter shows the first page so the control is never blank.
    const CAP = 400;
    const list = shown.slice(0, CAP);

    for (const map of list) {
      this.pickerHost.appendChild(el('option', {
        value: map.path,
        selected: this.selected?.path === map.path,
      }, [this.mapLabel(map)]));
    }

    if (shown.length > CAP) {
      this.pickerHost.appendChild(el('option', {
        disabled: true,
      }, [`… 還有 ${(shown.length - CAP).toLocaleString()} 張，請搜尋縮小範圍`]));
    } else if (!shown.length) {
      this.pickerHost.appendChild(el('option', { disabled: true }, ['沒有符合的地圖']));
    }
  }

  renderResult() {
    const r = this.result;
    const right = (r.direction ?? 1) > 0;

    // Three outcomes, not two. "Reached the edge" and "the chain ran out" are
    // both ways of getting somewhere, and only the second is a finding.
    const verdict = r.reachedEdge
      ? 'Crossed'
      : r.fellOut
        ? 'Blocked'
        : 'Chain ends here';

    // Every field is optional here: a link stub returns before filling any of them,
// and reading r.ticks.toLocaleString() on that throws and blanks the section.
// A row with nothing to show is left out.
    const num = (v) => (v == null ? null : Number(v).toLocaleString());
    // Each entry stays a PAIR. Spreading it into the list flattened the label
    // and the value into separate children, which is what made the definition
    // list render one character per line.
    const row = (label, value) => (value == null ? null : [label, value]);

    const rows = [
      row('Direction', right ? 'left to right' : 'right to left'),
      row('Footholds', num(r.footholds)),
      row('Steps', r.ticks == null ? null : `${num(r.ticks)} frames (${Number(r.seconds).toFixed(1)}s)`),
      row('Distance', r.distance == null ? null : `${num(r.distance)} / ${num(r.mapWidth)} px`),
      row('Start', r.startX == null ? null : `${r.startX}, ${r.startY}`),
      row('End', r.finalX == null ? null : `${r.finalX}, ${r.finalY}`),
      row('Step size', r.stepSeconds == null ? null : `1/${Math.round(1 / r.stepSeconds)} s`),
      row('Walk speed', r.acceleration == null ? null : `${r.acceleration} per frame, max ${r.maxWalkSpeed}`),
    ].filter(Boolean);

    return el('div', { class: 'sim-result' }, [
      el('div', { class: `sim-verdict ${r.fellOut ? 'bad' : 'ok'}` }, [
        icon(r.fellOut ? 'alert' : 'check'),
        el('div', {}, [
          el('strong', {}, [verdict]),
          el('div', { class: 'sim-verdict-pct' }, [
            `${Number(r.percentOfMap ?? 0).toFixed(1)}% of the walkable span`,
          ]),
        ]),
      ]),

      el('p', { class: 'sim-reason' }, [r.reason]),

      this.advice ? el('div', { class: 'sim-advice' }, [
        icon('info'),
        el('div', {}, [
          el('strong', {}, [this.advice.problem]),
          el('p', {}, [this.advice.suggestion]),
        ]),
      ]) : null,

      r.fellOut ? el('div', { class: 'sim-caveat' }, [
        icon('alert'),
        'A character cannot cross this map on foot. The foothold chain links are '
        + 'intact — /validate says so — but the ground runs out partway. A ladder, '
        + 'a portal or a jump may bridge it; this walk models none of those.',
      ]) : null,

      el('dl', { class: 'sim-facts' }, rows.flatMap(([k, v]) => [
        el('dt', {}, [k]),
        el('dd', {}, [v]),
      ])),

      r.track?.length && r.bounds ? this.renderTrack(r) : null,

      el('p', { class: 'sim-foot' }, [`map: ${r.path}`]),
    ]);
  }

  /** The walked path, drawn to scale against the map's own bounds. */
  renderTrack(r) {
    const b = r.bounds;
    if (!b || !r.track?.length) return null;

    const W = 100, H = 34;
    const spanX = Math.max(1, b.right - b.left);
    const spanY = Math.max(1, b.bottom - b.top);
    // Tall maps would make the strip useless, so the box follows the map's own
    // aspect and is allowed to be tall; the numbers under it say what it is.
    const boxH = Math.max(H, Math.round((spanY / spanX) * W));

    const px = (x) => ((x - b.left) / spanX) * W;
    const py = (y) => ((y - b.top) / spanY) * boxH;

    const path = r.track
      .filter((p) => Number.isFinite(p.x) && Number.isFinite(p.y))
      .map((p) => `${px(p.x).toFixed(2)},${py(p.y).toFixed(2)}`)
      .join(' ');
    if (!path) return null;

    return el('div', { class: 'sim-track' }, [
      el('h3', { class: 'sim-track-title' }, ['Walked path']),
      el('svg', {
        class: 'sim-track-svg',
        viewBox: `0 0 ${W} ${boxH}`,
        preserveAspectRatio: 'xMidYMid meet',
      }, [
        el('line', {
          x1: 0, y1: py(b.bottom), x2: W, y2: py(b.bottom),
          class: 'sim-track-edge',
        }),
        el('polyline', { points: path, class: 'sim-track-line' }),
        el('circle', { cx: px(r.startX), cy: py(r.startY), r: 1.4, class: 'sim-track-start' }),
        el('circle', { cx: px(r.finalX), cy: py(r.finalY), r: 1.4, class: 'sim-track-end' }),
      ]),
      el('p', { class: 'sim-track-legend' }, [
        `green = start, red = end · ${r.track.length} sampled positions`,
      ]),
    ]);
  }
}
