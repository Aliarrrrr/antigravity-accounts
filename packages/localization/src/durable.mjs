import fs from 'node:fs/promises';
import path from 'node:path';
import crypto from 'node:crypto';
import {execFile,spawn} from 'node:child_process';
import {promisify} from 'node:util';
import {TOOL_ROOT,inspectInstallation,archiveEntries,buildInstallation,applyArtifact,sha256,localizationFingerprint,prepareLocalizationRefresh,refreshLocalizedArtifact} from './toolkit.mjs';
import {extractFile,getRawHeader} from '@electron/asar';
import {acquireProcessLock as acquireLock} from './process-lock.mjs';
import {accountRecoveryFiles,ensureInstalledAccounts,acquireAccountsLaunchLock,inspectAccountsNativeFallback} from './accounts-recovery.mjs';
import {confirmNativeFallback} from './native-fallback.mjs';
const run=promisify(execFile);
const pause=ms=>new Promise(resolve=>setTimeout(resolve,ms));
export const DEFAULT_STATE_ROOT=path.join(TOOL_ROOT,'state');
const readJson=async file=>JSON.parse(await fs.readFile(file,'utf8'));
const sameHashes=(a,b)=>a&&b&&Object.keys(a).length===Object.keys(b).length&&Object.keys(a).every(key=>a[key]===b[key]);

export async function maintenanceProcesses(installRoot) {
  const {stdout}=await run('powershell.exe',['-NoProfile','-File',path.join(TOOL_ROOT,'scripts','Maintenance-State.ps1'),'-InstallRoot',installRoot],{windowsHide:true,maxBuffer:1024*1024});
  return JSON.parse(stdout||'[]');
}
export async function installationStamp(installRoot,options={}) {
  const files=['Antigravity.exe','resources/app.asar','resources/bin/language_server.exe','resources/antigravity-zh-cn.install.json',
    ...await accountRecoveryFiles(installRoot,options),
    ...['dictionaries/zh-CN.json','src/translator.mjs','src/native-patches.mjs','src/native-compatibility.mjs','src/native-scope-signatures.json','src/native-fallback.mjs'].map(name=>path.join(TOOL_ROOT,name))];
  return JSON.stringify(await Promise.all(files.map(async name=>{
    try { const s=await fs.stat(path.isAbsolute(name)?name:path.join(installRoot,name)); return [name,s.size,s.mtimeMs,s.birthtimeMs]; }
    catch(e) { if(e.code==='ENOENT') return [name,null]; throw e; }
  })));
}

async function publishStatus(stateRoot,result) {
  const status={...result,checkedUtc:new Date().toISOString()};
  const file=path.join(stateRoot,'status.json'), stage=file+'.'+crypto.randomUUID();
  await fs.writeFile(stage,JSON.stringify(status,null,2)+'\n');
  try { await fs.rename(stage,file); } finally { await fs.rm(stage,{force:true}); }
  const eventKey=JSON.stringify([result.status,result.appVersion,result.sourceSha256,result.patchedSha256,result.accounts,result.reason,result.error]);
  let previous;
  try { previous=await fs.readFile(path.join(stateRoot,'last-event'),'utf8'); } catch(e) { if(e.code!=='ENOENT') throw e; }
  if(eventKey!==previous) {
    await fs.appendFile(path.join(stateRoot,'events.jsonl'),JSON.stringify(status)+'\n');
    await fs.writeFile(path.join(stateRoot,'last-event'),eventKey);
  }
  return result;
}

