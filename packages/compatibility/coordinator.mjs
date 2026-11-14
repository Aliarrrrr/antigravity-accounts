import fs from 'node:fs/promises';
import path from 'node:path';
import {pathToFileURL} from 'node:url';
import {createHash,randomUUID} from 'node:crypto';
import {validateBundle} from './manifest.mjs';
import {prepareTransaction,applyTransaction,recoverTransactions} from './transaction.mjs';

const sha=bytes=>createHash('sha256').update(bytes).digest('hex');
const same=(a,b)=>path.resolve(a).toLowerCase()===path.resolve(b).toLowerCase();
const within=(file,root)=>{const r=path.relative(root,file);return r!==''&&r!=='..'&&!r.startsWith('..'+path.sep)&&!path.isAbsolute(r);};
const fail=code=>{throw Object.assign(new Error(code),{code});};
async function json(file){try{const s=await fs.lstat(file);if(!s.isFile()||s.isSymbolicLink())fail('compatibility_metadata_invalid');return JSON.parse(await fs.readFile(file,'utf8'));}catch(e){if(e.code==='ENOENT')return;throw e;}}
async function save(file,value){await fs.mkdir(path.dirname(file),{recursive:true});const stage=file+'.'+randomUUID();await fs.writeFile(stage,JSON.stringify(value,null,2)+'\n',{flag:'wx'});try{await fs.rename(stage,file);}finally{await fs.rm(stage,{force:true});}}
async function immutable(file,bytes){await fs.mkdir(path.dirname(file),{recursive:true});try{await fs.writeFile(file,bytes,{flag:'wx'});}catch(e){if(e.code!=='EEXIST'||!Buffer.from(bytes).equals(await fs.readFile(file)))throw e;}}
async function stamp(config){const files=[path.join(config.installRoot,'Antigravity.exe'),path.join(config.installRoot,'resources/app.asar'),path.join(config.installRoot,'resources/bin/language_server.exe'),path.join(config.installRoot,'resources/antigravity-zh-cn.install.json'),path.join(config.accountsStateRoot,'adapter-installation.json')];return JSON.stringify(await Promise.all(files.map(async file=>{try{const s=await fs.stat(file);return[file,s.size,s.mtimeMs];}catch(e){if(e.code==='ENOENT')return[file,null];throw e;}})));}
const compatibilityError=e=>/^unsupported_native_|^Unsupported |^incompatible_native_source|source contract|native_anchor|unreviewed_native/.test(e.message||'');
const component=(installationState,extra={})=>({installationState,runtimeState:'pending',...extra});
function enabledComponents(value){
  if(!value||typeof value!=='object'||Array.isArray(value)||Object.keys(value).some(key=>!['accounts','localization'].includes(key))||['accounts','localization'].some(key=>typeof value[key]!=='boolean'))fail('compatibility_components_invalid');
  return{accounts:value.accounts,localization:value.localization};
}
const sameComponents=(left,right)=>left?.accounts===right.accounts&&left?.localization===right.localization;

