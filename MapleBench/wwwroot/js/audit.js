/**
 * Audit section: check a whole client on disk before the game disagrees with it.
 *
 * Built in the shape the other sections use -- one class, a head / stats /
 * toolbar / table stack, honest empty states -- with two deliberate departures,
 * both of which are the point of the screen rather than shortcuts.
 *
 * It reads a FOLDER, not the session. What the auditor checks is what a client
 * is once it is mounted: a family of archives resolving links into each other,
 * names that have to match names in a different archive. The session holds
 * whatever the user happened to open, which is usually one archive of a family,
 * and that is exactly the view that hides a shadowed image. A screen that
 * audited "what is open" would answer a different question and answer it
 * reassuringly.
 *
 * It offers to fix exactly one thing, and the shape of that exception is the
 * rule rather than a hole in it. Every other finding here is a fact whose remedy
 * is a judgement (which duplicate is the real one? should the link be repointed
 * or the target restored?) that the auditor is in no position to make and that a
 * wrong guess makes worse, so the screen's job ends at "here it is, here is what
 * it means, here is where to look".
 *
 * `canvas.row_width_zero` is different on all three counts. The damage is this
 * app's own -- a canvas format written split across the format and magnification
 * fields, fixed in the writer at 3cf73c7 and still sitting in every archive
 * written before it. The remedy is not a judgement: the two numbers add back to
 * one number, and which canvases qualify is decided by inflating the blob and
 * seeing which format its pixels are actually in, not by a guess about intent.
 * And it is verifiable -- the repaired archive is reopened and the pixels are
 * decoded before anything claims to have been repaired.
 *
 * Even so, nothing here writes to a client. The repair produces a NEW archive
 * beside the source and prints the command to install it, backup first. Copying
 * it over the archive the user plays from is the user's keystroke, not this
 * screen's button.
 */

import { api } from './api.js';
import { t } from './i18n.js';
import { el, clear, toast, toastError, fmt } from './ui.js';
import { emptyState } from './inspector.js';
import { icon } from './icons.js';

const FOLDER_KEY = 'mb.auditFolder';
const POLL_MS = 700;

/** Worst first, and the order the summary chips are laid out in. */
const SEVERITIES = ['Critical', 'Error', 'Warning', 'Info'];

/** Archive sizes, in the unit a client is actually measured in. */
const gb = (bytes) => (bytes >= 1073741824
  ? `${(bytes / 1073741824).toFixed(2)} GB`
  : `${Math.max(1, Math.round(bytes / 1048576))} MB`);

/**
 * What a severity means here, in one line, on hover.
 *
 * Written out rather than left to the word because "warning" in most tools
 * means "probably fine" and half of these are not. The line is the promise the
 * check made when it fired.
 */
const SEVERITY_BLURB = {
  Critical: 'The client cannot be right. Something will not load, will not draw, or is not the file you edited.',
  Error: 'Definitely broken and survivable — a link that draws nothing, a name that resolves to nothing.',
  Warning: 'Suspicious, or a rule the client happens to tolerate. Worth a look before it becomes the other kind.',
  Info: 'A measured fact with no judgement attached, including the things deliberately not resolved.',
};

export class AuditSection {
  constructor({ host, app }) {
    this.host = host;
    this.app = app;

    this.folder = localStorage.getItem(FOLDER_KEY) ?? '';
    this.plan = null;
    this.progress = null;
    this.report = null;
    this.expanded = new Set();
    this.timer = null;

    // The repair is its own little machine hanging off one finding. Kept beside
    // the report rather than inside it: it re-measures the archive itself and
    // does not trust the audit's count, so its numbers are allowed to disagree
    // -- and on the client this was built against they do, because the audit
    // check fires on a symptom (a row that shifts to zero) and the repair looks
    // for the cause (a format split across two fields), which is the larger set.
    this.repair = { archive: '', progress: null, scan: null, result: null, busy: false };
    this.repairTimer = null;
  }

