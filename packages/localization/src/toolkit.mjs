import fs from 'node:fs/promises';
import path from 'node:path';
import crypto from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { getRawHeader, extractFile, extractAll, createPackageWithOptions, uncache } from '@electron/asar';
import {WHITELIST} from './native-patches.mjs';
import {acquireProcessLock} from './process-lock.mjs';

const run = promisify(execFile);
export const TOOL_ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
export const APP_VERSION = '2.19.1';
export const SOURCE_SHA256 = '341234FAF45BD1776FD5418A3C288DEDC5487EBFCF153F53D75DE17CFE15C1DE';
export const SOURCE_SHA256_BY_VERSION = Object.freeze({
  [APP_VERSION]: SOURCE_SHA256,
  '2.21.1': 'D075E5D9FF01F8806016AA88C39BF1429F068A941FDBE46147D8409843E62113'
});
// Reviewed 2.21.1 native sources, also byte-identical in the 2.22.0 original.
// This is a compatibility gate, not a claim that the ASAR has an official signature.
const COMPATIBLE_NATIVE_SHA256 = Object.freeze({
  'dist/preload.js':'81D220DE63447D27DB95ADF67D9711B59EE317B2338BA538F419B354A54B1F60',
  'dist/menu.js':'0BB689F3F77F5F30D9345302FF94EBABBE461C4B94DE9FFDB711A19EECA0F04F',
  'dist/tray.js':'6C6B0968B45A1C7088E459657D57A9EFD72D3733614349095F17B6EE94B47135',
  'dist/ipcHandlers.js':'0A58FE6EBA5B985702D5F81EC18E8EFD71A9AA017E250CC9446055D8040E9524',
  'dist/updater.js':'D8F44A0BB440C3F20CE0564BF46DE80DEFE709D7F088F00B738726C7B9120E1B',
  'dist/main.js':'ADF9F814CE9A7DF067D7C226354F5B5C160797ED6AEFC3E45C343D2F91AF52F8',
  'dist/wsl.js':'933315C492BBB6CA3C8038CDE91236C319FDE00E6525553111E1D5E099805674',
  'dist/provisionSplash.js':'737F40F02130440A9EC9A3FED83C2E62290029AECE6D0AD558136FA510C45F35',
  'dist/ideInstall/wizardPreload.js':'4D222BAC7A93AAFD5CB8CD59DFB1DE35BAFC35E267BA7B8EF24F74DB151068D5',
  'dist/ideInstall/wizardHtml.js':'5EFF5A777D5CFC26877EE1EA7889310C2AFE0AA66D596F87F9BDC57ED41264B9'
});
const COMPATIBLE_NATIVE_REVISIONS = Object.freeze([
  COMPATIBLE_NATIVE_SHA256,
  // Reviewed 2.25.0: only main.js changed, removing app.dock.hide() after all windows close.
  Object.freeze({...COMPATIBLE_NATIVE_SHA256,
    'dist/main.js':'AFADFBB9C0880F081DD763CAA2B9525B2590631AB8FD58CD82E2D67075DF1A42'
  }),
  // Reviewed installed 2.26.0, preserving its existing local hostauth integration.
  Object.freeze({...COMPATIBLE_NATIVE_SHA256,
    'dist/preload.js':'9F3A99CD4A85203DD1CE6F6F07B6463684C39173C91522E6765459361903082D',
    'dist/menu.js':'908318FECF7B99C1AE26DBF25EF9CBF05221EBEC04F20BCA81D059BAA337E186',
    'dist/ipcHandlers.js':'C9AF195D82ABA20300D48C763C002E277DB226336E0529F2D9D26E64C5D7D078',
    'dist/main.js':'7B09E030FD11D2083BB0F4333AD3B538D1EAB33F68D94404F820C4D7964CE777'
  }),
  // Reviewed 2.27.0: WebAuthn, webview new-tab callbacks and hub window selection.
  Object.freeze({...COMPATIBLE_NATIVE_SHA256,
    'dist/preload.js':'B8B61E3B2237C6F5FB4798B3C116A94DD0530500B304729FC1D1B192B567D46B',
    'dist/menu.js':'908318FECF7B99C1AE26DBF25EF9CBF05221EBEC04F20BCA81D059BAA337E186',
    'dist/ipcHandlers.js':'D66028B41ED56F66A56268E8C139039C0464F4EEE56ACA3B665476FC9E6D4F71',
    'dist/main.js':'D3D46E516198F32A16B4CF53787693F76C692F1696CB64AB328C4737539FE501'
  }),
  // Same reviewed 2.27 source with Accounts preview.4's host bridge hooks.
  // Localization must preserve the independent runtime and native programs.
  Object.freeze({...COMPATIBLE_NATIVE_SHA256,
    'dist/preload.js':'B8B61E3B2237C6F5FB4798B3C116A94DD0530500B304729FC1D1B192B567D46B',
    'dist/menu.js':'908318FECF7B99C1AE26DBF25EF9CBF05221EBEC04F20BCA81D059BAA337E186',
    'dist/ipcHandlers.js':'D66028B41ED56F66A56268E8C139039C0464F4EEE56ACA3B665476FC9E6D4F71',
    'dist/main.js':'A775F42ABA1AF7CDC1C733D929358A31F399D499F809853BD1947D85D40DF133'
  }),
  // Reviewed 2.28.0: script-jail isolation and webview security-state callbacks.
  // The translation anchors leave those additions and WebAuthn handling intact.
  Object.freeze({...COMPATIBLE_NATIVE_SHA256,
    'dist/preload.js':'1B702D07B8415AFDB41D27D62515FA88CE91D31DBC15E9DC6CC7AE80DDDB0C7B',
    'dist/menu.js':'908318FECF7B99C1AE26DBF25EF9CBF05221EBEC04F20BCA81D059BAA337E186',
    'dist/ipcHandlers.js':'3824070036FE6545BC18FC359D13457D25A7437BA0121A32E73036514DE3D74A',
    'dist/main.js':'66FA884C96DEE5DD6A536711D35FC90FEE853E9A2EEDE40FB4D1AB576E4EA50F'
  }),
  // Same reviewed 2.28 source with Accounts preview.5's host bridge hooks.
  Object.freeze({...COMPATIBLE_NATIVE_SHA256,
    'dist/preload.js':'1B702D07B8415AFDB41D27D62515FA88CE91D31DBC15E9DC6CC7AE80DDDB0C7B',
    'dist/menu.js':'908318FECF7B99C1AE26DBF25EF9CBF05221EBEC04F20BCA81D059BAA337E186',
    'dist/ipcHandlers.js':'3824070036FE6545BC18FC359D13457D25A7437BA0121A32E73036514DE3D74A',
    'dist/main.js':'E33D9359C3DC6BD1C0C78A0CA31B6EAA73389D017ED650D11EC76070B72EC7B9'
  }),
  // Reviewed 2.29.0 with Accounts preview.8. Of these ten scripts, only main.js
  // differs from the unmodified 2.29 source and it equals the Accounts builder's
  // exact result. The independent runtime, auth bridge and native copies remain
  // outside the localization changes.
  Object.freeze({...COMPATIBLE_NATIVE_SHA256,
    'dist/preload.js':'2F4348C24FA6FEEBA6FEF81644603304667B43A446B3887370781E90EC37CA2C',
    'dist/menu.js':'5F09CE9CEC448C6FFEB30D2C58DAF2D671A09EE03C7EBDAF784F1B61754225E8',
    'dist/ipcHandlers.js':'584770318464DA504DFBAA2ADD454EFF55E70418C8B0550145C55D2CB79818BB',
    'dist/updater.js':'EF38DA806D1DB759FF94EC8B1181F057440150CEDBAB7992B633DF8933951E04',
    'dist/main.js':'E1E0D0728C4BDBCDFF9659AD7F6D11A3398CD043696D067C63A9755942295891'
  }),
  // Same 2.29 contract with the per-updater-instance guarded relaunch hook.
  // The updater implementation is preserved; only the reviewed main hook is added.
  Object.freeze({...COMPATIBLE_NATIVE_SHA256,
    'dist/preload.js':'2F4348C24FA6FEEBA6FEF81644603304667B43A446B3887370781E90EC37CA2C',
    'dist/menu.js':'5F09CE9CEC448C6FFEB30D2C58DAF2D671A09EE03C7EBDAF784F1B61754225E8',
    'dist/ipcHandlers.js':'584770318464DA504DFBAA2ADD454EFF55E70418C8B0550145C55D2CB79818BB',
    'dist/updater.js':'EF38DA806D1DB759FF94EC8B1181F057440150CEDBAB7992B633DF8933951E04',
    'dist/main.js':'F7FDCAE0629FE6E6F1618967CA035CF45A97809BC45D7DBE3E9903A0872393ED'
  }),
  // Reviewed 2.31.0 with Accounts preview.12. The main hooks retain the
  // new browser-session shutdown and startup configuration. All other native
  // translation inputs equal this version's original files.
  Object.freeze({...COMPATIBLE_NATIVE_SHA256,
    'dist/preload.js':'8457DC1FE5FA34FF138E041923A66B852E0A6FB1623B9AEDCFB80F15DD98E9D7',
    'dist/menu.js':'34CE3FABE919E74EAE9034FA1EE32C3202F3C40B2F5FFA642BEE23A0958A5820',
    'dist/ipcHandlers.js':'E45B442A3E8A55E521EAD048BB858A1BBE8C282E1A5F9D7FAB9366B843A44EAD',
    'dist/updater.js':'EF38DA806D1DB759FF94EC8B1181F057440150CEDBAB7992B633DF8933951E04',
    'dist/main.js':'077B1A60FF8D3FED60826A4D332CD831CCBC898F2D53768635682956A5E3CEFB'
  })
]);
export const UPSTREAM_COMMIT = 'b541ec6654a068beb476fef07ed437786908fdb2';
export const sha256 = bytes => crypto.createHash('sha256').update(bytes).digest('hex').toUpperCase();
export async function localizationFingerprint() {
  const files=['dictionaries/zh-CN.json','src/translator.mjs','src/native-patches.mjs'];
  return sha256(Buffer.concat(await Promise.all(files.map(name=>fs.readFile(path.join(TOOL_ROOT,name))))));
}
const json = async p => JSON.parse(await fs.readFile(p, 'utf8'));
const saveJson = (p, v) => fs.writeFile(p, JSON.stringify(v, null, 2) + '\n', {flag:'wx'});
const fail = (code, detail = '') => { throw new Error(code + (detail ? ': ' + detail : '')); };
const archiveFile = (archive, name) => extractFile(archive, name.split('/').join(path.sep));
const validVersion = version => typeof version==='string' && /^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$/.test(version);
const validHash = hash => typeof hash==='string' && /^[A-F0-9]{64}$/.test(hash);
const sameUnpacked = (a,b) => a && b && Object.keys(a).length===Object.keys(b).length && Object.keys(a).every(name=>Object.hasOwn(b,name) && a[name]===b[name]);
async function compatibleNativeArchive(archive,entries) {
  if(!WHITELIST.every(name=>entries.has(name) && !entries.get(name).unpacked && !entries.get(name).link)) return false;
  const files=new Map(WHITELIST.map(name=>[name,archiveFile(archive,name)]));
  if(COMPATIBLE_NATIVE_REVISIONS.some(revision=>WHITELIST.every(name=>sha256(files.get(name))===revision[name]))) return true;
  const [rules,profiles,patches]=await Promise.all(['native-compatibility.mjs','native-scope-signatures.json','native-patches.mjs'].map(name=>fs.readFile(path.join(TOOL_ROOT,'src',name))));
  const revision=sha256(Buffer.concat([rules,profiles,patches]));
  const [{nativeCompatibility},{nativePatchOperations}]=await Promise.all([
    import('./native-compatibility.mjs?compatibility='+revision),
    import('./native-patches.mjs?compatibility='+revision)
  ]);
  return nativeCompatibility(files,{profiles:JSON.parse(profiles.toString('utf8')),operations:nativePatchOperations('/* native compatibility analysis */')}).compatible;
}

