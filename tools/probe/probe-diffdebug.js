(() => {
  const v = window.app?.simulator?.viewer;
  if (!v) return { error: 'no viewer' };
  const c = v.canvas, g = c.getContext('2d');
  const out = {};
  out.tickCount = v.ticks?.length ?? 0;
  out.lastTick = v.ticks?.[v.ticks.length - 1] ?? null;
  out.avatarState = v.avatar?.part?.state ?? null;
  out.avatarIndex = v.avatar?.part?.index ?? null;

  // Is draw() a no-op here? Check the guards at the top of draw().
  const src = String(v.draw);
  out.drawSrcHead = src.slice(0, 400);
  out.hasDraw = typeof v.draw === 'function';

  // A direct, observable blit test: paint a known colour into the exact rect
  // drawAvatar would use, and see whether the canvas records it. That isolates
  // "canvas accepts writes" from "draw() repaints at all".
  const before = g.getImageData(0, 0, c.width, c.height).data.slice();
  g.fillStyle = 'rgb(1,2,3)';
  g.fillRect(0, 0, 4, 4);
  const after = g.getImageData(0, 0, c.width, c.height).data.slice();
  let manualChanged = 0;
  for (let i = 0; i < before.length; i += 4) {
    if (before[i] !== after[i] || before[i+1] !== after[i+1] ||
        before[i+2] !== after[i+2] || before[i+3] !== after[i+3]) manualChanged++;
  }
  out.manualFillPixelsChanged = manualChanged;
  return out;
})()