  async open() {
    // A sensible default the first time: the folder the session's first archive
    // came from. Guessed once and then never again -- once the user has typed a
    // folder, the remembered one wins, because a client being audited is often
    // not the client being edited.
    if (!this.folder) {
      const first = this.app.files?.[0]?.path ?? '';
      const cut = Math.max(first.lastIndexOf('\\'), first.lastIndexOf('/'));
      if (cut > 0) this.folder = first.slice(0, cut);
    }
    this.render();
    await this.refreshProgress();
    if (!this.plan && this.folder) await this.loadPlan();
  }

  refresh() { return this.open(); }

  /* ============================================================
     TALKING TO THE SERVER
     ============================================================ */

  async loadPlan() {
    try {
      this.plan = await api.auditPlan(this.folder);
      localStorage.setItem(FOLDER_KEY, this.folder);
    } catch (error) {
      this.plan = null;
      toastError(error, t('Could not read that folder'));
    }
    this.render();
  }

  async start() {
    try {
      this.report = null;
      this.progress = await api.auditStart({ folder: this.folder, maxPerCheck: 300 });
      this.poll();
    } catch (error) {
      toastError(error, t('Could not start the audit'));
    }
    this.render();
  }

  async cancel() {
    try { this.progress = await api.auditCancel(); } catch { /* the poll re-reads it */ }
    this.render();
  }

  poll() {
    clearTimeout(this.timer);
    this.timer = setTimeout(() => this.refreshProgress(), POLL_MS);
  }

  /* ---- canvas format repair ------------------------------------------- */

  /** Read-only. Opens the archive, measures, writes nothing. */
  async repairScan(archive) {
    this.repair.archive = archive;
    this.repair.scan = null;
    this.repair.result = null;
    try {
      this.repair.progress = await api.repairScanStart({ path: archive, maxCases: 400 });
      this.pollRepair();
    } catch (error) {
      toastError(error, t('Could not scan that archive'));
    }
    this.render();
  }

  /**
   * Writes. Behind a confirm() as well as the server's own confirm flag,
   * because the two guards answer different questions: the server's is "did a
   * caller mean this?" and this one is "does the person reading the screen
   * know how big it is?".
   */
  async repairApply() {
    const archive = this.repair.archive;
    if (!archive) return;
    const ok = window.confirm(t(
      'Write a repaired copy of {name}?\n\n'
      + 'This creates a NEW file beside it and does not touch the original or your client. '
      + 'It needs as much free disk as the archive is big, and takes about as long as a save.',
      { name: archive }));
    if (!ok) return;

    try {
      this.repair.result = null;
      this.repair.progress = await api.repairApply({ path: archive, confirm: true });
      this.pollRepair();
    } catch (error) {
      toastError(error, t('Could not start the repair'));
    }
    this.render();
  }

  pollRepair() {
    clearTimeout(this.repairTimer);
    this.repairTimer = setTimeout(() => this.refreshRepair(), POLL_MS);
  }

  async refreshRepair() {
    let state;
    try {
      this.repair.progress = await api.repairProgress();
      state = this.repair.progress.state;
    } catch {
      return;   // the server going away is the app's problem to report
    }
    if (state === 'scanning' || state === 'repairing' || state === 'saving' || state === 'verifying') {
      this.pollRepair();
      this.render();
      return;
    }
    if (state === 'done') {
      try { this.repair.scan = await api.repairScan(); } catch { /* 404 until one finishes */ }
      try { this.repair.result = await api.repairResult(); } catch { /* only after an apply */ }
    }
    this.render();
  }

  async refreshProgress() {
    try {
      this.progress = await api.auditProgress();
    } catch {
      return;   // the server going away is the app's problem to report, not this screen's
    }
    if (this.progress.state === 'running') { this.poll(); this.render(); return; }
    if (this.progress.state === 'done' && !this.report) {
      try { this.report = await api.auditReport(); } catch { /* 404 until one finishes */ }
    }
    this.render();
  }

  /* ============================================================
     RENDER
     ============================================================ */

