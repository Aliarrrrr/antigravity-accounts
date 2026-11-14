import {testDataRoot} from './fixture-paths.mjs';
import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import {spawn,spawnSync} from 'node:child_process';
import {extractAll,createPackageWithOptions} from '@electron/asar';
import {TOOL_ROOT, inspectInstallation, sha256} from '../src/toolkit.mjs';
let durable={};
try { durable=await import('../src/durable.mjs'); } catch(e) { if(e.code!=='ERR_MODULE_NOT_FOUND') throw e; }
const original=path.join(testDataRoot,'verification','repair-2026-10-10','baseline');
const older=path.join(testDataRoot,'verification','repair-2026-10-09','baseline');
async function fixture(t) {
  const root=await fs.mkdtemp(path.join(os.tmpdir(),'agy-durable-'));
  t.after(()=>fs.rm(root,{recursive:true,force:true}));
  const install=path.join(root,'install'),stateRoot=path.join(root,'external state');
  await fs.cp(original,install,{recursive:true});
  return {install,stateRoot,settleMs:0,processProbe:async()=>[]};
}
async function unsupportedOriginal(f,{unpackedMain=false}={}) {
  await fs.copyFile(process.execPath,path.join(f.install,'Antigravity.exe'));
  const extracted=path.join(path.dirname(f.install),'updated sources');
  extractAll(path.join(f.install,'resources','app.asar'),extracted);
  const metadataPath=path.join(extracted,'package.json'),metadata=JSON.parse(await fs.readFile(metadataPath,'utf8'));
  metadata.version='9.9.9';
  await fs.writeFile(metadataPath,JSON.stringify(metadata));
  await fs.appendFile(path.join(extracted,'dist','preload.js'),'\nelectron_1.contextBridge.exposeInMainWorld("updated", {});\n');
  await createPackageWithOptions(extracted,path.join(f.install,'resources','app.asar'),{unpackDir:unpackedMain?'dist':path.join('node_modules','chrome-devtools-mcp')});
}
const launchArguments=output=>['-e',`require('node:fs').writeFileSync(${JSON.stringify(output)},JSON.stringify(process.argv.slice(1)))`,'--','路径 with spaces','"quoted"'];
async function waitForwarded(output) {
  for(let n=0;n<100;n++) {
    try {return JSON.parse(await fs.readFile(output,'utf8'));}
    catch(error) {if(error.code!=='ENOENT') throw error;}
    await new Promise(resolve=>setTimeout(resolve,50));
  }
  assert.fail('executable did not receive the launch');
}

test('a coordinated launch preserves disabled components without invoking legacy repair',async t=>{
  const f=await fixture(t),before=await inspectInstallation(f.install);
  await fs.copyFile(process.execPath,path.join(f.install,'Antigravity.exe'));
  const accountsStateRoot=path.join(path.dirname(f.install),'accounts');await fs.mkdir(accountsStateRoot);
  const output=path.join(path.dirname(f.install),'coordinated-launch.json');
  const components={accounts:{installationState:'disabled'},localization:{installationState:'disabled'}};
  const result=await durable.launchLocalized(f.install,{...f,accountsStateRoot,preparedResult:{status:'ready',components}},launchArguments(output));
  assert.equal(result.launched,true);
  assert.deepEqual(await waitForwarded(output),['路径 with spaces','"quoted"']);
  assert.equal((await inspectInstallation(f.install)).asarSha256,before.asarSha256);
  await assert.rejects(fs.access(path.join(f.stateRoot,'backups')),{code:'ENOENT'});
  await assert.rejects(fs.access(path.join(f.install,'resources','antigravity-zh-cn.install.json')),{code:'ENOENT'});
});

test('an incomplete coordinated installation refuses launch before invoking repair',async t=>{
  const f=await fixture(t);
  await assert.rejects(durable.launchLocalized(f.install,{...f,preparedResult:{status:'pending_update'}}),error=>error.launchBlocked&&error.message==='invalid_prepared_launch_result');
});

