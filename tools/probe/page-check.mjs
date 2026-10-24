// Drives the real page through the Chrome DevTools Protocol and reports what
// the browser actually did.
//
// Why this exists: every other check in this project has been a syntax check or
// a curl against the API. A syntax check cannot see a ReferenceError -- the bug
// that blanked the simulator passed `node --check` cleanly, and every backend
// test passed with the section dead. Only running the page can see it.
//
// `--dump-dom` returns nothing on this machine, so this talks CDP over a
// websocket instead: it captures console messages and exceptions, evaluates
// expressions in the page, and screenshots.
//
// Usage: node tools/probe/page-check.mjs <baseUrl> [outPng]
//        node tools/probe/page-check.mjs --self-test [--dead-port 59997]
//
// THIS IS A MANUAL TOOL.  It is not registered in pm/SPEC.yaml, the gate does
// not run it, and it decides nothing about the build.  A page-level check
// needs a real browser and a live instance; the gate's suites are headless
// HTTP/WZ checks, and this one is not one of them.  Run it by hand against an
// instance you have confirmed is listening.
// Its failure output DOES carry the gate's red shape (`>>> RED` and
// `BAD=[1-9]`, per tools/verify/AGENTS.md rule 14), so wiring it in later is
// an addition to pm/SPEC.yaml's suites block plus a suite entry -- but that
// registration is deliberately NOT done here, because pm/SPEC.yaml and
// tools/pm.py are another owner's in-flight work.

