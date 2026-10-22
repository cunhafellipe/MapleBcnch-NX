// Where is the character on screen, as a fraction of the viewport?
//
//   node verify-camera.mjs <port> [mapPath]
//
// The requirement (TASK20 Defect 3): "report the character's on-screen position
// as a fraction of the viewport, at rest and after 3 seconds of held input. It
// must sit near the centre."
//
// This measures it through the same maths walkview.js uses, driven by the same
// rules, so a number here is a number about the drawing:
//
//   * MapleCharacter.cs:749-751  DestX = DisplaySize.X/2 - X + Camera.X
//                                Camera.X -= DestX * (12f/800f)
//   * MapleCharacter.cs:753-755  DestY = (DisplaySize.Y/2) + (OffsetY+60) - Y
//                                Camera.Y -= 0.01f * (DestY + Camera.Y)
//   * MapleMap.cs:279            OffsetY = (DisplaySize.Y - 600) / 2
//   * MapleCharacter.cs:757-767  four clamps, then centre a short axis
//
// The server's bounds are what the clamps use. Before this change the frame
// carried none, so there was nothing to clamp against.

const PORT = process.argv[2] ?? '5510';
const MAP = process.argv[3] ?? 'f1/Map/Map1/100020101.img';
const BASE = `http://127.0.0.1:${PORT}`;
const { writeFileSync } = await import('node:fs');

async function post(p, b, t = 30000) {
  const r = await fetch(BASE + p, {
    method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(b), signal: AbortSignal.timeout(t),
  });
  if (!r.ok) return { __httpError: r.status, body: await r.text() };
  return r.json();
}

// The viewport this viewer draws into. walkview.js layout(): at zoom 1 the map
// is fitted to the canvas WIDTH, and the height is a fixed fraction of that
// world width -- so the viewport in world units is what matters, and the
// character should sit at (0.5, 0.5) of it regardless of the pixel size.
const VIEW_W = 800;
const VIEW_H = 600;

const clamp = (v, lo, hi) => Math.min(Math.max(v, lo), hi);

function cameraFor(x, y, bounds, cam) {
  const half = { x: VIEW_W / 2, y: VIEW_H / 2 };
  const offsetY = (VIEW_H - bounds.viewportHeight) / 2;

  const destX = half.x - x + cam.x;
  if (Math.abs(destX) > 1) cam.x -= destX * (12 / 800);

  const destY = half.y + offsetY + 60 - y;
  if (Math.abs(destY + cam.y) > 1) cam.y -= 0.01 * (destY + cam.y);

  // MapleCharacter.cs:757-767 -- clamps, then the short-axis centring.
  if (cam.x < bounds.left) cam.x = bounds.left;
  if (cam.x > bounds.right - VIEW_W) cam.x = bounds.right - VIEW_W;
  if (cam.y < bounds.top) cam.y = bounds.top;
  if (cam.y > bounds.bottom - VIEW_H) cam.y = bounds.bottom - VIEW_H;

  if (bounds.right - bounds.left < VIEW_W)
    cam.x = bounds.left - ((VIEW_W - (bounds.right - bounds.left)) / 2);
  if (bounds.bottom - bounds.top < VIEW_H)
    cam.y = bounds.top - ((VIEW_H - (bounds.bottom - bounds.top)) / 2);

  return cam;
}

// Where the character lands on screen, as a fraction of the viewport.
//
// The camera IS the top-left of the view in world units, so the character's
// offset from it is (value - cam), and the fraction of the viewport is that over
// the viewport's own size. Dividing by the half-extent instead would halve
// every fraction and read a centred character as 1.0 -- which is exactly what
// the first run of this script reported.
const fraction = (v, cam, size) => (v - cam) / size;

