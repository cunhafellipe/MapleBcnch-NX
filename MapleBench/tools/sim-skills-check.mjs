// Logic tests for the pieces this session added, run in Node with no browser.
//
// WHAT THIS IS FOR
// `node --check` only proves the file parses. Everything JS-side added for 6.3
// and 6.4 had been syntax-checked and nothing else: the key->skill resolution,
// the "does this key belong to the walk instead" guard, and the skill->pose
// mapping had never been EXECUTED. These run them.
//
// This does NOT substitute for a desktop screenshot. It answers "does the logic
// do what it claims", not "does the user see the panel" -- 工作室守則 2.1 is
// explicit that only a desktop screenshot answers the second.
//
// Run:  GATE_BASE=http://127.0.0.1:<port> node MapleBench/tools/sim-skills-check.mjs
//       (GATE_BASE is required -- there is no default port on purpose, and BASE
//        set to the empty string or to whitespace counts as unset)

import { pathToFileURL } from 'node:url';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';

// Located from this file, not from the current directory: the whole point is
// that it runs the same way for whoever picks it up next.
const HERE = dirname(fileURLToPath(import.meta.url));
const JS = resolve(HERE, '..', 'wwwroot', 'js');
// A bare Windows path is not an ESM specifier; it has to be a file:// URL.
const mod = (name) => import(pathToFileURL(`${JS}/${name}`).href);
// The backend comes from GATE_BASE and from nowhere else (task-126). `BASE` used
// to be read FIRST, so a caller who set only BASE made the .mjs suites and the
// python suites hit two different instances in the same run -- and the old
// hard-coded default port meant this suite could silently measure an instance
// nobody chose.
const GATE_BASE = (process.env.GATE_BASE || '').trim();
const OTHER_BASE = (process.env.BASE || '').trim();
if (GATE_BASE && OTHER_BASE && GATE_BASE !== OTHER_BASE) {
  console.log(`>>> RED: GATE_BASE and BASE name different instances: `
    + `GATE_BASE=${JSON.stringify(GATE_BASE)} BASE=${JSON.stringify(OTHER_BASE)}. `
    + `Set GATE_BASE only.`);
  process.exit(1);
}
if (OTHER_BASE && !GATE_BASE) {
  console.log(`>>> RED: BASE is set (${JSON.stringify(OTHER_BASE)}) but GATE_BASE is not. `
    + `GATE_BASE is the variable the suites read; set it and unset BASE.`);
  process.exit(1);
}
if (!GATE_BASE) {
  console.log('>>> RED: GATE_BASE is not set, so there is no backend to measure.');
  console.log('       Set it to the instance under test, for example:');
  console.log("         GATE_BASE=http://127.0.0.1:<port> node " + (process.argv[1] || 'this suite'));
  console.log('       There is no default port on purpose: an older instance on the');
  console.log('       usual port answers every one of these requests.');
  process.exit(1);
}
// `BASE` set to the empty string or to whitespace counts as UNSET, the same as
// the 32 suites read it: HEAD's `??` fell through only on null/undefined, so an
// empty BASE used to mask GATE_BASE and send every request to a relative URL.
// The trailing-slash strip is the 32 suites' own line, and it is load-bearing:
// measured against the backend, `/api/files` is HTTP 200 and `//api/files` is
// HTTP 404, so a base written with a trailing slash would 404 on every call.
// KNOWN, INHERITED from that same shape: the comparison above is a raw string
// compare, so GATE_BASE=http://h:p together with BASE=http://h:p/ names ONE
// instance and is reported as two. Do not file that as a new bug.
const BASE = GATE_BASE.replace(/\/+$/, '');

// api.js and i18n.js reach for browser globals at import time in a browser. Stub
// the two they need before importing, so the modules under test load unmodified.
globalThis.localStorage ??= {
  getItem: () => null, setItem: () => {}, removeItem: () => {}, clear: () => {},
};
globalThis.document ??= { documentElement: { lang: 'en' }, addEventListener() {} };
globalThis.window ??= globalThis;

let pass = 0, fail = 0;
function check(name, got, want) {
  const ok = JSON.stringify(got) === JSON.stringify(want);
  if (ok) { pass++; console.log(`  PASS  ${name}`); }
  else { fail++; console.log(`  FAIL  ${name}\n          got  ${JSON.stringify(got)}\n          want ${JSON.stringify(want)}`); }
}

