import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, readFile, writeFile, rm } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { createRequire } from 'node:module';
const { createAccountPreferences } = createRequire(import.meta.url)('../src/account-preferences.cjs');

test('data usage preference defaults to unchecked and persists without account data', async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), 'accounts-preferences-'));
  const filename = path.join(directory, 'account-preferences.json');
  try {
    const preferences = createAccountPreferences(filename);
    assert.deepEqual(await preferences.get(), { dataUsageConsent: 'off' });
    for (const dataUsageConsent of ['on', 'manual', 'off']) {
      assert.deepEqual(await preferences.set({ dataUsageConsent }), { dataUsageConsent });
      assert.deepEqual(await createAccountPreferences(filename).get(), { dataUsageConsent });
    }
    assert.deepEqual(JSON.parse(await readFile(filename, 'utf8')), { schemaVersion: 1, dataUsageConsent: 'off' });
    for (const value of [null, [], {}, { dataUsageConsent: true }, { dataUsageConsent: 'yes' }, { dataUsageConsent: 'off', token: 'private' }])
      await assert.rejects(preferences.set(value), { code: 'unsupported_parameter' });
    await writeFile(filename, '{invalid');
    await assert.rejects(preferences.get(), { code: 'account_preferences_unavailable' });
    await preferences.set({ dataUsageConsent: 'manual' });
    assert.deepEqual(await preferences.get(), { dataUsageConsent: 'manual' });
  } finally { await rm(directory, { recursive: true, force: true }); }
});
