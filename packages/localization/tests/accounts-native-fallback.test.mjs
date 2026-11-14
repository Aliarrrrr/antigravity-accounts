import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import {spawnSync} from 'node:child_process';
import {fileURLToPath} from 'node:url';
import {createPackage} from '@electron/asar';
import {ensureLocalized,launchLocalized,watchInstallation} from '../src/durable.mjs';
import {acquireAccountsInstallationLock} from '../src/process-lock.mjs';
import {adaptedNativeDigests} from '../src/native-fallback.mjs';

const toolRoot=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'..');
const pause=ms=>new Promise(resolve=>setTimeout(resolve,ms));

async function fixture(t) {
  const temporary=await fs.realpath(os.tmpdir());
  const root=await fs.mkdtemp(path.join(temporary,'agy-native-start-'));
  t.after(async()=>{
    assert.equal(await fs.realpath(root),root);
    assert.equal(path.dirname(root),temporary);
    await fs.rm(root,{recursive:true,force:true,maxRetries:5,retryDelay:100});
  });
  const installRoot=path.join(root,'install'),resources=path.join(installRoot,'resources');
  const stateRoot=path.join(root,'localization'),accountsStateRoot=path.join(root,'accounts');
  const source=path.join(root,'source'),packageRoot=path.join(accountsStateRoot,'recovery-package-test');
  for(const directory of [path.join(resources,'bin'),stateRoot,accountsStateRoot,path.join(source,'dist')]) await fs.mkdir(directory,{recursive:true});
  await fs.copyFile(process.execPath,path.join(installRoot,'Antigravity.exe'));
  await fs.copyFile(process.execPath,path.join(resources,'bin','language_server.exe'));
  await fs.writeFile(path.join(source,'package.json'),JSON.stringify({name:'antigravity',version:process.versions.node,main:'dist/main.js'}));
  for(const name of ['main','languageServer','hostBridgeServer','preload']) await fs.writeFile(path.join(source,'dist',name+'.js'),'module.exports = {};');
  await createPackage(source,path.join(resources,'app.asar'));
  const receipt={schemaVersion:1,phase:'applied',resourcesRoot:resources,packageRoot,
    preparedRoot:path.join(accountsStateRoot,'prepared-test'),backupRoot:path.join(accountsStateRoot,'restore-test'),
    originalAsarSha256:'1'.repeat(64),patchedAsarSha256:'2'.repeat(64),originalNativeSha256:'3'.repeat(64),patchedNativeSha256:'4'.repeat(64)};
  await fs.writeFile(path.join(accountsStateRoot,'adapter-installation.json'),JSON.stringify(receipt));
  await fs.writeFile(path.join(accountsStateRoot,'accounts.dpapi'),'untouched authorization');
  await fs.writeFile(path.join(stateRoot,'accounts-installation.json'),JSON.stringify({schemaVersion:1,installRoot,accountsStateRoot}));
  return {root,installRoot,resources,stateRoot,accountsStateRoot,packageRoot,receipt,settleMs:0,processProbe:async()=>[]};
}

async function waitForJson(filename) {
  for(let attempt=0;attempt<100;attempt++) {
    try { return JSON.parse(await fs.readFile(filename,'utf8')); }
    catch(error) { if(error.code!=='ENOENT' && !(error instanceof SyntaxError)) throw error; await pause(30); }
  }
  assert.fail('expected application output');
}

test('missing recovery cache permits a complete native update only after confirmation',{skip:process.platform!=='win32'},async t=>{
  const f=await fixture(t);
  const archive=await fs.readFile(path.join(f.resources,'app.asar'));
  const receipt=await fs.readFile(path.join(f.accountsStateRoot,'adapter-installation.json'));
  const result=await ensureLocalized(f.installRoot,f);
  assert.equal(result.status,'degraded');
  assert.equal(result.mode,'native');
  assert.equal(result.reason,'accounts_recovery_runtime_unavailable');
  const output=path.join(f.root,'opened.json');
  const args=['-e',`require('node:fs').writeFileSync(${JSON.stringify(output)},JSON.stringify(process.argv.slice(1)))`,'--','路径 with spaces'];
  const cancelled=await launchLocalized(f.installRoot,{...f,confirmNativeMode:async()=>false},args);
  assert.equal(cancelled.cancelled,true);
  await assert.rejects(fs.access(output),{code:'ENOENT'});
  const launched=await launchLocalized(f.installRoot,{...f,confirmNativeMode:async()=>{
    assert.equal(await acquireAccountsInstallationLock(f.resources),null,'manual installer must stay excluded during confirmation');
    return true;
  }},args);
  assert.equal(launched.launched,true);
  assert.deepEqual(await waitForJson(output),['路径 with spaces']);
  assert.deepEqual(await fs.readFile(path.join(f.resources,'app.asar')),archive);
  assert.deepEqual(await fs.readFile(path.join(f.accountsStateRoot,'adapter-installation.json')),receipt);
  assert.equal(await fs.readFile(path.join(f.accountsStateRoot,'accounts.dpapi'),'utf8'),'untouched authorization');
});