// The archive id is handed out in the order the archives were opened, so 'f1'
// is Character.nx on one instance and something else on another. Resolve it by
// NAME, and tell the ways it can fail apart -- the archives could not be
// listed, the archive is absent, or the archive is listed but carries no id.
// Each prints the red marker the gate reads and stops. The shape is the one
// 0645f62 put in tools/verify/suites/skillshape-verify.mjs.
async function archiveId(name) {
  let files;
  try { files = await (await fetch(`${BASE}/api/files`)).json(); }
  catch (exc) { console.log(`>>> RED: could not list the open archives on this instance: ${exc.message ?? exc}`); process.exit(1); }
  const listed = new Map((files ?? []).filter((f) => f && typeof f === 'object').map((f) => [f.name, f.id]));
  if (!listed.has(name)) { console.log(`>>> RED: ${name} is not open on this instance, so there is nothing to judge`); process.exit(1); }
  if (!listed.get(name)) { console.log(`>>> RED: ${name} is listed by this instance but carries no id`); process.exit(1); }
  return listed.get(name);
}

const { actionFor } = await mod('avatar.js');
const { LiveWalk } = await mod('livewalk.js');
const { WalkViewer } = await mod('walkview.js');

// A body with only the two methods skillFor() needs. No constructor, no window.
const body = Object.create(LiveWalk.prototype);
// 'K' and 'M', NOT 'D' and 'G'. keyFor() binds WASD to walking
// (livewalk.js:882-885, `case 'd': case 'D': return 'right'`), so a bound 'D'
// is correctly REFUSED -- the first version of this harness asserted it should
// work and failed for the right reason on the wrong key.
body.skillKeys = new Map([['K', '1121008'], ['M', '12101006']]);
const ev = (key, extra = {}) => ({ key, ctrlKey: false, metaKey: false, altKey: false,
                                  shiftKey: false, target: { tagName: 'BODY' },
                                  // A real KeyboardEvent has these. onKeyDown calls
                                  // preventDefault() on the movement path
                                  // (livewalk.js:963), so a synthetic event without
                                  // them throws before reaching the assertion under
                                  // test -- the harness failed on its own stub, not
                                  // on the code.
                                  preventDefault() {}, stopPropagation() {},
                                  ...extra });
const INPUT = { tagName: 'INPUT' };

console.log('=== skillFor(): a bound letter casts, and nothing else does ===');
check('bound "K" resolves',            body.skillFor(ev('K')), { key: 'K', skillId: '1121008' });
check('lower case "k" resolves too',   body.skillFor(ev('k')), { key: 'K', skillId: '1121008' });
check('shift+k still the same key',    body.skillFor(ev('K', { shiftKey: true })), { key: 'K', skillId: '1121008' });
check('unbound "Q" does not',          body.skillFor(ev('Q')), null);
check('arrow key does not',            body.skillFor(ev('ArrowLeft')), null);
check('F1 does not (length != 1)',     body.skillFor(ev('F1')), null);
check('Ctrl+K does not',               body.skillFor(ev('K', { ctrlKey: true })), null);
check('Alt+K does not',                body.skillFor(ev('K', { altKey: true })), null);
check('Meta+K does not',               body.skillFor(ev('K', { metaKey: true })), null);
check('typing in an input does not',   body.skillFor(ev('K', { target: INPUT })), null);
check('typing in a textarea does not', body.skillFor(ev('K', { target: { tagName: 'TEXTAREA' } })), null);
check('picking in a select does not',  body.skillFor(ev('K', { target: { tagName: 'SELECT' } })), null);

// W-071. The EXCEPTION, and it is the whole fix.
//
// A <select> outside the hotkey panel must still swallow the key -- that is the
// assertion above, and it stays. But the hotkey panel is a grid of <select>s and
// has no text field at all: the workflow is "pick a key here, press it there".
// Under the blanket rule the key you had just bound was eaten by the dropdown
// that bound it, the panel stayed open on top of that, and the skill could not be
// cast by a person -- while 26 suites and an end-to-end proof were green.
const inPanel = (tag, panel) => ({ tagName: tag, closest: () => (panel ? {} : null) });
check('a select INSIDE the hotkey panel does not swallow the key',
      body.skillFor(ev('K', { target: inPanel('SELECT', true) })),
      { key: 'K', skillId: '1121008' });
