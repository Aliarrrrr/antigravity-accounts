import {testDataRoot} from './fixture-paths.mjs';
import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import vm from 'node:vm';
import {execFile,spawn} from 'node:child_process';
import {once} from 'node:events';
import {promisify} from 'node:util';
import {JSDOM} from 'jsdom';
import {extractFile,extractAll,createPackageWithOptions} from '@electron/asar';
import * as toolkit from '../src/toolkit.mjs';

const baseline=process.env.ANTIGRAVITY_TEST_AUTOMATIC_INSTALL || path.join(testDataRoot,'verification','repair-2026-10-10','baseline');
const originalHash='259C83FFA266088DDA6EDE520A601BBF06CC5C34985CDF56C5D68549C98DBB0E';
const run=promisify(execFile);
const temporary=async fn=>{
  const root=await fs.mkdtemp(path.join(os.tmpdir(),'agy-zh-automatic-test-'));
  try { return await fn(root); } finally { await fs.rm(root,{recursive:true,force:true}); }
};
async function copyInstallation(install) {
  await fs.mkdir(path.join(install,'resources'),{recursive:true});
  await fs.copyFile(path.join(baseline,'Antigravity.exe'),path.join(install,'Antigravity.exe'));
  await fs.copyFile(path.join(baseline,'resources','app.asar'),path.join(install,'resources','app.asar'));
  await fs.cp(path.join(baseline,'resources','app.asar.unpacked'),path.join(install,'resources','app.asar.unpacked'),{recursive:true});
}
async function changeOriginal(root,change) {
  const install=path.join(root,'install'); await copyInstallation(install);
  const extracted=path.join(root,'extracted'); extractAll(path.join(install,'resources','app.asar'),extracted);
  const metadataPath=path.join(extracted,'package.json');
  const metadata=JSON.parse(await fs.readFile(metadataPath,'utf8'));
  metadata.version='9.9.9'; await fs.writeFile(metadataPath,JSON.stringify(metadata));
  if(change) await change(extracted);
  await createPackageWithOptions(extracted,path.join(install,'resources','app.asar'),{unpackDir:path.join('node_modules','chrome-devtools-mcp')});
  return install;
}

test('inspect reports compatible native source on the 2.22 original without changing it',async()=>{
  const archive=path.join(baseline,'resources','app.asar'),before=await fs.stat(archive);
  const info=await toolkit.inspectInstallation(baseline);
  assert.equal(info.appVersion,'2.22.0'); assert.equal(info.asarSha256,originalHash);
  assert.equal(info.sourceKnown,false); assert.equal(info.isLocalized,false);
  assert.equal(info.compatibleNativeSource,true);
  assert.equal(info.entries,1085); assert.equal(Object.keys(info.unpacked).length,347);
  assert.equal((await fs.stat(archive)).mtimeMs,before.mtimeMs);
});

test('compatible mode builds an unknown version while default build still refuses it',async()=>{
  await temporary(async root=>{
    const install=await changeOriginal(root),info=await toolkit.inspectInstallation(install);
    assert.equal(info.appVersion,'9.9.9'); assert.equal(info.sourceKnown,false);
    await assert.rejects(toolkit.buildInstallation(install,{outputRoot:path.join(root,'default')}),/unsupported_version/);
    const {artifactDir,manifest}=await toolkit.buildInstallation(install,{outputRoot:path.join(root,'automatic'),compatibleSource:true});
    assert.equal(manifest.appVersion,'9.9.9'); assert.equal(manifest.sourceSha256,info.asarSha256);
    assert.equal(manifest.comparison.changed.length,9);
    assert.equal((await toolkit.inspectInstallation(install)).asarSha256,info.asarSha256);
    await assert.rejects(fs.stat(path.join(root,'default')),{code:'ENOENT'});
    const dom=new JSDOM('<nav><button>Version 9.9.9</button></nav><textarea>Version 9.9.9</textarea>',{url:'https://127.0.0.1:43123/',runScripts:'outside-only',pretendToBeVisual:true});
    try {
      const sandbox={exports:{},window:{},document:{readyState:'complete'},require(){return {
        contextBridge:{exposeInMainWorld(){}},ipcRenderer:{},
        webFrame:{executeJavaScript(code){dom.window.eval(code);return Promise.resolve();}}
      };}};
      sandbox.window.top=sandbox.window;
      vm.runInNewContext(extractFile(path.join(artifactDir,'app.asar'),path.join('dist','preload.js')).toString(),sandbox);
      await new Promise(resolve=>setTimeout(resolve,70));
      assert.equal(dom.window.document.querySelector('button').textContent,'版本 9.9.9');
      assert.equal(dom.window.document.querySelector('textarea').value,'Version 9.9.9');
    } finally {dom.window.close();}
  });
});

