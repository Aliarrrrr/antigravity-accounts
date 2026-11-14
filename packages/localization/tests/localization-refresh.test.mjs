import {testDataRoot} from './fixture-paths.mjs';
import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import * as toolkit from '../src/toolkit.mjs';

const original=path.join(testDataRoot,'verification','repair-2026-10-10','baseline');
const originalHash='259C83FFA266088DDA6EDE520A601BBF06CC5C34985CDF56C5D68549C98DBB0E';

test('refresh stages new localization without restoring or replacing the installed package first',{skip:process.platform!=='win32'},async t=>{
  assert.equal(typeof toolkit.prepareLocalizationRefresh,'function');
  assert.equal(typeof toolkit.refreshLocalizedArtifact,'function');
  const root=await fs.mkdtemp(path.join(os.tmpdir(),'agy-refresh-test-'));
  const knownRoot=await fs.realpath(root);
  t.after(async()=>{
    const resolved=await fs.realpath(root);
    assert.equal(resolved,knownRoot,'temporary directory changed before cleanup');
    assert.equal(path.dirname(resolved),path.resolve(os.tmpdir()));
    assert.ok(path.basename(resolved).startsWith('agy-refresh-test-'));
    await fs.rm(resolved,{recursive:true,force:true});
  });
  const install=path.join(root,'install');
  await fs.cp(original,install,{recursive:true});
  const backupRoot=path.join(root,'backups');
  const first=await toolkit.buildInstallation(install,{outputRoot:path.join(root,'first'),compatibleSource:true});
  const applied=await toolkit.applyArtifact(install,first.artifactDir,{backupRoot,compatibleSource:true});
  const target=path.join(install,'resources','app.asar');
  const recordPath=path.join(install,'resources','antigravity-zh-cn.install.json');
  const backupPath=path.join(applied.backupDir,'app.asar');
  const previousFingerprint='0'.repeat(64);
  const record=JSON.parse(await fs.readFile(recordPath,'utf8'));
  record.localizationFingerprint=previousFingerprint;
  await fs.writeFile(recordPath,JSON.stringify(record,null,2)+'\n');
  const installedBytes=await fs.readFile(target);
  const recordBytes=await fs.readFile(recordPath);
  const backupTime=(await fs.stat(backupPath)).mtimeMs;
  let prepared;

  await t.test('preparation leaves the installed archive, record and source backup intact',async()=>{
    prepared=await toolkit.prepareLocalizationRefresh(install,{outputRoot:path.join(root,'refresh'),backupRoot});
    assert.notEqual(prepared.artifactDir,first.artifactDir);
    assert.equal(prepared.manifest.appVersion,'2.22.0');
    assert.equal(prepared.manifest.sourceSha256,originalHash);
    assert.match(prepared.manifest.localizationFingerprint,/^[A-F0-9]{64}$/);
    assert.notEqual(prepared.manifest.localizationFingerprint,previousFingerprint);
    assert.deepEqual(await fs.readFile(target),installedBytes);
    assert.deepEqual(await fs.readFile(recordPath),recordBytes);
    assert.equal(toolkit.sha256(await fs.readFile(backupPath)),originalHash);
    assert.equal((await fs.stat(backupPath)).mtimeMs,backupTime);
    assert.ok(!(await fs.readdir(path.join(root,'refresh'))).some(name=>name.startsWith('.refresh-source-')));
  });

  await t.test('wrong source identity and changed candidate bytes cannot replace the installed archive',async()=>{
    assert.ok(prepared,'preparation did not return a candidate');
    const manifestPath=path.join(prepared.artifactDir,'manifest.json');
    const manifestBytes=await fs.readFile(manifestPath);
    try {
      await fs.writeFile(manifestPath,JSON.stringify({...prepared.manifest,sourceSha256:'F'.repeat(64)}));
      await assert.rejects(toolkit.refreshLocalizedArtifact(install,prepared.artifactDir,{backupRoot}),/invalid_artifact_identity/);
      assert.deepEqual(await fs.readFile(target),installedBytes);
      assert.deepEqual(await fs.readFile(recordPath),recordBytes);
    } finally {await fs.writeFile(manifestPath,manifestBytes);}
    const candidate=path.join(prepared.artifactDir,'app.asar');
    const candidateBytes=await fs.readFile(candidate);
    try {
      await fs.appendFile(candidate,'\nchanged candidate\n');
      await assert.rejects(toolkit.refreshLocalizedArtifact(install,prepared.artifactDir,{backupRoot}),/candidate_hash_mismatch/);
      assert.deepEqual(await fs.readFile(target),installedBytes);
      assert.deepEqual(await fs.readFile(recordPath),recordBytes);
    } finally {await fs.writeFile(candidate,candidateBytes);}
  });

  await t.test('installation drift is refused without overwriting the changed package',async()=>{
    const drifted=Buffer.concat([installedBytes,Buffer.from('\nchanged installed archive\n')]);
    try {
      await fs.writeFile(target,drifted);
      await assert.rejects(toolkit.refreshLocalizedArtifact(install,prepared.artifactDir,{backupRoot}),/installation_changed|invalid_localized_installation|unverified_localized_installation/);
      assert.deepEqual(await fs.readFile(target),drifted);
      assert.deepEqual(await fs.readFile(recordPath),recordBytes);
      assert.equal(toolkit.sha256(await fs.readFile(backupPath)),originalHash);
    } finally {await fs.writeFile(target,installedBytes);}
  });

  await t.test('refresh updates the installed record and keeps the original source restorable',async()=>{
    const result=await toolkit.refreshLocalizedArtifact(install,prepared.artifactDir,{backupRoot});
    assert.equal(result.refreshed,true);
    const refreshedRecord=JSON.parse(await fs.readFile(recordPath,'utf8'));
    assert.equal(refreshedRecord.artifactDir,prepared.artifactDir);
    assert.equal(refreshedRecord.localizationFingerprint,prepared.manifest.localizationFingerprint);
    assert.equal(refreshedRecord.sourceSha256,originalHash);
    assert.equal(refreshedRecord.patchedSha256,prepared.manifest.patchedSha256);
    assert.equal((await toolkit.inspectInstallation(install)).isLocalized,true);
    assert.equal(toolkit.sha256(await fs.readFile(target)),prepared.manifest.patchedSha256);
    assert.equal(toolkit.sha256(await fs.readFile(backupPath)),originalHash);
    assert.equal((await fs.stat(backupPath)).mtimeMs,backupTime);
    await toolkit.restoreInstallation(install,{backupRoot,compatibleSource:true});
    const restored=await toolkit.inspectInstallation(install);
    assert.equal(restored.isLocalized,false);
    assert.equal(restored.asarSha256,originalHash);
    assert.equal((await fs.stat(backupPath)).mtimeMs,backupTime);
  });
});