export async function ensureLocalized(directory,{stateRoot=DEFAULT_STATE_ROOT,processProbe=maintenanceProcesses,settleMs=1500,accountsStateRoot}={}) {
  const installRoot=await fs.realpath(path.resolve(directory));
  stateRoot=path.resolve(stateRoot);
  const relative=path.relative(installRoot,stateRoot);
  if(!relative || (!relative.startsWith('..'+path.sep) && relative!=='..' && !path.isAbsolute(relative))) throw new Error('state_must_be_outside_installation');
  await fs.mkdir(stateRoot,{recursive:true});
  stateRoot=await fs.realpath(stateRoot);
  const resolvedRelative=path.relative(installRoot,stateRoot);
  if(!resolvedRelative || (!resolvedRelative.startsWith('..'+path.sep) && resolvedRelative!=='..' && !path.isAbsolute(resolvedRelative))) throw new Error('state_must_be_outside_installation');
  const release=await acquireLock(stateRoot,'repair.lock');
  if(!release) return {status:'busy'};
  try {
    const settings={stateRoot,processProbe,settleMs,accountsStateRoot};
    const accounts=await ensureInstalledAccounts(installRoot,{...settings,
      relocalize:()=>ensureLocalizedFiles(installRoot,settings,release)});
    if(['pending_idle','pending_update'].includes(accounts.status))
      return await publishStatus(stateRoot,{status:accounts.status,reason:'accounts_update'});
    if(accounts.status==='degraded' && accounts.mode==='native') {
      let localization,localizationError;
      try { localization=await ensureLocalizedFiles(installRoot,settings,release); }
      catch(error) { localizationError=error.message; }
      const program=await inspectAccountsNativeFallback(installRoot,settings);
      return await publishStatus(stateRoot,{status:'degraded',appVersion:program.appVersion,accounts:'unavailable',
        mode:'native',reason:accounts.reason,program,credentialFilesRead:false,
        localization:localizationError?'unavailable':localization?.status,
        ...(localizationError?{error:localizationError}:{})});
    }
    if(accounts.localizationError) return await publishStatus(stateRoot,{status:'degraded',accounts:'ready',
      localization:'unavailable',error:accounts.localizationError});
    const result=await ensureLocalizedFiles(installRoot,settings,release);
    return accounts.status==='repaired' && result.status==='ready'
      ? await publishStatus(stateRoot,{...result,status:'repaired',accounts:'repaired'}) : result;
  } catch(e) {
    if(!e.accountsRecoveryRequired && /\b(?:application_running|maintenance_busy|installation_changed|operation_locked|localization_sources_changed)\b/.test(e.message)) return await publishStatus(stateRoot,{status:'pending_update'});
    await publishStatus(stateRoot,{status:'error',error:e.message});
    throw e;
  } finally { await release(); }
}

async function ensureLocalizedFiles(installRoot,{stateRoot,processProbe,settleMs,accountsStateRoot},release) {
    const stamp=await installationStamp(installRoot,{accountsStateRoot,stateRoot});
    const info=await inspectInstallation(installRoot);
    const recordPath=path.join(installRoot,'resources','antigravity-zh-cn.install.json');
    let record;
    try {
      if((await fs.lstat(recordPath)).isSymbolicLink()) throw new Error('symlink_record_refused');
      record=await readJson(recordPath);
    } catch(e) { if(e.code!=='ENOENT') throw e; }
    if(info.isLocalized) {
      if(!record || record.schemaVersion!==1 || record.installRoot!==installRoot || record.appVersion!==info.appVersion || record.patchedSha256!==info.asarSha256 || record.executableSha256!==info.executableSha256 || !sameHashes(record.unpacked,info.unpacked)) throw new Error('unverified_localized_installation');
      if(!/^[A-F0-9]{64}$/.test(record.sourceSha256)) throw new Error('invalid_source_hash');
      let backupValid=false,verifiedBackupRoot;
      for(const backupRoot of [path.join(stateRoot,'backups'),path.join(TOOL_ROOT,'backups')]) {
        const backupDir=path.join(backupRoot,info.appVersion,record.sourceSha256);
        try {
          const backup=await readJson(path.join(backupDir,'manifest.json'));
          if(backup.schemaVersion!==1 || backup.appVersion!==info.appVersion || backup.sourceSha256!==record.sourceSha256 || backup.executableSha256!==record.executableSha256 || !sameHashes(backup.unpacked,record.unpacked)) throw new Error('backup_identity_mismatch');
          if(sha256(await fs.readFile(path.join(backupDir,'app.asar')))!==record.sourceSha256) throw new Error('backup_hash_mismatch');
          backupValid=true; verifiedBackupRoot=backupRoot; break;
        } catch(e) { if(e.code!=='ENOENT') throw e; }
      }
      if(!backupValid) throw new Error('original_backup_missing');
      if(record.localizationFingerprint!==await localizationFingerprint()) {
        if((await processProbe(installRoot)).length) return await publishStatus(stateRoot,{status:'pending_idle',appVersion:info.appVersion,sourceSha256:record.sourceSha256,reason:'localization_update'});
        if(settleMs) await pause(settleMs);
        if(stamp!==await installationStamp(installRoot,{accountsStateRoot,stateRoot})) return await publishStatus(stateRoot,{status:'pending_update',appVersion:info.appVersion});
        const artifact=await prepareLocalizationRefresh(installRoot,{outputRoot:path.join(stateRoot,'artifacts'),backupRoot:verifiedBackupRoot});
        if((await processProbe(installRoot)).length || stamp!==await installationStamp(installRoot,{accountsStateRoot,stateRoot})) return await publishStatus(stateRoot,{status:'pending_update',appVersion:info.appVersion});
        release.assertHeld?.();
        const updated=await refreshLocalizedArtifact(installRoot,artifact.artifactDir,{backupRoot:verifiedBackupRoot});
        return await publishStatus(stateRoot,{status:'repaired',appVersion:info.appVersion,sourceSha256:record.sourceSha256,patchedSha256:updated.patchedSha256,artifactDir:artifact.artifactDir,refreshed:true});
      }
      return await publishStatus(stateRoot,{status:'ready',appVersion:info.appVersion,sourceSha256:record.sourceSha256,patchedSha256:info.asarSha256});
    }
    if(!info.sourceKnown && !info.compatibleNativeSource) throw new Error('incompatible_native_source: '+info.appVersion);
    if((await processProbe(installRoot)).length) return await publishStatus(stateRoot,{status:'pending_idle',appVersion:info.appVersion,sourceSha256:info.asarSha256});
    if(settleMs) await pause(settleMs);
    if(stamp!==await installationStamp(installRoot,{accountsStateRoot,stateRoot})) return await publishStatus(stateRoot,{status:'pending_update',appVersion:info.appVersion});
    release.assertHeld?.();
    const artifact=await buildInstallation(installRoot,{outputRoot:path.join(stateRoot,'artifacts'),compatibleSource:true});
    if((await processProbe(installRoot)).length || stamp!==await installationStamp(installRoot,{accountsStateRoot,stateRoot})) return await publishStatus(stateRoot,{status:'pending_update',appVersion:info.appVersion});
    // Updaters may leave our old sidecar behind. Preserve it only after the new original
    // and its complete candidate have passed compatibility and identity validation.
    if(record) {
      const archiveDir=path.join(stateRoot,'superseded-records');
      await fs.mkdir(archiveDir,{recursive:true});
      // Installation and manager state may live on different Windows drives.
      // Keep the exact old record before removing its obsolete installed copy.
      const archived=path.join(archiveDir,Date.now()+'-'+crypto.randomUUID()+'.json');
      const bytes=await fs.readFile(recordPath);
      await fs.writeFile(archived,bytes,{flag:'wx'});
      if(!(await fs.readFile(archived)).equals(bytes) || !(await fs.readFile(recordPath)).equals(bytes))
        throw new Error('installation_changed');
      await fs.unlink(recordPath);
    }
    release.assertHeld?.();
    const applied=await applyArtifact(installRoot,artifact.artifactDir,{backupRoot:path.join(stateRoot,'backups'),compatibleSource:true});
    const verified=await inspectInstallation(installRoot);
    if(!verified.isLocalized || verified.asarSha256!==applied.patchedSha256) throw new Error('repair_verification_failed');
    return await publishStatus(stateRoot,{status:'repaired',appVersion:verified.appVersion,sourceSha256:applied.sourceSha256,patchedSha256:verified.asarSha256,artifactDir:artifact.artifactDir,backupDir:applied.backupDir});
}

