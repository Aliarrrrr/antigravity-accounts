import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import os from 'node:os';
import {inspectInstallation,prepareInstallation,applyInstallation,ensureInstallation}from '../coordinator.mjs';

async function fixture(t,{accountsFail=false,localizationFail=false,redirectedAccounts=false,accountRootConflict=false}={}){
  const root=await fs.mkdtemp(path.join(os.tmpdir(),'agy-coordinator-'));t.after(()=>fs.rm(root,{recursive:true,force:true}));
  const options={installRoot:path.join(root,'app'),stateRoot:path.join(root,'state'),accountsStateRoot:path.join(root,'account-data'),localizationStateRoot:path.join(root,'zh-state'),bundleRoot:path.join(root,'bundle'),enabledComponents:{accounts:true,localization:true},checkStopped:async()=>{},validateBundle:async()=>({version:'0.4.0-preview.17'})};
  const write=async(name,text)=>{const target=path.join(options.bundleRoot,name);await fs.mkdir(path.dirname(target),{recursive:true});await fs.writeFile(target,text);};
  const physicalAccountsRoot=path.join(root,'physical-account-data');
  for(const directory of [path.join(options.installRoot,'resources/bin'),path.join(options.installRoot,'resources/app.asar.unpacked'),options.stateRoot,options.accountsStateRoot,options.localizationStateRoot,...(redirectedAccounts?[physicalAccountsRoot]:[])])await fs.mkdir(directory,{recursive:true});
  await fs.writeFile(path.join(options.installRoot,'Antigravity.exe'),'official-exe');await fs.writeFile(path.join(options.installRoot,'resources/app.asar'),'official-asar');await fs.writeFile(path.join(options.installRoot,'resources/bin/language_server.exe'),'official-native');await fs.writeFile(path.join(options.accountsStateRoot,'accounts.v1.dpapi'),'do-not-touch');
  await write('localization/package.json','{"type":"module"}');await write('accounts/package.json','{"type":"module"}');
  await write('localization/src/toolkit.mjs',`import fs from 'node:fs/promises';import path from 'node:path';import{createHash}from'node:crypto';const sha=b=>createHash('sha256').update(b).digest('hex');export async function inspectInstallation(root){const asarPath=path.join(root,'resources/app.asar'),bytes=await fs.readFile(asarPath);return{appVersion:'2.32.1',asarPath,asarSha256:sha(bytes),executableSha256:sha(await fs.readFile(path.join(root,'Antigravity.exe'))),unpacked:{},isLocalized:bytes.toString().endsWith('+zh')}};export async function prepareLocalization({sourceInstallRoot,targetInstallRoot,outputRoot}){${localizationFail?"throw Error('incompatible_native_source');":""}const info=await inspectInstallation(sourceInstallRoot);await fs.mkdir(outputRoot,{recursive:true});const bytes=await fs.readFile(info.asarPath),patched=Buffer.from(bytes.toString()+'+zh');await fs.writeFile(path.join(outputRoot,'app.asar'),patched);return{artifactDir:outputRoot,manifest:{schemaVersion:1,appVersion:info.appVersion,installRoot:targetInstallRoot,sourceSha256:sha(bytes).toUpperCase(),patchedSha256:sha(patched).toUpperCase(),executableSha256:info.executableSha256,unpacked:{}}};}export function localizationInstallRecord(manifest,options){return{...manifest,...options}}`);
  await write('localization/src/process-lock.mjs','export async function acquireProcessLock(){return async()=>{}}');
  await write('accounts/account-plugin/manage-install.mjs',`import fs from'node:fs/promises';import path from'node:path';export async function resolveAccountsStateRoot({installRoot,stateRoot,create}){if(installRoot!==${JSON.stringify(options.installRoot)}||create!==true)throw Error('test_resolver_arguments_invalid');${accountRootConflict?"throw Error('accounts_state_root_conflict');":redirectedAccounts?'return '+JSON.stringify(physicalAccountsRoot)+';':"await fs.mkdir(stateRoot,{recursive:true});return fs.realpath(stateRoot);"}}export async function withInstallationLock(root,action){return action({assertHeld(){}})}export async function resolveRecoveryReceiptPaths(receipt){return receipt}export async function cacheRecoveryPackage({stateRoot,suffix}){const root=path.join(stateRoot,'recovery-package-'+suffix);await fs.mkdir(root);return root}`);
  await write('accounts/hostauth-adapter/src/process-guard.mjs','export function assertApplicationStopped(){}');
  await write('accounts/hostauth-adapter/src/package-copy.mjs',`import fs from'node:fs/promises';import path from'node:path';import{createHash}from'node:crypto';const sha=b=>createHash('sha256').update(b).digest('hex');export async function buildCopy({sourceAsar,outputRoot,languageServerPath}){${accountsFail?"throw Error('Unsupported exact program source: synthetic unknown');":""}const a=await fs.readFile(sourceAsar),n=await fs.readFile(languageServerPath),b=Buffer.from(a.toString()+'+accounts'),m=Buffer.from(n.toString()+'+accounts');for(const[name,bytes]of Object.entries({'app.asar':b,'bin/language_server.exe':m,'hostauth-addon/config.json':Buffer.from('{}'),'hostauth-addon/native/language_server.exe':Buffer.from('provider'),'hostauth-addon/component/Antigravity.Accounts.exe':Buffer.from('component')})){const file=path.join(outputRoot,'resources',name);await fs.mkdir(path.dirname(file),{recursive:true});await fs.writeFile(file,bytes);}return{schemaVersion:3,antigravityVersion:'2.32.1',originalAsarSha256:sha(a),patchedAsarSha256:sha(b),originalNativeSha256:sha(n),patchedNativeSha256:sha(m)}}`);
  return options;
}