import { spawn } from 'node:child_process';
import { mkdtempSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';

const EDGE = 'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe';
const SELF_TEST = process.argv.includes('--self-test');
const SELFTEST = process.argv[2] === '--self-test' || SELF_TEST;
const argv = process.argv.slice(2).filter((a) => !a.startsWith('--self-test'));
const deadPortArg = process.argv.indexOf('--dead-port');
const DEAD_PORT = deadPortArg >= 0 ? Number(process.argv[deadPortArg + 1]) : 59997;

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

// ---------------------------------------------------------------- the verdict

// A failed navigation commits the BROWSER'S OWN error page: 14k chars of "can't
// reach this page".  The check this replaces was `bodyLength > 500`, which that
// error page satisfies -- so a dead port scored PASS on "page loaded (body not
// empty)".  The tool was structurally unable to report red there (rule 14).
//
// Two INDEPENDENT paths decide it now, and either one can turn it red:
//   1. the DevTools transport -- Page.navigate reports errorText
//      (net::ERR_CONNECTION_REFUSED) when the navigation never happened;
//   2. the committed document -- a failed navigation lands on chrome-error://,
//      which `document.title`/`#app` do not survive.
const BROWSER_ERROR = /^chrome-error:\/\//i;

function pageLoadedVerdict(probe, navError) {
  const href = String(probe?.href ?? '');
  const title = String(probe?.title ?? '').trim();
  const appRoot = probe?.appRoot === true;
  if (navError) return { pass: false, detail: `navigation failed: ${navError}` };
  if (BROWSER_ERROR.test(href)) {
    return { pass: false, detail: `the browser committed its own error page at ${href}` };
  }
  if (!title && !appRoot) {
    return { pass: false, detail: `no page marker: no <title> and no #app (at ${href || 'an unknown url'})` };
  }
  return { pass: true, detail: `${title || '(no title)'} @ ${href}${appRoot ? ' +#app' : ''}` };
}

// ---------------------------------------------------------------- one browser run

async function runOnce(url, portOffset, wantShot) {
  const PORT = 9222 + (process.pid % 400) + (portOffset || 0);
  const profile = mkdtempSync(join(tmpdir(), 'mb-cdp-'));
  const edge = spawn(EDGE, [
    '--headless=new', '--disable-gpu', '--no-sandbox', '--no-first-run',
    '--disable-extensions', '--mute-audio',
    `--remote-debugging-port=${PORT}`,
    `--user-data-dir=${profile}`,
    '--window-size=1600,1000',
    url,
  ], { windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] });

  let edgeErr = '';
  edge.stderr.on('data', (d) => { edgeErr += d.toString(); });

  try {
    let targets = null;
    for (let i = 0; i < 40 && !targets; i++) {
      await sleep(500);
      try {
        const res = await fetch(`http://127.0.0.1:${PORT}/json/list`);
        targets = await res.json();
      } catch { /* not up yet */ }
    }
    if (!targets) return { ok: false, reason: 'devtools-unreachable', edgeErr };

    const page = targets.find((t) => t.type === 'page') ?? targets[0];
    const ws = new WebSocket(page.webSocketDebuggerUrl);

    let nextId = 1;
    const pending = new Map();
    const console_ = [];
    const exceptions = [];

    ws.addEventListener('message', (ev) => {
      const msg = JSON.parse(ev.data);
      if (msg.id && pending.has(msg.id)) {
        pending.get(msg.id)(msg.result ?? msg.error);
        pending.delete(msg.id);
        return;
      }
      if (msg.method === 'Runtime.consoleAPICalled') {
        const text = (msg.params.args ?? [])
          .map((a) => a.value ?? a.description ?? a.unserializableValue ?? '')
          .join(' ');
        console_.push({ level: msg.params.type, text });
      }
      if (msg.method === 'Runtime.exceptionThrown') {
        const d = msg.params.exceptionDetails;
        exceptions.push(d.exception?.description ?? d.text ?? 'unknown');
      }
      if (msg.method === 'Log.entryAdded') {
        const e = msg.params.entry;
        if (e.level === 'error') exceptions.push(`[${e.source}] ${e.text}`);
      }
    });

    const send = (method, params = {}) => new Promise((resolve) => {
      const id = nextId++;
      pending.set(id, resolve);
      ws.send(JSON.stringify({ id, method, params }));
    });

    await new Promise((r) => ws.addEventListener('open', r, { once: true }));
    await send('Runtime.enable');
    await send('Log.enable');
    await send('Page.enable');
    const nav = await send('Page.navigate', { url });
    // The browser's own account of the navigation.  Independent of anything
    // the page says about itself.
    const navError = String(nav?.errorText ?? nav?.message ?? '');

    // Let the page load the archive, list 5,366 maps and render.
    await sleep(9000);

    const evaluate = async (expression) => {
      const r = await send('Runtime.evaluate', {
        expression, returnByValue: true, awaitPromise: true,
      });
      if (r?.exceptionDetails) return { __error: r.exceptionDetails.text ?? 'error' };
      return r?.result?.value;
    };

    // NOTE what is NOT collected here, and why.  This probe runs BEFORE the
    // simulator is opened, so:
    //   * `selectCount` and `firstOptions` measured PAGE furniture -- the
    //     first <select> on the page is the sort dropdown, not the simulator's
    //     map picker, and the picker is an overlay inside .sim that is CLOSED
    //     on open (simulator.js:74-78).  Both are gone: they were the only
    //     consumers of the page-wide select list.
    //   * `canvas` could not be the simulator's canvas for the same reason,
    //     so it is read in the READ-BACK instead, after the open.
    const probe = await evaluate(`(() => {
      const q = (s) => document.querySelector(s);
      const root = q('#app') || document.body;
      const text = (root?.innerText ?? '').slice(0, 400);
      return {
        href: location.href,
        title: document.title,
        appRoot: !!q('#app'),
        bodyLength: document.body.innerHTML.length,
        textHead: text,
        simRoot: !!q('.sim'),
        simChildren: q('.sim')?.children.length ?? 0,
        errors: [...document.querySelectorAll('.sim-error, .err, .toast-error')].map(e => e.textContent.trim()).slice(0, 5),
      };
    })()`);

    // task-147.  This probe never entered the simulator.  `.sim` is built by
    // SimulatorSection's constructor and appended by sections.js:461-464, so
    // the two simulator checks below were measuring a section that had never
    // been opened, and could not pass on any instance at any time.
    //
    // MEASURED against 5100, and the order matters:
    //   sections.open('simulator') ALONE  -> resolves "ok", sections.active
    //     becomes "simulator", and the stage stays EMPTY.  Nothing is mounted,
    //     and it does not throw, so a caller that trusts "it did not throw"
    //     believes the section opened when it did not.
    //   open() THEN ensureSimulator()    -> .sim is in the stage.
    // This is the order browser-verify.mjs:173-175 already uses; it is the
    // order this probe now uses too, rather than one invented here.
    const enter = await evaluate(`(async () => {
      const app = window.app;
      if (!app || !app.sections || typeof app.sections.open !== 'function') {
        return { entered: false, reason: 'this page exposes no window.app.sections' };
      }
      try {
        await app.sections.open('simulator');
        if (typeof app.ensureSimulator === 'function') await app.ensureSimulator();
      } catch (e) {
        return { entered: false, reason: 'open/ensureSimulator threw: ' + (e && e.message) };
      }
      return { entered: true, active: app.sections.active ?? null };
    })()`);

    if (enter && enter.entered) {
      await sleep(4000); // let the simulator paint before asking about children
    }

    const shot = wantShot ? await send('Page.captureScreenshot', { format: 'png' }) : null;

    // Read the simulator back from the DOM, AFTER the open.
    const sim = (enter && enter.entered) ? await evaluate(`(() => {
      const q = (s) => document.querySelector(s);
      const app = window.app;
      const root = q('.sim');
      const stage = app?.sections?.stageFor?.('simulator');
      const canvas = q('canvas.walkview-canvas');
      const simulator = app?.simulator ?? null;
      return {
        active: app?.sections?.active ?? null,
        simRoot: !!root,
        simChildren: root ? root.children.length : 0,
        simHtml: (root ? root.innerHTML : '').slice(0, 160),
        stageKids: stage ? stage.children.length : -1,
        stageHtml: (stage ? stage.innerHTML : '').slice(0, 160),
        // The simulator's OWN state, read from the object the app built
        // (app.js:234-248 ensureSimulator -> new SimulatorSection).  This is
        // what the checks below judge; everything above it is the page's
        // furniture.
        //
        // simulatorPresent is separate because simulator.error ?? null
        // cannot tell "the simulator reported nothing" from "there is no
        // simulator at all" -- and that difference is the whole question.
        simulatorPresent: !!simulator,
        simulatorError: simulator ? (simulator.error ?? null) : null,
        // caps.available is the BACKEND's "a Map archive is open"
        // (MapEditorService.Simulate.cs:37), and the simulator renders an
        // empty state instead of a canvas when it is false
        // (simulator.js:379-386).  So false is a PRECONDITION, not a
        // fault, and null (the simulator was never opened) is one too.
        capsAvailable: simulator ? (simulator.caps?.available ?? null) : null,
        mapsLoaded: simulator ? (simulator.mapsLoaded ?? null) : null,
        mapsTotal: simulator ? (simulator.mapsTotal ?? null) : null,
        // Whether the page has any archive open.  The simulator section is
        // mounted only then (sections.js:174), so this is the precondition
        // that says "the simulator was never asked to work".
        archiveOpen: (app?.files?.length ?? 0) > 0,
        // The walk canvas, read HERE and not in the first probe: the first
        // probe runs before the simulator is opened.
        canvas: canvas ? { w: canvas.width, h: canvas.height } : null,
      };
    })()`) : null;

    ws.close();
    return { ok: true, probe, navError, console_, exceptions, shot, enter, sim };
  } finally {
    edge.kill();
  }
}