async function requireLaunchable(info) {
  if(!/^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$/.test(info.appVersion)) throw new Error('invalid_application_version');
  const executable=await fs.lstat(info.executable);
  if(executable.isSymbolicLink() || !executable.isFile()) throw new Error('invalid_application_executable');
  const bytes=await fs.readFile(info.executable);
  const peOffset=bytes.length>=64?bytes.readUInt32LE(60):-1;
  if(bytes.length<1024 || bytes.toString('ascii',0,2)!=='MZ' || peOffset<64 || peOffset>bytes.length-264 || bytes.toString('binary',peOffset,peOffset+4)!=='PE\0\0' || bytes.readUInt16LE(peOffset+4)!==0x8664 || bytes.readUInt16LE(peOffset+24)!==0x20b) throw new Error('invalid_application_executable');
  const sections=bytes.readUInt16LE(peOffset+6),optionalSize=bytes.readUInt16LE(peOffset+20),table=peOffset+24+optionalSize;
  if(!sections || sections>96 || optionalSize<240 || table+sections*40>bytes.length) throw new Error('invalid_application_executable');
  for(let index=0;index<sections;index++) {
    const entry=table+index*40;
    if(bytes.readUInt32LE(entry+20)+bytes.readUInt32LE(entry+16)>bytes.length) throw new Error('incomplete_application_executable');
  }
  if(bytes.readUInt32LE(peOffset+24+144)+bytes.readUInt32LE(peOffset+24+148)>bytes.length) throw new Error('incomplete_application_executable');
  const entries=archiveEntries(info.asarPath),{headerSize}=getRawHeader(info.asarPath);
  const archiveSize=(await fs.stat(info.asarPath)).size,dataStart=8+headerSize;
  for(const [name,entry] of entries) {
    if(name.split('/').some(part=>!part || part==='.' || part==='..' || /[\\:\0]/.test(part))) throw new Error('invalid_application_archive');
    if(entry.link) continue;
    if(!Number.isSafeInteger(entry.size) || entry.size<0) throw new Error('invalid_application_archive');
    if(!entry.unpacked) {
      const offset=Number(entry.offset);
      if(typeof entry.offset!=='string' || !/^\d+$/.test(entry.offset) || !Number.isSafeInteger(offset) || dataStart+offset+entry.size>archiveSize) throw new Error('incomplete_application_archive');
    }
    const content=extractFile(info.asarPath,path.join(...name.split('/')));
    if(content.length!==entry.size) throw new Error('incomplete_application_archive');
    if(entry.integrity && (entry.integrity.algorithm!=='SHA256' || !/^[A-Fa-f0-9]{64}$/.test(entry.integrity.hash??'') || sha256(content)!==entry.integrity.hash.toUpperCase())) throw new Error('application_archive_integrity_mismatch');
  }
  const main=entries.get('dist/main.js');
  if(!main || main.link || !extractFile(info.asarPath,path.join('dist','main.js')).length) throw new Error('application_main_missing');
  if(info.isLocalized) {
    const recordPath=path.join(info.installRoot,'resources','antigravity-zh-cn.install.json');
    let record;
    try {
      if((await fs.lstat(recordPath)).isSymbolicLink()) throw new Error('symlink_record_refused');
      record=await readJson(recordPath);
    } catch(error) { if(error.code!=='ENOENT') throw error; }
    if(!record || record.schemaVersion!==1 || record.installRoot!==info.installRoot || record.appVersion!==info.appVersion || record.patchedSha256!==info.asarSha256 || record.executableSha256!==info.executableSha256 || !sameHashes(record.unpacked,info.unpacked)) throw new Error('unverified_localized_installation');
  }
}

