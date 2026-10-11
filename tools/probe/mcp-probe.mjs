// Probe the MCP server the way a real client does: speak JSON-RPC over stdio.
//
//   node mcp-probe.mjs <path-to-wz-mcp.exe>
//
// A server that starts and then says nothing is indistinguishable from one that
// does not exist, so this sends initialize, then tools/list, and reports what
// actually came back. stdout is the protocol channel, which is why the server
// routes its own logging to stderr.

import { spawn } from 'node:child_process';

const exe = process.argv[2];
if (!exe) {
  console.error('usage: node mcp-probe.mjs <path-to-wz-mcp.exe>');
  process.exit(2);
}

const child = spawn(exe, [], { stdio: ['pipe', 'pipe', 'pipe'] });
const stderr = [];
let buffer = '';
const replies = new Map();

child.stderr.on('data', (d) => stderr.push(d.toString()));
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
const send = (method, params) => new Promise((resolve, reject) => {
  const id = nextId++;
  replies.set(id, resolve);
  child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id, method, params }) + '\n');
  setTimeout(() => {
    if (replies.has(id)) { replies.delete(id); reject(new Error(`${method}: no reply in 20s`)); }
  }, 20000);
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

  process.exit(0);
} catch (e) {
  console.log('\nERROR:', e.message);
  if (stderr.length) console.log('--- server stderr ---\n' + stderr.join('').slice(0, 800));
  process.exit(1);
}