async function context(options){
  const config={...options};
  for(const key of ['installRoot','stateRoot','accountsStateRoot','localizationStateRoot','bundleRoot'])if(!path.isAbsolute(config[key]||''))fail('compatibility_paths_invalid');
  config.installRoot=await fs.realpath(config.installRoot);
  for(const key of ['stateRoot','localizationStateRoot']){await fs.mkdir(config[key],{recursive:true});config[key]=await fs.realpath(config[key]);if(same(config[key],config.installRoot)||within(config[key],config.installRoot))fail('compatibility_state_inside_installation');}
  if(config.enabledComponents!==undefined)config.enabledComponents=enabledComponents(config.enabledComponents);
  const manifest=await (options.validateBundle||validateBundle)(config.bundleRoot);
  const accountsRoot=path.join(config.bundleRoot,'accounts'),localizationRoot=path.join(config.bundleRoot,'localization');
  const toolkit=await import(pathToFileURL(path.join(localizationRoot,'src/toolkit.mjs')).href);
  const manage=await import(pathToFileURL(path.join(accountsRoot,'account-plugin/manage-install.mjs')).href);
  // Account metadata may be redirected independently from its parent directory
  // under MSIX. Use the installer's anchored public-file resolution rather
  // than silently selecting a different ambient account store.
  config.accountsStateRoot=await manage.resolveAccountsStateRoot({installRoot:config.installRoot,stateRoot:config.accountsStateRoot,create:true});
  if(!path.isAbsolute(config.accountsStateRoot||'')||same(config.accountsStateRoot,config.installRoot)||within(config.accountsStateRoot,config.installRoot))fail('compatibility_state_inside_installation');
  const accountBuild=await import(pathToFileURL(path.join(accountsRoot,'hostauth-adapter/src/package-copy.mjs')).href);
  const processGuard=await import(pathToFileURL(path.join(accountsRoot,'hostauth-adapter/src/process-guard.mjs')).href);
  const locks=await import(pathToFileURL(path.join(localizationRoot,'src/process-lock.mjs')).href);
  const checkStopped=options.checkStopped||(()=>processGuard.assertApplicationStopped({installRoot:config.installRoot}));
  return {config,manifest,toolkit,manage,accountBuild,locks,checkStopped};
}

async function installation(ctx){
  const{config,toolkit}=ctx,info=await toolkit.inspectInstallation(config.installRoot),resources=path.join(config.installRoot,'resources');
  const receipt=await json(path.join(config.accountsStateRoot,'adapter-installation.json'));
  const localization=await json(path.join(resources,'antigravity-zh-cn.install.json'));
  const persistence=await json(path.join(config.localizationStateRoot,'windows-persistence/install.json'));
  const enabled=enabledComponents(config.enabledComponents||{accounts:receipt?.phase==='applied',localization:Boolean(localization||persistence?.installed)});
  return {info,receipt,localization,enabled};
}

export async function inspectInstallation(options){
  const ctx=await context(options),current=await installation(ctx),last=await json(path.join(ctx.config.stateRoot,'applied-state.json'));
  const currentStamp=await stamp(ctx.config),matches=last?.stamp===currentStamp&&last?.bundleVersion===ctx.manifest.version&&sameComponents(last.enabledComponents,current.enabled);
  return {schemaVersion:1,hostVersion:current.info.appVersion,bundleVersion:ctx.manifest.version,enabledComponents:current.enabled,phase:matches?'ready':'inspection_required',installationState:matches?'ready':'pending',runtimeState:'pending',components:matches?last.components:{accounts:component(current.enabled.accounts?'pending':'disabled'),localization:component(current.enabled.localization?'pending':'disabled')},activationAllowed:false,credentialFilesRead:false};
}