export function archiveEntries(archive) {
  uncache(archive);
  const entries = new Map();
  function walk(files, prefix = '') {
    for (const [name, item] of Object.entries(files)) {
      const key = prefix + name;
      if (item.files) walk(item.files, key + '/'); else entries.set(key, item);
    }
  }
  walk(getRawHeader(archive).header.files);
  return entries;
}

// @electron/asar intentionally drops POSIX executable flags when packing on Windows.
// Preserve those original header bits while keeping its freshly generated offsets/integrity.
async function preserveExecutableFlags(source,candidate) {
  const original=archiveEntries(source);
  const candidateBytes=await fs.readFile(candidate);
  const {header,headerSize}=getRawHeader(candidate);
  function walk(files,prefix='') {
    for (const [name,entry] of Object.entries(files)) {
      const key=prefix+name;
      if (entry.files) walk(entry.files,key+'/');
      else if (original.get(key)?.executable) entry.executable=true;
      else delete entry.executable;
    }
  }
  walk(header.files);
  const string=Buffer.from(JSON.stringify(header),'utf8');
  const payloadSize=Math.ceil((4+string.length)/4)*4;
  const pickle=Buffer.alloc(4+payloadSize);
  pickle.writeUInt32LE(payloadSize,0); pickle.writeUInt32LE(string.length,4); string.copy(pickle,8);
  const size=Buffer.alloc(8); size.writeUInt32LE(4,0); size.writeUInt32LE(pickle.length,4);
  await fs.writeFile(candidate,Buffer.concat([size,pickle,candidateBytes.subarray(8+headerSize)]));
  uncache(candidate);
}

