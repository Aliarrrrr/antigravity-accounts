import { applyPrepared, restorePrepared } from '../src/package-copy.mjs';
import { assertApplicationStopped } from '../src/process-guard.mjs';
import path from 'node:path';

if (process.versions.electron) process.noAsar = true;

const operation = process.argv[2];
const values = new Map();
for (let index = 3; index < process.argv.length; index += 2) {
  if (!['--prepared', '--resources', '--backup', '--acknowledge-unverified-runtime'].includes(process.argv[index]) || !process.argv[index + 1]) throw new Error('Unknown or incomplete installer argument');
  values.set(process.argv[index], process.argv[index + 1]);
}
try {
  assertApplicationStopped({ installRoot: values.get('--resources') ? path.dirname(path.resolve(values.get('--resources'))) : undefined });
  const common = { resourcesRoot: values.get('--resources'), backupRoot: values.get('--backup'), processesStopped: true };
  if (operation === 'apply') {
    await applyPrepared({ ...common, preparedRoot: values.get('--prepared'), acknowledgeUnverifiedRuntime: values.get('--acknowledge-unverified-runtime') === 'true' });
    console.log('Adapter applied. Native authentication behavior remains unverified.');
  } else if (operation === 'restore') {
    await restorePrepared(common);
    console.log('Original program archive restored.');
  } else throw new Error('Expected apply or restore');
} catch (error) { console.error(error.message); process.exitCode = 1; }
