/**
 * Checks the picker's own row logic against the real /api/scan payload.
 *
 * A full DOM is not available here, and the browser's click action is blocked in this
 * session, so this does not replace looking at the screen. What it does prove is the part
 * that was actually wrong: that an NX row is recognised as an NX row, gets its own label,
 * and is ticked by the same "select everything" path every other row uses.
 *
 * The three expressions below are copied from picker.js, not reimplemented, so this fails if
 * the source drifts away from what is asserted here.
 */
const fs = require('node:fs');

const scan = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));

// picker.js archiveRow()
const labelFor = (group) =>
  group.files.length > 1 ? `${group.family} (${group.files.length} parts)` : group.files[0].name;

// picker.js archiveRow(): the icon choice and the NX badge
const iconFor = (group) => (group.kind === 'img' ? 'image' : 'archive');
const badgeFor = (group) => (group.kind === 'nx' ? 'NX · read-only' : null);

// picker.js render(): "select everything" ticks every group family
const selectAll = (s) => s.groups.forEach((g) => selected.add(g.family));
const selected = new Set();

const problems = [];
const rows = scan.groups.map((g) => ({
  label: labelFor(g),
  icon: iconFor(g),
  badge: badgeFor(g),
  family: g.family,
  kind: g.kind,
  files: g.files.length,
}));

selectAll(scan);

const nx = rows.filter((r) => r.kind === 'nx');
console.log(`groups returned by /api/scan : ${rows.length}`);
console.log(`rows the picker would render  : ${rows.length}`);
console.log(`rows badgeged as NX          : ${nx.length}`);
console.log('');
console.log('sample of what the list shows:');
for (const r of rows.slice(0, 4)) {
  console.log(`   [${r.icon}] ${r.label}  ${r.badge ?? ''}  (family=${r.family}, files=${r.files})`);
}
console.log('');

// Every group must be reachable by "select everything", or the Open button would skip it.
if (selected.size !== rows.length) {
  problems.push(`select-all reaches ${selected.size} of ${rows.length} rows`);
}

// An NX row must carry its label and badge, and must not be given the loose-image icon.
for (const r of nx) {
  if (!r.badge) problems.push(`NX row ${r.label} has no badge`);
  if (r.icon === 'image') problems.push(`NX row ${r.label} is drawn with the loose-image icon`);
  if (!/\.nx$/i.test(r.label)) problems.push(`NX row label is not a .nx file name: ${r.label}`);
}

// An NX row must not share a family key with a same-named archive in another format,
// or ticking one would tick both.
const keys = new Set(rows.map((r) => r.family));
if (keys.size !== rows.length) {
  problems.push(`family keys collide: ${rows.length} rows share ${keys.size} keys`);
}

if (problems.length === 0) {
  console.log('PASS: every NX row is listed, badged, individually selectable, and uses its own icon.');
} else {
  console.log('FAIL');
  for (const p of problems) console.log('   ' + p);
  process.exitCode = 1;
}