test('both layers are built before live files change and commit the destination identity together',async t=>{
  const f=await fixture(t),prepared=await prepareInstallation(f);assert.equal(prepared.components.accounts.installationState,'prepared');assert.equal(prepared.components.localization.installationState,'prepared');assert.equal(await fs.readFile(path.join(f.installRoot,'resources/app.asar'),'utf8'),'official-asar');
  const applied=await applyInstallation(f);assert.equal(applied.activationAllowed,true);assert.equal(await fs.readFile(path.join(f.installRoot,'resources/app.asar'),'utf8'),'official-asar+accounts+zh');const record=JSON.parse(await fs.readFile(path.join(f.installRoot,'resources/antigravity-zh-cn.install.json')));assert.equal(record.installRoot,await fs.realpath(f.installRoot));assert.equal(await fs.readFile(path.join(f.accountsStateRoot,'accounts.v1.dpapi'),'utf8'),'do-not-touch');
  assert.equal((await ensureInstallation(f)).phase,'ready');
});
test('failed Chinese compatibility keeps the fully prepared Accounts candidate usable in English',async t=>{const f=await fixture(t,{localizationFail:true});const result=await ensureInstallation(f);assert.equal(result.components.accounts.installationState,'ready');assert.equal(result.components.localization.installationState,'unsupported');assert.equal(await fs.readFile(path.join(f.installRoot,'resources/app.asar'),'utf8'),'official-asar+accounts');});
test('unknown Accounts programs retain native programs and explicitly require identity confirmation',async t=>{const f=await fixture(t,{accountsFail:true}),result=await ensureInstallation(f);assert.equal(result.components.accounts.installationState,'unsupported');assert.equal(result.components.accounts.identityConfirmationRequired,true);assert.equal(await fs.readFile(path.join(f.installRoot,'resources/bin/language_server.exe'),'utf8'),'official-native');assert.equal(await fs.readFile(path.join(f.accountsStateRoot,'accounts.v1.dpapi'),'utf8'),'do-not-touch');});
test('live applications allow candidate preparation but defer all installation writes',async t=>{const f=await fixture(t);const result=await ensureInstallation({...f,checkStopped:async()=>{throw Error('application_running')}});assert.equal(result.phase,'pending_idle');assert.equal(result.activationAllowed,false);assert.equal(await fs.readFile(path.join(f.installRoot,'resources/app.asar'),'utf8'),'official-asar');});
test('a second vendor update invalidates a prepared plan instead of applying an older host',async t=>{const f=await fixture(t);await prepareInstallation(f);await fs.writeFile(path.join(f.installRoot,'Antigravity.exe'),'official-newer');await assert.rejects(applyInstallation(f),/compatibility_host_changed_before_apply/);assert.equal(await fs.readFile(path.join(f.installRoot,'Antigravity.exe'),'utf8'),'official-newer');});

