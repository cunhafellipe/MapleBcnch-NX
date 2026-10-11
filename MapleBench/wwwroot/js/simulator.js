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

  matches(map) {
    if (!this.filter) return true;
    return map.path.toLowerCase().includes(this.filter.toLowerCase());
  }

  async walk(map) {
    this.selected = map;
    this.result = null;
    this.render();
    try {
      this.result = await post('/api/simulate/walk', { path: map.path });
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
    const verdict = r.walkable
      ? (r.conclusive ? 'Walkable' : 'Walkable, but not the whole map')
      : 'Blocked';

    const rows = [
      ['Footholds', r.footholds.toLocaleString()],
      ['Unusable footholds', r.unusableFootholds.toLocaleString()],
      ['Chain segments walked', r.steps.toLocaleString()],
      ['Distance', `${r.distanceWalked.toLocaleString()} / ${r.mapWidth.toLocaleString()} px`],
      ['Start', `${r.startX}, ${r.startY}  (foothold ${r.startFoothold})`],
      ['End', `${r.stoppedX}, ${r.stoppedY}  (foothold ${r.stoppedFoothold})`],
    ];

    if (r.startScannedFrom != null) {
      rows.push(['Started from', `x=${r.startScannedFrom} — the left edge had only a wall`]);
    }

    return el('div', { class: 'sim-result' }, [
      el('div', { class: `sim-verdict ${r.walkable ? 'ok' : 'bad'}` }, [
        icon(r.walkable ? 'check' : 'alert'),
        el('div', {}, [
          el('strong', {}, [verdict]),
          el('div', { class: 'sim-verdict-pct' }, [`${Number(r.percentOfMap ?? 0).toFixed(1)}% of the walkable span`]),
        ]),
      ]),

      el('p', { class: 'sim-reason' }, [r.reason]),

      r.isLinkStub ? el('div', { class: 'sim-stub' }, [
        icon('info'),
        `This map is a link stub pointing at '${r.link}'. Its geometry is in that map, not this one.`,
      ]) : null,

      el('dl', { class: 'sim-facts' }, rows.flatMap(([k, v]) => [
        el('dt', {}, [k]),
        el('dd', {}, [v]),
      ])),

      !r.conclusive ? el('div', { class: 'sim-caveat' }, [
        icon('alert'),
        'This walk models foothold links only. It does not simulate gravity, '
        + 'jumping, ladders or portals, so a map it calls inconclusive may still be '
        + 'perfectly playable. Read it as "nothing blocked the chain", not as '
        + '"the map was fully verified".',
      ]) : null,

      el('p', { class: 'sim-foot' }, [
        `map: ${r.path}`,
      ]),
    ]);
  }
}