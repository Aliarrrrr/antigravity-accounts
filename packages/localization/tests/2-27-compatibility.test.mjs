import {testDataRoot} from './fixture-paths.mjs';
import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import {extractFile,extractAll,createPackageWithOptions} from '@electron/asar';
import * as toolkit from '../src/toolkit.mjs';

const original=path.join(testDataRoot,'verification','repair-2026-10-29','baseline');
const originalHash='5ED34675D2D5E42F9BB1C0087B06D398C2E2EB052512EC1D0FEFD6B71E2DB807';
const readEntry=(archive,name)=>extractFile(archive,name.split('/').join(path.sep));
async function fixture(t) {
  const root=await fs.mkdtemp(path.join(os.tmpdir(),'agy-2-27-'));
  t.after(async()=>{
    assert.equal(await fs.realpath(root),root,'temporary directory changed before cleanup');
    await fs.rm(root,{recursive:true,force:true});
  });
  const install=path.join(root,'install');
  await fs.cp(original,install,{recursive:true});
  return {root,install};
}

test('reviewed 2.27.0 native revision is accepted without changing its source',async()=>{
  const before=await fs.stat(path.join(original,'resources','app.asar'));
  const info=await toolkit.inspectInstallation(original);
  assert.equal(info.appVersion,'2.27.0');
  assert.equal(info.asarSha256,originalHash);
  assert.equal(info.isLocalized,false);
  assert.equal(info.compatibleNativeSource,true);
  assert.equal((await fs.stat(info.asarPath)).mtimeMs,before.mtimeMs);
});

test('2.27 localization and restore preserve WebAuthn and hub window handling',async t=>{
  const {root,install}=await fixture(t);
  const before=await toolkit.inspectInstallation(install);
  const {artifactDir,manifest}=await toolkit.buildInstallation(install,{outputRoot:path.join(root,'artifacts'),compatibleSource:true});
  const candidate=path.join(artifactDir,'app.asar');
  assert.equal(manifest.comparison.entries,1081);
  assert.equal(manifest.comparison.changed.length,9);
  for(const name of ['dist/webauthn.js','dist/webviewBrowser.js','dist/utils.js','dist/debugConsole.js','dist/beyondcorpPac.js']) {
    assert.deepEqual(readEntry(candidate,name),readEntry(before.asarPath,name),name+' was changed');
  }
  const main=readEntry(before.asarPath,'dist/main.js').toString();
  const localizedMain=readEntry(candidate,'dist/main.js').toString();
  const windowLines=source=>source.split('\n').filter(line=>/webauthn_1|webviewBrowser_1|getHubWindows|hubWindows/.test(line));
  assert.ok(windowLines(main).length>10);
  assert.deepEqual(windowLines(localizedMain),windowLines(main));
  const ipc=readEntry(before.asarPath,'dist/ipcHandlers.js').toString();
  const localizedIpc=readEntry(candidate,'dist/ipcHandlers.js').toString();
  const targetWindow=/    const getTargetHubWindow = \(\) => \{[\s\S]*?^    \};/m;
  assert.ok(ipc.match(targetWindow));
  assert.equal(localizedIpc.match(targetWindow)?.[0],ipc.match(targetWindow)[0]);
  const preload=readEntry(before.asarPath,'dist/preload.js').toString();
  const localizedPreload=readEntry(candidate,'dist/preload.js').toString();
  const newTab=/    onWebviewOpenNewTab: \(callback\) => \{[\s\S]*?^    \},/m;
  assert.ok(preload.match(newTab));
  assert.equal(localizedPreload.match(newTab)?.[0],preload.match(newTab)[0]);
  assert.equal([...toolkit.archiveEntries(candidate).keys()].some(name=>name.includes('hostauth-addon')),false);
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

test('2.27 IPC drift is refused with intact translation anchors',async t=>{
  const {root,install}=await fixture(t);
  const archive=path.join(install,'resources','app.asar');
  const extracted=path.join(root,'extracted');
  extractAll(archive,extracted);
  await fs.appendFile(path.join(extracted,'dist','ipcHandlers.js'),'\nelectron_1.ipcMain.handle("unexpected", () => {});\n');
  await createPackageWithOptions(extracted,archive,{unpackDir:path.join('node_modules','chrome-devtools-mcp')});
  const before=await toolkit.inspectInstallation(install);
  assert.equal(before.compatibleNativeSource,false);
  const outputRoot=path.join(root,'artifacts');
  await assert.rejects(toolkit.buildInstallation(install,{outputRoot,compatibleSource:true}),/incompatible_native_source/);
  await assert.rejects(fs.stat(outputRoot),{code:'ENOENT'});
  assert.equal((await toolkit.inspectInstallation(install)).asarSha256,before.asarSha256);
});