export async function inspectInstallation(directory) {
  const installRoot = await fs.realpath(path.resolve(directory));
  const executable = path.join(installRoot, 'Antigravity.exe');
  await fs.access(executable);
  const asarPath = path.join(installRoot, 'resources', 'app.asar');
  if ((await fs.lstat(asarPath)).isSymbolicLink()) fail('symlink_target_refused');
  const entries = archiveEntries(asarPath);
  const metadata = JSON.parse(archiveFile(asarPath, 'package.json').toString('utf8'));
  if (metadata.name !== 'antigravity' || metadata.main !== 'dist/main.js') fail('wrong_application');
  const asarSha256 = sha256(await fs.readFile(asarPath));
  const unpacked = {};
  for (const name of [...entries.keys()].sort()) {
    const entry=entries.get(name);
    if (entry.unpacked && !entry.link) unpacked[name] = sha256(archiveFile(asarPath, name));
  }
  return {installRoot, executable, executableSha256:sha256(await fs.readFile(executable)), asarPath,
    appVersion:metadata.version, asarSha256, sourceKnown:asarSha256 === SOURCE_SHA256_BY_VERSION[metadata.version],
    compatibleNativeSource:validVersion(metadata.version) && await compatibleNativeArchive(asarPath,entries),
    isLocalized:entries.has('dist/preload.js') && archiveFile(asarPath,'dist/preload.js').toString('utf8').includes('__antigravityZhCN'),
    entries:entries.size, unpacked};
}