  render() {
    clear(this.host);
    this.host.className = 'stage-body audit';
    this.host.append(this.head());

    if (this.progress?.state === 'running') { this.host.append(this.running()); return; }
    if (this.progress?.state === 'failed') this.host.append(this.failure());
    if (this.report) { this.host.append(...this.reportView()); return; }
    if (this.plan) { this.host.append(this.planView()); return; }

    this.host.append(emptyState(
      'search', t('Point it at a client folder'),
      t('The auditor reads every .wz a client mounts and reports what will not resolve once they are '
        + 'mounted together. It opens files read-only and writes nothing.')));
  }

  head() {
    const input = el('input', {
      class: 'input audit-folder',
      type: 'text',
      value: this.folder,
      placeholder: 'C:\\MapleStory\\232',
      spellcheck: 'false',
      oninput: (event) => { this.folder = event.target.value.trim(); },
      onkeydown: (event) => { if (event.key === 'Enter') this.loadPlan(); },
    });

    return el('div', { class: 'audit-head' },
      el('div', { class: 'audit-title' },
        el('h2', { text: t('Client integrity') }),
        el('p', {
          class: 'audit-sub',
          text: t('Every check here answers a question no single archive can: what happens once they are '
                + 'all mounted together. Read-only — nothing is written, moved or fixed.'),
        })),
      el('div', { class: 'audit-controls' },
        input,
        el('button', {
          class: 'btn',
          onclick: () => this.loadPlan(),
          'data-tip': t('List the archives a client would mount from this folder. Opens nothing.'),
        }, icon('folderOpen', { size: 15 }), t('Read folder')),
        el('button', {
          class: 'btn btn-primary',
          disabled: !this.plan,
          onclick: () => this.start(),
          'data-tip': this.plan ? t('Minutes on a full client. Progress below; cancel any time.') : t('Read a folder first'),
        }, icon('search', { size: 15 }), t('Run audit'))));
  }

  planView() {
    const rows = this.plan.families.flatMap((family) =>
      family.archives.map((archive, index) => el('tr', {},
        el('td', { text: index === 0 ? family.family : '' }),
        el('td', {}, el('code', { text: archive.name })),
        el('td', { class: 'num', text: archive.mountOrder === 0 ? t('first') : `#${archive.mountOrder}` }),
        el('td', { class: 'num', text: gb(archive.bytes) }))));

    const skipped = this.plan.skipped ?? [];

    return el('div', { class: 'audit-plan' },
      el('div', { class: 'audit-card' },
        el('h3', { text: t('{n} archives in {families} families',
          { n: rows.length, families: this.plan.families.length }) }),
        el('table', { class: 'table audit-table' },
          el('thead', {}, el('tr', {},
            el('th', { text: t('Family') }), el('th', { text: t('Archive') }),
            el('th', { class: 'num', text: t('Mounts') }), el('th', { class: 'num', text: t('Size') }))),
          el('tbody', {}, ...rows))),
      // Shown, not hidden. A backup sitting beside the live archive is the most
      // common reason a folder audits differently from the client that runs, and
      // the user is the only one who knows which of them they meant.
      skipped.length
        ? el('div', { class: 'audit-card audit-muted' },
            el('h3', { text: t('{n} files skipped', { n: skipped.length }) }),
            el('ul', {}, ...skipped.map((file) =>
              el('li', {},
                el('code', { text: file.name }),
                el('span', { text: `${file.bytes ? ` (${gb(file.bytes)})` : ''} — ${file.why}` })))))
        : null,
      this.notes(this.plan.assumptions, t('What this run assumes')));
  }

  running() {
    const p = this.progress;
    const share = p.archivesTotal ? Math.round((p.archivesDone / p.archivesTotal) * 100) : 0;

    return el('div', { class: 'audit-card audit-running' },
      el('h3', { text: `${p.phase}${p.archive ? ` — ${p.archive}` : ''}` }),
      el('div', { class: 'audit-bar' }, el('div', { class: 'audit-bar-fill', style: `width:${share}%` })),
      el('p', {
        class: 'audit-sub',
        text: t('{done} of {total} archives · {images} images · {findings} findings so far', {
          done: p.archivesDone, total: p.archivesTotal,
          images: fmt.format(p.imagesDone), findings: fmt.format(p.findings),
        }),
      }),
      el('button', { class: 'btn', onclick: () => this.cancel() }, t('Stop')));
  }

