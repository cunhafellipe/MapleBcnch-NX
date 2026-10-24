// Probe the MCP server the way a real client does: speak JSON-RPC over stdio.
//
//   node mcp-probe.mjs <path-to-wz-mcp.exe>
//   node mcp-probe.mjs <exe> <args...> --call <toolName> [--args '<json>'] [--script '<json array>']
//
// A server that starts and then says nothing is indistinguishable from one that
// does not exist, so this sends initialize, then tools/list, and reports what
// actually came back. stdout is the protocol channel, which is why the server
// routes its own logging to stderr.
//
// --call exists because a tool that LISTED is not a tool that WORKS. Each step of
// --script is {tool, args}; `{{last}}` in an argument string is replaced with the
// JSON text of the previous step's reply, which is how a multi-step flow (open a
// session, summon a mob, cast, read the state) can be expressed without a client.

import { spawn } from 'node:child_process';
import { readFileSync } from 'node:fs';

// `--args @file.json` / `--script @file.json`. PowerShell strips the double
// quotes out of a native-command argument, so a JSON literal passed inline
// arrives as `{path:x}` and fails to parse -- which looks like a broken probe
// rather than a broken shell. A file is unambiguous.
const asJson = (value) => {
  if (value == null) return null;
  const text = value.startsWith('@') ? readFileSync(value.slice(1), 'utf8') : value;
  return JSON.parse(text);
};

const argv = process.argv.slice(2);
const flag = (name) => {
  const i = argv.indexOf(name);
  return i >= 0 ? argv[i + 1] : null;
};
const script = flag('--script');
const oneCall = flag('--call');
const callArgs = flag('--args');

// Everything before --call/--script/--args is the command, so a runtime shim can
// be passed as two tokens. The system `dotnet` on PATH is SDK 8 and cannot run the
// net10.0 MapleMcp binary, so the probe needs to reach SDK 10 some other way --
// this is the same trap docs/CLI-AND-MCP.md and 工作室守則.md both warn about.
const cmd = [];
for (const a of argv) {
  if (a === '--call' || a === '--args' || a === '--script') break;
  cmd.push(a);
}
const [exe, ...exeArgs] = cmd;
if (!exe) {
  console.error('usage: node mcp-probe.mjs <command> [args...] [--call <tool> [--args <json>]] [--script <json>]');
  process.exit(2);
}

// The spawn can fail in TWO ways, and they need different handling:
//   * a path that does not exist  -> an 'error' event (ENOENT), handled below;
//   * a path that exists but is not a program -> spawn() THROWS synchronously
//     (EFTYPE on Windows).  MEASURED: without this try/catch the probe died
//     with an uncaught 'node:internal/child_process' exception, and the
//     caller could not tell that from a crash of its own.
let child;
try {
  child = spawn(exe, exeArgs, { stdio: ['pipe', 'pipe', 'pipe'] });
} catch (err) {
  console.log('\nPROBE-STATE: child-never-started');
  console.log(`ERROR: initialize: the child never started: ${(err.code ?? '') + ' ' + (err.message ?? '')}`.trim());
  console.log(`the child never started: ${(err.code ?? '') + ' ' + (err.message ?? '')}`.trim());
  process.exit(1);
}
const stderr = [];
let buffer = '';
const replies = new Map();

// THE CHILD'S OWN STATE, because "a server that says nothing" and "a server
// that never started" are different facts.  A runtime that cannot start at
// all is visible in the child's stderr (the .NET 8 apphost trap that
// AGENTS.md records: the system `dotnet` on PATH is SDK 8 and cannot run the
// net10.0 MapleMcp binary), and a child that never started produces an
// 'error' event rather than an exit.  The state is printed as a
// PROBE-STATE line, so the caller can tell the three apart without
// guessing.
let childState = 'alive';        // alive | exited | never-started
let childExit = null;            // { code, signal }
let spawnError = null;

