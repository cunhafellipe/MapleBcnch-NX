// Render a composed look straight to a PNG, no browser needed.
//
//   node render-look.mjs <port> <outDir> [slotSetsJson]
//
// Reads the canvas back with toDataURL-equivalent (the endpoint returns the
// part list; the bytes come from /api/canvas per part) and composites with the
// same anchor maths the client uses.
//
// Why not a screenshot: Page.captureScreenshot returned a BLACK frame while the
// canvas held 577,589 non-background pixels. Pixel counts are the truth.

import { writeFileSync, mkdirSync } from 'node:fs';
import { join } from 'node:path';

const PORT = process.argv[2] ?? '5514';
const OUT = process.argv[3] ?? 'shots/equip';
mkdirSync(OUT, { recursive: true });
const BASE = `http://127.0.0.1:${PORT}`;

const SETS = [
  { name: '1-naked', equip: {} },
  { name: '2-cap-only', equip: { 'Cap-1': '01000000' } },
  { name: '3-four-slots', equip: { 'Cap-1': '01000000', Coat: '01040000', Shoes: '01070000', 'Weapon-1': '01302000' } },
  { name: '4-full-kit', equip: {
      'Cap-1': '01000000', 'Cap-2': '01000010', Coat: '01040000', Pants: '01060000',
      Shoes: '01070000', Glove: '01080000', Cape: '01102000', 'Weapon-1': '01302000',
      'Weapon-2': '01702000', 'Hair-1': '00030000', 'Face-1': '00020000',
    } },
];

async function post(p, b) {
  const r = await fetch(BASE + p, {
    method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(b),
  });
  if (!r.ok) return { error: r.status, body: await r.text() };
  return r.json();
}

// Minimal PNG writer: 8-bit RGBA, no filter, zlib via node.
import { deflateSync } from 'node:zlib';

function crc32(buf) {
  let c, crc = 0xffffffff;
  for (let n = 0; n < buf.length; n++) {
    c = (crc ^ buf[n]) & 0xff;
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    crc = c ^ (crc >>> 8);
  }
  return (crc ^ 0xffffffff) >>> 0;
}
function chunk(type, data) {
  const len = Buffer.alloc(4); len.writeUInt32BE(data.length);
  const td = Buffer.concat([Buffer.from(type, 'ascii'), data]);
  const crc = Buffer.alloc(4); crc.writeUInt32BE(crc32(td));
  return Buffer.concat([len, td, crc]);
}
function png(w, h, rgba) {
  const sig = Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(w, 0); ihdr.writeUInt32BE(h, 4);
  ihdr[8] = 8; ihdr[9] = 6; ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
  const raw = Buffer.alloc((w * 4 + 1) * h);
  for (let y = 0; y < h; y++) {
    raw[y * (w * 4 + 1)] = 0;
    rgba.copy(raw, y * (w * 4 + 1) + 1, y * w * 4, (y + 1) * w * 4);
  }
  return Buffer.concat([sig, chunk('IHDR', ihdr), chunk('IDAT', deflateSync(raw)), chunk('IEND', Buffer.alloc(0))]);
}