function requireVersion(info) {
  if (!Object.hasOwn(SOURCE_SHA256_BY_VERSION,info.appVersion)) fail('unsupported_version', info.appVersion);
  return SOURCE_SHA256_BY_VERSION[info.appVersion];
}
function requireSource(info,compatibleSource=false) {
  if(compatibleSource) {
    if(!validVersion(info.appVersion) || (!info.sourceKnown && !info.compatibleNativeSource)) fail('incompatible_native_source');
    return info.asarSha256;
  }
  const sourceSha256=requireVersion(info); if (!info.sourceKnown) fail('unknown_source_hash', info.asarSha256); return sourceSha256;
}

export async function compareArchives(source, candidate, whitelist,{sourceUnpacked}={}) {
  const original = archiveEntries(source), output = archiveEntries(candidate);
  if (original.size !== output.size) fail('archive_entry_set_changed');
  const changes = [];
  for (const [name, before] of original) {
    const after = output.get(name);
    if (!after) fail('archive_entry_set_changed', name);
    if (Boolean(before.unpacked) !== Boolean(after.unpacked) || before.link !== after.link || Boolean(before.executable) !== Boolean(after.executable)) fail('archive_metadata_changed', name);
    if (before.link) continue;
    const from = before.unpacked && sourceUnpacked ? sourceUnpacked[name] : sha256(archiveFile(source,name));
    if(!validHash(from)) fail('unpacked_identity_missing',name);
    const to = sha256(archiveFile(candidate,name));
    if (from !== to) {
      if (!whitelist.includes(name) || before.unpacked) fail('unexpected_entry_change',name);
      changes.push({entry:name,beforeSha256:from,afterSha256:to});
    }
  }
  return {entries:original.size, changed:changes, unchanged:original.size-changes.length};
}

