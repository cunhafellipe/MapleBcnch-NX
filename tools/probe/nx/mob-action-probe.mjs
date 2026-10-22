/**
 * What the Mob archive actually carries, measured rather than assumed.
 *
 *   node tools/probe/nx/mob-action-probe.mjs <baseUrl> <fileId> <mobId...>
 *
 * Reports, per mob: the actions present, how many frames each holds, the
 * per-frame origin and delay, and whether info/link moves the art somewhere
 * else. The point is the two facts that decide how a mob is drawn at all:
 * whether `stand` exists (a mob with no `stand` cannot idle) and whether the
 * frames are canvases or containers (character frames are containers; mob
 * frames, measured here, are canvases with origin/head/lt/rb/delay children).
 */

const [, , base, fileId, ...ids] = process.argv;
if (!base || !fileId || !ids.length) {
  console.error('usage: mob-action-probe.mjs <baseUrl> <fileId> <mobId...>');
  process.exit(2);
}

const pad = (n) => String(n).padStart(7, '0');
const get = async (path) => {
  const r = await fetch(`${base}/api/children?path=${encodeURIComponent(path)}`);
  if (!r.ok) return null;
  return r.json();
};

for (const raw of ids) {
  const mobId = Number(raw);
  const path = `${fileId}/${pad(mobId)}.img`;
  const top = await get(path);
  if (!top) { console.log(`${raw}: no image at ${path}`); continue; }

  const info = top.find((c) => c.name === 'info');
  const infoKids = info ? await get(info.path) : null;
  const link = infoKids?.find((c) => c.name === 'link');

  console.log(`\n=== ${pad(mobId)} link=${link?.value ?? 'none'} `);
  console.log(`  actions: ${top.filter((c) => c.kind === 'Property').map((c) => c.name).join(' ')}`);

  for (const action of top.filter((c) => c.kind === 'Property' && c.name !== 'info')) {
    const frames = (await get(action.path)) ?? [];
    const nums = frames.filter((f) => /^\d+$/.test(f.name));
    if (!nums.length) continue;

    const first = nums[0];
    const kids = (await get(first.path)) ?? [];
    const origin = kids.find((k) => k.name === 'origin');
    const delay = kids.find((k) => k.name === 'delay');
    const head = kids.find((k) => k.name === 'head');
    const extra = action.extra ?? first.extra ?? {};
    console.log(
      `  ${action.name.padEnd(7)} frames=${String(nums.length).padStart(2)}` +
      ` canvas=${first.value?.value ?? '?'} origin=${origin?.value ?? 'MISSING'}` +
      ` delay=${delay?.value ?? 'none'} head=${head?.value ?? 'none'}` +
      ` zig=${action.extra?.zigzag ?? '-'}`,
    );
  }
}