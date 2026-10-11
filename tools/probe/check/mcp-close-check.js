// Drives the wz-mcp stdio server the way an MCP client does: newline-delimited
// JSON-RPC over stdin/stdout, real initialize handshake, then tools/list and
// tools/call.
//
// "The tool is declared" and "the tool answers" are different claims, and only
// the second matters to the agent that will call it.
//
// Output is buffered and flushed at the end on purpose: process.exit() drops
// buffered stdout on a pipe, which is how a real failure here printed nothing
// at all and looked like a hang.

import { spawn } from 'node:child_process';
import { createInterface } from 'node:readline';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '../../..');
const [DLL, SERVER, IDS_CSV, RUNTIME, MODE, COMMIT] = [
  process.argv[2], process.argv[3], process.argv[4],
  process.env.DOTNET_PATH || 'dotnet',
  process.argv[6] ?? 'all', process.argv[7] ?? 'preview',
];

const log = [];
const say = (...a) => log.push(a.join(' '));
let exitCode = 0;
const pass = (what) => say('PASS  ' + what);
const fail = (what) => { say('FAIL  ' + what); exitCode = 1; };

const child = spawn(RUNTIME, [DLL], {
  env: { ...process.env, WZ_SERVER: SERVER },
  stdio: ['pipe', 'pipe', 'pipe'],
});

const pending = new Map();
let nextId = 1;
const serverErr = [];

createInterface({ input: child.stdout }).on('line', (line) => {
  if (!line.trim()) return;
  let msg;
  try { msg = JSON.parse(line); } catch { return; }
  if (msg.id != null && pending.has(msg.id)) {
    pending.get(msg.id)(msg);
    pending.delete(msg.id);
  }
});
child.stderr.on('data', (d) => serverErr.push(d.toString()));

function call(method, params, timeoutMs = 45000) {
  const id = nextId++;
  const p = new Promise((resolveP, rejectP) => {
    pending.set(id, resolveP);
    setTimeout(() => rejectP(new Error(`timeout waiting for ${method}`)), timeoutMs);
  });
  child.stdin.write(`${JSON.stringify({ jsonrpc: '2.0', id, method, params })}\n`);
  return p;
}

function callTool(args) {
  return call('tools/call', { name: 'wz_close', arguments: args });
}

// Comma-separated, not a JSON array: PowerShell strips the inner quotes of a
// JSON string argument before the process ever sees it, so ["f3","f4"] arrives
// as [f3,f4] and dies in JSON.parse.
const ids = IDS_CSV.split(',').map((s) => s.trim()).filter(Boolean);
const all = MODE === 'all';

try {
  await call('initialize', {
    protocolVersion: '2024-11-05', capabilities: {},
    clientInfo: { name: 'mcp-close-check', version: '1.0.0' },
  });
  child.stdin.write(`${JSON.stringify({
    jsonrpc: '2.0', method: 'notifications/initialized', params: {},
  })}\n`);

  const list = await call('tools/list', {});
  const tools = list.result?.tools ?? [];
  const close = tools.find((t) => t.name === 'wz_close');
  if (!close) {
    fail(`wz_close missing. registered: ${tools.map((t) => t.name).join(', ')}`);
  } else {
    pass('wz_close is registered');
    const props = Object.keys(close.inputSchema?.properties ?? {});
    say(`      params: ${props.join(', ')}`);
    say(`      ${String(close.description ?? '').split('\n')[0]}`);
    for (const need of ['fileIds', 'all', 'commit']) {
      if (props.includes(need)) say(`      has ${need}`);
      else fail(`wz_close is missing the ${need} parameter`);
    }
  }

  // Preview first: commit=false must change nothing.
  const preview = await callTool({ fileIds: ids, all, commit: false });
  const previewText = preview.result?.content?.[0]?.text ?? '';
  let previewJson;
  try { previewJson = JSON.parse(previewText); }
  catch {
    fail('wz_close preview did not return JSON');
    say(`      raw response: ${JSON.stringify(preview).slice(0, 1200)}`);
    previewJson = null;
  }
  if (previewJson) {
    previewJson.committed === false ? pass('commit=false returned committed:false')
      : fail(`commit=false returned committed:${previewJson.committed}`);
    say(`      targets: ${(previewJson.targets ?? [])
      .map((t) => `${t.id}:${t.name}:dirty=${t.dirty}:discard=${t.discardChanges}`).join('  ')}`);
    say(`      dirtyCount=${previewJson.dirtyCount} unsavedTotal=${previewJson.unsavedTotal} unknown=${JSON.stringify(previewJson.unknown)}`);
    if ((previewJson.targets ?? []).length === ids.length && ids.length) pass('preview listed every requested archive');
  }

  if (COMMIT === 'commit') {
    const applied = await callTool({ fileIds: ids, all, commit: true });
    const text = applied.result?.content?.[0]?.text ?? '';
    let appliedJson;
    try { appliedJson = JSON.parse(text); } catch { appliedJson = null; }
    if (!appliedJson) {
      fail('wz_close commit did not return JSON');
      say(`      raw response: ${JSON.stringify(applied).slice(0, 1200)}`);
    } else {
      appliedJson.committed === true ? pass('commit=true returned committed:true')
        : fail(`commit=true returned committed:${appliedJson.committed}`);
      say(`      closed: ${(appliedJson.closed ?? []).map((c) => c.id).join(',')}`);
      say(`      failed: ${(appliedJson.failed ?? []).map((f) => `${f.id}(${f.reason})`).join(',')}`);
    }
  }
} catch (e) {
  fail(String(e.message ?? e));
  if (serverErr.length) say(`      server stderr: ${serverErr.join('').slice(0, 800)}`);
}

child.kill();
if (serverErr.length) say(`\nserver stderr: ${serverErr.join('').slice(0, 600)}`);
process.stdout.write(`${log.join('\n')}\n`);
// exitCode, not exit(): a write to a pipe is async on Windows, and exit()
// drops whatever has not been flushed yet -- which is the whole report.
process.exitCode = exitCode;