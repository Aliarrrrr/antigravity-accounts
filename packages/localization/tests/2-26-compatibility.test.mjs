import {testDataRoot} from './fixture-paths.mjs';
import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import {extractFile,extractAll,createPackageWithOptions} from '@electron/asar';
import * as toolkit from '../src/toolkit.mjs';

const original=path.join(testDataRoot,'verification','repair-2026-10-24','baseline');
const originalHash='EA1E741D68C5D5C2EFA5B76793F98D45B27D0976F10C98597E64118EB977B085';
const readEntry=(archive,name)=>extractFile(archive,name.split('/').join(path.sep));
async function fixture(t) {
  const root=await fs.mkdtemp(path.join(os.tmpdir(),'agy-2-26-'));
  t.after(async()=>{
    assert.equal(await fs.realpath(root),root,'temporary directory changed before cleanup');
    await fs.rm(root,{recursive:true,force:true});
  });
  const install=path.join(root,'install');
  await fs.cp(original,install,{recursive:true});
  return {root,install};
}

test('reviewed installed 2.26.0 revision with its account adapter is accepted',async()=>{
  const before=await fs.stat(path.join(original,'resources','app.asar'));
  const info=await toolkit.inspectInstallation(original);
  assert.equal(info.appVersion,'2.26.0');
  assert.equal(info.asarSha256,originalHash);
  assert.equal(info.isLocalized,false);
  assert.equal(info.compatibleNativeSource,true);
  assert.equal((await fs.stat(info.asarPath)).mtimeMs,before.mtimeMs);
});

test('2.26 localization and restore preserve account integration and webview checks',async t=>{
  const {root,install}=await fixture(t);
  const before=await toolkit.inspectInstallation(install);
  const {artifactDir,manifest}=await toolkit.buildInstallation(install,{outputRoot:path.join(root,'artifacts'),compatibleSource:true});
  const candidate=path.join(artifactDir,'app.asar');
  assert.equal(manifest.comparison.entries,1087);
  assert.equal(manifest.comparison.changed.length,9);
  const accountEntries=[...toolkit.archiveEntries(before.asarPath).keys()].filter(name=>name.startsWith('dist/hostauth-addon/'));
  assert.equal(accountEntries.length,12);
  for(const name of accountEntries) assert.deepEqual(readEntry(candidate,name),readEntry(before.asarPath,name),name+' was changed');
  const main=readEntry(before.asarPath,'dist/main.js').toString();
  const localizedMain=readEntry(candidate,'dist/main.js').toString();
  const accountLines=source=>source.split('\n').filter(line=>line.includes('hostauth'));
  assert.ok(accountLines(main).length>10);
  assert.deepEqual(accountLines(localizedMain),accountLines(main));
  const ipc=readEntry(before.asarPath,'dist/ipcHandlers.js').toString();
  const localizedIpc=readEntry(candidate,'dist/ipcHandlers.js').toString();
  const marker='    // Execute JavaScript inside an Electron <webview>';
  assert.ok(ipc.indexOf(marker)>0);
  assert.equal(localizedIpc.slice(localizedIpc.indexOf(marker)),ipc.slice(ipc.indexOf(marker)));
  const menu=readEntry(candidate,'dist/menu.js').toString();
  assert.ok(menu.includes('(0, debugConsole_1.configureDebugConsoleMenu)(menu);'));
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

test('2.26 preload drift is refused even with unchanged translation anchors',async t=>{
  const {root,install}=await fixture(t);
  const archive=path.join(install,'resources','app.asar');
  const extracted=path.join(root,'extracted');
  extractAll(archive,extracted);
  await fs.appendFile(path.join(extracted,'dist','preload.js'),'\nelectron_1.contextBridge.exposeInMainWorld("unexpected", {});\n');
  await createPackageWithOptions(extracted,archive,{unpackDir:path.join('node_modules','chrome-devtools-mcp')});
  const before=await toolkit.inspectInstallation(install);
  assert.equal(before.compatibleNativeSource,false);
  const outputRoot=path.join(root,'artifacts');
  await assert.rejects(toolkit.buildInstallation(install,{outputRoot,compatibleSource:true}),/incompatible_native_source/);
  await assert.rejects(fs.stat(outputRoot),{code:'ENOENT'});
  assert.equal((await toolkit.inspectInstallation(install)).asarSha256,before.asarSha256);
});
