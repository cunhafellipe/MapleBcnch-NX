(function () {
  const v = window.app?.simulator?.viewer;
  if (!v) return { error: 'no viewer' };
  const cur = v.ticks?.[0];
  if (!cur) return { error: 'no tick' };

  const part = v.avatar.part;
  const moving = cur?.action === 'walk'
    || (cur?.speed ?? 0) > 0.01
    || (!cur?.onGround && !cur?.onLadder);
  const state = moving ? 'walk1' : 'stand1';

  return {
    curAction: cur.action,
    curOnGround: cur.onGround,
    curOnLadder: cur.onLadder ?? null,
    curSpeed: cur.speed ?? null,
    moving: moving,
    derivedState: state,
    partState: part?.state ?? null,
    partFrames: part?.frames?.length ?? 0,
    guardPasses: part?.state === state,
    stepReturns: v.avatar.step(cur),
    stepReturns2: v.avatar.step(cur),
    avatarStateField: v.avatarState,
  };
})()