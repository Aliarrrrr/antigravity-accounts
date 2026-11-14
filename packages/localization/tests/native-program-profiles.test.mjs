import test from 'node:test';
import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
import {adaptedNativeDigests} from '../src/native-fallback.mjs';
import profiles from '../src/native-program-profiles.cjs';

test('the standalone localization native description is generated from the Accounts-owned source',async()=>{
  assert.deepEqual(await readFile(new URL('../src/native-program-profiles.cjs',import.meta.url)),
    await readFile(new URL('../../hostauth-adapter/src/native-program-profiles.cjs',import.meta.url)));
  assert.deepEqual(adaptedNativeDigests,profiles.ADAPTED_NATIVE_DIGESTS);
  assert.ok(adaptedNativeDigests.includes(profiles.NATIVE_PROGRAM_PROFILES.find(item=>item.version==='2.32.1').providerSha256));
});