async function originalPrograms(ctx,current){
  const{config,toolkit}=ctx,{info,receipt,localization}=current;
  let asar=await fs.readFile(info.asarPath),native=await fs.readFile(path.join(config.installRoot,'resources/bin/language_server.exe'));
  if(localization?.patchedSha256?.toLowerCase()===sha(asar)){
    if(localization.appVersion!==info.appVersion||!same(localization.installRoot,config.installRoot)||localization.executableSha256?.toLowerCase()!==info.executableSha256.toLowerCase())fail('compatibility_localization_identity_changed');
    const filename=path.join(config.localizationStateRoot,'backups',info.appVersion,localization.sourceSha256,'app.asar');asar=await fs.readFile(filename);if(sha(asar)!==localization.sourceSha256.toLowerCase())fail('compatibility_original_changed');
  }else if(info.isLocalized)fail('compatibility_unverified_localization');
  if(receipt?.phase==='applied'&&receipt.patchedAsarSha256===sha(asar)){
    if(receipt.patchedNativeSha256!==sha(native)||!same(receipt.resourcesRoot,path.join(config.installRoot,'resources')))fail('compatibility_mixed_account_programs');
    const resolved=await ctx.manage.resolveRecoveryReceiptPaths(receipt,{stateRoot:config.accountsStateRoot});
    asar=await fs.readFile(path.join(resolved.backupRoot,'app.asar'));native=await fs.readFile(path.join(resolved.backupRoot,'language_server.exe'));
    if(sha(asar)!==receipt.originalAsarSha256||sha(native)!==receipt.originalNativeSha256)fail('compatibility_original_changed');
  }
  const source=path.join(config.stateRoot,'preparations',randomUUID(),'source');await fs.mkdir(path.join(source,'resources/bin'),{recursive:true});
  await fs.writeFile(path.join(source,'resources/app.asar'),asar);await fs.writeFile(path.join(source,'resources/bin/language_server.exe'),native);
  await fs.copyFile(path.join(config.installRoot,'Antigravity.exe'),path.join(source,'Antigravity.exe'));
  await fs.cp(path.join(config.installRoot,'resources/app.asar.unpacked'),path.join(source,'resources/app.asar.unpacked'),{recursive:true,errorOnExist:true,force:false});
  const original=await toolkit.inspectInstallation(source);if(original.appVersion!==info.appVersion||original.executableSha256!==info.executableSha256||JSON.stringify(original.unpacked)!==JSON.stringify(info.unpacked))fail('compatibility_original_host_mismatch');
  return {source,asar,native};
}

async function ownedAddonFiles(root){
  const allowed=new Set(['config.json','component/Antigravity.Accounts.exe','native/language_server.exe']),result=[];
  async function walk(directory,prefix=''){let entries;try{entries=await fs.readdir(directory,{withFileTypes:true});}catch(e){if(e.code==='ENOENT'&&!prefix)return;throw e;}
    for(const entry of entries){const relative=prefix+entry.name;if(entry.isSymbolicLink())fail('compatibility_payload_invalid');if(entry.isDirectory()){if(!['component','native'].includes(relative))fail('compatibility_payload_not_owned');await walk(path.join(directory,entry.name),relative+'/');}else if(entry.isFile()&&allowed.has(relative))result.push(relative);else fail('compatibility_payload_not_owned');}}
  await walk(root);return result;
}

