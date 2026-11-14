import {testDataRoot} from './fixture-paths.mjs';
import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import {spawn} from 'node:child_process';
import {once} from 'node:events';
import { createPackage } from '@electron/asar';

let toolkit = {};
try { toolkit = await import('../src/toolkit.mjs'); } catch (e) { if (e.code !== 'ERR_MODULE_NOT_FOUND') throw e; }
const original = process.env.ANTIGRAVITY_TEST_INSTALL || path.join(testDataRoot,'verification','baseline');
const temporary = async (fn) => {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), 'agy-zh-test-'));
  try { return await fn(root); } finally { await fs.rm(root, {recursive: true, force: true}); }
};

test('inspect is read-only and recognizes the pinned original source', async () => {
  assert.equal(typeof toolkit.inspectInstallation, 'function', 'inspect API is missing');
  const before = await fs.stat(path.join(original, 'resources/app.asar'));
  const info = await toolkit.inspectInstallation(original);
  assert.equal(info.appVersion, '2.19.1');
  assert.equal(info.sourceKnown, true);
  assert.equal(info.asarSha256, toolkit.SOURCE_SHA256);
  assert.equal((await fs.stat(info.asarPath)).mtimeMs, before.mtimeMs);
});

test('build refuses an unknown version before changing any installation bytes', async () => {
  assert.equal(typeof toolkit.buildInstallation, 'function', 'build API is missing');
  await temporary(async root => {
    const staged = path.join(root, 'package');
    await fs.mkdir(staged);
    await fs.writeFile(path.join(staged, 'package.json'), JSON.stringify({name:'antigravity',version:'9.9.9',main:'dist/main.js'}));
    const install = path.join(root, 'install');
    await fs.mkdir(path.join(install, 'resources'), {recursive:true});
    await fs.writeFile(path.join(install, 'Antigravity.exe'), 'fixture never launched');
    await createPackage(staged, path.join(install, 'resources/app.asar'));
    const bytes = await fs.readFile(path.join(install, 'resources/app.asar'));
    await assert.rejects(toolkit.buildInstallation(install, {outputRoot:path.join(root,'build')}), /unsupported_version/);
    assert.deepEqual(await fs.readFile(path.join(install, 'resources/app.asar')), bytes);
    await assert.rejects(fs.stat(path.join(root,'build')), {code:'ENOENT'});
  });
});

test('deployment refuses a modified candidate and leaves the source untouched', async () => {
  assert.equal(typeof toolkit.applyArtifact, 'function', 'apply API is missing');
  await temporary(async root => {
    const install=path.join(root,'install');
    await fs.mkdir(path.join(install,'resources'),{recursive:true});
    await fs.copyFile(path.join(original,'resources/app.asar'),path.join(install,'resources/app.asar'));
    await fs.cp(path.join(original,'resources/app.asar.unpacked'),path.join(install,'resources/app.asar.unpacked'),{recursive:true});
    await fs.copyFile(path.join(original,'Antigravity.exe'),path.join(install,'Antigravity.exe'));
    const artifact=path.join(root,'artifact'); await fs.mkdir(artifact);
    await fs.writeFile(path.join(artifact,'app.asar'),'tampered');
    await fs.writeFile(path.join(artifact,'manifest.json'),JSON.stringify({schemaVersion:1,appVersion:'2.19.1',installRoot:await fs.realpath(install),sourceSha256:toolkit.SOURCE_SHA256,patchedSha256:'0'.repeat(64)}));
    await assert.rejects(toolkit.applyArtifact(install,artifact,{backupRoot:path.join(root,'backups')}), /candidate_hash_mismatch/);
    assert.equal((await toolkit.inspectInstallation(install)).asarSha256,toolkit.SOURCE_SHA256);
  });
});

test('build preserves every original packed and unpacked file outside the text whitelist', async () => {
  assert.equal(typeof toolkit.buildInstallation,'function');
  await temporary(async root => {
    const before=(await toolkit.inspectInstallation(original)).asarSha256;
    const result=await toolkit.buildInstallation(original,{outputRoot:root});
    assert.equal(result.manifest.comparison.changed.length,9);
    assert.equal(result.manifest.comparison.entries,1048);
    assert.ok(result.manifest.comparison.unchanged>1000);
    assert.equal((await toolkit.inspectInstallation(original)).asarSha256,before);
  });
});