// Decode a PNG the server handed us into RGBA. Only what our own encoder emits
// plus the 8-bit forms NX uses: colour type 6 and 2, no interlace.
import { inflateSync } from 'node:zlib';
function decodePng(buf) {
  let p = 8, w = 0, h = 0, depth = 0, ct = 0;
  const idat = [];
  while (p < buf.length) {
    const len = buf.readUInt32BE(p);
    const type = buf.toString('ascii', p + 4, p + 8);
    const data = buf.subarray(p + 8, p + 8 + len);
    if (type === 'IHDR') { w = data.readUInt32BE(0); h = data.readUInt32BE(4); depth = data[8]; ct = data[9]; }
    else if (type === 'IDAT') idat.push(data);
    else if (type === 'IEND') break;
    p += 12 + len;
  }
  if (depth !== 8 || (ct !== 6 && ct !== 2)) throw new Error(`unsupported png depth=${depth} ct=${ct}`);
  const ch = ct === 6 ? 4 : 3;
  const raw = inflateSync(Buffer.concat(idat));
  const stride = w * ch;
  const out = Buffer.alloc(w * h * 4);
  let prev = Buffer.alloc(stride);
  for (let y = 0; y < h; y++) {
    const ft = raw[y * (stride + 1)];
    const line = Buffer.from(raw.subarray(y * (stride + 1) + 1, y * (stride + 1) + 1 + stride));
    for (let i = 0; i < stride; i++) {
      const a = i >= ch ? line[i - ch] : 0, b = prev[i], c = i >= ch ? prev[i - ch] : 0;
      let v = line[i];
      if (ft === 1) v += a;
      else if (ft === 2) v += b;
      else if (ft === 3) v += (a + b) >> 1;
      else if (ft === 4) { const pp = a + b - c, pa = Math.abs(pp - a), pb = Math.abs(pp - b), pc = Math.abs(pp - c); v += (pa <= pb && pa <= pc) ? a : (pb <= pc ? b : c); }
      line[i] = v & 0xff;
    }
    for (let x = 0; x < w; x++) {
      out[(y * w + x) * 4 + 0] = line[x * ch];
      out[(y * w + x) * 4 + 1] = line[x * ch + (ch === 4 ? 1 : 1)];
      out[(y * w + x) * 4 + 2] = line[x * ch + (ch === 4 ? 2 : 2)];
      out[(y * w + x) * 4 + 3] = ch === 4 ? line[x * ch + 3] : 255;
    }
    prev = line;
  }
  return { w, h, rgba: out };
}

const cache = new Map();
async function canvas(path) {
  if (cache.has(path)) return cache.get(path);
  const r = await fetch(`${BASE}/api/canvas?path=${encodeURIComponent(path)}`);
  if (!r.ok) { cache.set(path, null); return null; }
  const buf = Buffer.from(await r.arrayBuffer());
  const img = decodePng(buf);
  cache.set(path, img);
  return img;
}

for (const set of SETS) {
  const look = await post('/api/simulate/look', { fileId: 'f1', stance: 'stand1', frame: 0, equip: set.equip });
  if (look.error || !look.frames) { console.log(`${set.name}: HTTP ${look.error} ${(look.body || '').slice(0, 120)}`); continue; }
  const f = look.frames[0];
  const parts = [...f.parts].sort((a, b) => parseFloat(b.z || 0) - parseFloat(a.z || 0));

  const b = f.bounds;
  const pad = 6;
  const W = (b.maxX - b.minX) + pad * 2;
  const H = (b.maxY - b.minY) + pad * 2;
  const canvasBuf = Buffer.alloc(W * H * 4);
  // checker so transparency is visible
  for (let y = 0; y < H; y++) for (let x = 0; x < W; x++) {
    const v = ((x >> 3) + (y >> 3)) & 1 ? 58 : 40;
    const i = (y * W + x) * 4;
    canvasBuf[i] = v; canvasBuf[i + 1] = v; canvasBuf[i + 2] = v + 6; canvasBuf[i + 3] = 255;
  }

  let drawn = 0;
  for (const p of parts) {
    if (p.hidden) continue;
    const img = await canvas(p.path);
    if (!img) continue;
    const sx = p.screen.x - b.minX + pad;
    const sy = p.screen.y - b.minY + pad;
    for (let y = 0; y < img.h; y++) {
      const dy = sy + y;
      if (dy < 0 || dy >= H) continue;
      for (let x = 0; x < img.w; x++) {
        const dx = sx + x;
        if (dx < 0 || dx >= W) continue;
        const si = (y * img.w + x) * 4;
        if (img.rgba[si + 3] === 0) continue;
        const di = (dy * W + dx) * 4;
        canvasBuf[di] = img.rgba[si]; canvasBuf[di + 1] = img.rgba[si + 1];
        canvasBuf[di + 2] = img.rgba[si + 2]; canvasBuf[di + 3] = 255;
        drawn++;
      }
    }
  }
  const f2 = join(OUT, `${set.name}.png`);
  writeFileSync(f2, png(W, H, canvasBuf));
  const hidden = parts.filter((q) => q.hidden).map((q) => q.zName);
  console.log(`${set.name}: ${parts.length} parts, ${drawn} px, ${W}x${H}` +
    (hidden.length ? `  hidden: ${hidden.join(', ')}` : ''));
  if (look.unresolved?.length) console.log(`   unresolved: ${JSON.stringify(look.unresolved)}`);
}