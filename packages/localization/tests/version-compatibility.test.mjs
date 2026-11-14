import {testDataRoot} from './fixture-paths.mjs';
import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import {createPackage} from '@electron/asar';
import * as toolkit from '../src/toolkit.mjs';

const previousVersion='2.19.1';
const previousHash='341234FAF45BD1776FD5418A3C288DEDC5487EBFCF153F53D75DE17CFE15C1DE';
const currentVersion='2.21.1';
const currentHash='D075E5D9FF01F8806016AA88C39BF1429F068A941FDBE46147D8409843E62113';
const previous=process.env.ANTIGRAVITY_TEST_INSTALL || path.join(testDataRoot,'verification','baseline');
const current=process.env.ANTIGRAVITY_TEST_UPGRADE_INSTALL || path.join(testDataRoot,'verification','repair-2026-10-09','baseline');
const temporary=async fn=>{
  const root=await fs.mkdtemp(path.join(os.tmpdir(),'agy-zh-version-test-'));
  try { return await fn(root); } finally { await fs.rm(root,{recursive:true,force:true}); }
};
async function copyInstallation(source,install) {
  await fs.mkdir(path.join(install,'resources'),{recursive:true});
  await fs.copyFile(path.join(source,'Antigravity.exe'),path.join(install,'Antigravity.exe'));
  await fs.copyFile(path.join(source,'resources','app.asar'),path.join(install,'resources','app.asar'));
  await fs.cp(path.join(source,'resources','app.asar.unpacked'),path.join(install,'resources','app.asar.unpacked'),{recursive:true});
}
async function seedPreviousBackup(backupRoot) {
  const info=await toolkit.inspectInstallation(previous);
  const directory=path.join(backupRoot,previousVersion,previousHash);
  await fs.mkdir(directory,{recursive:true});
  const archive=path.join(directory,'app.asar');
  const manifest=path.join(directory,'manifest.json');
  await fs.copyFile(info.asarPath,archive);
  await fs.writeFile(manifest,JSON.stringify({schemaVersion:1,appVersion:previousVersion,sourceSha256:previousHash,executableSha256:info.executableSha256,unpacked:info.unpacked,createdUtc:'2026-10-04T00:00:00.000Z'}));
  return {archive,manifest,archiveTime:(await fs.stat(archive)).mtimeMs,manifestBytes:await fs.readFile(manifest),manifestTime:(await fs.stat(manifest)).mtimeMs};
}
async function assertPreviousBackupUntouched(backup) {
  assert.equal(toolkit.sha256(await fs.readFile(backup.archive)),previousHash);
  assert.equal((await fs.stat(backup.archive)).mtimeMs,backup.archiveTime);
  assert.deepEqual(await fs.readFile(backup.manifest),backup.manifestBytes);
  assert.equal((await fs.stat(backup.manifest)).mtimeMs,backup.manifestTime);
}

test('inspect recognizes the verified 2.21.1 original without changing it',async()=>{
  const before=await fs.stat(path.join(current,'resources','app.asar'));
  const info=await toolkit.inspectInstallation(current);
  assert.equal(info.appVersion,currentVersion);
  assert.equal(info.asarSha256,currentHash);
  assert.equal(info.sourceKnown,true);
  assert.equal(info.entries,1048);
  assert.equal(Object.keys(info.unpacked).length,293);
  assert.equal((await fs.stat(info.asarPath)).mtimeMs,before.mtimeMs);
});

for(const version of [previousVersion,currentVersion]) {
  test('build and apply refuse an unverified archive for supported '+version,async()=>{
    await temporary(async root=>{
      const staged=path.join(root,'package'); await fs.mkdir(staged);
      await fs.writeFile(path.join(staged,'package.json'),JSON.stringify({name:'antigravity',version,main:'dist/main.js'}));
      const install=path.join(root,'install'); await fs.mkdir(path.join(install,'resources'),{recursive:true});
      await fs.writeFile(path.join(install,'Antigravity.exe'),'fixture never launched');
      const archive=path.join(install,'resources','app.asar');
      await createPackage(staged,archive);
      const bytes=await fs.readFile(archive);
      assert.equal((await toolkit.inspectInstallation(install)).sourceKnown,false);
      await assert.rejects(toolkit.buildInstallation(install,{outputRoot:path.join(root,'build')}),/unknown_source_hash/);
      await assert.rejects(toolkit.applyArtifact(install,path.join(root,'artifact'),{backupRoot:path.join(root,'backups')}),/unknown_source_hash/);
      assert.deepEqual(await fs.readFile(archive),bytes);
      await assert.rejects(fs.stat(path.join(root,'build')),{code:'ENOENT'});
      await assert.rejects(fs.stat(path.join(root,'backups')),{code:'ENOENT'});
    });
  });
}