  failure() {
    return el('div', { class: 'audit-card audit-failed' },
      el('h3', { text: t('The audit stopped') }),
      el('p', { text: this.progress.error ?? t('No reason was reported.') }));
  }

  reportView() {
    const report = this.report;
    const counts = Object.fromEntries(SEVERITIES.map((s) => [s, 0]));
    for (const finding of report.findings) counts[finding.severity] = (counts[finding.severity] ?? 0) + 1;

    // The chips count what the CHECKS found, not what the list holds: a check
    // that fired 40,000 times keeps 300 rows, and a summary that said 300 would
    // be quietly wrong about the size of the problem.
    const totals = Object.fromEntries(SEVERITIES.map((s) => [s, 0]));
    for (const check of report.checks) totals[check.severity] = (totals[check.severity] ?? 0) + check.found;

    const summary = el('div', { class: 'audit-chips' },
      ...SEVERITIES.map((severity) => el('div', {
        class: 'audit-chip',
        'data-severity': severity,
        'data-tip': t(SEVERITY_BLURB[severity]),
      },
        el('span', { class: 'audit-chip-n', text: fmt.format(totals[severity]) }),
        el('span', { class: 'audit-chip-l', text: t(severity) }))));

    const fired = report.checks.filter((check) => check.found > 0)
      .sort((a, b) => SEVERITIES.indexOf(a.severity) - SEVERITIES.indexOf(b.severity) || b.found - a.found);
    const quiet = report.checks.filter((check) => check.found === 0);

    const images = report.archives.reduce((n, a) => n + (a.images || 0), 0);
    const canvases = report.archives.reduce((n, a) => n + (a.canvases || 0), 0);

    return [
      el('div', { class: 'audit-card' },
        el('h3', {
          text: t('{n} archives · {images} images · {canvases} canvases · {seconds}s', {
            n: report.archives.length, images: fmt.format(images),
            canvases: fmt.format(canvases), seconds: report.seconds.toFixed(0),
          }),
        }),
        summary),

      ...fired.flatMap((check) => (check.id === 'canvas.row_width_zero'
        ? [this.checkCard(check, report), this.repairCard(report)]
        : [this.checkCard(check, report)])),

      this.stringCoverage(report),

      // The checks that found nothing, listed with what they looked at. Without
      // this the report is unfalsifiable: a clean screen and a check that never
      // ran look identical, and the number beside each one is the difference.
      el('div', { class: 'audit-card audit-muted' },
        el('h3', { text: t('{n} checks found nothing', { n: quiet.length }) }),
        el('ul', { class: 'audit-quiet' }, ...quiet.map((check) =>
          el('li', {},
            el('span', { text: check.title }),
            el('span', { class: 'audit-dim', text: t(' — {n} examined', { n: fmt.format(check.examined) }) }),
            // A zero beside a zero is the one thing this screen must never leave
            // ambiguous: "found nothing" and "looked at nothing" read the same,
            // and only the examined count and the limit tell them apart.
            check.examined === 0
              ? el('span', { class: 'audit-none', text: t(' — nothing was examined') })
              : null,
            check.notChecked
              ? el('div', { class: 'audit-dim audit-limit', text: check.notChecked })
              : null)))),

      this.notes(report.notChecked, t('What was deliberately not checked')),
      this.notes(report.assumptions, t('What this run assumed')),
      this.skipped(report),
      this.archives(report),
    ];
  }