export async function prepareInstallation(options){
  const ctx=await context(options),{config,manifest,toolkit}=ctx,startStamp=await stamp(config),current=await installation(ctx);
  const original=await originalPrograms(ctx,current),id=randomUUID(),components={accounts:component('disabled',current.receipt?.phase==='applied'&&!current.enabled.accounts?{identityConfirmationRequired:true}:{}),localization:component('disabled')};
  let accountPrepared,accountManifest,localized,finalAsar=original.asar,finalNative=original.native;
  if(current.enabled.accounts){
    accountPrepared=path.join(config.accountsStateRoot,'prepared-'+id);
    try{accountManifest=await ctx.accountBuild.buildCopy({sourceAsar:path.join(original.source,'resources/app.asar'),outputRoot:accountPrepared,componentExecutable:path.join(config.bundleRoot,'accounts/component/Antigravity.Accounts.exe'),uiDirectory:path.join(config.bundleRoot,'accounts/account-plugin'),languageServerPath:path.join(original.source,'resources/bin/language_server.exe'),accountsStateRoot:config.accountsStateRoot});finalAsar=await fs.readFile(path.join(accountPrepared,'resources/app.asar'));finalNative=await fs.readFile(path.join(accountPrepared,'resources/bin/language_server.exe'));components.accounts=component('prepared');}
    catch(e){if(!compatibilityError(e))throw e;accountPrepared=undefined;components.accounts=component('unsupported',{stage:'accounts_prepare',reason:e.message,identityConfirmationRequired:true});}
  }
  if(current.enabled.localization){
    await fs.writeFile(path.join(original.source,'resources/app.asar'),finalAsar);
    try{localized=await toolkit.prepareLocalization({sourceInstallRoot:original.source,targetInstallRoot:config.installRoot,outputRoot:path.join(config.stateRoot,'localization-artifacts'),compatibleSource:true});finalAsar=await fs.readFile(path.join(localized.artifactDir,'app.asar'));components.localization=component('prepared');}
    catch(e){if(!compatibilityError(e))throw e;components.localization=component('unsupported',{stage:'localization_prepare',reason:e.message});}
  }
  if(startStamp!==await stamp(config))fail('compatibility_host_changed_during_prepare');
  const changes=[{root:'install',path:'resources/app.asar',bytes:finalAsar},{root:'install',path:'resources/bin/language_server.exe',bytes:finalNative}];
  const addonFiles=await ownedAddonFiles(path.join(config.installRoot,'resources/hostauth-addon'));
  for(const relative of new Set([...addonFiles,...(accountPrepared?['config.json','component/Antigravity.Accounts.exe','native/language_server.exe']:[])]))changes.push({root:'install',path:'resources/hostauth-addon/'+relative,bytes:accountPrepared?await fs.readFile(path.join(accountPrepared,'resources/hostauth-addon',relative)):null});
  if(accountPrepared){
    const backupRoot=path.join(config.accountsStateRoot,'restore-'+id);await immutable(path.join(backupRoot,'app.asar'),original.asar);await immutable(path.join(backupRoot,'language_server.exe'),original.native);await immutable(path.join(backupRoot,'manifest.json'),JSON.stringify(accountManifest,null,2)+'\n');
    if(current.receipt)await immutable(path.join(config.accountsStateRoot,'adapter-installation-before-update-'+id+'.json'),JSON.stringify(current.receipt,null,2)+'\n');
    const cachedPackage=await ctx.manage.cacheRecoveryPackage({stateRoot:config.accountsStateRoot,suffix:id,packageRoot:path.join(config.bundleRoot,'accounts'),preparedRoot:accountPrepared});
    const receipt={schemaVersion:1,phase:'applied',resourcesRoot:path.join(config.installRoot,'resources'),backupRoot,preparedRoot:accountPrepared,packageRoot:cachedPackage,distributionRoot:path.join(config.bundleRoot,'accounts'),originalAsarSha256:accountManifest.originalAsarSha256,patchedAsarSha256:accountManifest.patchedAsarSha256,originalNativeSha256:accountManifest.originalNativeSha256,patchedNativeSha256:accountManifest.patchedNativeSha256};
    changes.push({root:'accounts',path:'adapter-installation.json',bytes:Buffer.from(JSON.stringify(receipt,null,2)+'\n')});
  }
  if(localized){
    const backupRoot=path.join(config.localizationStateRoot,'backups',current.info.appVersion,localized.manifest.sourceSha256);
    await immutable(path.join(backupRoot,'app.asar'),accountPrepared?await fs.readFile(path.join(accountPrepared,'resources/app.asar')):original.asar);
    const previous=await json(path.join(backupRoot,'manifest.json'));
    if(previous){if(previous.appVersion!==localized.manifest.appVersion||previous.sourceSha256!==localized.manifest.sourceSha256||previous.executableSha256!==localized.manifest.executableSha256||JSON.stringify(previous.unpacked)!==JSON.stringify(localized.manifest.unpacked))fail('compatibility_localization_backup_conflict');}
    else await immutable(path.join(backupRoot,'manifest.json'),JSON.stringify(localized.manifest,null,2)+'\n');
    const record=toolkit.localizationInstallRecord(localized.manifest,{artifactDir:localized.artifactDir,appliedUtc:new Date().toISOString()});
    changes.push({root:'install',path:'resources/antigravity-zh-cn.install.json',bytes:Buffer.from(JSON.stringify(record,null,2)+'\n')});
  }else changes.push({root:'install',path:'resources/antigravity-zh-cn.install.json',bytes:null});
  const guards=[{file:path.join(config.installRoot,'Antigravity.exe')},...Object.keys(current.info.unpacked).map(name=>({file:path.join(config.installRoot,'resources/app.asar.unpacked',...name.split('/'))}))];
  const metadata={bundleVersion:manifest.version,hostVersion:current.info.appVersion,sourceStamp:startStamp,enabledComponents:current.enabled,components,original:{asar:sha(original.asar),native:sha(original.native)}};
  const transaction=await prepareTransaction({stateRoot:config.stateRoot,roots:{install:config.installRoot,accounts:config.accountsStateRoot,localization:config.localizationStateRoot},changes,guards,metadata});
  const prepared={schemaVersion:1,transactionRoot:transaction.transactionRoot,...metadata,installationState:'prepared',phase:'prepared',runtimeState:'pending',credentialFilesRead:false,activationAllowed:false};
  await save(path.join(config.stateRoot,'prepared-installation.json'),prepared);return prepared;
}