for(const [source,artifactVersion,artifactHash] of [[current,previousVersion,previousHash],[previous,currentVersion,currentHash]]) {
  test('apply rejects an artifact from '+artifactVersion+' on the other supported version',async()=>{
    await temporary(async root=>{
      const install=path.join(root,'install'); await copyInstallation(source,install);
      const info=await toolkit.inspectInstallation(install);
      const artifact=path.join(root,'artifact'); await fs.mkdir(artifact);
      await fs.writeFile(path.join(artifact,'manifest.json'),JSON.stringify({schemaVersion:1,appVersion:artifactVersion,sourceSha256:artifactHash}));
      await assert.rejects(toolkit.applyArtifact(install,artifact,{backupRoot:path.join(root,'backups')}),/invalid_artifact_identity/);
      assert.equal((await toolkit.inspectInstallation(install)).asarSha256,info.asarSha256);
      await assert.rejects(fs.stat(path.join(root,'backups')),{code:'ENOENT'});
    });
  });
}

test('restore rejects a legacy install record on another supported version',async()=>{
  await temporary(async root=>{
    const install=path.join(root,'install'); await copyInstallation(current,install);
    const backupRoot=path.join(root,'backups');
    const backup=await seedPreviousBackup(backupRoot);
    const previousInfo=await toolkit.inspectInstallation(previous);
    const recordPath=path.join(install,'resources','antigravity-zh-cn.install.json');
    const recordBytes=Buffer.from(JSON.stringify({schemaVersion:1,appVersion:previousVersion,installRoot:await fs.realpath(install),sourceSha256:previousHash,patchedSha256:'F'.repeat(64),executableSha256:previousInfo.executableSha256,unpacked:previousInfo.unpacked}));
    await fs.writeFile(recordPath,recordBytes);
    await assert.rejects(toolkit.restoreInstallation(install,{backupRoot}),/invalid_install_record/);
    assert.equal((await toolkit.inspectInstallation(install)).asarSha256,currentHash);
    assert.deepEqual(await fs.readFile(recordPath),recordBytes);
    await assertPreviousBackupUntouched(backup);
  });
});

test('2.21.1 build, apply, restore and reapply keep version-specific immutable backups',async()=>{
  await temporary(async root=>{
    const install=path.join(root,'install'); await copyInstallation(current,install);
    const options={backupRoot:path.join(root,'backups')};
    const previousBackup=await seedPreviousBackup(options.backupRoot);
    const {artifactDir,manifest}=await toolkit.buildInstallation(install,{outputRoot:path.join(root,'build')});
    assert.equal(manifest.appVersion,currentVersion);
    assert.equal(manifest.sourceSha256,currentHash);
    assert.ok(path.basename(artifactDir).startsWith(currentVersion+'-'));
    assert.equal(manifest.comparison.changed.length,9);
    assert.equal(manifest.comparison.entries,1048);
    assert.equal((await toolkit.inspectInstallation(install)).asarSha256,currentHash);

    const applied=await toolkit.applyArtifact(install,artifactDir,options);
    assert.equal(applied.appVersion,currentVersion);
    assert.equal(applied.sourceSha256,currentHash);
    assert.equal(applied.backupDir,path.join(options.backupRoot,currentVersion,currentHash));
    assert.equal((await toolkit.inspectInstallation(install)).asarSha256,manifest.patchedSha256);
    const recordPath=path.join(install,'resources','antigravity-zh-cn.install.json');
    const record=JSON.parse(await fs.readFile(recordPath,'utf8'));
    assert.equal(record.appVersion,currentVersion);
    assert.equal(record.sourceSha256,currentHash);
    const backup=path.join(applied.backupDir,'app.asar');
    const backupTime=(await fs.stat(backup)).mtimeMs;
    assert.equal(toolkit.sha256(await fs.readFile(backup)),currentHash);
    const backupManifest=JSON.parse(await fs.readFile(path.join(applied.backupDir,'manifest.json'),'utf8'));
    assert.equal(backupManifest.appVersion,currentVersion);
    assert.equal(backupManifest.sourceSha256,currentHash);

    const restored=await toolkit.restoreInstallation(install,options);
    assert.equal(restored.appVersion,currentVersion);
    assert.equal(restored.sourceSha256,currentHash);
    assert.equal((await toolkit.inspectInstallation(install)).asarSha256,currentHash);
    await toolkit.applyArtifact(install,artifactDir,options);
    assert.equal((await toolkit.inspectInstallation(install)).asarSha256,manifest.patchedSha256);
    assert.equal((await fs.stat(backup)).mtimeMs,backupTime);
    await toolkit.restoreInstallation(install,options);
    assert.equal((await toolkit.inspectInstallation(install)).asarSha256,currentHash);

    const actualRename=fs.rename;
    fs.rename=async (...args)=>{
      if(args[1]===recordPath) throw Object.assign(new Error('simulated_upgrade_record_commit_failure'),{code:'EIO'});
      return actualRename(...args);
    };
    try { await assert.rejects(toolkit.applyArtifact(install,artifactDir,options),/simulated_upgrade_record_commit_failure/); }
    finally { fs.rename=actualRename; }
    assert.equal((await toolkit.inspectInstallation(install)).asarSha256,currentHash);
    await assert.rejects(fs.stat(recordPath),{code:'ENOENT'});
    await assertPreviousBackupUntouched(previousBackup);
  });
});
