(async () => {
  const app = window.app;
  if (!app) return { error: 'no app' };
  try {
    // The real entry point (app.js:234): the section is lazy-loaded here and
    // assigned onto app.simulator. Calling app.simulator.open() first would
    // read undefined, which is why the page looks empty after a reload.
    const sim = await app.ensureSimulator();
    await sim.open();
    return {
      hasSimulator: !!app.simulator,
      mapCount: sim.maps?.length ?? null,
      hasViewer: !!sim.viewer,
      firstMap: sim.maps?.[0]?.id ?? null,
      hasTarget: (sim.maps ?? []).some((m) => String(m.id) === '109050001'),
    };
  } catch (e) {
    return { error: 'ensureSimulator threw: ' + e.message };
  }
})()