for(const [name,mutate] of [
  ['native behavior changes with intact patch anchors',source=>source+'\nelectron_1.contextBridge.exposeInMainWorld("unexpected", {});\n'],
  ['a changed preload anchor',source=>source.replace("exposeInMainWorld('wsl', wslAPI)","exposeInMainWorld('wsl-changed', wslAPI)")]
]) {
  test('compatible mode refuses '+name+' before creating output',async()=>{
    await temporary(async root=>{
      const install=await changeOriginal(root,async extracted=>{
        const file=path.join(extracted,'dist','preload.js');
        await fs.writeFile(file,mutate(await fs.readFile(file,'utf8')));
      });
      const before=await toolkit.inspectInstallation(install);
      assert.equal(before.compatibleNativeSource,false);
      await assert.rejects(toolkit.buildInstallation(install,{outputRoot:path.join(root,'build'),compatibleSource:true}),/incompatible_native_source/);
      await assert.rejects(toolkit.applyArtifact(install,path.join(root,'missing-artifact'),{backupRoot:path.join(root,'backups'),compatibleSource:true}),/incompatible_native_source/);
      assert.equal((await toolkit.inspectInstallation(install)).asarSha256,before.asarSha256);
      await assert.rejects(fs.stat(path.join(root,'build')),{code:'ENOENT'});
      await assert.rejects(fs.stat(path.join(root,'backups')),{code:'ENOENT'});
    });
  });
}

test('compatible 2.22 apply and restore preserve immutable version-specific original backups',async()=>{
  await temporary(async root=>{
    const install=path.join(root,'install'); await copyInstallation(install);
    const options={backupRoot:path.join(root,'backups'),compatibleSource:true};
    const {artifactDir,manifest}=await toolkit.buildInstallation(install,{outputRoot:path.join(root,'build'),compatibleSource:true});
    assert.equal(manifest.sourceSha256,originalHash); assert.equal(manifest.comparison.entries,1085);
    await assert.rejects(toolkit.applyArtifact(install,artifactDir,{backupRoot:options.backupRoot}),/unsupported_version/);
    // A crashed repair's diagnostic file must not permanently block future repairs.
    await fs.writeFile(path.join(install,'resources','.antigravity-zh-cn.lock'),'stale diagnostic record');
    const applied=await toolkit.applyArtifact(install,artifactDir,options);
    assert.equal(applied.backupDir,path.join(options.backupRoot,'2.22.0',originalHash));
    const backup=path.join(applied.backupDir,'app.asar'),backupTime=(await fs.stat(backup)).mtimeMs;
    assert.equal(toolkit.sha256(await fs.readFile(backup)),originalHash);
    const current=await toolkit.inspectInstallation(install);
    assert.equal(current.isLocalized,true); assert.equal(current.compatibleNativeSource,false);
    assert.equal(current.asarSha256,manifest.patchedSha256);
    await assert.rejects(toolkit.restoreInstallation(install,{backupRoot:options.backupRoot}),/unsupported_version/);
    assert.equal((await toolkit.restoreInstallation(install,options)).sourceSha256,originalHash);
    assert.equal((await toolkit.inspectInstallation(install)).asarSha256,originalHash);
    await toolkit.applyArtifact(install,artifactDir,options);
    assert.equal((await fs.stat(backup)).mtimeMs,backupTime);
    await toolkit.restoreInstallation(install,options);
    assert.equal((await toolkit.inspectInstallation(install)).asarSha256,originalHash);
  });
});

test('compatible apply rejects cross-version and incorrect original-hash manifests',async()=>{
  await temporary(async root=>{
    const install=path.join(root,'install'); await copyInstallation(install);
    const artifact=path.join(root,'artifact'); await fs.mkdir(artifact);
    for(const identity of [{appVersion:'2.21.1',sourceSha256:originalHash},{appVersion:'2.22.0',sourceSha256:'F'.repeat(64)}]) {
      await fs.writeFile(path.join(artifact,'manifest.json'),JSON.stringify({schemaVersion:1,...identity}));
      await assert.rejects(toolkit.applyArtifact(install,artifact,{backupRoot:path.join(root,'backups'),compatibleSource:true}),/invalid_artifact_identity/);
      assert.equal((await toolkit.inspectInstallation(install)).asarSha256,originalHash);
    }
    await assert.rejects(fs.stat(path.join(root,'backups')),{code:'ENOENT'});
  });
});

