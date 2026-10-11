// Drive the map MCP server the way an agent would, end to end.
//
//   node mcp-flow.mjs <maple-mcp.exe> <Map.nx abs path>
//
// Nothing here calls the HTTP API directly. If this passes, an agent connected
// over MCP can reach a playable map without ever seeing a URL — which is the
// entire point of the server.
//
// The steps mirror the order in ServerInstructions, because an agent will
// follow that order and a server whose own instructions do not work is a
// server nobody can use.

import { spawn } from 'node:child_process';

const [, , exe, mapPath] = process.argv;
if (!exe || !mapPath) {
  console.error('usage: node mcp-flow.mjs <maple-mcp.exe> <Map.nx abs path>');
  process.exit(2);
}

const child = spawn(exe, [], {
  stdio: ['pipe', 'pipe', 'pipe'],
  // The server reads WZ_SERVER; take it from this process's environment so the
  // test and the server cannot disagree about which backend they are talking
  // to. Hard-coding a port here is how this run first failed.
  env: process.env,
});
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
      const m = JSON.parse(line);
      if (m.id !== undefined && replies.has(m.id)) {
        replies.get(m.id)(m);
        replies.delete(m.id);
      }
    } catch { /* a log line on stdout is itself a finding; note it */ }
  }
});

let nextId = 1;
const rpc = (method, params) => new Promise((resolve, reject) => {
  const id = nextId++;
  replies.set(id, resolve);
  child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id, method, params }) + '\n');
  setTimeout(() => {
    if (replies.has(id)) { replies.delete(id); reject(new Error(`${method}: no reply in 120s`)); }
  }, 120000);
});

let failed = 0;
const check = (name, pass, detail) => {
  if (!pass) failed++;
  console.log(`${pass ? 'PASS' : 'FAIL'}  ${name}${detail ? `  — ${detail}` : ''}`);
};

/**
 * The map's current undo depth, read back through the server.
 *
 * The batch contract is "adds exactly one entry", which cannot be asserted
 * against an absolute number: anything this flow did earlier also occupies
 * history, and re-running it adds more. Comparing before and after is the only
 * statement that stays true across runs.
 */
async function currentDepth(path) {
  const d = await tool('wz_map_doc', { path });
  return d.undoDepth ?? 0;
}

// Calls a tool and parses its JSON text result. A tool that answers with prose
// instead of JSON is a defect, not something to be lenient about.
async function tool(name, args) {
  const r = await rpc('tools/call', { name, arguments: args ?? {} });
  if (r.error) throw new Error(`${name}: ${JSON.stringify(r.error)}`);
  const text = (r.result?.content ?? [])
    .map((c) => c.text ?? '')
    .join('\n');
  try {
    return JSON.parse(text);
  } catch {
    return { _unparsed: text.slice(0, 400) };
  }
}

