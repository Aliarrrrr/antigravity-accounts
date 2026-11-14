import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import os from 'node:os';
import {testDataRoot} from './fixture-paths.mjs';
import {prepareLocalization,localizationInstallRecord,sha256} from '../src/toolkit.mjs';

test('preparation binds a separate source candidate to the real destination without writing either input',async t=>{
  const root=await fs.mkdtemp(path.join(await fs.realpath(os.tmpdir()),'agy-zh-prepared-'));
  t.after(async()=>{assert.equal(path.dirname(await fs.realpath(root)),await fs.realpath(os.tmpdir()));await fs.rm(root,{recursive:true,force:true});});
  const original=process.env.LOCALIZATION_TEST_PREPARE_INSTALL||path.join(testDataRoot,'verification','repair-2026-10-10','baseline');
  const source=path.join(root,'source'),target=path.join(root,'target');
  await fs.cp(original,source,{recursive:true});await fs.cp(original,target,{recursive:true});
  const sourceFile=path.join(source,'resources','app.asar'),targetFile=path.join(target,'resources','app.asar');
  const sourceBefore=sha256(await fs.readFile(sourceFile)),targetBefore=sha256(await fs.readFile(targetFile));
  const result=await prepareLocalization({sourceInstallRoot:source,targetInstallRoot:target,outputRoot:path.join(root,'output')});
  assert.equal(result.manifest.installRoot,await fs.realpath(target));
  assert.equal(result.manifest.sourceSha256,sourceBefore);
  assert.equal(sha256(await fs.readFile(sourceFile)),sourceBefore);
  assert.equal(sha256(await fs.readFile(targetFile)),targetBefore);
  const record=localizationInstallRecord(result.manifest,{artifactDir:result.artifactDir,appliedUtc:'2026-11-14T00:00:00.000Z'});
  assert.equal(record.installRoot,await fs.realpath(target));assert.equal(record.sourceSha256,sourceBefore);
  assert.equal(record.patchedSha256,result.manifest.patchedSha256);
  await fs.appendFile(path.join(target,'Antigravity.exe'),'changed executable');
  await assert.rejects(prepareLocalization({sourceInstallRoot:source,targetInstallRoot:target,outputRoot:path.join(root,'other')}),/target_installation_identity_mismatch/);
});