export async function launchLocalized(directory,options={},args=[]) {
  const refused=error=>Object.assign(typeof error==='string'?new Error(error):error,{launchBlocked:true});
  const validate=async action=>{try{return await action();}catch(error){throw refused(error);}};
  if(!Array.isArray(args) || args.some(a=>typeof a!=='string'||a.includes('\0'))) throw refused('invalid_launch_arguments');
  if(options.preparedResult && !['ready','repaired','degraded'].includes(options.preparedResult.status)) throw refused('invalid_prepared_launch_result');
  let result,localizationError;
  try {
    if(options.preparedResult) {
      result=options.preparedResult;
    } else {
    for(let retry=0;retry<60;retry++) {
      result=await ensureLocalized(directory,options);
      if(result.status!=='busy' && result.status!=='pending_update') break;
      await pause(1000);
    }
    }
  } catch(error) { localizationError=error; }
  if(localizationError?.accountsRecoveryRequired) throw refused(localizationError);
  if(result?.reason==='accounts_update') throw refused('accounts_repair_'+result.status);
  // A localization failure must not turn an intact updated application into a
  // failed launch. Revalidate the current files independently, never an older
  // backup or a partially built candidate, while excluding concurrent repair.
  const installRoot=await validate(()=>fs.realpath(path.resolve(directory)));
  let stateRoot=path.resolve(options.stateRoot??DEFAULT_STATE_ROOT);
  const relative=path.relative(installRoot,stateRoot);
  if(!relative || (!relative.startsWith('..'+path.sep) && relative!=='..' && !path.isAbsolute(relative))) throw refused('state_must_be_outside_installation');
  await fs.mkdir(stateRoot,{recursive:true});
  stateRoot=await fs.realpath(stateRoot);
  const resolvedRelative=path.relative(installRoot,stateRoot);
  if(!resolvedRelative || (!resolvedRelative.startsWith('..'+path.sep) && resolvedRelative!=='..' && !path.isAbsolute(resolvedRelative))) throw refused('state_must_be_outside_installation');
  const release=await acquireLock(stateRoot,'repair.lock');
  if(!release) throw refused('localization_busy');
  let releaseAccounts,releaseOperation;
  try {
    const nativeMode=result?.status==='degraded' && result.mode==='native';
    releaseAccounts=await validate(()=>acquireAccountsLaunchLock(installRoot,{...options,stateRoot,
      ...(nativeMode?{nativeFallback:result}:{})}));
    releaseOperation=await acquireLock(path.join(installRoot,'resources'),'.antigravity-zh-cn.lock');
    if(!releaseOperation) throw refused('operation_locked');
    const processProbe=options.processProbe??maintenanceProcesses;
    const requireNoUpdater=async()=>{
      if((await processProbe(installRoot)).some(process=>process.kind!=='application')) throw refused('maintenance_busy');
    };
    await requireNoUpdater();
    const stamp=await validate(()=>installationStamp(installRoot,{...options,stateRoot})),info=await validate(()=>inspectInstallation(installRoot));
    await validate(()=>requireLaunchable(info));
    if(nativeMode) {
      const confirm=options.confirmNativeMode??confirmNativeFallback;
      if(!await validate(()=>confirm(result))) return {...result,launched:false,cancelled:true};
    }
    const settleMs=options.settleMs??1500;
    if(settleMs) await pause(settleMs);
    await requireNoUpdater();
    if(stamp!==await validate(()=>installationStamp(installRoot,{...options,stateRoot}))) throw refused('installation_changed');
    if(nativeMode) {
      const program=await validate(()=>inspectAccountsNativeFallback(installRoot,{...options,stateRoot}));
      if(JSON.stringify(program)!==JSON.stringify(result.program)) throw refused('installation_changed');
    }
    await validate(()=>{release.assertHeld?.(); releaseAccounts?.assertHeld?.(); releaseOperation.assertHeld?.();});
    const refreshPending=result?.status==='pending_idle' && result.reason==='localization_update';
    if(!nativeMode && (localizationError || (!['ready','repaired'].includes(result?.status) && !refreshPending))) {
      result={status:'degraded',appVersion:info.appVersion,localization:info.isLocalized?'stale':'unavailable',
        ...(info.isLocalized?{patchedSha256:info.asarSha256}:{sourceSha256:info.asarSha256}),
        error:localizationError?.message??'localization_'+result.status};
    }
    const child=await validate(async()=>{
      const child=spawn(info.executable,args,{cwd:installRoot,detached:true,stdio:'ignore',windowsHide:false});
      await new Promise((resolve,reject)=>{child.once('spawn',resolve);child.once('error',reject);});
      return child;
    });
    child.unref();
    const launched={...result,launched:true};
    if(result.status==='degraded') {
      // Status storage is optional once the native application has started.
      // A write failure here must not report its successful launch as failed.
      try { await publishStatus(stateRoot,launched); } catch {}
    }
    return launched;
  } finally {
    try {await releaseOperation?.();}
    finally {try {await releaseAccounts?.();} finally {await release();}}
  }
}