// A pending reply is rejected the moment the child can no longer answer, so
// a DEAD child fails in the second it dies instead of waiting out the reply
// timeout.  MEASURED before this: a child that had already exited made the
// probe sit for the full 120 s and then say "initialize: no reply in 120s",
// which reads as "the server is broken" and names neither the exit nor the
// stderr that says why.
const pending = new Map();        // id -> { reject, method }
const failPending = (why) => {
  for (const [id, p] of [...pending]) {
    pending.delete(id);
    replies.delete(id);
    p.reject(new Error(`${p.method}: ${why}`));
  }
};

child.on('error', (err) => {
  childState = 'never-started';
  spawnError = err;
  failPending(`the child never started: ${(err.code ?? '') + ' ' + (err.message ?? '')}`.trim());
});
child.on('exit', (code, signal) => {
  childState = 'exited';
  childExit = { code, signal };
  failPending(`the server exited (code ${code}, signal ${signal}) before answering`);
});

child.stderr.on('data', (d) => stderr.push(d.toString()));
// A child that never started takes stdin with it, and writing to it then
// raises EPIPE.  MEASURED: without this handler the probe died with an
// uncaught 'node:internal/child_process' error instead of reporting that
// the child never started -- which is the very thing the caller needs to
// know.
let stdinError = null;
child.stdin.on('error', (err) => { stdinError = err; });
child.stdout.on('error', () => { /* the pipe died with the child */ });
child.stdout.on('data', (d) => {
  buffer += d.toString();
  let nl;
  while ((nl = buffer.indexOf('\n')) >= 0) {
    const line = buffer.slice(0, nl).trim();
    buffer = buffer.slice(nl + 1);
    if (!line) continue;
    try {
      const msg = JSON.parse(line);
      if (msg.id !== undefined && replies.has(msg.id)) {
        replies.get(msg.id)(msg);
        replies.delete(msg.id);
      } else if (msg.method) {
        console.log('  (server notification)', msg.method);
      }
    } catch {
      console.log('  (non-JSON stdout)', line.slice(0, 120));
    }
  }
});

let nextId = 1;
// 20s was too short. MEASURED 2026-10-17: `wz_sim_open` on a COLD server has to
// parse Map.nx (678 MB, ~13.5 GB in memory) and the probe reported "no reply" for
// a call that was about to succeed -- which reads as a broken tool, not a cold one.
const REPLY_MS = Number(process.env.PROBE_TIMEOUT_MS ?? 120000);
const send = (method, params) => new Promise((resolve, reject) => {
  const id = nextId++;
  replies.set(id, resolve);
  pending.set(id, { reject, method });
  child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id, method, params }) + '\n');
  setTimeout(() => {
    if (replies.has(id)) {
      replies.delete(id);
      pending.delete(id);
      const why = childState === 'exited'
        ? `the server exited (code ${childExit?.code}, signal ${childExit?.signal}) before answering`
        : childState === 'never-started'
          ? 'the server never started'
          : 'the server was still running and said nothing';
      reject(new Error(`${method}: no reply in ${REPLY_MS / 1000}s (${why})`));
    }
  }, REPLY_MS);
});

