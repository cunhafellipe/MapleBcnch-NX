/**
 * Reports anything that goes wrong in the page to the server.
 *
 * Why this file exists: this project checked itself with `node --check` and
 * with curl against the API, and both pass while the page is broken. A leftover
 * `this.pickerHost = list` in simulator.js -- referring to a variable that no
 * longer existed -- passed every one of those checks, and the simulator section
 * simply did not render. The browser knows exactly what happened; nothing here
 * listened to it.
 *
 * So: uncaught errors, promise rejections and console errors are POSTed to the
 * server, where they can be read without a developer console. Load this before
 * app.js so a module that throws during evaluation is still caught.
 */

const REPORT_URL = '/api/clientlog';

/** Keep the payload small enough to read at a glance. */
function trim(value, max = 600) {
  const text = typeof value === 'string' ? value : safeStringify(value);
  return text.length > max ? `${text.slice(0, max)}…` : text;
}

function safeStringify(value) {
  try {
    if (value instanceof Error) return `${value.name}: ${value.message}\n${value.stack ?? ''}`;
    return JSON.stringify(value);
  } catch {
    return String(value);
  }
}

function report(kind, detail, extra = {}) {
  const entry = {
    kind,
    detail,
    url: location.pathname + location.search,
    at: new Date().toISOString(),
    ...extra,
  };
  // Fire and forget. A failed report must never become a second error.
  try {
    fetch(REPORT_URL, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(entry),
      keepalive: true,
    }).catch(() => {});
  } catch { /* nothing more to do */ }
}

window.addEventListener('error', (event) => {
  report('error', trim(event.message ?? 'error'), {
    file: event.filename,
    line: event.lineno,
    column: event.colno,
    stack: trim(event.error?.stack ?? '', 1200),
  });
});

window.addEventListener('unhandledrejection', (event) => {
  report('unhandledrejection', trim(safeStringify(event.reason)), {
    stack: trim(event.reason?.stack ?? '', 1200),
  });
});

// Console errors are the ones a developer would otherwise read by eye.
const nativeError = console.error.bind(console);
console.error = (...args) => {
  report('console.error', args.map((a) => trim(safeStringify(a), 300)).join(' '));
  nativeError(...args);
};

const nativeWarn = console.warn.bind(console);
console.warn = (...args) => {
  report('console.warn', args.map((a) => trim(safeStringify(a), 300)).join(' '));
  nativeWarn(...args);
};

// Say so, so the page itself carries evidence that reporting is live.
report('ready', 'error reporting installed', { userAgent: navigator.userAgent });