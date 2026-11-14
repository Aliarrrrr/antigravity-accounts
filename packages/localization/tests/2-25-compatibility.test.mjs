import {testDataRoot} from './fixture-paths.mjs';
import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import {extractFile,extractAll,createPackageWithOptions} from '@electron/asar';
import * as toolkit from '../src/toolkit.mjs';

const original=path.join(testDataRoot,'verification','repair-2026-10-22','baseline');
const originalHash='AD7FB1A177A11522490CBA910C0970A117BEC568CD9AD85233FA62873361ED05';
async function fixture(t) {
  const root=await fs.mkdtemp(path.join(os.tmpdir(),'agy-2-25-'));
  t.after(async()=>{
    assert.equal(await fs.realpath(root),root,'temporary directory changed before cleanup');
    await fs.rm(root,{recursive:true,force:true});
  });
  const install=path.join(root,'install');
  await fs.cp(original,install,{recursive:true});
  return {root,install};
}

test('reviewed 2.25.0 native revision is accepted without changing the original',async()=>{
  const before=await fs.stat(path.join(original,'resources','app.asar'));
  const info=await toolkit.inspectInstallation(original);
  assert.equal(info.appVersion,'2.25.0');
  assert.equal(info.asarSha256,originalHash);
  assert.equal(info.isLocalized,false);
  assert.equal(info.compatibleNativeSource,true);
  assert.equal((await fs.stat(info.asarPath)).mtimeMs,before.mtimeMs);
});

test('2.25.0 build, apply and restore preserve the upstream window-close change',async t=>{
  const {root,install}=await fixture(t);
  const before=await toolkit.inspectInstallation(install);
  const {artifactDir,manifest}=await toolkit.buildInstallation(install,{outputRoot:path.join(root,'artifacts'),compatibleSource:true});
  assert.equal(manifest.comparison.entries,1087);
  assert.equal(manifest.comparison.changed.length,9);
  const main=extractFile(path.join(artifactDir,'app.asar'),path.join('dist','main.js')).toString('utf8');
  assert.ok(!main.includes('electron_1.app.dock?.hide();'),'localization restored the removed Dock behavior');
  assert.ok(main.includes('您确定要退出吗？'));
  const options={backupRoot:path.join(root,'backups'),compatibleSource:true};
  const applied=await toolkit.applyArtifact(install,artifactDir,options);
  const localized=await toolkit.inspectInstallation(install);
  assert.equal(localized.isLocalized,true);
  assert.equal(localized.executableSha256,before.executableSha256);
  assert.deepEqual(localized.unpacked,before.unpacked);
  assert.equal(toolkit.sha256(await fs.readFile(path.join(applied.backupDir,'app.asar'))),originalHash);
  await toolkit.restoreInstallation(install,options);
  assert.equal((await toolkit.inspectInstallation(install)).asarSha256,originalHash);
});

test('unreviewed main changes are still refused when all localization anchors remain',async t=>{
  const {root,install}=await fixture(t);
  const archive=path.join(install,'resources','app.asar');
  const extracted=path.join(root,'extracted');
  extractAll(archive,extracted);
  await fs.appendFile(path.join(extracted,'dist','main.js'),'\nelectron_1.dialog.showMessageBox = () => {};\n');
  await createPackageWithOptions(extracted,archive,{unpackDir:path.join('node_modules','chrome-devtools-mcp')});
  const before=await toolkit.inspectInstallation(install);
  assert.equal(before.compatibleNativeSource,false);
  const outputRoot=path.join(root,'artifacts');
  await assert.rejects(toolkit.buildInstallation(install,{outputRoot,compatibleSource:true}),/incompatible_native_source/);
  await assert.rejects(fs.stat(outputRoot),{code:'ENOENT'});
  assert.equal((await toolkit.inspectInstallation(install)).asarSha256,before.asarSha256);
});
