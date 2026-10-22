(() => {
  const v = window.app?.simulator?.viewer;
  if (!v) return { error: 'no viewer' };

  const seen = { draw: 0, drawWalk: 0, drawAvatar: 0, stepAvatar: 0, layout: 0 };
  const realDraw = v.draw, realDrawWalk = v.drawWalk, realDrawAvatar = v.drawAvatar;
  const realStepAvatar = v.stepAvatar, realLayout = v.layout;

  const wrap = (name, key) => function (...a) { seen[key]++; return realLayout ? realDrawApply(this, a) : undefined; };

  // Straightforward tap: count calls, always behave identically.
  v.draw = function (...a) { seen.draw++; return realDraw.apply(this, a); };
  v.drawWalk = function (...a) { seen.drawWalk++; return realDrawWalk.apply(this, a); };
  v.drawAvatar = function (...a) { seen.drawAvatar++; return realDrawAvatar.apply(this, a); };
  v.stepAvatar = function (...a) {
    seen.stepAvatar++;
    const r = realStepAvatar.apply(this, a);
    seen.stepAvatarReturn = r;
    return r;
  };
  v.layout = function (...a) { seen.layout++; return realLayout.apply(this, a); };

  v.draw();

  const out = {
    seen: { ...seen },
    ticksLen: v.ticks?.length ?? 0,
    hasUpTo: typeof v.upto === 'object',
    geometry: !!v.geometry,
    canvasCtx: !!v.canvas?.getContext?.('2d'),
    partState: v.avatar?.part?.state ?? null,
  };

  v.draw = realDraw; v.drawWalk = realDrawWalk; v.drawAvatar = realDrawAvatar;
  v.stepAvatar = realStepAvatar; v.layout = realLayout;
  return out;
})()