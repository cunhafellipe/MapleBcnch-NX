// Does the vertical camera actually converge on the character, and is the
// residual the OffsetY term or a convergence bug in the harness?
//
//   node cam-debug.mjs
//
// Two runs of the SAME expression from MapleCharacter.cs:753-755 with and
// without the Map.OffsetY term (MapleMap.cs:279), each iterated to a fixed
// point. If both converge on different values, the difference IS the offset and
// it belongs in the expression; if they converge on the same value, the offset
// cancels and the harness is at fault for dropping it.

const half = 600 / 2;
const charY = 91;
const bounds = { top: -1200, bottom: 372 };

function converge(withOffset) {
  const offsetY = withOffset ? (600 - 600) / 2 : 0;
  let camY = charY - half;   // snap, as walkview does on first placement
  for (let i = 0; i < 100000; i++) {
    const destY = half + offsetY + 60 - charY;
    if (Math.abs(destY + camY) > 1) camY -= 0.01 * (destY + camY);
    if (camY < bounds.top) camY = bounds.top;
    if (camY > bounds.bottom - 600) camY = bounds.bottom - 600;
    if (Math.abs(destY + camY) <= 1) return { camY, iters: i };
  }
  return { camY, iters: -1 };
}

for (const withOffset of [true, false]) {
  const { camY, iters } = converge(withOffset);
  const frac = (charY - camY) / 600;
  console.log(
    `OffsetY term ${withOffset ? 'INCLUDED' : 'omitted '}: camY=${camY.toFixed(3)}` +
    ` screenFrac=${frac.toFixed(4)} after ${iters} iters` +
    `  residual from 0.5 = ${(frac - 0.5).toFixed(4)}`);
}

console.log('\nThe fixed point of the loop is camY such that destY + camY == 0:');
console.log(`  camY = half + offsetY + 60 - Y  =>  charY - camY = Y - half - offsetY - 60`);
console.log(`  with Y=91, half=300, offsetY=0, +60:  frac = (91 - 300 - 0 - 60)/600 = ${(91 - 300 - 60) / 600}`);