try {
  console.log('=== initialize ===');
  const init = await send('initialize', {
    protocolVersion: '2024-11-05',
    capabilities: {},
    clientInfo: { name: 'mcp-probe', version: '1' },
  });
  console.log('server:', JSON.stringify(init.result?.serverInfo));
  console.log('protocol:', init.result?.protocolVersion);

  child.stdin.write(JSON.stringify({ jsonrpc: '2.0', method: 'notifications/initialized' }) + '\n');

  console.log('\n=== tools/list ===');
  const tools = await send('tools/list', {});
  const list = tools.result?.tools ?? [];
  console.log(`tool count: ${list.length}`);
  for (const t of list) console.log('  -', t.name);

  // The question that matters for map generation: can this server touch a map?
  const mapish = list.filter((t) => /map|tile|foothold|portal|obj/i.test(t.name + (t.description ?? '')));
  console.log(`\nmap-related tools: ${mapish.length}`);
  for (const t of mapish) console.log('  *', t.name, '-', (t.description ?? '').slice(0, 70));

  // ---- actually CALL something ------------------------------------------------
  // tools/list proves the tool is declared. It proves nothing about the route
  // behind it, and a tool that resolves a wrong path or a wrong body property
  // still lists perfectly.
  const steps = script
    ? asJson(script)
    : oneCall ? [{ tool: oneCall, args: asJson(callArgs) ?? {} }] : [];
  if (steps.length) {
    console.log(`\n=== tools/call x${steps.length} ===`);
    let last = '{}';
    // A session flow needs the same id three or four times and only the FIRST
    // reply carries it at top level; binding a skill, summoning and stepping
    // answer with the frame or a result object instead. Remembering any
    // `sessionId` seen at any depth, and letting a flow write `{{sessionId}}`, is
    // what makes those flows expressible at all -- otherwise step 3 posts
    // `/api/simulate/session//summon` and the backend refuses it for a reason that
    // has nothing to do with the tool under test.
    const vars = {};
    const remember = (text) => {
      try {
        (function walk(node) {
          if (node == null || typeof node !== 'object') return;
          if (!Array.isArray(node) && typeof node.sessionId === 'string' && node.sessionId) {
            vars.sessionId = node.sessionId;
          }
          for (const v of Object.values(node)) walk(v);
        })(JSON.parse(text));
      } catch { /* not JSON; nothing to remember */ }
    };
    for (const step of steps) {
      // `{{last.sessionId}}` reads a field out of the previous reply; `{{last}}`
      // is the whole reply; `{{sessionId}}` is the remembered session id.
      const walk = (path) => {
        if (path === 'sessionId' && vars.sessionId) return vars.sessionId;
        let node;
        try { node = JSON.parse(last); } catch { return last; }
        // the capture keeps the leading dot, so `{{last.a.b}}` arrives as ".a.b"
        for (const part of path.replace(/^\./, '').split('.').filter(Boolean)) {
          if (node == null) return undefined;
          node = node[part];
        }
        return node;
      };
      // The placeholder already sits inside a JSON string literal, so the
      // substituted value must be the ESCAPED value, not JSON.stringify(value) --
      // stringify would add a second pair of quotes and produce {"id":"""x""".
      const argsText = JSON.stringify(step.args ?? {}).replace(
        /\{\{(?:last((?:\.[\w]+)*)|sessionId)\}\}/g,
        (_, path) => JSON.stringify(walk(path || 'sessionId') ?? '').slice(1, -1));
      if (process.env.PROBE_TRACE) console.log('   args ->', argsText);
      let parsedArgs;
      try { parsedArgs = JSON.parse(argsText); }
      catch { console.log('\n>>> RED: mcp-probe -- argument substitution produced invalid JSON\n  ' + argsText); process.exit(1); }
      const res = await send('tools/call', { name: step.tool, arguments: parsedArgs });
      const text = res.result?.content?.map(c => c.text ?? '').join('\n') ?? '';
      const isError = res.result?.isError === true;
      last = text;
      remember(text);
      console.log(`\n-- ${step.tool} ${isError ? '(isError)' : ''}`);
      const cap = process.env.PROBE_FULL ? Number.MAX_SAFE_INTEGER : 1400;
      console.log(text.length > cap ? text.slice(0, cap) + '\n  ...[truncated]' : text);
      if (isError) { console.log('\n>>> RED: mcp-probe -- the tool returned isError'); process.exit(1); }
    }
  }

  console.log('\nPROBE-STATE: answered');
  process.exit(0);
} catch (e) {
  // The STATE, printed first and on its own line: the caller needs to tell
  // "the child never started" from "the child started and said nothing".
  const state = childState === 'never-started' ? 'child-never-started'
    : childState === 'exited' ? 'child-exited' : 'child-alive-silent';
  console.log('\nPROBE-STATE:', state);
  console.log('ERROR:', e.message);
  if (spawnError) {
    console.log(`the child never started: ${spawnError.code ?? ''} ${spawnError.message}`.trim());
  }
  if (stdinError && !spawnError) {
    console.log(`the child's stdin failed: ${stdinError.code ?? ''} ${stdinError.message}`.trim());
  }
  if (childExit) {
    console.log(`the child exited: code ${childExit.code} signal ${childExit.signal}`);
  }
  if (stderr.length) console.log('--- server stderr ---\n' + stderr.join('').slice(0, 800));
  process.exit(1);
}