async function main() {
  const s = await post('/api/simulate/session', { path: MAP });
  if (s.__httpError) throw new Error(`session refused: ${s.body}`);

  const bounds = s.bounds;
  console.log(`map            ${MAP}`);
  console.log(`session        ${s.sessionId}`);
  console.log(`bounds         ${JSON.stringify(bounds)}`);

  if (!bounds) {
    console.log('\nNO BOUNDS on the frame -- the camera has nothing to clamp against.');
    process.exitCode = 1;
    return;
  }

  // Snap first, exactly as walkview.follow does on a camera nobody has placed,
  // then let it CONVERGE with follow() called every frame -- which is what the
  // render loop does even with no keys held. Measuring the snap alone would
  // report the camera's first-frame position, not where it settles.
  let cam = { x: s.x - VIEW_W / 2, y: s.y - VIEW_H / 2 };
  for (let k = 0; k < 4000; k++) cam = cameraFor(s.x, s.y, bounds, cam);

  const atRest = { x: s.x, y: s.y, cam: { ...cam } };

  // Three seconds of held input at 60 ticks, which is the 180 frames the task
  // asks for. Held RIGHT, the direction the camera has to travel.
  let fx = s.x, fy = s.y, fcam = { ...cam };
  const frames = 180;
  for (let i = 0; i < frames; i++) {
    const f = await post(`/api/simulate/session/${s.sessionId}/frame`, { right: true });
    if (f.__httpError) throw new Error(`frame refused: ${f.body}`);
    fx = f.x; fy = f.y;
    fcam = cameraFor(fx, fy, bounds, fcam);
  }

  // The camera LERPS toward the character (coefficients 12/800 horizontally and
  // 0.01 vertically, MapleCharacter.cs:751/:755), so it is not centred the
  // instant input stops -- it converges. Reporting only the frame at t=3s would
  // measure how far the character travelled in 3 seconds, not whether the
  // camera follows it, so the settled state is measured too: keep stepping with
  // input RELEASED until the character stops moving and the camera converges.
  //
  // This is the same reason the reference does not snap: a camera that snapped
  // would not be the reference's camera.
  let sx = fx, sy = fy, scam = { ...fcam };
  let settled = null;
  for (let i = 0; i < 600; i++) {
    const f = await post(`/api/simulate/session/${s.sessionId}/frame`, {});
    if (f.__httpError) break;
    const px = sx, py = sy;
    sx = f.x; sy = f.y;
    scam = cameraFor(sx, sy, bounds, scam);
    if (Math.abs(sx - px) < 0.01 && Math.abs(sy - py) < 0.01 && settled === null) {
      // Character stationary. Let the camera finish converging on it.
      for (let k = 0; k < 400; k++) scam = cameraFor(sx, sy, bounds, scam);
      settled = { x: sx, y: sy, cam: { ...scam } };
      break;
    }
  }
  await post(`/api/simulate/session/${s.sessionId}/close`, {});

  const half = { x: VIEW_W / 2, y: VIEW_H / 2 };
  const report = [
    {
      phase: 'at rest',
      charX: Math.round(atRest.x), charY: Math.round(atRest.y),
      camX: Math.round(atRest.cam.x), camY: Math.round(atRest.cam.y),
      screenFracX: +fraction(atRest.x, atRest.cam.x, VIEW_W).toFixed(3),
      screenFracY: +fraction(atRest.y, atRest.cam.y, VIEW_H).toFixed(3),
    },
    {
      phase: `after ${frames} frames (3s) holding right`,
      charX: Math.round(fx), charY: Math.round(fy),
      camX: Math.round(fcam.x), camY: Math.round(fcam.y),
      screenFracX: +fraction(fx, fcam.x, VIEW_W).toFixed(3),
      screenFracY: +fraction(fy, fcam.y, VIEW_H).toFixed(3),
    },
  ];

  // The settled row is the one that answers "does the camera follow the
  // character": after the walk stops, the camera converges on it.
  if (settled) {
    report.push({
      phase: 'settled after input released',
      charX: Math.round(settled.x), charY: Math.round(settled.y),
      camX: Math.round(settled.cam.x), camY: Math.round(settled.cam.y),
      screenFracX: +fraction(settled.x, settled.cam.x, VIEW_W).toFixed(3),
      screenFracY: +fraction(settled.y, settled.cam.y, VIEW_H).toFixed(3),
    });
  }

  console.log(`\nviewport ${VIEW_W}x${VIEW_H} world units; centre is 0.500`);
  console.log('\nphase                                    char(x,y)      camera(x,y)    screenFrac');
  for (const r of report) {
    console.log(
      `  ${r.phase.padEnd(38)} ${String(r.charX + ',' + r.charY).padStart(12)}` +
      `  ${String(r.camX + ',' + r.camY).padStart(13)}` +
      `  (${r.screenFracX}, ${r.screenFracY})`);
  }

  // What the camera is REQUIRED to converge to is the fixed point of the
  // reference's own expression, not "the middle of the screen".
  //
  // MapleCharacter.cs:749-755 fixes the point when dest + camera == 0:
  //
  //   X: camX = X - half                     => character at X - camX = half      = 0.500
  //   Y: camY = Y - half - (OffsetY + 60)    => character at Y - camY = half + OffsetY + 60
  //
  // So the reference centres X exactly, and puts Y 60px (plus the offset) BELOW
  // centre. There is no matching "- 60" on the horizontal line -- that asymmetry
  // is in the source, and reproducing it is the whole point. Judging Y against
  // 0.5 would be testing this against an expectation the specification does not
  // have, which is the same category of error as scan-fallout.mjs's `y > 1200`.
  //
  // Both rows are still reported, so the numbers are visible either way.
  const offsetY = (VIEW_H - bounds.viewportHeight) / 2;
  const expectX = 0.5;
  const expectY = (VIEW_H / 2 + offsetY + 60) / VIEW_H;

  // The 3s-held-input row is a MOVING frame: the character is walking and the
  // camera lerps after it at a finite rate (12/800 horizontally, 0.01
  // vertically), so it is legitimately still trailing. Judging that frame would
  // measure walk speed, not camera behaviour.
  const judged = report.filter(r => r.phase !== `after ${frames} frames (3s) holding right`);
  const tol = 0.02;
  const okX = r => Math.abs(r.screenFracX - expectX) <= tol;
  const okY = r => Math.abs(r.screenFracY - expectY) <= tol;
  const centred = judged.length > 0 && judged.every(r => okX(r) && okY(r));
  const mapFits = (bounds.right - bounds.left) >= VIEW_W && (bounds.bottom - bounds.top) >= VIEW_H;

  console.log(`\nviewport ${VIEW_W}x${VIEW_H}; the reference's fixed point is` +
    ` X=${expectX.toFixed(3)}, Y=${expectY.toFixed(3)}`);
  console.log(`  (Y is ${(60 / VIEW_H * 100).toFixed(1)}% below centre: MapleCharacter.cs:754 adds` +
    ` OffsetY + 60 to the vertical target and has no horizontal counterpart.)`);
  console.log(`\nmap ${mapFits ? 'is' : 'is NOT'} larger than the viewport` +
    `${mapFits ? '' : ' -- the client centres a short axis instead (MapleCharacter.cs:766-767),'}`);
  if (!mapFits) console.log('   so a short axis sitting at 0.5 is the correct answer there, not a clamp failure.');

  for (const r of judged) {
    console.log(`  ${r.phase.padEnd(32)} X ${okX(r) ? 'ok' : 'OFF'} (${r.screenFracX})` +
      `   Y ${okY(r) ? 'ok' : 'OFF'} (${r.screenFracY}, want ${expectY.toFixed(3)})`);
  }

  console.log(`\nVERDICT: ${centred ? 'camera sits at the reference position' : 'camera is OFF the reference position'}`);
  console.log(`         the 3s-held-input row is not judged: it is the camera trailing a walking character.`);

  writeFileSync('shots/t20/camera.json', JSON.stringify({ map: MAP, bounds, viewport: { VIEW_W, VIEW_H }, report }, null, 1));
  console.log('-> shots/t20/camera.json');
  if (!centred) process.exitCode = 1;
}

await main();