test('a changed component selection invalidates same-version applied state and rebuilds the requested combination',async t=>{
  const f=await fixture(t),first=await ensureInstallation(f);
  assert.deepEqual(first.enabledComponents,{accounts:true,localization:true});
  const selected={...f,enabledComponents:{accounts:true,localization:false}};
  const inspected=await inspectInstallation(selected);
  assert.equal(inspected.phase,'inspection_required');assert.equal(inspected.components.localization.installationState,'disabled');
  const changed=await ensureInstallation(selected);
  assert.deepEqual(changed.enabledComponents,selected.enabledComponents);assert.equal(changed.components.localization.installationState,'disabled');
  assert.equal(await fs.readFile(path.join(f.installRoot,'resources/app.asar'),'utf8'),'official-asar+accounts');
  assert.equal((await inspectInstallation(selected)).phase,'ready');
  assert.deepEqual(JSON.parse(await fs.readFile(path.join(f.stateRoot,'applied-state.json'))).enabledComponents,selected.enabledComponents);
});
test('pending candidates retain their component selection and cannot apply after it changes',async t=>{
  const f=await fixture(t),prepared=await prepareInstallation(f);
  assert.deepEqual(prepared.enabledComponents,f.enabledComponents);
  const selected={...f,enabledComponents:{accounts:false,localization:true}};
  await assert.rejects(applyInstallation(selected),/compatibility_components_changed_before_apply/);
  assert.equal(await fs.readFile(path.join(f.installRoot,'resources/app.asar'),'utf8'),'official-asar');
  const pending=await ensureInstallation({...selected,checkStopped:async()=>{throw Error('application_running')}});
  assert.equal(pending.phase,'pending_idle');assert.deepEqual(pending.enabledComponents,selected.enabledComponents);
  assert.equal(pending.components.accounts.installationState,'disabled');
  assert.deepEqual(JSON.parse(await fs.readFile(path.join(f.stateRoot,'prepared-installation.json'))).enabledComponents,selected.enabledComponents);
});
test('account public-file resolution controls the physical receipt location without copying a logical account store',async t=>{
  const f=await fixture(t,{redirectedAccounts:true}),physicalRoot=path.join(path.dirname(f.accountsStateRoot),'physical-account-data');
  await fs.writeFile(path.join(physicalRoot,'accounts.v1.dpapi'),'physical-authorization-stays');
  const result=await ensureInstallation(f);
  assert.equal(result.components.accounts.installationState,'ready');
  await assert.rejects(fs.access(path.join(f.accountsStateRoot,'adapter-installation.json')),{code:'ENOENT'});
  const receipt=JSON.parse(await fs.readFile(path.join(physicalRoot,'adapter-installation.json')));
  assert.equal(path.dirname(receipt.backupRoot),physicalRoot);assert.equal(path.dirname(receipt.preparedRoot),physicalRoot);
  const prepared=JSON.parse(await fs.readFile(path.join(f.stateRoot,'prepared-installation.json')));
  const journal=JSON.parse(await fs.readFile(path.join(prepared.transactionRoot,'transaction.json')));
  assert.equal(journal.roots.accounts,physicalRoot);
  assert.equal(await fs.readFile(path.join(physicalRoot,'accounts.v1.dpapi'),'utf8'),'physical-authorization-stays');
  assert.equal(await fs.readFile(path.join(f.accountsStateRoot,'accounts.v1.dpapi'),'utf8'),'do-not-touch');
});
test('an explicit conflicting account root is rejected before any preparation or program replacement',async t=>{
  const f=await fixture(t,{accountRootConflict:true});
  await assert.rejects(ensureInstallation(f),/accounts_state_root_conflict/);
  assert.equal(await fs.readFile(path.join(f.installRoot,'resources/app.asar'),'utf8'),'official-asar');
  await assert.rejects(fs.access(path.join(f.stateRoot,'prepared-installation.json')),{code:'ENOENT'});
});