export async function watchInstallation(installRoot,{stateRoot=DEFAULT_STATE_ROOT,intervalMs=5000,onInstallationChanged,...options}={}) {
  stateRoot=path.resolve(stateRoot);
  const release=await acquireLock(stateRoot,'watcher.lock');
  if(!release) return {status:'watcher_already_running'};
  try {
    let previousStamp,lastStatus;
    while(true) {
      release.assertHeld?.();
      try { await fs.access(path.join(stateRoot,'windows-persistence','disabled')); break; }
      catch(e) { if(e.code!=='ENOENT') throw e; }
      try {
        const stamp=await installationStamp(installRoot,{...options,stateRoot});
        if(stamp!==previousStamp && onInstallationChanged) await onInstallationChanged();
        try { await fs.access(path.join(stateRoot,'windows-persistence','disabled')); break; }
        catch(e) { if(e.code!=='ENOENT') throw e; }
        const unchangedPending=stamp===previousStamp && ['pending_idle','pending_update'].includes(lastStatus);
        const stillBusy=unchangedPending && (await (options.processProbe??maintenanceProcesses)(installRoot)).length>0;
        if(!stillBusy && (stamp!==previousStamp || !['ready','repaired','error','degraded'].includes(lastStatus))) {
          const result=await ensureLocalized(installRoot,{stateRoot,...options});
          // NSIS can recreate shortcuts after an earlier check during installation.
          // Reconcile again after the original package has been safely repaired.
          if(result.status==='repaired' && onInstallationChanged) await onInstallationChanged();
          lastStatus=result.status;
          previousStamp=await installationStamp(installRoot,{...options,stateRoot});
        }
      } catch(e) {
        let alreadyPublished=false;
        try {const status=await readJson(path.join(stateRoot,'status.json'));alreadyPublished=status.status==='error'&&status.error===e.message;}catch{}
        if(!alreadyPublished) await publishStatus(stateRoot,{status:'error',error:e.message});
        lastStatus='error';
        // A permanent compatibility failure is retried only when its files change.
        // Missing files during an installer retain no stamp and retry on the next tick.
        try { previousStamp=await installationStamp(installRoot,{...options,stateRoot}); }
        catch { previousStamp=undefined; }
      }
      await pause(intervalMs);
    }
    return {status:'watcher_disabled'};
  } finally { await release(); }
}