test('normal launch holds the Accounts mutex during final validation and releases it after spawn', async t=>{
  const source=new URL('../../account-plugin/installer-lock.mjs',import.meta.url);
  try {await fs.access(source);} catch {t.skip('Accounts installer source is unavailable');return;}
  const f=await fixture(t);
  await unsupportedOriginal(f);
  const root=path.dirname(f.install),accountsStateRoot=path.join(root,'accounts'),packageRoot=path.join(root,'account package');
  await fs.mkdir(accountsStateRoot);
  await fs.mkdir(path.join(packageRoot,'account-plugin'),{recursive:true});
  await fs.writeFile(path.join(packageRoot,'account-plugin','update-recovery.mjs'),'export async function ensureAccounts(){return {status:"ready"};}');
  await fs.writeFile(path.join(packageRoot,'account-plugin','installer-lock.mjs'),`export {acquireInstallationLock} from ${JSON.stringify(source.href)};`);
  const resourcesRoot=path.join(f.install,'resources');
  await fs.writeFile(path.join(accountsStateRoot,'adapter-installation.json'),JSON.stringify({schemaVersion:1,phase:'applied',resourcesRoot,packageRoot}));
  const {acquireInstallationLock}=await import(source.href);
  let finalValidationExcludedWriter=false;
  const output=path.join(root,'forwarded-accounts-launch.json');
  const result=await durable.launchLocalized(f.install,{...f,accountsStateRoot,processProbe:async()=>{
    const other=await acquireInstallationLock(resourcesRoot);
    if(other)await other();else finalValidationExcludedWriter=true;
    return [];
  }},launchArguments(output));
  assert.equal(result.launched,true);
  assert.equal(finalValidationExcludedWriter,true,'final validation must hold the same mutex as manual installation');
  assert.deepEqual(await waitForwarded(output),['路径 with spaces','"quoted"']);
  const available=await acquireInstallationLock(resourcesRoot);
  assert.ok(available,'the launch must release Accounts ownership after spawning');
  await available();
});

test('ensure repairs an overwritten newer package and preserves version-specific backups',async t=>{
  assert.equal(typeof durable.ensureLocalized,'function','ensure API missing');
  const f=await fixture(t);
  const first=await durable.ensureLocalized(f.install,f);
  assert.equal(first.status,'repaired');
  const localized=await inspectInstallation(f.install);
  assert.equal(localized.isLocalized,true);
  const sourceHash='259C83FFA266088DDA6EDE520A601BBF06CC5C34985CDF56C5D68549C98DBB0E';
  const backup=path.join(f.stateRoot,'backups','2.22.0',sourceHash,'app.asar');
  const backupTime=(await fs.stat(backup)).mtimeMs;
  assert.equal(sha256(await fs.readFile(backup)),sourceHash);
  assert.equal((await durable.ensureLocalized(f.install,f)).status,'ready');
  assert.equal((await inspectInstallation(f.install)).asarSha256,localized.asarSha256);
  // The update overwrites app files and removes the installation record; manager state survives.
  await fs.unlink(path.join(f.install,'resources','antigravity-zh-cn.install.json'));
  await fs.cp(older,f.install,{recursive:true,force:true});
  const second=await durable.ensureLocalized(f.install,f);
  assert.equal(second.status,'repaired');
  assert.equal(second.appVersion,'2.21.1');
  assert.equal((await inspectInstallation(f.install)).isLocalized,true);
  assert.equal(sha256(await fs.readFile(backup)),sourceHash);
  assert.equal((await fs.stat(backup)).mtimeMs,backupTime);
  assert.equal(sha256(await fs.readFile(path.join(f.stateRoot,'backups','2.21.1','D075E5D9FF01F8806016AA88C39BF1429F068A941FDBE46147D8409843E62113','app.asar'))),'D075E5D9FF01F8806016AA88C39BF1429F068A941FDBE46147D8409843E62113');
});

test('running app or updater defers repair without replacing package or creating backups',async t=>{
  assert.equal(typeof durable.ensureLocalized,'function','ensure API missing');
  const f=await fixture(t), before=await inspectInstallation(f.install);
  const result=await durable.ensureLocalized(f.install,{...f,processProbe:async()=>[{ProcessId:123,kind:'updater'}]});
  assert.equal(result.status,'pending_idle');
  assert.equal((await inspectInstallation(f.install)).asarSha256,before.asarSha256);
  await assert.rejects(fs.stat(path.join(f.stateRoot,'backups')),{code:'ENOENT'});
});