// A target with no `closest` at all must not crash the guard.
check('a target without closest() is handled',
      body.skillFor(ev('K', { target: { tagName: 'BODY' } })) !== undefined, true);

console.log('=== a bound key must not shadow a key the walk already owns ===');
// DERIVED from the prototype's own keyFor() rather than restated, so this test
// fails if the walk's bindings ever change -- the first version listed 'Right'
// and 'D' by hand and was missing, which is how a walk key slipped through as a
// usable hotkey. This comment CLAIMED that derivation while the code restated a
// literal; W-056 found the claim false when WASD left keyFor() and the restated
// list went stale behind it -- silently, because the gate could not see the
// failure it caused.
const CANDIDATE_KEYS = [
  'ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown', ' ', 'Spacebar',
  'Control', 'Shift', 'Alt', 'Enter', 'z', 'Z',
  'a', 'A', 'd', 'D', 'w', 'W', 's', 'S', 'q', 'Q', 'k', 'K', 'm', 'M',
];
const WALK_KEYS = CANDIDATE_KEYS.filter((k) => body.keyFor(k) !== null);
console.log(`  the walk owns: ${JSON.stringify(WALK_KEYS)}`);
const shadowed = [];
for (const k of WALK_KEYS) {
  body.skillKeys.clear();
  body.skillKeys.set(k.length === 1 ? k.toUpperCase() : k, '9999999');
  if (body.skillFor(ev(k)) !== null) shadowed.push(k);
  body.skillKeys.clear();
}
check('no walk key can be bound', shadowed, []);

// The two facts the derivation now rests on, asserted directly so that a change
// to keyFor() has to confront them instead of slipping past a derived list.
// clean-client `IO/Input/Keyboard.cpp:78-81` binds InputKey::LEFT/RIGHT/UP/DOWN
// to KeyAction, and no letter is bound to an action anywhere in that file --
// while `A`,`S`,`D` sit in the Keytable at :32 and `W` at :30. Reading the
// Keytable (:25-43) as the bindings is the mistake that put WASD into keyFor()
// in the first place, and it cost a real defect: a skill bound to A/D/W/S could
// not be pressed while the panel reported it bound.
check('the walk owns all four arrows',
      ['ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown'].every((k) => body.keyFor(k) !== null), true);
check('the walk does NOT own WASD (Keyboard.cpp:78-81 binds arrows only)',
      ['a', 'A', 'd', 'D', 'w', 'W', 's', 'S'].every((k) => body.keyFor(k) === null), true);
check('plain letters stay free for skill hotkeys',
      ['q', 'Q', 'k', 'K', 'm', 'M'].every((k) => body.keyFor(k) === null), true);

console.log('=== onKeyDown(): what gets ARMED is the id, not the letter ===');
// W-065. skillFor() has always returned BOTH `key` and `skillId`, and every
// assertion above checks that pair. None of them checked what onKeyDown does
// with it -- and it armed `skillDown` with `skill.key`, the LETTER. The frame
// loop then sent that letter as the `skillId` field, which the server parses
// with int.TryParse (SkillCastService.Plan:231), so every cast from the panel
// died at `if (plan is null) return;` (MapSimSessionStore.cs:1479).
//
// Measured against the running server, same endpoint, same frame:
//   {"skillId": "A"}       -> action='stand' skillId=''              no cast
//   {"skillId": "1121008"} -> action='skill' skillStance='brandish1' cast
//
// and the binding itself reported `rejected: []`, so the panel showed the key
// bound and pressing it did nothing, silently. Every HTTP suite passed the
// whole time because they post the numeric id directly and never come through
// here. This is the assertion that closes that hole: it runs in Node, needs no
// browser keystroke, and fails on the exact line that was wrong.
const kb = Object.create(LiveWalk.prototype);
kb.skillKeys = new Map([['K', '1121008'], ['M', '12101006']]);
kb.skillDown = '';
kb.skillLatch = true;
kb.keys = { left: false, right: false, up: false, down: false, jump: false, attack: false };
kb.onKeyDown(ev('K'));
check('a bound key arms skillDown with the numeric SKILL ID', kb.skillDown, '1121008');
check('it does not arm the letter', kb.skillDown === 'K', false);
check('and it re-arms the latch for the next press', kb.skillLatch, false);