async function exclusive(ctx,action){
  const {config,locks}=ctx;
  await ctx.checkStopped();
  const releaseRepair=await locks.acquireProcessLock(config.localizationStateRoot,'repair.lock');if(!releaseRepair)return{phase:'pending_update',installationState:'pending_update',activationAllowed:false};
  try{return await ctx.manage.withInstallationLock(path.join(config.installRoot,'resources'),async()=>{
    const releaseOperation=await locks.acquireProcessLock(path.join(config.installRoot,'resources'),'.antigravity-zh-cn.lock');if(!releaseOperation)return{phase:'pending_update',installationState:'pending_update',activationAllowed:false};
    try{
      return await action();
    }finally{await releaseOperation();}
  });}finally{await releaseRepair();}
}

export async function applyInstallation(options){
  const ctx=await context(options),{config}=ctx;
  return exclusive(ctx,async()=>{
      await recoverTransactions(config.stateRoot,{checkStopped:ctx.checkStopped});
      const prepared=await json(path.join(config.stateRoot,'prepared-installation.json'));if(!prepared||prepared.bundleVersion!==ctx.manifest.version)fail('compatibility_preparation_missing');
      const current=await installation(ctx);
      if(!sameComponents(prepared.enabledComponents,current.enabled))fail('compatibility_components_changed_before_apply');
      if(prepared.sourceStamp!==await stamp(config))fail('compatibility_host_changed_before_apply');
      const journal=await applyTransaction(prepared.transactionRoot,{checkStopped:ctx.checkStopped});
      const components=Object.fromEntries(Object.entries(journal.metadata.components).map(([key,value])=>[key,{...value,installationState:value.installationState==='prepared'?'ready':value.installationState}]));
      const result={schemaVersion:1,bundleVersion:ctx.manifest.version,hostVersion:prepared.hostVersion,enabledComponents:current.enabled,phase:'ready',installationState:'ready',runtimeState:'pending',components,stamp:await stamp(config),credentialFilesRead:false,activationAllowed:true};
      await save(path.join(config.stateRoot,'applied-state.json'),result);return result;
  });
}

export async function ensureInstallation(options){
  const ctx=await context(options),{config}=ctx;
  let busy=false;try{await ctx.checkStopped();}catch(e){if(/Exit all Antigravity|application_running/.test(e.message))busy=true;else throw e;}
  const last=await json(path.join(config.stateRoot,'applied-state.json'));
  let current=await installation(ctx);
  if(last?.stamp===await stamp(config)&&last.bundleVersion===ctx.manifest.version&&sameComponents(last.enabledComponents,current.enabled))return{...last,activationAllowed:!busy};
  if(!busy){const recovered=await exclusive(ctx,()=>recoverTransactions(config.stateRoot,{checkStopped:ctx.checkStopped}));if(recovered?.activationAllowed===false)return recovered;}
  current=await installation(ctx);
  let prepared=await json(path.join(config.stateRoot,'prepared-installation.json'));
  if(!prepared||prepared.sourceStamp!==await stamp(config)||prepared.bundleVersion!==ctx.manifest.version||!sameComponents(prepared.enabledComponents,current.enabled))prepared=await prepareInstallation(options);
  if(busy)return{...prepared,phase:'pending_idle',installationState:'pending_idle',activationAllowed:false};
  return applyInstallation(options);
}