test('program changes during native-mode confirmation prevent launch',{skip:process.platform!=='win32'},async t=>{
  const f=await fixture(t);
  await assert.rejects(launchLocalized(f.installRoot,{...f,confirmNativeMode:async()=>{
    await fs.appendFile(path.join(f.resources,'app.asar'),'changed');
    return true;
  }}),error=>error.launchBlocked===true&&error.message==='installation_changed');
});

test('degraded watcher waits for program or recovery files to change',{skip:process.platform!=='win32'},async t=>{
  const f=await fixture(t),counter=path.join(f.root,'checks.json');
  await fs.mkdir(path.join(f.packageRoot,'account-plugin'),{recursive:true});
  const inspector=new URL('../src/native-fallback.mjs',import.meta.url).href;
  const module=path.join(f.packageRoot,'account-plugin','update-recovery.mjs');
  const writeModule=async revision=>fs.writeFile(module,`
    import fs from 'node:fs/promises';
    import {inspectNativeFallback} from ${JSON.stringify(inspector)};
    export async function ensureAccounts(input) {
      let count=0;try{count=JSON.parse(await fs.readFile(${JSON.stringify(counter)},'utf8'))}catch{}
      await fs.writeFile(${JSON.stringify(counter)},JSON.stringify(count+1));
      return {status:'degraded',mode:'native',reason:${JSON.stringify(revision)},program:await inspectNativeFallback({...input,receipt:${JSON.stringify(f.receipt)}})};
    }`);
  await writeModule('first');
  const task=watchInstallation(f.installRoot,{...f,intervalMs:30});
  const stop=async()=>{
    await fs.mkdir(path.join(f.stateRoot,'windows-persistence'),{recursive:true});
    await fs.writeFile(path.join(f.stateRoot,'windows-persistence','disabled'),'stop');
    await task;
  };
  t.after(stop);
  await waitForJson(counter);
  assert.equal((await waitForJson(path.join(f.stateRoot,'status.json'))).status,'degraded');
  await pause(500);
  assert.equal(await waitForJson(counter),1);
  await writeModule('compatible-package-arrived');
  for(let n=0;n<100;n++){if(await waitForJson(counter)>=2) break;await pause(30);}
  assert.equal(await waitForJson(counter),2);
});

test('PowerShell bootstrap native mode also confirms identity and preserves launch arguments',{skip:process.platform!=='win32'},async t=>{
  const f=await fixture(t),scripts=path.join(f.root,'toolkit','scripts');
  await fs.mkdir(scripts,{recursive:true});
  const launcher=await fs.readFile(path.join(toolRoot,'scripts','Launch-Localized.ps1'),'utf8');
  const confirmation=launcher.match(/function Confirm-NativeMode \{[\s\S]*?\n  \}/)?.[0];
  assert.ok(confirmation);
  assert.doesNotMatch(confirmation,/if\s*\(\$NoPopup\)/,'the normal VBS shortcut must still show the identity choice');
  const copied=launcher.replace(/function Confirm-NativeMode \{[\s\S]*?\n  \}\n  try \{/,"function Confirm-NativeMode { $env:AGY_TEST_CONFIRMATION='shown'; return $true }\n  try {");
  assert.notEqual(copied,launcher);
  const script=path.join(scripts,'Launch-Localized.ps1');
  await fs.writeFile(script,copied);
  await fs.writeFile(path.join(scripts,'Maintenance-State.ps1'),"param([string]$InstallRoot)\n'[]'\n");
  const output=path.join(f.root,'opened.json');
  const args=['-e',`require('node:fs').writeFileSync(${JSON.stringify(output)},JSON.stringify({args:process.argv.slice(1),confirmation:process.env.AGY_TEST_CONFIRMATION}))`,'--','中文 path'];
  const shell=path.join(process.env.SystemRoot,'System32','WindowsPowerShell','v1.0','powershell.exe');
  const result=spawnSync(shell,['-NoProfile','-File',script,'-InstallRoot',f.installRoot,'-StateRoot',f.stateRoot,'-ToolkitRoot',path.dirname(scripts),
    '-NodePath',path.join(f.root,'missing.exe'),'-NoPopup','-ArgumentsJsonBase64',Buffer.from(JSON.stringify(args),'utf8').toString('base64')],{encoding:'utf8',windowsHide:true,timeout:30000});
  assert.equal(result.status,0,result.stderr);
  assert.deepEqual(await waitForJson(output),{args:['中文 path'],confirmation:'shown'});
  const status=await waitForJson(path.join(f.stateRoot,'status.json'));
  assert.equal(status.mode,'native');
  assert.equal(status.accounts,'unavailable');
});

test('bootstrap and JavaScript reject the same known modified native programs',async()=>{
  const launcher=await fs.readFile(path.join(toolRoot,'scripts','Launch-Localized.ps1'),'utf8');
  for(const digest of adaptedNativeDigests) assert.ok(launcher.includes(digest),digest);
});
