// A stub of the MapleBench endpoints a batch close touches, with one archive
// that refuses to close.
//
// The failure path cannot be produced against a real backend: Close() on an
// unknown id is a silent no-op returning 204, and the only way the server
// returns an error is file.Dispose() throwing. So the claim "a locked archive
// does not abort the batch" has never been tested against anything real --
// only against a mental model of one. This is that test, for real this time.
//
// Records the order requests arrive in, so "did not abort" is checked by what
// the server actually received rather than by what the CLI printed.

import { createServer } from 'node:http';

const PORT = Number(process.argv[2] ?? 5399);
const LOCKED = new Set((process.argv[3] ?? 'f2').split(',').filter(Boolean));
const log = [];

const FILES = [
  { id: 'f1', name: 'Character.wz', filePath: 'C:\\stub\\Character.wz', kind: 'wz', mapleVersion: 'GMS', gameVersion: 62, is64Bit: false, dirty: false, dirtyNodeCount: 0, detached: false, readOnly: false },
  { id: 'f2', name: 'String.wz', filePath: 'C:\\stub\\String.wz', kind: 'wz', mapleVersion: 'GMS', gameVersion: 62, is64Bit: false, dirty: true, dirtyNodeCount: 0, detached: false, readOnly: false },
  { id: 'f3', name: 'Morph.wz', filePath: 'C:\\stub\\Morph.wz', kind: 'wz', mapleVersion: 'GMS', gameVersion: 62, is64Bit: false, dirty: false, dirtyNodeCount: 0, detached: false, readOnly: false },
];

// f2 is dirty but its changeCount is deliberately absent from /api/changes is
// NOT what we are testing here; it IS listed, with a count that differs from
// its dirtyNodeCount of 0 -- the images-only trap, in the only shape that can
// prove the CLI reads the right field.
const CHANGES = {
  files: [{
    file: { ...FILES[1] },
    images: [{ name: '100.img', path: 'f2/100.img', fullPath: '\\100.img' }],
    nodes: [],
    structuralEdits: 0,
    changeCount: 7,
    unlistedChanges: false,
  }],
};

const json = (res, code, body) => {
  const text = JSON.stringify(body);
  res.writeHead(code, { 'content-type': 'application/json', 'content-length': Buffer.byteLength(text) });
  res.end(text);
};

createServer((req, res) => {
  const url = new URL(req.url, `http://127.0.0.1:${PORT}`);
  const path = url.pathname;
  log.push(`${req.method} ${path}${url.search}`);

  if (path === '/api/files' && req.method === 'GET') return json(res, 200, FILES);

  if (path === '/api/changes' && req.method === 'GET') return json(res, 200, CHANGES);

  if (path === '/api/families' && req.method === 'GET') return json(res, 200, []);

  const del = path.match(/^\/api\/files\/([^/]+)$/);
  if (del && req.method === 'DELETE') {
    const id = decodeURIComponent(del[1]);
    if (LOCKED.has(id)) {
      log.push(`  -> LOCKED ${id} (409)`);
      return json(res, 409, { error: `${id} is locked by another process` });
    }
    log.push(`  -> closed ${id}`);
    return res.writeHead(204).end();
  }

  return json(res, 404, { error: `no stub route for ${req.method} ${path}` });
}).listen(PORT, '127.0.0.1', () => {
  process.stdout.write(`stub listening on ${PORT}, locked: ${[...LOCKED].join(',') || '(none)'}\n`);
});

process.on('SIGTERM', () => {
  process.stdout.write(`\n=== requests the server received ===\n${log.join('\n')}\n`);
  process.exit(0);
});