test('outdated localization waits for app exit instead of accepting ready or replacing live files',async t=>{
  const f=await fixture(t);
  await durable.ensureLocalized(f.install,f);
  const before=await inspectInstallation(f.install);
  const recordPath=path.join(f.install,'resources','antigravity-zh-cn.install.json');
  const record=JSON.parse(await fs.readFile(recordPath,'utf8'));
  record.localizationFingerprint='outdated';
  await fs.writeFile(recordPath,JSON.stringify(record));
  const result=await durable.ensureLocalized(f.install,{...f,processProbe:async()=>[{ProcessId:123}]});
  assert.equal(result.status,'pending_idle');
  assert.equal(result.reason,'localization_update');
  assert.equal((await inspectInstallation(f.install)).asarSha256,before.asarSha256);
  const updated=await durable.ensureLocalized(f.install,f);
  assert.equal(updated.status,'repaired');
  assert.equal(updated.refreshed,true);
  assert.equal((await durable.ensureLocalized(f.install,f)).status,'ready');
});

test('watcher stamps include the actual translation files on their separate drive',async t=>{
  const f=await fixture(t);
  const stamp=JSON.parse(await durable.installationStamp(f.install,f));
  for(const name of ['dictionaries/zh-CN.json','src/translator.mjs','src/native-patches.mjs','src/native-compatibility.mjs','src/native-scope-signatures.json']) {
    const file=path.join(TOOL_ROOT,name),entry=stamp.find(item=>item[0]===file);
    assert.equal(entry?.[1],(await fs.stat(file)).size,'watcher treated '+file+' as missing');
  }
});

test('launch still forwards to the verified localized executable while a dictionary refresh waits',async t=>{
  const f=await fixture(t);
  await fs.copyFile(process.execPath,path.join(f.install,'Antigravity.exe'));
  await durable.ensureLocalized(f.install,f);
  const recordPath=path.join(f.install,'resources','antigravity-zh-cn.install.json');
  const record=JSON.parse(await fs.readFile(recordPath,'utf8'));
  record.localizationFingerprint='outdated';
  await fs.writeFile(recordPath,JSON.stringify(record));
  const output=path.join(f.stateRoot,'forwarded.json');
  const result=await durable.launchLocalized(f.install,{...f,processProbe:async()=>[{ProcessId:123,kind:'application'}]},['-e',`require('node:fs').writeFileSync(${JSON.stringify(output)},'forwarded')`]);
  assert.equal(result.status,'pending_idle');
  assert.equal(result.launched,true);
  for(let n=0;n<100;n++) {
    try {assert.equal(await fs.readFile(output,'utf8'),'forwarded');return;}
    catch(error) {if(error.code!=='ENOENT') throw error;}
    await new Promise(resolve=>setTimeout(resolve,50));
  }
  assert.fail('localized executable did not receive the launch');
});

test('native process classification permits another launch while the application and language server run',async t=>{
  const f=await fixture(t);
  await fs.copyFile(process.execPath,path.join(f.install,'Antigravity.exe'));
  const languageServer=path.join(f.install,'resources','bin','language_server.exe');
  await fs.mkdir(path.dirname(languageServer),{recursive:true});
  await fs.copyFile(process.execPath,languageServer);
  await t.test('running application and language server accept forwarded launch',async running=>{
    const ready=path.join(path.dirname(f.install),'runtime-ready.json');
    const program=`const child=require('node:child_process').spawn(${JSON.stringify(languageServer)},['-e','setInterval(()=>{},1000)'],{stdio:'ignore',windowsHide:true});child.once('spawn',()=>require('node:fs').writeFileSync(${JSON.stringify(ready)},JSON.stringify({pid:child.pid})));process.stdin.resume();process.stdin.on('end',()=>child.kill());child.on('close',()=>process.exit());`;
    const application=spawn(path.join(f.install,'Antigravity.exe'),['-e',program],{stdio:['pipe','ignore','ignore'],windowsHide:true});
    application.stdin.on('error',()=>{});
    running.after(async()=>{
      if(application.exitCode!==null) return;
      const closed=new Promise(resolve=>application.once('close',resolve));
      application.stdin.end();
      await closed;
    });
    const server=await waitForwarded(ready);
    const processes=await durable.maintenanceProcesses(f.install);
    assert.equal(processes.find(item=>item.ProcessId===application.pid)?.kind,'application');
    assert.equal(processes.find(item=>item.ProcessId===server.pid)?.kind,'application');
    const output=path.join(f.stateRoot,'forwarded.json');
    const result=await durable.launchLocalized(f.install,{stateRoot:f.stateRoot,settleMs:0},launchArguments(output));
    assert.equal(result.launched,true);
    assert.equal(result.status,'degraded');
    assert.deepEqual(await waitForwarded(output),['路径 with spaces','"quoted"']);
  });
});

