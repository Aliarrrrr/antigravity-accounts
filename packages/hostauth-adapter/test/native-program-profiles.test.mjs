import test from 'node:test';
import assert from 'node:assert/strict';
import profiles from '../src/native-program-profiles.cjs';
import {NATIVE_DIAGNOSTICS_PROFILES} from '../src/native-diagnostics-patch.mjs';
import {PROJECT_REFRESH_PROFILES} from '../src/native-project-refresh-patch.mjs';
import {adaptedNativeDigests} from '../../account-plugin/native-fallback.mjs';
import {createRequire} from 'node:module';
const {NATIVE_PROGRAM_PROFILES: runtimeProfiles}=createRequire(import.meta.url)('../src/native-task-adapter.cjs');

test('every reviewed native description reaches diagnostics, project refresh, runtime and fallback',()=>{
  const versions=profiles.NATIVE_PROGRAM_PROFILES.map(profile=>profile.version);
  assert.equal(new Set(versions).size,versions.length);
  assert.deepEqual(NATIVE_DIAGNOSTICS_PROFILES.map(profile=>profile.version),versions);
  assert.deepEqual(PROJECT_REFRESH_PROFILES.map(profile=>profile.version),versions);
  assert.deepEqual(runtimeProfiles.map(profile=>profile.version),versions);
  for(const profile of profiles.NATIVE_PROGRAM_PROFILES){
    assert.ok(['execution-status','aggregate-counts'].includes(profile.activityProtocol));
    assert.ok(profile.oauth.code.digest && profile.oauth.id.digest && profile.oauth.secret.digest);
    for(const name of ['originalSha256','diagnosticsSha256','projectSha256','providerSha256']) assert.match(profile[name],/^[a-f0-9]{64}$/);
    assert.equal(adaptedNativeDigests.includes(profile.originalSha256),false,'An original official program must not be classified as adapted');
    for(const name of ['diagnosticsSha256','projectSha256','providerSha256']) assert.equal(adaptedNativeDigests.includes(profile[name]),true,'No adapted output may enter native fallback');
    assert.ok(Object.isFrozen(profile) && Object.isFrozen(profile.diagnostics.changes));
  }
  assert.equal(new Set(adaptedNativeDigests).size,versions.length*3);
});
