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
  }

  async open() {
    this.render();
    await this.refresh();
  }

  async refresh() {
    this.loading = true;
    this.render();
    try {
      const caps = await get('/api/simulate/capabilities');
      this.caps = caps;
      if (caps.available) {
        const data = await get('/api/simulate/maps?limit=2000');
        this.maps = data.maps ?? [];
      } else {
        this.maps = [];
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
    return map.path.toLowerCase().includes(this.filter.toLowerCase());
  }

  async walk(map) {
    this.selected = map;
    this.result = null;
    this.render();
    try {
      // The CHARACTER, not the chain. The chain walk answers "are the links
      // intact"; this answers "can a character actually cross it", and the
      // second is the question that matters — /validate already answers the
      // first.
      this.result = await post('/api/simulate/character', {
        path: map.path,
        direction: this.direction,
      });
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
      el('button', {
        class: 'btn ghost', onclick: () => this.refresh(), title: 'Reload',
      }, [icon('refresh'), 'Refresh']),
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

    this.root.appendChild(this.renderBody());
  }

  renderBody() {
    const left = el('div', { class: 'sim-left' });
    const right = el('div', { class: 'sim-right' });

    // ---- map picker
    const search = el('input', {
      class: 'sim-search', type: 'search', value: this.filter,
      placeholder: `${this.caps.mapCount.toLocaleString()} maps — filter by path`,
      oninput: (e) => { this.filter = e.target.value; this.renderPicker(); },
    });

    const list = el('div', { class: 'sim-list' });
    list.id = 'sim-map-list';

    const picker = el('div', { class: 'sim-picker' }, [
      search,
      list,
    ]);
    left.appendChild(picker);

    right.appendChild(this.result
      ? this.renderResult()
      : emptyState(
          'play',
          'Pick a map',
          'Choose one from the list to walk it. A map is not proven walkable '
          + 'by looking at it — the chain has to be walked.',
        ));

    this.pickerHost = list;
    this.renderPicker();

    return el('div', { class: 'sim-grid' }, [left, right]);
  }

  renderPicker() {
    if (!this.pickerHost) return;
    clear(this.pickerHost);

    const shown = this.maps.filter((m) => this.matches(m));
    // A 5,000-row list is a scroll container, not a page. Cap the DOM and say
    // so, rather than silently showing a prefix that looks like everything.
    const CAP = 300;
    for (const map of shown.slice(0, CAP)) {
      const isSel = this.selected?.path === map.path;
      this.pickerHost.appendChild(el('button', {
        class: `sim-row${isSel ? ' sel' : ''}`,
        onclick: () => this.walk(map),
      }, [
        el('span', { class: 'sim-row-path' }, [map.path]),
        el('span', { class: 'sim-row-src' }, [map.source ?? '']),
      ]));
    }

    if (shown.length > CAP) {
      this.pickerHost.appendChild(el('p', { class: 'sim-note' }, [
        `showing ${CAP} of ${shown.length.toLocaleString()} — narrow the filter to see the rest`,
      ]));
    } else if (!shown.length) {
      this.pickerHost.appendChild(el('p', { class: 'sim-note' }, ['no map matches that filter']));
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