test('an unsupported update launches the current original with its arguments and a degraded localization status',async t=>{
  const f=await fixture(t);
  await unsupportedOriginal(f);
  const before=await inspectInstallation(f.install),output=path.join(f.stateRoot,'forwarded.json');
  assert.equal(before.compatibleNativeSource,false);
  const result=await durable.launchLocalized(f.install,f,launchArguments(output));
  assert.equal(result.status,'degraded');
  assert.equal(result.localization,'unavailable');
  assert.equal(result.launched,true);
  assert.match(result.error,/incompatible_native_source/);
  assert.deepEqual(await waitForwarded(output),['路径 with spaces','"quoted"']);
  assert.equal((await inspectInstallation(f.install)).asarSha256,before.asarSha256);
  await assert.rejects(fs.stat(path.join(f.stateRoot,'backups')),{code:'ENOENT'});
  const status=JSON.parse(await fs.readFile(path.join(f.stateRoot,'status.json'),'utf8'));
  assert.equal(status.status,'degraded');
  assert.equal(status.launched,true);
});

test('an unavailable localization build leaves the current original launchable',async t=>{
  const f=await fixture(t);
  await fs.copyFile(process.execPath,path.join(f.install,'Antigravity.exe'));
  const before=await inspectInstallation(f.install),output=path.join(f.stateRoot,'forwarded.json');
  const originalMkdir=fs.mkdir;
  fs.mkdir=async function(directory,...args) {
    if(typeof directory==='string' && directory.startsWith(path.join(f.stateRoot,'artifacts')+path.sep)) throw new Error('simulated_build_unavailable');
    return originalMkdir.call(this,directory,...args);
  };
  let result;
  try {result=await durable.launchLocalized(f.install,f,launchArguments(output));}
  finally {fs.mkdir=originalMkdir;}
  assert.equal(result.status,'degraded');
  assert.match(result.error,/simulated_build_unavailable/);
  assert.deepEqual(await waitForwarded(output),['路径 with spaces','"quoted"']);
  assert.equal((await inspectInstallation(f.install)).asarSha256,before.asarSha256);
});

test('fallback permits a readable original main script moved to the unpacked package',async t=>{
  const f=await fixture(t);
  await unsupportedOriginal(f,{unpackedMain:true});
  const before=await inspectInstallation(f.install),output=path.join(f.stateRoot,'forwarded.json');
  assert.ok(Object.hasOwn(before.unpacked,'dist/main.js'));
  const result=await durable.launchLocalized(f.install,f,launchArguments(output));
  assert.equal(result.status,'degraded');
  assert.deepEqual(await waitForwarded(output),['路径 with spaces','"quoted"']);
  assert.equal((await inspectInstallation(f.install)).asarSha256,before.asarSha256);
});

test('a missing repair backup does not prevent an exactly matching localized app from opening',async t=>{
  const f=await fixture(t);
  await fs.copyFile(process.execPath,path.join(f.install,'Antigravity.exe'));
  const applied=await durable.ensureLocalized(f.install,f),before=await inspectInstallation(f.install);
  await fs.unlink(path.join(applied.backupDir,'app.asar'));
  const output=path.join(f.stateRoot,'forwarded.json');
  const result=await durable.launchLocalized(f.install,f,launchArguments(output));
  assert.equal(result.status,'degraded');
  assert.equal(result.localization,'stale');
  assert.match(result.error,/original_backup_missing/);
  assert.deepEqual(await waitForwarded(output),['路径 with spaces','"quoted"']);
  assert.equal((await inspectInstallation(f.install)).asarSha256,before.asarSha256);
});

