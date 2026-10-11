// Isolated real backend + synthetic IMG. No game files or existing editor sessions.
import { spawn, spawnSync } from 'node:child_process';
import fs from 'node:fs/promises';
import path from 'node:path';
import assert from 'node:assert/strict';
const root = process.cwd();
const work = path.resolve('tools/probe/real-' + Date.now());
const fixture = path.resolve('tools/AssetFixture/bin/Release/net10.0-windows/AssetFixture.exe');
function fixtureRun(args) {
  const result = spawnSync(fixture, args, { encoding: 'utf8' });
  assert.equal(result.status, 0, result.stdout + result.stderr); console.log(result.stdout.trim());
}
fixtureRun([work]);
const originalBytes = await fs.readFile(path.join(work, 'Fixture.img'));
let passed = false;
const port = 15973;
// Refuse to start if this fixed test port is already serving anything.
let occupied = false;
try { await fetch(`http://127.0.0.1:${port}`); occupied = true; } catch {}
assert.equal(occupied, false, 'Test port is occupied');
const backend = spawn(path.resolve('MapleBench/bin/Release/net10.0-windows/MapleBenchX.exe'), ['--port=' + port, '--allow-multiple', '--no-browser', '--no-warmup'], { cwd: root, stdio: ['ignore', 'pipe', 'pipe'] });
let logs = ''; backend.stdout.on('data', d => logs += d); backend.stderr.on('data', d => logs += d);
let child;
try {
  let ready = false;
  for (let i = 0; i < 100; i++) {
    if (backend.exitCode !== null) throw new Error('Backend exited: ' + logs);
    try { const res = await fetch(`http://127.0.0.1:${port}/api/files`); if (res.ok) { ready = true; break; } } catch {}
    await new Promise(r => setTimeout(r, 200));
  }
  assert.ok(ready, logs);
  const config = path.join(work, 'sources.json');
  await fs.writeFile(config, JSON.stringify({ fixture: { path: path.join(work, 'Fixture.img'), mapleVersion: 'BMS', readOnly: false } }));
  child = spawn(path.resolve('MapleMcp/bin/Release/net10.0-windows/maple-mcp.exe'), [], { env: { ...process.env, WZ_SERVER: `http://127.0.0.1:${port}`, MAPLEBENCH_SOURCES: config }, stdio: ['pipe', 'pipe', 'pipe'] });
  let buffer = '', id = 0;
  const pending = new Map();
  child.stderr.on('data', d => logs += d);
  child.stdout.on('data', d => {
    buffer += d;
    let n; while ((n = buffer.indexOf('\n')) >= 0) {
      const reply = JSON.parse(buffer.slice(0, n)); buffer = buffer.slice(n + 1);
      pending.get(reply.id)?.(reply); pending.delete(reply.id);
    }
  });
  function rpc(method, params) {
    return new Promise((resolve, reject) => {
      const key = ++id;
      const timer = setTimeout(() => reject(new Error('RPC timeout: ' + method)), 20000);
      pending.set(key, r => { clearTimeout(timer); resolve(r); });
      child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id: key, method, params }) + '\n');
    });
  }
  async function tool(name, args = {}) {
    const reply = await rpc('tools/call', { name, arguments: args });
    assert.ok(!reply.error && !reply.result.isError, JSON.stringify(reply));
    return JSON.parse(reply.result.content[0].text);
  }
  await rpc('initialize', { protocolVersion: '2025-03-26', capabilities: {}, clientInfo: { name: 'real-assets-test', version: '1' } });
  child.stdin.write(JSON.stringify({ jsonrpc: '2.0', method: 'notifications/initialized' }) + '\n');
  assert.equal((await tool('wz_asset_sources'))[0].alias, 'fixture');
  const opened = await tool('wz_asset_open_source', { source: 'fixture', commit: true });
  console.log('Opened:', JSON.stringify(opened));
  const tree = await tool('wz_asset_tree', { path: opened.id, depth: 2 });
  console.log('Tree:', JSON.stringify(tree));
  const imagePath = opened.id; // Loose IMG itself is the mounted root.
  await tool('wz_asset_set_values', { paths: [imagePath + '/value'], value: '42', commit: true });
  await tool('wz_asset_replace_canvas', { path: imagePath + '/sprite', inputPath: path.join(work, 'replacement.png'), commit: true });
  await tool('wz_asset_export_canvas', { path: imagePath + '/sprite', outputPath: path.join(work, 'candidate.png'), commit: true });
  await tool('wz_asset_export_img', { path: imagePath, outputPath: path.join(work, 'candidate.img'), commit: true });
  fixtureRun(['verify', work]);
  const reopened = await tool('wz_asset_open', { path: path.join(work, 'candidate.img'), mapleVersion: 'BMS', readOnly: true, commit: true });
  await tool('wz_asset_node', { path: reopened.id + '/value' });
  const refused = await rpc('tools/call', { name: 'wz_asset_set_value', arguments: { path: reopened.id + '/value', value: '99', commit: true } });
  assert.equal(refused.result.isError, true, 'Read-only candidate must reject edits');
  assert.deepEqual(await fs.readFile(path.join(work, 'Fixture.img')), originalBytes, 'Source must remain byte-identical');
  passed = true;
  console.log('PASS: real MCP/HTTP open alias, bulk scalar, canvas upload, PNG/IMG export, independent reparse, backend reopen.');
} catch (error) {
  await fs.writeFile(path.join(work, 'failure.log'), logs);
  console.error('Retained failure fixtures:', work); throw error;
} finally {
  const processes = [child, backend].filter(Boolean);
  const exited = processes.map(p => p.exitCode !== null ? Promise.resolve() : new Promise(resolve => p.once('exit', resolve)));
  for (const process of processes) process.kill();
  await Promise.all(exited);
  if (passed) await fs.rm(work, { recursive: true, force: true });
}
