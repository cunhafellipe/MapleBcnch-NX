(() => {
  const v = window.app?.simulator?.viewer;
  if (!v) return { error: 'no viewer' };
  const c = v.canvas, g = c.getContext('2d');
  const grab = () => g.getImageData(0, 0, c.width, c.height).data.slice();

  // Freeze the frame clock so both passes draw the SAME avatar frame. Otherwise
  // a pose change between passes can mask the comparison.
  const part = v.avatar?.part;
  if (part) { part.time = -1e9; }   // keeps step() from advancing the index

  v.draw();
  const withAvatar = grab();
  const idxA = part?.index ?? null;

  const real = v.drawAvatar;
  let calls = 0;
  v.drawAvatar = function () { calls++; };
  v.draw();
  const withoutAvatar = grab();
  v.drawAvatar = real;

  let diff = 0, minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity;
  for (let i = 0; i < withAvatar.length; i += 4) {
    if (withAvatar[i] !== withoutAvatar[i] || withAvatar[i+1] !== withoutAvatar[i+1] ||
        withAvatar[i+2] !== withoutAvatar[i+2] || withAvatar[i+3] !== withoutAvatar[i+3]) {
      diff++;
      const p = i / 4;
      const x = p % c.width, y = Math.floor(p / c.width);
      if (x < minX) minX = x; if (x > maxX) maxX = x;
      if (y < minY) minY = y; if (y > maxY) maxY = y;
    }
  }
  if (part) { part.time = 0; }

  return {
    drawAvatarCalls: calls,
    avatarFrameIndex: idxA,
    pixelsChangedByAvatar: diff,
    bbox: diff ? { x: minX, y: minY, w: maxX - minX + 1, h: maxY - minY + 1 } : null,
    canvas: [c.width, c.height],
  };
})()