  /**
   * String.wz against the data, per kind.
   *
   * These two checks fire tens of thousands of times and the report keeps a few
   * hundred rows of each, so the findings list cannot be counted or grouped by
   * kind. The counts can, and they are the shape of the answer: which kind is
   * out of step, and in which direction. A kind whose data archive was absent
   * says so in words rather than showing four zeroes, because a row of zeroes
   * and "nothing was compared" are the same picture and opposite facts.
   */
  stringCoverage(report) {
    const rows = report.stringCoverage ?? [];
    if (!rows.length) return null;

    return el('div', { class: 'audit-card' },
      el('h3', { text: t('String.wz against the archives that define what it names') }),
      el('table', { class: 'table audit-table' },
        el('thead', {}, el('tr', {},
          el('th', { text: t('Kind') }),
          el('th', { text: t('String.wz image') }),
          el('th', { class: 'num', text: t('Named') }),
          el('th', { class: 'num', text: t('Defined') }),
          el('th', { class: 'num', text: t('Named, not defined') }),
          el('th', { class: 'num', text: t('Defined, not named') }))),
        el('tbody', {}, ...rows.map((row) => el('tr', {},
          el('td', { text: row.kind }),
          el('td', {}, el('code', { text: row.stringImage })),
          ...(row.compared
            ? [
                el('td', { class: 'num', text: fmt.format(row.named) }),
                el('td', { class: 'num', text: fmt.format(row.defined) }),
                el('td', { class: 'num', text: fmt.format(row.orphans) }),
                el('td', { class: 'num', text: fmt.format(row.unnamed) }),
              ]
            : [el('td', { class: 'audit-dim', colspan: 4,
              text: t('not compared — {why}', { why: row.why }) })]))))));
  }

  /**
   * Every .wz in the folder the run did not audit, with the reason.
   *
   * Shown because a backup sitting beside the live archive is the most common
   * reason a folder audits differently from the client that runs, and because
   * "what was not looked at" is half of what makes the rest of this checkable.
   */
  skipped(report) {
    const rows = report.skipped ?? [];
    if (!rows.length) return null;

    return el('div', { class: 'audit-card audit-muted' },
      el('h3', { text: t('{n} files in the folder were not audited', { n: rows.length }) }),
      el('ul', {}, ...rows.map((file) => el('li', {},
        el('code', { text: file.name }),
        el('span', { text: ` (${gb(file.bytes)}) — ${file.why}` })))));
  }

  /**
   * The archives, last, because it is reference rather than news. The status
   * column is the load-bearing one: an archive that did not open, or that was
   * deliberately never opened, is the reason a check above it found nothing.
   */
  archives(report) {
    return el('div', { class: 'audit-card audit-muted' },
      el('h3', { text: t('What was read, in the order the client mounts it') }),
      el('table', { class: 'table audit-table' },
        el('thead', {}, el('tr', {},
          el('th', { text: t('Archive') }),
          el('th', { text: t('Namespace') }),
          el('th', { class: 'num', text: t('Mounts') }),
          el('th', { class: 'num', text: t('Size') }),
          el('th', { text: t('Status') }),
          el('th', { class: 'num', text: t('Images') }),
          el('th', { class: 'num', text: t('Canvases') }),
          el('th', { class: 'num', text: t('Seconds') }))),
        el('tbody', {}, ...report.archives.map((archive) => el('tr', {},
          el('td', {}, el('code', { text: archive.name })),
          el('td', { text: archive.family }),
          el('td', { class: 'num', text: archive.mountOrder === 0 ? t('first') : `#${archive.mountOrder}` }),
          el('td', { class: 'num', text: gb(archive.bytes) }),
          el('td', {
            class: archive.parseStatus === 'Success' ? '' : 'audit-dim',
            text: archive.parseStatus,
          }),
          el('td', { class: 'num', text: fmt.format(archive.images) }),
          el('td', { class: 'num', text: fmt.format(archive.canvases) }),
          el('td', { class: 'num', text: archive.seconds.toFixed(1) }))))));
  }