// ---------------------------------------------------------------- self-test

// A control needs an arm that can only pass when the reader works, and an arm
// that must fail.  ARM_MARKER is a page we wrote, with a known title: a reader
// that always says RED fails it, and a reader that always says PASS fails
// ARM_DEAD.  One arm alone proves nothing.
async function selfTest() {
  const html = '<!doctype html><html><head><title>SOAK-PROBE-MARKER</title></head>'
             + '<body><div id="app">marker</div></body></html>';
  const file = join(mkdtempSync(join(tmpdir(), 'mb-cdp-st-')), 'marker.html');
  writeFileSync(file, html);
  const fileUrl = pathToFileURL(file).href;

  console.log('SELFTEST_DEAD_PORT=' + DEAD_PORT);
  console.log('(59997 and 9 are both wrong choices: 9 is on Chromium\'s blocked-port\n'
            + ' list, so the request never leaves the browser and no backend-layer\n'
            + ' evidence is produced at all -- rule 19.)');

  let pass = 0, fail = 0;
  const say = (ok, line) => { ok ? pass++ : fail++; console.log('  ' + (ok ? 'PASS' : 'FAIL') + '  ' + line); };

  // ARM_MARKER -- must be GREEN, and the title must be the one we wrote.
  const green = await runOnce(fileUrl, 0, false);
  if (!green.ok) {
    say(false, `ARM_MARKER: the browser did not start (${green.reason})`);
  } else {
    const v = pageLoadedVerdict(green.probe, green.navError);
    say(v.pass === true, `ARM_MARKER verdict is true (detail: ${v.detail})`);
    say(String(green.probe?.title ?? '').includes('SOAK-PROBE-MARKER'),
        `ARM_MARKER read the title we wrote (got: ${String(green.probe?.title ?? '(none)')})`);
  }

  // ARM_DEAD -- must be RED, and the evidence must name the connection failure.
  const deadUrl = `http://127.0.0.1:${DEAD_PORT}`;
  const red = await runOnce(deadUrl, 7, false);
  if (!red.ok) {
    say(false, `ARM_DEAD: the browser did not start (${red.reason})`);
  } else {
    const v = pageLoadedVerdict(red.probe, red.navError);
    console.log(`  ARM_DEAD evidence: navError=${JSON.stringify(red.navError)} `
              + `href=${JSON.stringify(red.probe?.href)} `
              + `bodyLength=${red.probe?.bodyLength}`);
    say(v.pass === false, `ARM_DEAD verdict is false (detail: ${v.detail})`);
    say(/ERR_[A-Z_]+|refused|connect/i.test(`${v.detail} ${red.navError}`),
        `ARM_DEAD evidence names a connection failure`);
  }

  console.log(`\nSELFTEST_PASS=${pass}`);
  console.log(`SELFTEST_FAIL=${fail}`);
  console.log('RESULT=' + (fail ? 'FAIL' : 'PASS'));
  process.exit(fail ? 1 : 0);
}

