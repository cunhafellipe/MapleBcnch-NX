(async () => {
  const app = window.app;
  const sim = app?.simulator;
  const v = sim?.viewer;
  if (!v) return { error: 'no viewer' };

  // Start the live body, which is what actually composes an avatar per action.
  let started = false;
  try {
    await sim.live?.start?.({ path: v.path });
    started = true;
  } catch (e) {
    return { error: 'live.start threw: ' + e.message };
  }

  const sleep = (ms) => new Promise(r => setTimeout(r, ms));
  await sleep(1200);

  // Walk right for a moment so the pose is a walk, not a stand.
  const fire = (type) => window.dispatchEvent(
    new KeyboardEvent(type, { code: 'ArrowRight', key: 'ArrowRight', bubbles: true }));
  fire('keydown');
  await sleep(1800);
  fire('keyup');
  await sleep(300);

  v.zoom = 4;
  v.draw();

  return {
    started,
    viewerPath: v.path,
    hasLive: !!sim.live,
    running: sim.live?.running ?? null,
    zoom: v.zoom,
    canvas: [v.canvas.width, v.canvas.height],
    state: v.avatar?.part?.state ?? null,
    frames: v.avatar?.part?.frames?.length ?? 0,
    bitmaps: v.avatar?.bitmaps?.size ?? 0,
    ready: !!v.avatar?.ready,
    reason: v.avatar?.reason ?? null,
    missingLayers: v.avatar?.missingLayers ?? [],
    ticks: v.ticks?.length ?? 0,
  };
})()