  checkCard(check, report) {
    const open = this.expanded.has(check.id);
    const rows = open
      ? report.findings.filter((finding) => finding.check === check.id)
      : [];

    return el('div', { class: 'audit-card audit-check', 'data-severity': check.severity },
      el('button', {
        class: 'audit-check-head',
        onclick: () => {
          if (open) this.expanded.delete(check.id); else this.expanded.add(check.id);
          this.render();
        },
      },
        el('span', { class: 'audit-sev', 'data-severity': check.severity, text: t(check.severity) }),
        el('span', { class: 'audit-check-title', text: check.title }),
        el('span', { class: 'audit-check-n', text: fmt.format(check.found) }),
        el('code', { class: 'audit-dim', text: check.id })),

      open
        ? el('div', {},
            // The check's own limits, verbatim and inside the check they belong
            // to. A report that carries them only in a global footer invites the
            // reader to take a row at face value and go looking for the caveat
            // afterwards, which nobody does.
            check.notChecked
              ? el('p', { class: 'audit-sub audit-limit',
                  text: t('Limits of this check: {limit}', { limit: check.notChecked }) })
              : null,
            check.truncated
              ? el('p', {
                  class: 'audit-sub',
                  text: t('Showing {shown} of {total}. The count is complete; the list is capped '
                    + 'so one noisy check cannot bury the rest.',
                    { shown: fmt.format(rows.length), total: fmt.format(check.found) }),
                })
              : null,
            el('table', { class: 'table audit-table' },
              el('thead', {}, el('tr', {},
                el('th', { text: t('Where') }), el('th', { text: t('What') }), el('th', { text: t('Target') }))),
              el('tbody', {}, ...rows.map((finding) => el('tr', {},
                // The path is selectable text rather than a link into the tree:
                // the archive it names is usually not open, and a link that
                // silently does nothing is worse than one that is not offered.
                el('td', {}, el('code', { text: finding.path })),
                el('td', { text: finding.detail }),
                el('td', {}, finding.target ? el('code', { text: finding.target }) : null))))))
        : null);
  }