export async function buildInstallation(directory, {outputRoot = path.join(TOOL_ROOT,'artifacts'),compatibleSource=false,targetInstallRoot} = {}) {
  const info = await inspectInstallation(directory); const sourceSha256=requireSource(info,compatibleSource);
  let destinationRoot=info.installRoot;
  if(targetInstallRoot!==undefined) {
    const target=await inspectInstallation(targetInstallRoot);
    if(target.appVersion!==info.appVersion || target.executableSha256!==info.executableSha256 || !sameUnpacked(target.unpacked,info.unpacked)) fail('target_installation_identity_mismatch');
    destinationRoot=target.installRoot;
  }
  const translationFingerprint=await localizationFingerprint();
  // A watcher may have loaded an earlier translation module in the same process.
  const {createTranslatorPayload} = await import('./translator.mjs?localization='+translationFingerprint);
  const {patchNativeFiles,WHITELIST} = await import('./native-patches.mjs?localization='+translationFingerprint);
  const dictionary = await json(path.join(TOOL_ROOT,'dictionaries','zh-CN.json'));
  dictionary['Version '+info.appVersion]='版本 '+info.appVersion;
  const payload = createTranslatorPayload(dictionary);
  const artifactDir = path.join(path.resolve(outputRoot), info.appVersion + '-' + Date.now() + '-' + crypto.randomUUID().slice(0,8));
  await fs.mkdir(artifactDir,{recursive:true});
  const extracted = path.join(artifactDir,'extracted');
  extractAll(info.asarPath,extracted);
  const files = new Map();
  for (const name of WHITELIST) files.set(name,await fs.readFile(path.join(extracted,name)));
  const patched = patchNativeFiles(files,payload);
  for (const name of WHITELIST) {
    const bytes = patched.files.get(name);
    if (!Buffer.isBuffer(bytes)) fail('invalid_patch_output',name);
    await fs.writeFile(path.join(extracted,name),bytes);
    await run(process.execPath,['--check',path.join(extracted,name)],{windowsHide:true});
  }
  const candidate = path.join(artifactDir,'app.asar');
  const unpackedNames = Object.keys(info.unpacked);
  const requireUnchangedSource = async () => {
    let bytes;
    try { bytes = await fs.readFile(info.asarPath); }
    catch (error) { if (error.code === 'ENOENT') fail('installation_changed'); throw error; }
    if (sha256(bytes) !== sourceSha256) fail('installation_changed');
  };
  // The pinned source unpacks only this complete subtree. The asar Windows implementation
  // handles this literal directory prefix without treating backslashes as glob escapes.
  const unpackPrefix='node_modules/chrome-devtools-mcp/';
  await requireUnchangedSource();
  let comparison;
  try {
    const originalEntries=archiveEntries(info.asarPath);
    if (unpackedNames.some(name=>!name.startsWith(unpackPrefix)) || [...originalEntries].some(([name,entry])=>name.startsWith(unpackPrefix)&&!entry.unpacked)) fail('unexpected_unpacked_layout');
    await createPackageWithOptions(extracted,candidate,unpackedNames.length ? {unpackDir:path.join('node_modules','chrome-devtools-mcp')} : {});
    await requireUnchangedSource();
    await preserveExecutableFlags(info.asarPath,candidate);
    comparison = await compareArchives(info.asarPath,candidate,WHITELIST);
  } catch (error) {
    // A separate installer can replace the source after extraction. Retry that
    // installation change; keep candidate errors fatal when the source is intact.
    await requireUnchangedSource();
    throw error;
  }
  await requireUnchangedSource();
  if (comparison.changed.length === 0) fail('empty_patch');
  if(translationFingerprint!==await localizationFingerprint()) fail('localization_sources_changed');
  const manifest = {schemaVersion:1, appVersion:info.appVersion, toolVersion:'1.0.0', upstreamCommit:UPSTREAM_COMMIT,
    installRoot:destinationRoot, executableSha256:info.executableSha256, sourceSha256,
    patchedSha256:sha256(await fs.readFile(candidate)), dictionaryEntries:Object.keys(dictionary).length,
    payloadSha256:sha256(payload), localizationFingerprint:translationFingerprint, createdUtc:new Date().toISOString(), whitelist:WHITELIST,
    unpacked:info.unpacked, comparison, anchors:patched.changes};
  await saveJson(path.join(artifactDir,'manifest.json'),manifest);
  // The extracted official sources are transient build inputs, never delivery assets.
  await fs.rm(extracted,{recursive:true});
  return {artifactDir,manifest};
}

// The source can be a separately prepared Accounts candidate while all public
// installation records continue to identify the actual destination. This never
// writes that destination or starts an application/account process.
export async function prepareLocalization({sourceInstallRoot,targetInstallRoot,outputRoot,compatibleSource=true}={}) {
  if(!path.isAbsolute(sourceInstallRoot||'') || !path.isAbsolute(targetInstallRoot||'')) fail('absolute_installation_roots_required');
  return buildInstallation(sourceInstallRoot,{outputRoot,compatibleSource,targetInstallRoot});
}

export function localizationInstallRecord(manifest,{artifactDir,appliedUtc=new Date().toISOString()}={}) {
  if(manifest?.schemaVersion!==1 || !validVersion(manifest.appVersion) || !path.isAbsolute(manifest.installRoot||'') ||
      !path.isAbsolute(artifactDir||'') || !validHash(manifest.sourceSha256) || !validHash(manifest.patchedSha256) ||
      !validHash(manifest.executableSha256) || !manifest.unpacked || typeof manifest.unpacked!=='object') fail('invalid_artifact_identity');
  return {schemaVersion:1,appVersion:manifest.appVersion,installRoot:manifest.installRoot,sourceSha256:manifest.sourceSha256,
    patchedSha256:manifest.patchedSha256,localizationFingerprint:manifest.localizationFingerprint,
    executableSha256:manifest.executableSha256,unpacked:manifest.unpacked,artifactDir:path.resolve(artifactDir),appliedUtc};
}

export async function runningInstallationProcesses(directory) {
  if (process.platform !== 'win32') fail('windows_required');
  const result = await run('powershell.exe',['-NoProfile','-File',path.join(TOOL_ROOT,'scripts','Process-State.ps1'),'-InstallRoot',directory],{windowsHide:true,maxBuffer:1024*1024});
  return JSON.parse(result.stdout || '[]');
}
async function requireStopped(directory) {
  const processes = await runningInstallationProcesses(directory);
  if (processes.length) fail('application_running',processes.map(p=>p.ProcessId).join(','));
}