try {
  await rpc('initialize', {
    protocolVersion: '2024-11-05', capabilities: {},
    clientInfo: { name: 'mcp-flow', version: '1' },
  });
  child.stdin.write(JSON.stringify({ jsonrpc: '2.0', method: 'notifications/initialized' }) + '\n');

  console.log('=== 1. capabilities ===');
  const caps = await tool('wz_map_capabilities');
  check('reachable', caps.reachable === true, caps.baseUrl);

  // The archive has to be open before any map tool means anything.
  if (caps.capabilities?.available !== true) {
    console.log('\n  no archive open on the backend — opening via HTTP is out of scope here.');
    console.log('  start the backend with Map.nx already loaded, then re-run.');
    process.exit(2);
  }
  const mapCount = caps.capabilities?.mapCount ?? 0;
  check('maps available', mapCount > 0, `${mapCount} maps`);

  console.log('\n=== 2. list ===');
  const list = await tool('wz_map_list', { limit: 5 });
  check('map list returns rows', (list.maps?.length ?? 0) > 0,
        `${list.maps?.length ?? 0} of ${list.total ?? '?'}`);
  const target = list.maps?.[0];
  check('a row carries a usable path', !!target?.path, target?.path);

  console.log('\n=== 3. open ===');
  const doc = await tool('wz_map_open', { path: target.path });
  check('map opens', (doc.layers?.length ?? 0) > 0, `${doc.layers?.length} layers`);
  check('no link-stub warning for a real map', !doc.warning, doc.warning ?? 'none');

  console.log('\n=== 4. palette ===');
  const pal = await tool('wz_map_palette', { kind: 'Tile' });
  check('tile sets listed', (pal.total ?? 0) > 0, `${pal.total} sets`);
  check('every set reports drawable entries', (pal.emptySets?.length ?? 0) === 0,
        `${pal.emptySets?.length ?? 0} empty, ${pal.usableCount} usable`);
  const set = pal.usable?.[0];
  check('a usable set has a name', !!set?.name, set?.name);

  console.log('\n=== 5. place (preview must not write) ===');
  const preview = await tool('wz_map_place', {
    path: target.path, kind: 'tile', x: -300, y: 200, layer: 0,
    set: 'ancientForest', u: 'bsc', no: 0,
  });
  check('preview is a preview', preview.preview === true, JSON.stringify(preview).slice(0, 80));

  console.log('\n=== 6. place (committed) ===');
  const placed = await tool('wz_map_place', {
    path: target.path, kind: 'tile', x: -300, y: 200, layer: 0,
    set: 'ancientForest', u: 'bsc', no: 0, commit: true,
  });
  check('tile placed', Array.isArray(placed.placed), JSON.stringify(placed.placed));

  console.log('\n=== 7. validate ===');
  const valid = await tool('wz_map_validate', { path: target.path });
  check('validate returns a verdict', typeof valid.ok === 'boolean',
        `ok=${valid.ok} errors=${valid.errors?.length} warnings=${valid.warnings?.length} ` +
        `footholds=${valid.footholds} brokenChains=${valid.brokenChains}`);

  console.log('\n=== 8. batch ===');
  // 300 scalar edits must collapse into one history entry: the stack holds 200.

  // The probe must commit: wz_map_batch defaults to preview, so a probe
  // without commit reads back its own preview and finds nothing. That is the
  // tool working correctly — the probe was the thing at fault.
  const tryAddr = async (addr, value) => {
    const probeOps = JSON.stringify([{ kind: 'setValue', addr, value }]);
    const probe = await tool('wz_map_batch', {
      path: target.path, opsJson: probeOps, label: 'probe', commit: true,
    });
    if (probe.applied > 0) return true;
    console.log(`  (probe refused at ${JSON.stringify(addr)}: ${probe.error ?? 'preview only'})`);
    return false;
  };

  // Find a writable leaf by reading the node, the way an agent would. Both
  // addresses above turned out to be containers, and the backend refuses to
  // write one — so the leaf has to be looked up rather than guessed.
  // The address of the `u` field is found rather than assumed — a tile's
  // children are ordered x/y/u/no/zM depending on how it was written, and the
  // backend refuses to retype a node, so guessing produces a clean-looking run
  // that changed nothing.
  // Find a writable leaf by reading the node, the way an agent would. Both
  // addresses above turned out to be containers, and the backend refuses to
  // write one — so the leaf has to be looked up rather than guessed.
  let uAddr = null, uVal = null;
  const tile = doc.layers?.flatMap((l) => l.tiles ?? [])[0];
  const candidates = [tile?.addr, doc.footholds?.[0]?.addr].filter(Boolean);

  for (const addr of candidates) {
    const node = await tool('wz_map_node', { path: target.path, addr: addr.join(',') });
    const leaf = (node.children ?? []).find(
      (c) => c.value !== undefined && c.value !== null && c.type !== 'SubProperty');
    if (!leaf) continue;
    if (await tryAddr(leaf.addr, String(leaf.value))) {
      uAddr = leaf.addr;
      uVal = String(leaf.value);
      console.log(`  (writable leaf at ${leaf.addr.join(',')} = ${leaf.name})`);
      break;
    }
  }

  if (uAddr) {
    const ops = JSON.stringify(Array.from({ length: 300 }, () => ({
      kind: 'setValue', addr: uAddr, value: uVal,
    })));
    const dry = await tool('wz_map_batch', { path: target.path, opsJson: ops });
    check('batch preview does not write', dry.preview === true, `${dry.operationCount} ops queued`);

    const depthBefore = await currentDepth(target.path);
    const committed = await tool('wz_map_batch', {
      path: target.path, opsJson: ops, label: 'mcp-flow', commit: true,
    });
    check('batch applied', committed.applied > 0,
          `applied=${committed.applied}/${committed.requested} ` +
          `error=${committed.error ?? 'none'} failedAt=${committed.failedAt}`);
    check('history collapsed to one entry', committed.undoDepth === depthBefore + 1,
          `${depthBefore} -> ${committed.undoDepth} for ${committed.applied} ops`);
  } else {
    console.log('  (could not find a scalar field on a tile — batch untested)');
    check('batch is reachable', false, 'no editable node found; the history claim is unproven');
  }

  console.log('\n=== 9. save (preview must not write) ===');
  const drySave = await tool('wz_map_save', { path: target.path });
  check('save preview is a preview', drySave.preview === true, drySave.note?.slice(0, 60));

  console.log(`\n${failed === 0 ? 'ALL PASSED' : failed + ' FAILED'}`);
  if (stderr.length) console.log('\n--- server stderr ---\n' + stderr.join('').slice(0, 400));
  process.exit(failed === 0 ? 0 : 1);
} catch (e) {
  console.log('\nERROR:', e.message);
  if (stderr.length) console.log('--- server stderr ---\n' + stderr.join('').slice(0, 800));
  process.exit(1);
}