test('fallback forwards to an already running original but refuses updater or unknown maintenance',async t=>{
  const f=await fixture(t);
  await unsupportedOriginal(f);
  const output=path.join(f.stateRoot,'forwarded.json');
  const launched=await durable.launchLocalized(f.install,{...f,processProbe:async()=>[{ProcessId:123,kind:'application'}]},launchArguments(output));
  assert.equal(launched.launched,true);
  await waitForwarded(output);
  await fs.unlink(output);
  for(const process of [{ProcessId:456,kind:'updater'},{ProcessId:789}]) {
    await assert.rejects(durable.launchLocalized(f.install,{...f,processProbe:async()=>[process]},launchArguments(output)),/maintenance_busy/);
    await assert.rejects(fs.stat(output),{code:'ENOENT'});
  }
});

test('fallback refuses a corrupt executable, incomplete archive, changed files, or unmatched localized record',async t=>{
  await t.test('corrupt executable',async t=>{
    const f=await fixture(t); await unsupportedOriginal(f);
    await fs.writeFile(path.join(f.install,'Antigravity.exe'),'incomplete update');
    await assert.rejects(durable.launchLocalized(f.install,f),/invalid_application_executable/);
  });
  await t.test('missing unpacked file',async t=>{
    const f=await fixture(t); await unsupportedOriginal(f);
    const info=await inspectInstallation(f.install),name=Object.keys(info.unpacked)[0];
    await fs.unlink(path.join(f.install,'resources','app.asar.unpacked',name));
    await assert.rejects(durable.launchLocalized(f.install,f),/ENOENT/);
  });
  await t.test('truncated executable section',async t=>{
    const f=await fixture(t); await unsupportedOriginal(f);
    const target=path.join(f.install,'Antigravity.exe');
    await fs.truncate(target,Math.floor((await fs.stat(target)).size/2));
    await assert.rejects(durable.launchLocalized(f.install,f),/incomplete_application_executable/);
  });
  await t.test('truncated packed content',async t=>{
    const f=await fixture(t); await unsupportedOriginal(f);
    const archive=path.join(f.install,'resources','app.asar');
    await fs.truncate(archive,(await fs.stat(archive)).size-1);
    await assert.rejects(durable.launchLocalized(f.install,f),/incomplete_application_archive/);
  });
  await t.test('truncated unpacked content',async t=>{
    const f=await fixture(t); await unsupportedOriginal(f);
    const info=await inspectInstallation(f.install),name=Object.keys(info.unpacked)[0];
    const target=path.join(f.install,'resources','app.asar.unpacked',name);
    await fs.truncate(target,Math.max(0,(await fs.stat(target)).size-1));
    await assert.rejects(durable.launchLocalized(f.install,f),/incomplete_application_archive/);
  });
  await t.test('changed packed content',async t=>{
    const f=await fixture(t); await unsupportedOriginal(f);
    const archive=path.join(f.install,'resources','app.asar'),content=await fs.readFile(archive);
    content[content.length-1]^=1;
    await fs.writeFile(archive,content);
    await assert.rejects(durable.launchLocalized(f.install,f),/application_archive_integrity_mismatch/);
  });
  await t.test('files changing while launch is prepared',async t=>{
    const f=await fixture(t); await unsupportedOriginal(f);
    let probes=0;
    await assert.rejects(durable.launchLocalized(f.install,{...f,processProbe:async()=>{
      if(++probes===2) await fs.appendFile(path.join(f.install,'Antigravity.exe'),'update in progress');
      return [];
    }}),/installation_changed/);
  });
  await t.test('unmatched localized record',async t=>{
    const f=await fixture(t);
    await fs.copyFile(process.execPath,path.join(f.install,'Antigravity.exe'));
    await durable.ensureLocalized(f.install,f);
    await fs.unlink(path.join(f.install,'resources','antigravity-zh-cn.install.json'));
    await assert.rejects(durable.launchLocalized(f.install,f),/unverified_localized_installation/);
  });
});

test('CLI distinguishes a refused application launch from a localization runtime bootstrap failure',async t=>{
  const f=await fixture(t);
  await fs.unlink(path.join(f.install,'resources','app.asar'));
  const result=spawnSync(process.execPath,[path.join(TOOL_ROOT,'durable.mjs'),'launch','--install-root',f.install,'--state-root',f.stateRoot],{encoding:'utf8',windowsHide:true});
  assert.equal(result.status,4,result.stderr);
  assert.match(JSON.parse(result.stderr.trim()).error,/ENOENT/);
});

