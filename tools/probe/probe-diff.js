(() => {
  const v = window.app?.simulator?.viewer;
  if (!v) return { error: 'no viewer' };
  const c = v.canvas, g = c.getContext('2d');

  // No requestAnimationFrame: draw() paints synchronously into the context, and
  // a backgrounded tab never fires rAF, so awaiting it hangs forever.
  const grab = () => g.getImageData(0, 0, c.width, c.height).data.slice();

  v.draw();
  const withAvatar = grab();

  // Same draw, except drawAvatar blits nothing. Map art, layer stack, mobs and
  // every other mark still draw, so a differing pixel is the avatar's alone.
  const real = v.drawAvatar;
  let suppressedCalls = 0;
  v.drawAvatar = function () { suppressedCalls++; };
  v.draw();
  const withoutAvatar = grab();
  v.drawAvatar = real;
  v.draw();
  const restored = grab();

  let diff = 0, minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity;
  for (let i = 0; i < withAvatar.length; i += 4) {
    if (withAvatar[i] !== withoutAvatar[i] ||
        withAvatar[i+1] !== withoutAvatar[i+1] ||
        withAvatar[i+2] !== withoutAvatar[i+2] ||
        withAvatar[i+3] !== withoutAvatar[i+3]) {
      diff++;
      const p = i / 4;
      const x = p % c.width, y = Math.floor(p / c.width);
      if (x < minX) minX = x;
      if (x > maxX) maxX = x;
      if (y < minY) minY = y;
      if (y > maxY) maxY = y;
    }
  }
  let restoredClean = true;
  for (let i = 0; i < withAvatar.length; i += 4) {
    if (withAvatar[i] !== restored[i]) { restoredClean = false; break; }
  }

  return {
    suppressedCalls,
    pixelsChangedByAvatar: diff,
    bbox: diff ? { x: minX, y: minY, w: maxX - minX + 1, h: maxY - minY + 1 } : null,
    restoredMatchesBaseline: restoredClean,
    canvas: [c.width, c.height],
  };
})()