async function operationLock(info,action) {
  const release=await acquireProcessLock(path.join(info.installRoot,'resources'),'.antigravity-zh-cn.lock');
  if(!release) fail('operation_locked');
  try {
    release.assertHeld();
    const result=await action();
    release.assertHeld();
    return result;
  } finally {await release();}
}

async function replaceAsar(info,candidate,expectedHash,candidateHash) {
  await run('powershell.exe',['-NoProfile','-File',path.join(TOOL_ROOT,'scripts','Replace-Asar.ps1'),'-InstallRoot',info.installRoot,
    '-CandidatePath',path.resolve(candidate),'-ExpectedHash',expectedHash,'-CandidateHash',candidateHash],{windowsHide:true,maxBuffer:1024*1024});
  uncache(info.asarPath);
}

async function validateCandidate(info,artifactDir,sourceSha256) {
  const manifest = await json(path.join(artifactDir,'manifest.json'));
  if (manifest.schemaVersion !== 1 || manifest.appVersion !== info.appVersion || manifest.sourceSha256 !== sourceSha256 || manifest.installRoot!==info.installRoot) fail('invalid_artifact_identity');
  const candidate = path.join(artifactDir,'app.asar');
  if (sha256(await fs.readFile(candidate)) !== manifest.patchedSha256) fail('candidate_hash_mismatch');
  if (manifest.executableSha256 !== info.executableSha256) fail('executable_changed');
  if (!sameUnpacked(manifest.unpacked,info.unpacked)) fail('unpacked_changed');
  const {WHITELIST} = await import('./native-patches.mjs');
  const comparison = await compareArchives(info.asarPath,candidate,WHITELIST);
  if (JSON.stringify(comparison) !== JSON.stringify(manifest.comparison)) fail('candidate_difference_mismatch');
  return {manifest,candidate};
}

async function refreshSource(directory,backupRoot) {
  const info=await inspectInstallation(directory);
  const recordPath=path.join(info.installRoot,'resources','antigravity-zh-cn.install.json');
  if((await fs.lstat(recordPath)).isSymbolicLink()) fail('symlink_record_refused');
  const record=await json(recordPath);
  if(!info.isLocalized || record.schemaVersion!==1 || record.installRoot!==info.installRoot || record.appVersion!==info.appVersion || record.patchedSha256!==info.asarSha256 || record.executableSha256!==info.executableSha256 || !sameUnpacked(record.unpacked,info.unpacked) || !validHash(record.sourceSha256)) fail('installation_changed');
  const backupDir=path.join(path.resolve(backupRoot),info.appVersion,record.sourceSha256);
  const backupPath=path.join(backupDir,'app.asar'),backup=await json(path.join(backupDir,'manifest.json'));
  if(backup.schemaVersion!==1 || backup.appVersion!==info.appVersion || backup.sourceSha256!==record.sourceSha256 || backup.executableSha256!==info.executableSha256 || !sameUnpacked(backup.unpacked,info.unpacked)) fail('backup_identity_mismatch');
  if(sha256(await fs.readFile(backupPath))!==record.sourceSha256) fail('backup_hash_mismatch');
  const entries=archiveEntries(backupPath),metadata=JSON.parse(archiveFile(backupPath,'package.json').toString('utf8'));
  if(metadata.name!=='antigravity' || metadata.main!=='dist/main.js' || metadata.version!==info.appVersion) fail('invalid_backup_identity');
  if(record.sourceSha256!==SOURCE_SHA256_BY_VERSION[info.appVersion] && !await compatibleNativeArchive(backupPath,entries)) fail('incompatible_native_source');
  return {info,record,recordPath,backupPath,backupDir};
}

// Build from this installation's original without restoring or touching the running app.
export async function prepareLocalizationRefresh(directory,{outputRoot=path.join(TOOL_ROOT,'artifacts'),backupRoot=path.join(TOOL_ROOT,'backups')}={}) {
  const {info,backupPath}=await refreshSource(directory,backupRoot);
  outputRoot=path.resolve(outputRoot);
  await fs.mkdir(outputRoot,{recursive:true});
  const sourceRoot=await fs.mkdtemp(path.join(outputRoot,'.refresh-source-'));
  try {
    await fs.mkdir(path.join(sourceRoot,'resources'));
    await fs.copyFile(info.executable,path.join(sourceRoot,'Antigravity.exe'));
    await fs.copyFile(backupPath,path.join(sourceRoot,'resources','app.asar'));
    await fs.cp(info.asarPath+'.unpacked',path.join(sourceRoot,'resources','app.asar.unpacked'),{recursive:true});
    return await prepareLocalization({sourceInstallRoot:sourceRoot,targetInstallRoot:info.installRoot,outputRoot,compatibleSource:true});
  } finally {
    if(await fs.realpath(sourceRoot)!==sourceRoot || path.dirname(sourceRoot)!==outputRoot) fail('refresh_temporary_path_changed');
    await fs.rm(sourceRoot,{recursive:true,force:true});
  }
}