test('surviving stale record after a verified update is archived before safe reapplication',async t=>{
  assert.equal(typeof durable.ensureLocalized,'function','ensure API missing');
  const f=await fixture(t);
  await durable.ensureLocalized(f.install,f);
  const recordPath=path.join(f.install,'resources','antigravity-zh-cn.install.json');
  const record=await fs.readFile(recordPath);
  await fs.copyFile(path.join(original,'resources','app.asar'),path.join(f.install,'resources','app.asar'));
  assert.equal((await durable.ensureLocalized(f.install,f)).status,'repaired');
  const archived=await fs.readdir(path.join(f.stateRoot,'superseded-records'));
  assert.equal(archived.length,1);
  assert.deepEqual(await fs.readFile(path.join(f.stateRoot,'superseded-records',archived[0])),record);
});

test('a live manager lock defers and is never removed by another repair',async t=>{
  assert.equal(typeof durable.ensureLocalized,'function','ensure API missing');
  const f=await fixture(t);
  const {acquireProcessLock}=await import('../src/process-lock.mjs');
  const release=await acquireProcessLock(f.stateRoot,'repair.lock');
  t.after(release);
  const lock=path.join(f.stateRoot,'repair.lock');
  const bytes=await fs.readFile(lock,'utf8');
  assert.equal((await durable.ensureLocalized(f.install,f)).status,'busy');
  assert.equal(await fs.readFile(lock,'utf8'),bytes);
  assert.equal((await inspectInstallation(f.install)).isLocalized,false);
});

test('localized marker without a matching install record is refused',async t=>{
  assert.equal(typeof durable.ensureLocalized,'function','ensure API missing');
  const f=await fixture(t);
  await durable.ensureLocalized(f.install,f);
  const before=await inspectInstallation(f.install);
  await fs.unlink(path.join(f.install,'resources','antigravity-zh-cn.install.json'));
  await assert.rejects(durable.ensureLocalized(f.install,f),/unverified_localized_installation/);
  assert.equal((await inspectInstallation(f.install)).asarSha256,before.asarSha256);
});

test('ready status requires an intact recoverable original backup',async t=>{
  assert.equal(typeof durable.ensureLocalized,'function','ensure API missing');
  const f=await fixture(t);
  const result=await durable.ensureLocalized(f.install,f);
  await fs.writeFile(path.join(result.backupDir,'app.asar'),'corrupt');
  await assert.rejects(durable.ensureLocalized(f.install,f),/backup_hash_mismatch/);
});

test('state junction into the official installation is refused before writing a lock',async t=>{
  assert.equal(typeof durable.ensureLocalized,'function','ensure API missing');
  const f=await fixture(t);
  await fs.symlink(path.join(f.install,'resources'),f.stateRoot,'junction');
  await assert.rejects(durable.ensureLocalized(f.install,f),/state_must_be_outside_installation/);
  await assert.rejects(fs.stat(path.join(f.install,'resources','repair.lock')),{code:'ENOENT'});
});

test('watcher does not continually reinspect an unchanged permanent error',async t=>{
  assert.equal(typeof durable.watchInstallation,'function','watch API missing');
  const f=await fixture(t);
  const prepared=await durable.ensureLocalized(f.install,f);
  assert.ok(['ready','repaired'].includes(prepared.status),JSON.stringify(prepared));
  await fs.unlink(path.join(f.install,'resources','antigravity-zh-cn.install.json'));
  const watching=durable.watchInstallation(f.install,{...f,intervalMs:50});
  const disable=async()=>{
    await fs.mkdir(path.join(f.stateRoot,'windows-persistence'),{recursive:true});
    await fs.writeFile(path.join(f.stateRoot,'windows-persistence','disabled'),'');
    await watching;
  };
  try {
    let first;
    for(let i=0;i<200;i++) {
      await new Promise(r=>setTimeout(r,10));
      try { const status=JSON.parse(await fs.readFile(path.join(f.stateRoot,'status.json'),'utf8')); if(status.status==='error'){first=status;break;} } catch(e) { if(e.code!=='ENOENT') throw e; }
    }
    assert.equal(first?.status,'error');
    await new Promise(r=>setTimeout(r,400));
    const next=JSON.parse(await fs.readFile(path.join(f.stateRoot,'status.json'),'utf8'));
    assert.equal(next.checkedUtc,first.checkedUtc);
  } finally { await disable(); }
});

