(async () => {
  const app = window.app;
  const sim = app?.simulator;
  if (!sim) return { error: 'no simulator' };
  const sleep = (ms) => new Promise(r => setTimeout(r, ms));

  // The archive id is handed out in the order the archives were opened, so 'f1'
  // is Map.nx on one instance and something else on another. Resolve it by
  // NAME. A browser snippet has no exit code, so the red marker goes to the
  // page console AND comes back as the value cdp-eval prints.
  const archiveId = async (name) => {
    let files;
    try { files = await (await fetch('/api/files')).json(); }
    catch (exc) { return { error: `>>> RED: could not list the open archives on this instance: ${exc.message ?? exc}` }; }
    const hit = (files ?? []).find((f) => f && f.name === name);
    if (!hit) return { error: `>>> RED: ${name} is not open on this instance, so there is nothing to judge` };
    if (!hit.id) return { error: `>>> RED: ${name} is listed by this instance but carries no id` };
    return { id: hit.id };
  };
  const mapArchive = await archiveId('Map.nx');
  if (mapArchive.error) { console.log(mapArchive.error); return mapArchive; }

  const open = await fetch('/api/mapedit/open', {
    method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ path: `${mapArchive.id}/Map/Map1/109050001.img` }),
  });
  if (!open.ok) return { error: 'open ' + open.status };

  const map = (sim.maps ?? []).find((m) => String(m.id) === '109050001');
  if (!map) return { error: 'map missing' };
  await sim.walk(map);

  await sim.live?.start?.({ path: sim.viewer.path });
  await sleep(1200);

  const fire = (type, code) => window.dispatchEvent(
    new KeyboardEvent(type, { code, key: code, bubbles: true }));
  fire('keydown', 'ArrowRight');
  await sleep(1800);
  fire('keyup', 'ArrowRight');
  await sleep(600);

  const v = sim.viewer;
  const tick = v.ticks?.[v.ticks.length - 1];
  if (!tick) return { error: 'no tick after walk' };

  const want = tick.action === 'walk' ? 'walk1' : 'stand1';
  if (v.avatar.part?.state !== want) {
    v.avatarState = '';
    const ok = await v.avatar.load(want);
    if (!ok) return { error: 'load ' + want, reason: v.avatar.reason };
    v.avatarState = want;
  }

  v.ticks = [tick];
  v.frame = 0;
  v.zoom = 1.6;
  for (let i = 0; i < 800; i++) v.follow(tick.x, tick.y);

  const c = v.canvas, g = c.getContext('2d');
  const part = v.avatar.part;
  part.time = -1e9;
  part.index = 0;

  v.draw();
  const withA = g.getImageData(0, 0, c.width, c.height).data.slice();
  const real = v.drawAvatar;
  let calls = 0;
  v.drawAvatar = function (...a) { calls++; return real.apply(this, a); };
  v.draw();
  const observed = calls;
  v.drawAvatar = function () {};
  v.draw();
  const withoutA = g.getImageData(0, 0, c.width, c.height).data.slice();
  v.drawAvatar = real;

  let diff = 0, bx0 = Infinity, by0 = Infinity, bx1 = -Infinity, by1 = -Infinity;
  for (let i = 0; i < withA.length; i += 4) {
    if (withA[i] !== withoutA[i] || withA[i+1] !== withoutA[i+1] ||
        withA[i+2] !== withoutA[i+2] || withA[i+3] !== withoutA[i+3]) {
      diff++;
      const p = i / 4;
      const x = p % c.width, y = Math.floor(p / c.width);
      if (x < bx0) bx0 = x; if (x > bx1) bx1 = x;
      if (y < by0) by0 = y; if (y > by1) by1 = y;
    }
  }
  part.time = 0;
  v.draw();

  const view = v.viewBox;
  const s = view.scale;
  const px = (x) => (x - view.minX) * s;
  const py = (y) => (y - view.minY) * s;
  const dir = tick.dir ?? 1;
  const at = v.avatar.placement(tick.x, tick.y, dir);
  const fr = part.frames[0];
  const anchorX = part.anchorX ?? fr.originX;
  const anchorY = part.anchorY ?? fr.originY;
  const layers = fr.layers?.length ? fr.layers : [fr];
  const body = layers[layers.length - 1];
  const bottom = at.y + (anchorY - body.originY) + body.height;

  const data = g.getImageData(0, 0, c.width, c.height).data;
  let nonBlank = 0;
  for (let i = 0; i < data.length; i += 4) {
    if (data[i] || data[i+1] || data[i+2]) nonBlank++;
  }

  return {
    map: v.path,
    canvas: [c.width, c.height],
    scale: +s.toFixed(3), zoom: v.zoom,
    cam: { x: +v.cam.x.toFixed(2), y: +v.cam.y.toFixed(2) },
    tick: { x: +tick.x.toFixed(3), y: +tick.y.toFixed(3), action: tick.action,
            dir, onGround: tick.onGround },
    avatar: { ready: !!v.avatar.ready, state: part.state, frames: part.frames.length,
              bitmaps: v.avatar.bitmaps.size, missing: v.avatar.missingLayers ?? [] },
    placement: at,
    placementTypes: Object.fromEntries(Object.entries(at).map(([k, x]) => [k, typeof x])),
    layerRects: layers.map((L) => ({ part: L.path.split('/').pop(),
      screen: [+px(at.flip
        ? at.x + at.w - (anchorX - L.originX) - L.width
        : at.x + (anchorX - L.originX)).toFixed(1),
        +py(at.y + (anchorY - L.originY)).toFixed(1),
        +(L.width * s).toFixed(1), +(L.height * s).toFixed(1)] })),
    drawAvatarCalls: observed,
    pixelsChangedByAvatar: diff,
    avatarRectScreen: diff ? { x: bx0, y: by0, w: bx1 - bx0 + 1, h: by1 - by0 + 1 } : null,
    nonBlank, totalPx: data.length / 4,
    feet: {
      tickY: tick.y,
      avatarBottomWorld: +bottom.toFixed(6),
      deltaFromTickY: +(bottom - tick.y).toFixed(6),
      screenTickY: +py(tick.y).toFixed(1),
      screenAvatarBottomY: +py(bottom).toFixed(1),
    },
  };
})()