export async function refreshLocalizedArtifact(directory,artifactDir,{backupRoot=path.join(TOOL_ROOT,'backups')}={}) {
  const {info,record,backupPath,recordPath}=await refreshSource(directory,backupRoot);
  artifactDir=path.resolve(artifactDir);
  const manifest=await json(path.join(artifactDir,'manifest.json')),candidate=path.join(artifactDir,'app.asar');
  if(manifest.schemaVersion!==1 || manifest.appVersion!==info.appVersion || manifest.sourceSha256!==record.sourceSha256) fail('invalid_artifact_identity');
  if(sha256(await fs.readFile(candidate))!==manifest.patchedSha256) fail('candidate_hash_mismatch');
  if(manifest.executableSha256!==info.executableSha256 || !sameUnpacked(manifest.unpacked,info.unpacked)) fail('installation_changed');
  const comparison=await compareArchives(backupPath,candidate,WHITELIST,{sourceUnpacked:record.unpacked});
  if(JSON.stringify(comparison)!==JSON.stringify(manifest.comparison)) fail('candidate_difference_mismatch');
  await requireStopped(info.installRoot);
  return operationLock(info,async()=>{
    const current=await refreshSource(directory,backupRoot);
    if(current.info.asarSha256!==info.asarSha256 || JSON.stringify(current.record)!==JSON.stringify(record)) fail('installation_changed');
    const previous=await fs.readFile(info.asarPath);
    const next={...record,patchedSha256:manifest.patchedSha256,localizationFingerprint:manifest.localizationFingerprint,artifactDir,appliedUtc:new Date().toISOString()};
    const stagedRecord=recordPath+'.stage-'+crypto.randomUUID();
    const rollback=path.join(artifactDir,'previous-'+crypto.randomUUID()+'.asar');
    await saveJson(stagedRecord,next);
    try {
      await fs.writeFile(rollback,previous,{flag:'wx'});
      await replaceAsar(info,candidate,info.asarSha256,manifest.patchedSha256);
      await fs.rename(stagedRecord,recordPath);
    } catch(error) {
      if(sha256(await fs.readFile(info.asarPath))===manifest.patchedSha256 && info.asarSha256!==manifest.patchedSha256) await replaceAsar(info,rollback,manifest.patchedSha256,info.asarSha256);
      throw error;
    } finally {
      await fs.rm(stagedRecord,{force:true});
      await fs.rm(rollback,{force:true});
    }
    return {refreshed:true,...next};
  });
}

export async function applyArtifact(directory,artifactDir,{backupRoot=path.join(TOOL_ROOT,'backups'),compatibleSource=false}={}) {
  const info = await inspectInstallation(directory); const sourceSha256=requireSource(info,compatibleSource);
  const {manifest,candidate} = await validateCandidate(info,path.resolve(artifactDir),sourceSha256);
  await requireStopped(info.installRoot);
  return operationLock(info,async()=>{
    const current = await inspectInstallation(directory); requireSource(current,compatibleSource);
    if (current.appVersion !== info.appVersion || current.asarSha256 !== sourceSha256 || current.executableSha256 !== info.executableSha256 || !sameUnpacked(current.unpacked,info.unpacked)) fail('installation_changed');
    const backupDir=path.join(path.resolve(backupRoot),info.appVersion,sourceSha256);
    await fs.mkdir(backupDir,{recursive:true});
    const backupPath=path.join(backupDir,'app.asar');
    try { await fs.copyFile(info.asarPath,backupPath,fs.constants.COPYFILE_EXCL); }
    catch(e) { if(e.code!=='EEXIST') throw e; }
    if (sha256(await fs.readFile(backupPath)) !== sourceSha256) fail('backup_hash_mismatch');
    const backupManifestPath=path.join(backupDir,'manifest.json');
    try { await saveJson(backupManifestPath,{schemaVersion:1,appVersion:info.appVersion,sourceSha256,executableSha256:info.executableSha256,unpacked:info.unpacked,createdUtc:new Date().toISOString()}); }
    catch(e) { if(e.code!=='EEXIST') throw e; }
    const backupManifest=await json(backupManifestPath);
    if(backupManifest.schemaVersion!==1 || backupManifest.appVersion!==info.appVersion || backupManifest.sourceSha256!==sourceSha256 || backupManifest.executableSha256!==info.executableSha256 || !sameUnpacked(backupManifest.unpacked,info.unpacked)) fail('backup_identity_mismatch');
    const record=localizationInstallRecord(manifest,{artifactDir:path.resolve(artifactDir)});
    const recordPath=path.join(info.installRoot,'resources','antigravity-zh-cn.install.json');
    try { await fs.lstat(recordPath); fail('install_record_exists'); } catch(e) { if(e.code!=='ENOENT') throw e; }
    const stagedRecord=recordPath+'.stage-'+crypto.randomUUID();
    await saveJson(stagedRecord,record);
    try {
      await replaceAsar(info,candidate,sourceSha256,manifest.patchedSha256);
      if (sha256(await fs.readFile(info.asarPath)) !== manifest.patchedSha256) fail('installed_hash_mismatch');
      await fs.rename(stagedRecord,recordPath);
    } catch(error) {
      const installedHash=sha256(await fs.readFile(info.asarPath));
      if(installedHash===manifest.patchedSha256) await replaceAsar(info,backupPath,manifest.patchedSha256,sourceSha256);
      else if(installedHash!==sourceSha256) fail('transaction_failed_target_changed',error.message);
      throw error;
    } finally { await fs.rm(stagedRecord,{force:true}); }
    return {installed:true,backupDir,...record};
  });
}

