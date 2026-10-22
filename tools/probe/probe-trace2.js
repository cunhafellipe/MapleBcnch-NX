(function () {
  const v = window.app?.simulator?.viewer;
  if (!v) return { error: 'no viewer' };
  const cur = v.ticks?.[0];
  const part = v.avatar.part;

  const out = { before: { time: part.time, index: part.index, state: part.state } };

  // Trace what step() sees from INSIDE draw(), without touching the clock first.
  const realStepAvatar = v.stepAvatar;
  let trace = null;
  v.stepAvatar = function (t) {
    const r = realStepAvatar.call(this, t);
    trace = {
      tickAction: t?.action,
      tickOnGround: t?.onGround,
      derivedState: t?.action === 'walk' ? 'walk1' : 'stand1',
      partState: this.avatar?.part?.state ?? null,
      returns: r,
    };
    return r;
  };
  v.draw();
  out.trace = trace;

  const realDrawWalk = v.drawWalk;
  let walkArgs = null;
  v.drawWalk = function (...a) {
    walkArgs = { uptoLen: a[4], avatarFrame: a[6], curExists: !!a[5] };
    return realDrawWalk.apply(this, a);
  };
  v.draw();
  out.walkArgs = walkArgs;
  v.drawWalk = realDrawWalk;
  v.stepAvatar = realStepAvatar;

  return out;
})()