test('compatible restore rejects a forged source backup even when record hashes match',async()=>{
  await temporary(async root=>{
    const install=await changeOriginal(root,async extracted=>{
      const file=path.join(extracted,'dist','preload.js'); await fs.appendFile(file,'\nelectron_1.contextBridge.exposeInMainWorld("unexpected", {});\n');
    });
    const info=await toolkit.inspectInstallation(install),backupRoot=path.join(root,'backups');
    const backup=path.join(backupRoot,info.appVersion,info.asarSha256); await fs.mkdir(backup,{recursive:true});
    await fs.copyFile(info.asarPath,path.join(backup,'app.asar'));
    const identity={schemaVersion:1,appVersion:info.appVersion,sourceSha256:info.asarSha256,executableSha256:info.executableSha256,unpacked:info.unpacked};
    await fs.writeFile(path.join(backup,'manifest.json'),JSON.stringify(identity));
    const recordPath=path.join(install,'resources','antigravity-zh-cn.install.json');
    const recordBytes=JSON.stringify({...identity,installRoot:info.installRoot,patchedSha256:'F'.repeat(64)});
    await fs.writeFile(recordPath,recordBytes);
    await assert.rejects(toolkit.restoreInstallation(install,{backupRoot,compatibleSource:true}),/incompatible_native_source/);
    assert.equal((await toolkit.inspectInstallation(install)).asarSha256,info.asarSha256);
    assert.equal(await fs.readFile(recordPath,'utf8'),recordBytes);
  });
});

test('compatible restore refuses another version record before changing original bytes',async()=>{
  await temporary(async root=>{
    const install=path.join(root,'install'); await copyInstallation(install);
    const info=await toolkit.inspectInstallation(install),recordPath=path.join(install,'resources','antigravity-zh-cn.install.json');
    const recordBytes=JSON.stringify({schemaVersion:1,appVersion:'2.21.1',sourceSha256:originalHash,installRoot:info.installRoot,patchedSha256:'F'.repeat(64)});
    await fs.writeFile(recordPath,recordBytes);
    await assert.rejects(toolkit.restoreInstallation(install,{backupRoot:path.join(root,'backups'),compatibleSource:true}),/invalid_install_record/);
    assert.equal((await toolkit.inspectInstallation(install)).asarSha256,originalHash);
    assert.equal(await fs.readFile(recordPath,'utf8'),recordBytes);
  });
});

test('atomic replacement refuses an updater outside the installation before modifying ASAR',{skip:process.platform!=='win32'},async()=>{
  await temporary(async root=>{
    const install=path.join(root,'install'),local=path.join(root,'localappdata');
    await fs.mkdir(path.join(install,'resources'),{recursive:true});
    await fs.writeFile(path.join(install,'Antigravity.exe'),'fixture never launched');
    const target=path.join(install,'resources','app.asar'),candidate=path.join(root,'candidate.asar');
    const original=Buffer.from('original'),replacement=Buffer.from('replacement');
    await fs.writeFile(target,original); await fs.writeFile(candidate,replacement);
    const updaterDirectory=path.join(local,'antigravity-updater');await fs.mkdir(updaterDirectory,{recursive:true});
    const helper=path.join(updaterDirectory,'update-helper.exe');await fs.copyFile(process.execPath,helper);
    const child=spawn(helper,['-e','setInterval(()=>{},1000)'],{windowsHide:true,stdio:'ignore'});
    await once(child,'spawn');
    try {
      await assert.rejects(run('powershell.exe',['-NoProfile','-File',path.join(toolkit.TOOL_ROOT,'scripts','Replace-Asar.ps1'),'-InstallRoot',install,'-CandidatePath',candidate,'-ExpectedHash',toolkit.sha256(original),'-CandidateHash',toolkit.sha256(replacement)],{windowsHide:true,env:{...process.env,LOCALAPPDATA:local}}),error=>/maintenance_busy/.test(error.stderr));
      assert.deepEqual(await fs.readFile(target),original);
      assert.deepEqual(await fs.readdir(path.join(install,'resources')),['app.asar']);
    } finally {const exited=once(child,'exit');child.kill();await exited;}
  });
});

test('atomic replacement succeeds when Windows PowerShell reports an empty maintenance array',{skip:process.platform!=='win32'},async()=>{
  await temporary(async root=>{
    const install=path.join(root,'install');await fs.mkdir(path.join(install,'resources'),{recursive:true});
    await fs.writeFile(path.join(install,'Antigravity.exe'),'fixture never launched');
    const target=path.join(install,'resources','app.asar'),candidate=path.join(root,'candidate.asar');
    const original=Buffer.from('original'),replacement=Buffer.from('replacement');
    await fs.writeFile(target,original);await fs.writeFile(candidate,replacement);
    await run('powershell.exe',['-NoProfile','-File',path.join(toolkit.TOOL_ROOT,'scripts','Replace-Asar.ps1'),'-InstallRoot',install,'-CandidatePath',candidate,'-ExpectedHash',toolkit.sha256(original),'-CandidateHash',toolkit.sha256(replacement)],{windowsHide:true,env:{...process.env,LOCALAPPDATA:path.join(root,'empty-localappdata')}});
    assert.deepEqual(await fs.readFile(target),replacement);
    assert.deepEqual(await fs.readdir(path.join(install,'resources')),['app.asar']);
  });
});