// The value the frame loop puts on the wire is `skillDown` verbatim
// (livewalk.js:1158-1173), so a numeric skillDown is the whole contract.
check('the armed value survives int.TryParse, which is what the server does',
      Number.isInteger(Number(kb.skillDown)) && Number(kb.skillDown) > 0, true);

// A key the walk owns must not arm anything at all.
kb.skillDown = '';
kb.onKeyDown(ev('ArrowLeft'));
check('a walk key arms no skill', kb.skillDown, '');

// And the release path still clears it (onKeyUp, :953-954).
kb.skillDown = '1121008';
kb.onKeyUp(ev('K'));
check('keyup clears the armed skill', kb.skillDown, '');

console.log('=== actionFor(): the swing pose contract still holds ===');
check('swing uses the server stance', actionFor({ action: 'attack', attackStance: 'stabO1', onGround: true }),
      { state: 'stabO1', animated: true });
// onGround must be stated. A tick that does not say is treated as airborne,
// which is right: `!onGround && !onLadder` is a moving term (avatar.js:219-221).
check('unarmed swing on the ground is not a pose',
      actionFor({ action: 'attack', attackStance: '', speed: 0, onGround: true }),
      { state: 'stand1', animated: false });
check('a tick that does not say it is grounded counts as airborne',
      actionFor({ action: 'attack', attackStance: '' }),
      { state: 'walk1', animated: true });

console.log('=== the skill -> pose mapping the panel relies on ===');
// walkview.skillPoseTick maps a skill frame onto the attack path so avatar.js
// needs no new branch. Reproduce that contract here and assert actionFor agrees.
const skillFrame = { action: 'skill', skillStance: 'brandish1', attackStance: '', speed: 0, onGround: true };
const mapped = { ...skillFrame, action: 'attack', attackStance: skillFrame.skillStance };
check('mapped tick draws the skill pose', actionFor(mapped), { state: 'brandish1', animated: true });
check('a skill with no stance is not a pose',
      actionFor({ ...skillFrame, skillStance: '', action: 'attack' }), { state: 'stand1', animated: false });
// The archive really does carry brandish1/brandish2 for 1121008 -- measured via
// /api/simulate/look/stances, not assumed. If the name were wrong the pose would
// resolve to nothing and silently draw nothing.
const stances = await (await fetch(`${BASE}/api/simulate/look/stances?fileId=${await archiveId('Character.nx')}`)).json();
check('brandish1 is a real pose in the open archive',
      typeof stances.stances?.brandish1?.frameCount === 'number' && stances.stances.brandish1.frameCount > 0, true);

console.log('=== walkview.skillPoseTick(): the frame the panel depends on ===');
// Built on the prototype so the real constructor -- which wants a canvas, a
// session and a live body -- never runs. skillPoseTick is a pure object rewrite,
// so that costs it nothing.
const view = Object.create(WalkViewer.prototype);
const castTick = { action: 'skill', skillStance: 'brandish1', attackStance: '', onGround: true, speed: 0 };
const out = view.skillPoseTick(castTick);
check('a cast frame is rewritten onto the swing path',
      [out.action, out.attackStance], ['attack', 'brandish1']);
check('the original tick is not mutated', castTick.action, 'skill');
check('and actionFor draws the skill pose from it', actionFor(out), { state: 'brandish1', animated: true });

const noStance = { action: 'skill', skillStance: '', attackStance: '', onGround: true, speed: 0 };
check('a cast with no reported pose is passed through untouched',
      view.skillPoseTick(noStance), noStance);
check('a walk frame is passed through untouched',
      view.skillPoseTick({ action: 'walk', skillStance: '', onGround: true }),
      { action: 'walk', skillStance: '', onGround: true });

console.log(`\n${pass} passed, ${fail} failed`);
// The gate decides on EXPLICIT markers only (pm/GATE.md: "If you add a suite, it
// must print its own verdict. Do not add a pattern to gate.ps1"). This suite
// printed `FAIL <name>` and a "N failed" count, neither of which the gate looks
// for -- so it ran 23/24 for an unknown number of runs while the gate reported
// green. Found by W-056. `>>> RED` is the marker the gate already reads.
if (fail) console.log(`>>> RED (${fail} failed)`);
process.exit(fail ? 1 : 0);