  /**
   * The repair for the check above it.
   *
   * Deliberately placed under the finding rather than in a toolbar: it is not a
   * general "fix my client" button and must never read as one. It repairs one
   * defect, with a stated rule, on one archive at a time.
   */
  repairCard(report) {
    const state = this.repair.progress?.state ?? 'idle';
    const running = ['scanning', 'repairing', 'saving', 'verifying'].includes(state);

    // Which archives the finding is actually in, taken from the finding paths
    // rather than from the folder listing -- repairing an archive the check
    // never fired on would be work with no reason attached to it.
    const archives = [...new Set(report.findings
      .filter((f) => f.check === 'canvas.row_width_zero')
      .map((f) => f.path.split('/')[0]))];
    const sep = this.folder.includes('/') && !this.folder.includes('\\') ? '/' : '\\';
    const full = (name) => `${this.folder.replace(/[\\/]+$/, '')}${sep}${name}`;

    const scan = this.repair.scan;
    const result = this.repair.result;

    return el('div', { class: 'audit-card audit-check', 'data-severity': 'Critical' },
      el('h3', { text: t('Repair: re-join the split format value') }),
      el('p', {
        class: 'audit-sub',
        text: t('These canvases carry a format that was written across two fields — the low byte in '
              + 'the format and the rest in the magnification beside it. This app wrote them that way '
              + 'before the writer was fixed; the client reads the second number as a magnification '
              + 'and shifts the sprite down to a couple of pixels, or to none.'),
      }),
      el('p', {
        class: 'audit-sub',
        text: t('A magnification of 1, 2 or 4 is ordinary in real data, and each of those joins to a '
              + 'real format (257, 513, 1026), so the pair on its own proves nothing. What decides is '
              + 'the payload: a genuinely scaled canvas stores its pixels at the reduced size, a split '
              + 'value stores them full-size in the joined format. Only the second is touched.'),
      }),

      running
        ? el('div', {},
            el('p', { class: 'audit-sub', text: t('{state} — {phase} {archive} · {canvases} canvases · {found} found', {
              state: t(state), phase: this.repair.progress.phase || '', archive: this.repair.progress.archive || '',
              canvases: fmt.format(this.repair.progress.canvasesDone),
              found: fmt.format(this.repair.progress.found),
            }) }),
            el('button', { class: 'btn', onclick: () => api.repairCancel().catch(() => {}) }, t('Stop')))
        : el('div', { class: 'audit-controls' },
            ...archives.map((name) => el('button', {
              class: 'btn',
              onclick: () => this.repairScan(full(name)),
              'data-tip': t('Open {name} read-only and measure every canvas. Writes nothing.', { name }),
            }, icon('search', { size: 15 }), t('Scan {name}', { name })))),

      state === 'failed'
        ? el('p', { class: 'audit-sub', text: this.repair.progress.error ?? t('The run stopped.') })
        : null,

      scan && !running
        ? el('div', {},
            el('p', { class: 'audit-sub', text: t('{canvases} canvases examined · {withMag} carry a magnification '
              + '· {split} are a split value · {genuine} are a real magnification '
              + '· {undecidable} could not be decided and will be left alone', {
                canvases: fmt.format(scan.canvases), withMag: fmt.format(scan.canvasesWithMag),
                split: fmt.format(scan.split), genuine: fmt.format(scan.genuine),
                undecidable: fmt.format(scan.undecidable),
              }) }),
            el('table', { class: 'table audit-table' },
              el('thead', {}, el('tr', {},
                el('th', { text: t('format') }), el('th', { text: t('mag') }),
                el('th', { class: 'num', text: t('canvases') }), el('th', { text: t('example') }))),
              el('tbody', {}, ...scan.pairs.map((pair) => el('tr', {},
                el('td', { text: String(pair.format) }),
                el('td', { text: String(pair.mag) }),
                el('td', { class: 'num', text: fmt.format(pair.count) }),
                el('td', {}, el('code', { text: pair.example })))))),
            scan.split > 0
              ? el('button', {
                  class: 'btn btn-primary',
                  onclick: () => this.repairApply(),
                  'data-tip': t('Writes a new archive beside this one. The original is not touched.'),
                }, icon('save', { size: 15 }),
                t('Write a repaired copy ({n})', { n: fmt.format(scan.split) }))
              : el('p', { class: 'audit-sub', text: t('Nothing in this archive is a split value.') }),
            this.notes(scan.notes, t('What the scan concluded')))
        : null,

      result && result.output && !running
        ? el('div', { class: 'audit-card audit-muted' },
            el('h3', { text: t('{repaired} repaired · {decoded} decode from the saved file', {
              repaired: fmt.format(result.repaired), decoded: fmt.format(result.decoded),
            }) }),
            result.failedToDecode
              ? el('p', { class: 'audit-sub', text: t('{n} did not decode. Do not install this file.',
                  { n: fmt.format(result.failedToDecode) }) })
              : null,
            ...(result.failures ?? []).map((line) => el('p', { class: 'audit-sub', text: line })),
            el('p', { class: 'audit-sub', text: t('Written to:') }),
            el('code', { text: result.output }),
            // The command, not a button. Copying a repaired archive over a live
            // client is the one step this app will not take on the user's
            // behalf -- and the command takes the backup first, in the same
            // line, so there is no ordering to get wrong.
            el('p', { class: 'audit-sub', text:
              t('To install it, run this in PowerShell. It backs the current archive up first, '
                + 'and only copies if that backup succeeded:') }),
            el('pre', {}, el('code', { text: result.installCommand })),
            el('button', {
              class: 'btn',
              onclick: () => {
                navigator.clipboard?.writeText(result.installCommand);
                toast(t('Command copied'));
              },
            }, t('Copy command')),
            this.notes(result.notes, t('What the repair did')))
        : null);
  }

  notes(lines, title) {
    if (!lines?.length) return null;
    return el('div', { class: 'audit-card audit-muted' },
      el('h3', { text: title }),
      el('ul', {}, ...lines.map((line) => el('li', { text: line }))));
  }
}