if (SELFTEST) {
  await selfTest();
} else {

// ---------------------------------------------------------------- normal run

// The instance comes from the required <baseUrl> argument and from nowhere else
// (task-139) -- the usage line above has always said it is required.  What goes
// away is the DEFAULT PORT it used to carry.
const baseArg = (argv[0] || '').trim();
if (!baseArg) {
  console.log('>>> RED: <baseUrl> is not given, so there is no backend to measure.');
  console.log('       Usage: node ' + (process.argv[1] || 'this probe') + ' <baseUrl> [outPng]');
  console.log('       for example:  node ' + (process.argv[1] || 'this probe') + " http://127.0.0.1:<port> shots/out.png");
  console.log('       There is no default port on purpose: an older instance answers every one of these requests.');
  process.exit(1);
}
const base = baseArg.replace(/\/+$/, '');
const outPng = argv[1] ?? '';

const run = await runOnce(base, 0, !!outPng);
if (!run.ok) {
  console.error('  could not reach the DevTools endpoint');
  console.error(String(run.edgeErr).split('\n').slice(0, 6).join('\n'));
  process.exit(2);
}
const { probe, navError, console_, exceptions, enter, sim } = run;

if (outPng && run.shot?.data) {
  writeFileSync(outPng, Buffer.from(run.shot.data, 'base64'));
  console.log(`  screenshot -> ${outPng}`);
}

const loaded = pageLoadedVerdict(probe, navError);
const enterNote = enter?.entered
  ? `sections.active=${sim?.active ?? '(none)'}`
  : `did not enter: ${enter?.reason ?? 'unknown'}`;
const checks = [];
checks.push(['page loaded (not a browser error page)', loaded.pass, loaded.detail]);
checks.push(['no uncaught exception', exceptions.length === 0, exceptions[0] ?? '']);
checks.push(['no console error', !console_.some((c) => c.level === 'error'), console_.find((c) => c.level === 'error')?.text ?? '']);
// The two rows below read what the simulator left in the DOM.  There USED to
// be a third row here, "simulator section is the ACTIVE section", and it was
// measuring the SECTION REGISTRY rather than the simulator: sections.open()
// sets `active` before it mounts anything (sections.js:163) and returns
// early when no file is open (sections.js:174), so it went green on a page
// whose simulator section had never been mounted.  The simulator's own state
// is read below instead.
checks.push(['simulator section present', sim?.simRoot === true,
  sim?.simRoot ? `${sim?.simChildren ?? 0} children` : `stage: ${sim?.stageHtml ?? ''}`]);
// The caveat belongs to the FAILING case only.  Printed unconditionally it
// reads as a warning attached to a green cell, which is its own kind of lie.
checks.push(['simulator rendered children', (sim?.simChildren ?? 0) > 0,
  (sim?.simChildren ?? 0) > 0 ? `${sim?.simChildren ?? 0} children`
    : `0 children — an empty .sim means no map has been walked,`
      + ` which is a precondition of this instance, not a rendering fault`]);
// THE SIMULATOR'S OWN STATE.  The rows above ask whether the page carries
// the simulator's DOM; these ask what the simulator itself says.  They are
// the rows that make this file able to answer "is the simulator working?".
//
// `caps.available` is the backend's "a Map archive is open"
// (MapEditorService.Simulate.cs:37).  When it is false the simulator renders
// an empty state and NO canvas (simulator.js:379-386), so `false` -- and
// `null`, meaning the simulator was never opened -- are PRECONDITIONS and
// must not turn a row red.  Only "available, and nothing loaded" is a fault.
const simulatorAvailable = sim?.capsAvailable === true;
const capsNote = simulatorAvailable
  ? `${sim?.mapsLoaded ?? 0}/${sim?.mapsTotal ?? '?'} maps`
  : (sim?.capsAvailable === false
    ? 'no Map archive is open -- a precondition, not a fault'
    : 'the simulator was never opened -- a precondition, not a fault');
checks.push(['simulator instance exists', sim?.simulatorPresent === true,
  sim?.simulatorPresent ? 'the app built a simulator'
    : (enter?.entered
      ? 'the section WAS entered, but the app built no simulator'
      : (enter?.reason ?? 'the simulator section was not entered'))]);
checks.push(['simulator reported no error', !sim?.simulatorError,
  sim?.simulatorError ?? '']);
// `mapsLoaded > 0` is the verdict; the loaded-vs-total RATIO is in the
// detail, because "the picker is short" and "the archive is short" look
// identical from outside and only the ratio separates them
// (simulator.js:202-206).  A verdict on the ratio would false-red on an
// instance whose backend counts more maps than it lists.
checks.push(['simulator loaded the map list', !simulatorAvailable || (sim?.mapsLoaded ?? 0) > 0,
  capsNote]);
// The walk canvas is REPORTED, not judged.  Measured against a live
// instance with Map.nx open -- the simulator working (5366/5366 maps, no
// error, the instance present) -- there was NO canvas element at all: the
// walk canvas is only created once a map has been selected and walked, and
// this probe never selects one.  A row that asserts on it would false-red on
// every healthy instance, and a row that can never be false is decoration
// (tools/verify/AGENTS.md rule 14).  So the canvas stays a field the
// failure dump prints, and the walk itself is not this probe's business.
checks.push(['no error banner', (probe?.errors?.length ?? 0) === 0, (probe?.errors ?? []).join(' / ')]);

let failed = 0;
for (const [name, pass, detail = ''] of checks) {
  if (!pass) failed++;
  console.log(`  ${pass ? 'PASS' : 'FAIL'}  ${name}${detail ? `  — ${detail}` : ''}`);
}

if (failed) {
  console.log('\n  --- simulator entry ---');
  console.log('  entered=' + String(enter?.entered) + '  reason=' + (enter?.reason ?? '(n/a)'));
  console.log('  sections.active=' + String(sim?.active ?? '(none)')
    + '  .sim present=' + String(sim?.simRoot) + '  children=' + String(sim?.simChildren ?? 0)
    + '  stage children=' + String(sim?.stageKids ?? -1));
  console.log('  simulator present=' + String(sim?.simulatorPresent)
    + '  error=' + String(sim?.simulatorError ?? '(none)')
    + '  caps.available=' + String(sim?.capsAvailable ?? '(unknown)')
    + '  maps=' + String(sim?.mapsLoaded ?? '?') + '/' + String(sim?.mapsTotal ?? '?')
    + '  archiveOpen=' + String(sim?.archiveOpen)
    + '  canvas=' + (sim?.canvas ? `${sim.canvas.w}x${sim.canvas.h} (present)` : '(no canvas element)'));
  if (sim?.simHtml) console.log('  .sim innerHTML: ' + String(sim.simHtml).split('\n')[0].slice(0, 120));
  console.log('\n  --- page text (first 400 chars) ---');
  console.log('  ' + String(probe?.textHead ?? '').split('\n').slice(0, 8).join('\n  '));
  if (exceptions.length) {
    console.log('\n  --- exceptions ---');
    for (const e of exceptions.slice(0, 6)) console.log('  ' + String(e).split('\n')[0]);
  }
}

if (failed) {
  // The gate decides a suite's verdict from its RED markers and from
  // `BAD=[1-9]` (tools/verify/AGENTS.md rule 14).  A tool that fails and
  // prints neither is GREEN to the gate, so print both.
  console.log(`\n>>> RED: ${failed} check(s) failed on ${base}`);
  for (const [name, pass, detail = ''] of checks) {
    if (!pass) console.log(`    FAIL  ${name}${detail ? `  — ${detail}` : ''}`);
  }
  console.log(`\n${failed} check(s) failed`);
  console.log(`BAD=${failed}`);
} else {
  console.log('\npage behaves as expected');
  console.log('BAD=0');
}
process.exit(failed ? 1 : 0);

}