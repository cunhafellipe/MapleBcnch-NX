// Writes the list of t() keys that have no dictionary entry, so the translator
// is handed the work rather than left to rediscover it.
//
// Regenerating this by hand each round is how a translation quietly loses a
// string, and it is also how the same key gets translated twice.
//
// SUPERSEDED by i18n-inventory.js, which does the same job through an AST and
// additionally folds constant concatenation -- t('a ' + 'b') is looked up at
// runtime as "a b", but this script's old regex only ever saw "a " and would
// hand the translator a fragment that is never looked up on its own.
//
// It is kept as a thin wrapper so anyone who still types
// `node nx-probe/i18n-missing-keys.js` gets the honest answer.
//
//   node nx-probe/i18n-missing-keys.js [outfile]
//
// i18n-inventory.js writes nx-probe/i18n-inventory.txt; pass that path here if
// you want the run's output somewhere else.

import { spawnSync } from 'node:child_process';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

console.error(
  'i18n-missing-keys.js is superseded by i18n-inventory.js, which reads call\n' +
  'sites through an AST and folds constant concatenation the way the runtime\n' +
  'does. Delegating.\n',
);

const here = dirname(fileURLToPath(import.meta.url));
const result = spawnSync(
  process.execPath,
  [resolve(here, 'i18n-inventory.js')],
  { stdio: 'inherit' },
);
process.exit(result.status ?? 1);