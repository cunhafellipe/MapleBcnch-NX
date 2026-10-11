// Does every translated string actually have a translation, and is every
// dictionary entry actually used?
//
// Both directions matter. A key with no entry shows English to a user who
// asked for Chinese. An entry with no call site is a translation somebody
// wrote that the app can never show -- which usually means a string was
// renamed in the code and the table kept its old copy.
//
// SUPERSEDED by i18n-inventory.js.
//
// This script used to scan call sites with /\bt\(\s*'((?:[^'\\]|\\.)*)'/g.
// That regex only ever sees fragment 1 of t('a ' + 'b'), while the runtime
// looks up the joined string -- so it reported 112 fragment entries as
// translated when they never render. It is kept as a thin wrapper so anyone
// who still types `node nx-probe/i18n-coverage.js` gets the honest answer
// instead of a confident wrong one, and gets the same exit code.
//
//   node nx-probe/i18n-coverage.js   -> delegates to i18n-inventory.js

import { spawnSync } from 'node:child_process';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

console.error(
  'i18n-coverage.js is superseded by i18n-inventory.js, which reads call sites\n' +
  'through an AST and folds constant concatenation the way the runtime does.\n' +
  'Delegating.\n',
);

const here = dirname(fileURLToPath(import.meta.url));
const result = spawnSync(
  process.execPath,
  [resolve(here, 'i18n-inventory.js')],
  { stdio: 'inherit' },
);
process.exit(result.status ?? 1);