test('restore refuses a newer installation even when an old valid backup exists', async () => {
  assert.equal(typeof toolkit.restoreInstallation, 'function', 'restore API is missing');
  await temporary(async root => {
    const install=path.join(root,'install'); await fs.mkdir(path.join(install,'resources'),{recursive:true});
    await fs.writeFile(path.join(install,'Antigravity.exe'),'fixture never launched');
    const staged=path.join(root,'package'); await fs.mkdir(staged);
    await fs.writeFile(path.join(staged,'package.json'),JSON.stringify({name:'antigravity',version:'2.20.0',main:'dist/main.js'}));
    await createPackage(staged,path.join(install,'resources/app.asar'));
    const backupRoot=path.join(root,'backups'); const backup=path.join(backupRoot,'2.19.1',toolkit.SOURCE_SHA256); await fs.mkdir(backup,{recursive:true});
    await fs.copyFile(path.join(original,'resources/app.asar'),path.join(backup,'app.asar'));
    await fs.writeFile(path.join(backup,'manifest.json'),JSON.stringify({schemaVersion:1,appVersion:'2.19.1',sourceSha256:toolkit.SOURCE_SHA256,patchedSha256:'f'.repeat(64),installRoot:install}));
    const before=await fs.readFile(path.join(install,'resources/app.asar'));
    await assert.rejects(toolkit.restoreInstallation(install,{backupRoot}), /unsupported_version/);
    assert.deepEqual(await fs.readFile(path.join(install,'resources/app.asar')),before);
  });
});

test('apply, restore and reapply preserve the immutable original backup', async () => {
  await temporary(async root => {
    const install=path.join(root,'install');
    await fs.mkdir(path.join(install,'resources'),{recursive:true});
    await fs.copyFile(path.join(original,'resources/app.asar'),path.join(install,'resources/app.asar'));
    await fs.cp(path.join(original,'resources/app.asar.unpacked'),path.join(install,'resources/app.asar.unpacked'),{recursive:true});
    await fs.copyFile(path.join(original,'Antigravity.exe'),path.join(install,'Antigravity.exe'));
    const {artifactDir,manifest}=await toolkit.buildInstallation(install,{outputRoot:path.join(root,'build')});
    const options={backupRoot:path.join(root,'backups')};
    const applied=await toolkit.applyArtifact(install,artifactDir,options);
    assert.equal((await toolkit.inspectInstallation(install)).asarSha256,manifest.patchedSha256);
    const backup=path.join(applied.backupDir,'app.asar');
    const before=await fs.stat(backup);
    assert.equal(toolkit.sha256(await fs.readFile(backup)),toolkit.SOURCE_SHA256);
    await toolkit.restoreInstallation(install,options);
    assert.equal((await toolkit.inspectInstallation(install)).asarSha256,toolkit.SOURCE_SHA256);
    await toolkit.applyArtifact(install,artifactDir,options);
    assert.equal((await toolkit.inspectInstallation(install)).asarSha256,manifest.patchedSha256);
    assert.equal((await fs.stat(backup)).mtimeMs,before.mtimeMs);
    await toolkit.restoreInstallation(install,options);
    assert.equal((await toolkit.inspectInstallation(install)).asarSha256,toolkit.SOURCE_SHA256);

    // A failed metadata commit after package replacement must roll the package back.
    const actualRename=fs.rename;
    fs.rename=async (...args)=>{
      if(args[1]===path.join(install,'resources','antigravity-zh-cn.install.json')) throw Object.assign(new Error('simulated_record_commit_failure'),{code:'EIO'});
      return actualRename(...args);
    };
    try { await assert.rejects(toolkit.applyArtifact(install,artifactDir,options),/simulated_record_commit_failure/); }
    finally { fs.rename=actualRename; }
    assert.equal((await toolkit.inspectInstallation(install)).asarSha256,toolkit.SOURCE_SHA256);
    await assert.rejects(fs.stat(path.join(install,'resources','antigravity-zh-cn.install.json')),{code:'ENOENT'});

    await toolkit.applyArtifact(install,artifactDir,options);
    const actualUnlink=fs.unlink;
    fs.unlink=async file=>{
      if(file===path.join(install,'resources','antigravity-zh-cn.install.json')) throw Object.assign(new Error('simulated_restore_record_cleanup_failure'),{code:'EACCES'});
      return actualUnlink(file);
    };
    try { await assert.rejects(toolkit.restoreInstallation(install,options),/simulated_restore_record_cleanup_failure/); }
    finally { fs.unlink=actualUnlink; }
    assert.equal((await toolkit.inspectInstallation(install)).asarSha256,toolkit.SOURCE_SHA256);
    assert.equal((await toolkit.restoreInstallation(install,options)).alreadyRestored,true);

    // This is our own temporary Node helper, never an Antigravity business instance.
    const helper=path.join(install,'test-helper.exe');
    await fs.copyFile(process.execPath,helper);
    const child=spawn(helper,['-e','setInterval(()=>{},1000)'],{windowsHide:true,stdio:'ignore'});
    await once(child,'spawn');
    try {
      await assert.rejects(toolkit.applyArtifact(install,artifactDir,options),/application_running/);
      assert.equal((await toolkit.inspectInstallation(install)).asarSha256,toolkit.SOURCE_SHA256);
    } finally { const exited=once(child,'exit'); child.kill(); await exited; }
  });
});
