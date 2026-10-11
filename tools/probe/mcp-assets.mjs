// Contract tests with an isolated fake backend. Does not touch game assets.
import { spawn } from 'node:child_process';
import http from 'node:http';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
const work = await fs.mkdtemp(path.resolve('tools/probe/asset-test-'));
await fs.mkdir(path.join(work, 'a'));
await fs.mkdir(path.join(work, 'b'));
await fs.writeFile(path.join(work, 'a', 'same.img'), 'fixture');
await fs.writeFile(path.join(work, 'b', 'same.img'), 'fixture');
const sourcesPath = path.join(work, 'sources.json');
await fs.writeFile(sourcesPath, JSON.stringify({ fixture: { path: work, mapleVersion: 'BMS' } }));
const calls = [];
const server = http.createServer(async (req, res) => {
  let text = ''; for await (const chunk of req) text += chunk;
  const body = text && req.headers['content-type']?.includes('application/json') ? JSON.parse(text) : text || null;
  calls.push({ method: req.method, url: req.url, body });
  if (req.url.startsWith('/api/export/img') || (req.method === 'GET' && req.url.startsWith('/api/canvas'))) {
    res.end(Buffer.from('test-candidate-payload')); return;
  }
  res.setHeader('Content-Type', 'application/json');
  if (req.url.includes('bad-node')) { res.statusCode = 400; res.end(JSON.stringify({ message: 'bad-node does not resolve' })); }
  else res.end(JSON.stringify({ ok: true, body, url: req.url }));
});
await new Promise(r => server.listen(0, '127.0.0.1', r));
const child = spawn(process.argv[2], [], { env: { ...process.env, MAPLEBENCH_SOURCES: sourcesPath, WZ_SERVER: `http://127.0.0.1:${server.address().port}` }, stdio: ['pipe', 'pipe', 'pipe'] });
let buffer = '', id = 0;
const pending = new Map();
child.stderr.on('data', d => process.stderr.write(d));
child.stdout.on('data', d => {
  buffer += d;
  let n; while ((n = buffer.indexOf('\n')) >= 0) {
    const line = buffer.slice(0, n); buffer = buffer.slice(n + 1);
    const reply = JSON.parse(line);
    if (pending.has(reply.id)) { pending.get(reply.id)(reply); pending.delete(reply.id); }
  }
});
function rpc(method, params) {
  return new Promise((resolve, reject) => {
    const key = ++id;
    const timer = setTimeout(() => { pending.delete(key); reject(new Error('RPC timeout')); }, 15000);
    pending.set(key, r => { clearTimeout(timer); resolve(r); });
    child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id: key, method, params }) + '\n');
  });
}
const tool = (name, args = {}) => rpc('tools/call', { name, arguments: args });
try {
  await rpc('initialize', { protocolVersion: '2025-03-26', capabilities: {}, clientInfo: { name: 'asset-test', version: '1' } });
  child.stdin.write(JSON.stringify({ jsonrpc: '2.0', method: 'notifications/initialized' }) + '\n');
  const listing = await rpc('tools/list', {});
  assert.equal(listing.result.tools.filter(t => t.name.startsWith('wz_asset_')).length, 22);
  assert.equal(listing.result.tools.filter(t => t.name.startsWith('wz_map_')).length, 10);
  assert.equal((await tool('wz_asset_open_source', { source: 'fixture', imgName: '../same.img', commit: true })).result.isError, true);
  assert.equal((await tool('wz_asset_open_source', { source: 'fixture', imgName: 'same.img', commit: true })).result.isError, true);
  assert.equal(calls.length, 0);
  await tool('wz_asset_open_source', { source: 'fixture', imgName: 'same.img', candidateIndex: 1, commit: true });
  assert.equal(calls.at(-1).body.readOnly, true);
  assert.equal(calls.at(-1).body.path, path.join(work, 'b', 'same.img'));
  calls.length = 0;
  for (const [name, args] of [
    ['open', { path: 'reference.nx' }], ['close', { fileId: 'f1' }],
    ['set_values', { paths: ['f1/a'], value: '1' }],
    ['set_value', { path: 'f1/a', value: '1' }], ['rename', { path: 'f1/a', name: 'b' }],
    ['add', { path: 'f1', type: 'Int', name: 'a' }],
    ['transfer', { paths: ['f1/a'], targetPath: 'f2' }], ['delete', { paths: ['f1/a'] }],
    ['undo', {}], ['save_as', { fileId: 'f1', targetPath: 'candidate.nx' }],
    ['export_img', { path: 'f1/a.img', outputPath: 'candidate.img' }],
    ['export_canvas', { path: 'f1/a', outputPath: 'candidate.png' }],
    ['replace_canvas', { path: 'f1/a', inputPath: 'input.png' }]
  ]) {
    const r = await tool('wz_asset_' + name, args);
    assert.ok(!r.result.isError, JSON.stringify(r));
  }
  assert.equal(calls.length, 0, 'all default previews must avoid backend calls');
  await tool('wz_asset_open', { path: 'reference.nx', commit: true });
  assert.equal(calls.at(-1).body.readOnly, true);
  await tool('wz_asset_node', { path: 'f1/中文/a' });
  assert.equal(new URL(calls.at(-1).url, 'http://test').searchParams.get('path'), 'f1/中文/a');
  await tool('wz_asset_set_value', { path: 'f1/a', value: '2', commit: true });
  assert.equal(calls.at(-1).method, 'PUT');
  await tool('wz_asset_close', { fileId: 'f1', commit: true });
  assert.equal(calls.at(-1).method, 'DELETE');
  await tool('wz_asset_save_as', { fileId: 'f1', targetPath: 'candidate.nx', commit: true });
  assert.equal(calls.at(-1).body.backup, true);
  const count = calls.length;
  assert.equal((await tool('wz_asset_search', { query: 'a', limit: 0 })).result.isError, true);
  assert.equal(calls.length, count);
  const error = await tool('wz_asset_node', { path: 'bad-node' });
  assert.equal(error.result.isError, true);
  assert.match(JSON.stringify(error), /bad-node does not resolve/);
  const output = path.join(work, 'candidate.img');
  const exported = await tool('wz_asset_export_img', { path: 'f1/a.img', outputPath: output, commit: true });
  assert.ok(!exported.result.isError, JSON.stringify(exported));
  assert.equal(await fs.readFile(output, 'utf8'), 'test-candidate-payload');
  const beforeOverwrite = calls.length;
  assert.equal((await tool('wz_asset_export_img', { path: 'f1/a.img', outputPath: output, commit: true })).result.isError, true);
  assert.equal(calls.length, beforeOverwrite);
  const input = path.join(work, 'input.png');
  await fs.writeFile(input, 'fake-input-image');
  await tool('wz_asset_replace_canvas', { path: 'f1/a', inputPath: input, commit: true });
  assert.match(calls.at(-1).body, /name=(?:"image"|image);/);
  assert.match(calls.at(-1).body, /fake-input-image/);
  assert.deepEqual((await fs.readdir(work)).sort(), ['a', 'b', 'candidate.img', 'input.png', 'sources.json']);
  console.log('PASS: 32 tools; trusted aliases; ambiguity and traversal rejection; 13 safe previews; read-only; Unicode; methods; backup; bounds; errors; binary export; no overwrite; multipart upload.');
} finally {
  child.kill(); server.close(); await fs.rm(work, { recursive: true, force: true });
}