export async function restoreInstallation(directory,{backupRoot=path.join(TOOL_ROOT,'backups'),compatibleSource=false}={}) {
  const info=await inspectInstallation(directory); const fixedSourceSha256=compatibleSource ? null : requireVersion(info);
  const recordPath=path.join(info.installRoot,'resources','antigravity-zh-cn.install.json');
  const record=await json(recordPath);
  const sourceSha256=compatibleSource ? record.sourceSha256 : fixedSourceSha256;
  if (record.schemaVersion!==1 || record.appVersion!==info.appVersion || record.sourceSha256!==sourceSha256 || record.installRoot!==info.installRoot || (compatibleSource && (!validVersion(info.appVersion) || !validHash(sourceSha256) || !validHash(record.patchedSha256)))) fail('invalid_install_record');
  const alreadyRestored=info.asarSha256===sourceSha256;
  if ((!alreadyRestored && info.asarSha256!==record.patchedSha256) || info.executableSha256!==record.executableSha256 || !sameUnpacked(info.unpacked,record.unpacked)) fail('installation_changed');
  const backupDir=path.join(path.resolve(backupRoot),info.appVersion,sourceSha256), backupPath=path.join(backupDir,'app.asar');
  if (sha256(await fs.readFile(backupPath))!==sourceSha256) fail('backup_hash_mismatch');
  if(compatibleSource) {
    const entries=archiveEntries(backupPath),metadata=JSON.parse(archiveFile(backupPath,'package.json').toString('utf8'));
    if(metadata.name!=='antigravity' || metadata.main!=='dist/main.js' || metadata.version!==info.appVersion) fail('invalid_backup_identity');
    if(sourceSha256!==SOURCE_SHA256_BY_VERSION[info.appVersion] && !await compatibleNativeArchive(backupPath,entries)) fail('incompatible_native_source');
    const backupManifest=await json(path.join(backupDir,'manifest.json'));
    if(backupManifest.schemaVersion!==1 || backupManifest.appVersion!==info.appVersion || backupManifest.sourceSha256!==sourceSha256 || backupManifest.executableSha256!==info.executableSha256 || !sameUnpacked(backupManifest.unpacked,info.unpacked)) fail('backup_identity_mismatch');
    const comparison=await compareArchives(backupPath,info.asarPath,WHITELIST,{sourceUnpacked:backupManifest.unpacked});
    if(!alreadyRestored && (!info.isLocalized || comparison.changed.length===0)) fail('invalid_localized_installation');
  }
  await requireStopped(info.installRoot);
  return operationLock(info,async()=>{
    const archivedRecord=path.join(backupDir,'restored-'+Date.now()+'-'+crypto.randomUUID().slice(0,8)+'.json');
    await saveJson(archivedRecord,record);
    const current=await inspectInstallation(directory);
    if(current.asarSha256!==info.asarSha256 || current.executableSha256!==info.executableSha256 || !sameUnpacked(current.unpacked,info.unpacked)) fail('installation_changed');
    if(!alreadyRestored) await replaceAsar(info,backupPath,record.patchedSha256,sourceSha256);
    if ((await inspectInstallation(directory)).asarSha256!==sourceSha256) fail('restore_hash_mismatch');
    await fs.unlink(recordPath);
    return {restored:true,alreadyRestored,appVersion:info.appVersion,sourceSha256,backupDir};
  });
}