test('watcher restores launch integration after an updater resets it during a deferred repair',async t=>{
  const f=await fixture(t), shortcut=path.join(f.stateRoot,'simulated shortcut');
  await fs.mkdir(f.stateRoot,{recursive:true});
  let busy=true;
  const watching=durable.watchInstallation(f.install,{...f,intervalMs:50,
    processProbe:async()=>busy?[{ProcessId:123,kind:'updater'}]:[],
    onInstallationChanged:()=>fs.writeFile(shortcut,'guarded')});
  async function waitStatus(want) {
    const until=Date.now()+30000;
    while(Date.now()<until) {
      try {const value=JSON.parse(await fs.readFile(path.join(f.stateRoot,'status.json'),'utf8')); if(value.status===want)return value;}catch(e){if(e.code!=='ENOENT')throw e;}
      await new Promise(r=>setTimeout(r,25));
    }
    throw new Error('watcher status timeout: '+want);
  }
  try {
    await waitStatus('pending_idle');
    await fs.writeFile(shortcut,'direct official exe');
    busy=false;
    await waitStatus('repaired');
    // Wait until the repair's integration callback can finish before observing its effect.
    await new Promise(r=>setTimeout(r,100));
    assert.equal(await fs.readFile(shortcut,'utf8'),'guarded');
    assert.equal((await inspectInstallation(f.install)).isLocalized,true);
  } finally {
    await fs.mkdir(path.join(f.stateRoot,'windows-persistence'),{recursive:true});
    await fs.writeFile(path.join(f.stateRoot,'windows-persistence','disabled'),'');
    await watching;
  }
});

for(const waitingStatus of ['pending_idle','pending_update']) {
  test('watcher only probes unchanged '+waitingStatus+' and responds to idle or disable',async t=>{
    const f=await fixture(t);
    const watchedFiles=new Set([path.join(f.install,'Antigravity.exe'),path.join(f.install,'resources','app.asar')]);
    const originalReadFile=fs.readFile;
    let reads=0,probes=0,busy=true;
    fs.readFile=async function(file,...args) {
      if(typeof file==='string' && watchedFiles.has(path.resolve(file))) reads++;
      return originalReadFile.call(this,file,...args);
    };
    const watching=durable.watchInstallation(f.install,{...f,intervalMs:50,processProbe:async()=>{
      probes++;
      // A maintenance process can start after the candidate build, producing
      // pending_update rather than the initial pending_idle deferral.
      if(waitingStatus==='pending_update' && probes===1) return [];
      return busy?[{ProcessId:123,kind:'updater'}]:[];
    }});
    const statusFile=path.join(f.stateRoot,'status.json');
    async function waitUntil(check,label) {
      const deadline=Date.now()+30000;
      while(Date.now()<deadline) {
        const result=await check();
        if(result) return result;
        await new Promise(resolve=>setTimeout(resolve,25));
      }
      throw new Error('watcher wait timeout: '+label);
    }
    async function waitStatus(want) {
      return waitUntil(async()=>{
        try { const status=JSON.parse(await fs.readFile(statusFile,'utf8')); return status.status===want&&status; }
        catch(error) { if(error.code!=='ENOENT') throw error; }
      },want);
    }
    let result;
    try {
      const first=await waitStatus(waitingStatus),firstReads=reads,firstProbes=probes;
      assert.ok(firstReads>0,'initial installation was not inspected');
      await waitUntil(()=>probes>=firstProbes+3,'repeated maintenance probes');
      assert.equal(reads,firstReads,'unchanged busy installation was read again');
      const next=JSON.parse(await fs.readFile(statusFile,'utf8'));
      assert.equal(next.checkedUtc,first.checkedUtc,'unchanged busy status was published again');
      if(waitingStatus==='pending_idle') {
        busy=false;
        await waitStatus('repaired');
        assert.equal((await inspectInstallation(f.install)).isLocalized,true);
      }
    } finally {
      try {
        await fs.mkdir(path.join(f.stateRoot,'windows-persistence'),{recursive:true});
        await fs.writeFile(path.join(f.stateRoot,'windows-persistence','disabled'),'');
        result=await watching;
      } finally { fs.readFile=originalReadFile; }
    }
    assert.equal(result.status,'watcher_disabled');
  });
}
