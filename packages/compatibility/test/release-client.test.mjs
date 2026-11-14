import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { createManifest, validateBundle, compareVersions, safeRelativePath, sha256, REPOSITORY } from '../manifest.mjs';
import { checkForUpdates, activateBundle, selectRelease, CHECK_INTERVAL_MS, extractBundle } from '../release-client.mjs';
import { engineDirectory, engineArgs, launchBootstrap, installBootstrap } from '../bootstrap.mjs';
import { resolveAccountsStateRoot } from '../../account-plugin/manage-install.mjs';
import { watchCompatibility } from '../watch.mjs';

const version = '0.4.0-preview.17', commit = 'a'.repeat(40);
const required = ['accounts/package-manifest.json','accounts/component/Antigravity.Accounts.exe','accounts/account-plugin/manage-install.mjs',
  'localization/src/toolkit.mjs','localization/src/native-compatibility.mjs','localization/dictionaries/zh-CN.json',
  'localization/node_modules/@electron/asar/package.json','localization/node_modules/acorn/package.json',
  'compatibility/cli.mjs','compatibility/bootstrap.mjs','compatibility/release-client.mjs','compatibility/manifest.mjs'];
async function fixture(t, selectedVersion = version) {
  const parent = await fs.realpath(os.tmpdir()), temporary = await fs.mkdtemp(path.join(parent, 'agy-compat-update-'));
  t.after(async () => {
    assert.equal(path.dirname(await fs.realpath(temporary)), parent);
    assert.match(path.basename(temporary), /^agy-compat-update-/);
    await fs.rm(temporary, { recursive: true });
  });
  const bundle = path.join(temporary, 'bundle'), state = path.join(temporary, 'state');
  await fs.mkdir(state);
  for (const file of required) { await fs.mkdir(path.dirname(path.join(bundle, file)), { recursive: true }); await fs.writeFile(path.join(bundle, file), file.endsWith('cli.mjs') ? "console.log(JSON.stringify({phase:'installed',activationAllowed:true}));" : 'synthetic'); }
  const manifest = await createManifest(bundle, { version: selectedVersion, provenance: { commit, dirty: false, release: true },
    components: { accounts: { path: 'accounts', version: selectedVersion }, localization: { path: 'localization', version: '1.0.0' } } });
  await fs.writeFile(path.join(bundle, 'bundle-manifest.json'), JSON.stringify(manifest));
  return { temporary, bundle, state, manifest };
}
function release(selectedVersion = version) {
  const tag = 'accounts-v' + selectedVersion, name = `AntigravityCompatibility-${selectedVersion}-win-x64.zip`;
  return { draft: false, prerelease: true, tag_name: tag, assets: [name, name + '.sha256.json'].map(name => ({ name,
    browser_download_url: `https://github.com/${REPOSITORY}/releases/download/${tag}/${name}` })) };
}
function jsonResponse(value) { return new Response(JSON.stringify(value)); }

test('preview releases are ordered by version and use only fixed-repository bundle assets', () => {
  assert.equal(compareVersions('0.4.0-preview.17','0.4.0-preview.9'), 1);
  assert.equal(compareVersions('0.4.0','0.4.0-preview.17'), 1);
  const wrong = release('0.4.0-preview.19'); wrong.assets[0].browser_download_url = 'https://example.invalid/evil.zip';
  assert.throws(() => selectRelease([wrong], '0.4.0-preview.16'), /release_invalid/);
  assert.equal(selectRelease([release(), release('0.4.0-preview.18')], version).version, '0.4.0-preview.18');
  assert.equal(selectRelease([release()], '0.4.0-preview.18'), undefined);
  const legacy = release('0.4.0-preview.20'); legacy.assets = [];
  assert.equal(selectRelease([legacy, release()], '0.4.0-preview.16').version, version);
});

test('manifest rejects changed, unlisted, traversal and Windows-alias payload names', async t => {
  const f = await fixture(t);
  assert.equal((await validateBundle(f.bundle)).version, version);
  for (const value of ['../x','/x','x\\y','x:y','x/CON.txt','x/trailing.','x//y']) assert.equal(safeRelativePath(value), false, value);
  await fs.writeFile(path.join(f.bundle, 'extra.mjs'), 'x');
  await assert.rejects(validateBundle(f.bundle), /payload_mismatch/);
  await fs.rm(path.join(f.bundle, 'extra.mjs'));
  await fs.writeFile(path.join(f.bundle, 'compatibility/cli.mjs'), 'changed');
  await assert.rejects(validateBundle(f.bundle), /payload_mismatch/);
});

test('automatic download checks ZIP, tag commit, complete payload and preserves inactive state', async t => {
  const f = await fixture(t), metadata = release(), bytes = Buffer.from('synthetic ZIP'), calls = [];
  const fetchImpl = async url => {
    calls.push(url);
    if (url.endsWith('/releases?per_page=30')) return jsonResponse([metadata]);
    if (url.endsWith('.sha256.json')) return jsonResponse({ file: metadata.assets[0].name, sha256: sha256(bytes) });
    if (url.includes('/git/ref/tags/')) return jsonResponse({ object: { type: 'commit', sha: commit } });
    if (url.endsWith('.zip')) return new Response(bytes);
    throw new Error('Unexpected URL');
  };
  const result = await checkForUpdates({ stateRoot: f.state, currentVersion: '0.4.0-preview.16', hostStamp: 'host1', fetchImpl,
    extract: async () => f.bundle, now: () => 1000 });
  assert.equal(result.status, 'prepared'); assert.equal(result.version, version);
  assert.equal(JSON.parse(await fs.readFile(path.join(f.state,'pending-bundle.json'),'utf8')).bundleRoot, f.bundle);
  await assert.rejects(fs.stat(path.join(f.state,'active-version.json')), { code: 'ENOENT' });
  const deferred = await checkForUpdates({ stateRoot: f.state, currentVersion: '0.4.0-preview.16', hostStamp: 'host1', fetchImpl: () => assert.fail('Do not poll before six hours'), now: () => 1001 });
  assert.equal(deferred.status, 'deferred'); assert.equal(deferred.retryAt, 1000 + CHECK_INTERVAL_MS); assert.equal(calls.length, 4);
});

test('offline and corrupt ZIP updates are nonthrowing and never replace the active pointer', async t => {
  const f = await fixture(t);
  await fs.writeFile(path.join(f.state,'active-version.json'), JSON.stringify({schemaVersion:1,version:'0.4.0-preview.16',directory:'unchanged'}));
  const before = await fs.readFile(path.join(f.state,'active-version.json'),'utf8');
  const offline = await checkForUpdates({stateRoot:f.state,currentVersion:'0.4.0-preview.16',fetchImpl:async()=>{throw Error('offline');}});
  assert.equal(offline.status,'unavailable');
  const metadata=release();
  const bad=await checkForUpdates({stateRoot:f.state,currentVersion:'0.4.0-preview.16',force:true,fetchImpl:async url=>
    url.includes('/releases?')?jsonResponse([metadata]):url.endsWith('.sha256.json')?jsonResponse({file:metadata.assets[0].name,sha256:'0'.repeat(64)}):
      url.includes('/git/ref/')?jsonResponse({object:{type:'commit',sha:commit}}):new Response('incorrect'),extract:()=>assert.fail('Bad ZIP must not be extracted')});
  assert.equal(bad.errorCode,'compatibility_zip_mismatch'); assert.equal(await fs.readFile(path.join(f.state,'active-version.json'),'utf8'),before);
});

test('failed candidate engine inspection leaves the previous active engine selected', async t => {
  const f=await fixture(t), previous={schemaVersion:1,version:'0.4.0-preview.16',directory:path.join(f.state,'versions','0.4.0-preview.16')};
  await fs.writeFile(path.join(f.state,'active-version.json'),JSON.stringify(previous));
  await assert.rejects(activateBundle({stateRoot:f.state,bundleRoot:f.bundle,probe:async()=>{throw Error('cannot load candidate');}}),/cannot load/);
  assert.deepEqual(JSON.parse(await fs.readFile(path.join(f.state,'active-version.json'),'utf8')),previous);
  const active=await activateBundle({stateRoot:f.state,bundleRoot:f.bundle,probe:async()=>{}});
  assert.equal(active.version,version); assert.equal(active.previousVersion,previous.version);
  await assert.rejects(activateBundle({stateRoot:f.state,bundleRoot:(await fixture(t,'0.4.0-preview.15')).bundle,probe:async()=>{}}),/manifest_invalid/);
});

test('stable bootstrap preserves registered paths and recovers only a failed engine load', async t => {
  const f=await fixture(t), old='0.4.0-preview.16', versions=path.join(f.state,'versions');
  for(const [v,source] of [[old,"console.log(JSON.stringify({phase:'old-engine'}));"],[version,'this is not javascript']]) {
    await fs.mkdir(path.join(versions,v,'compatibility'),{recursive:true}); await fs.writeFile(path.join(versions,v,'compatibility','cli.mjs'),source);
  }
  const config={schemaVersion:1,stateRoot:f.state,installRoot:path.join(f.temporary,'host'),accountsStateRoot:path.join(f.temporary,'accounts'),
    localizationStateRoot:path.join(f.temporary,'zh-state'),toolkitRoot:path.join(f.temporary,'old-toolkit'),nodePath:process.execPath,enabledComponents:{accounts:true,localization:true}};
  await fs.writeFile(path.join(f.state,'bootstrap.json'),JSON.stringify(config));
  await fs.writeFile(path.join(f.state,'active-version.json'),JSON.stringify({schemaVersion:1,version,directory:path.join(versions,version),previousVersion:old}));
  assert.equal((await launchBootstrap({stateRoot:f.state,command:'inspect'})).phase,'old-engine');
  assert.equal(JSON.parse(await fs.readFile(path.join(f.state,'active-version.json'),'utf8')).version,old);
  const args=engineArgs('launch',config,f.bundle,['agy://example']);
  assert.equal(args[args.indexOf('--accounts-state-root')+1],config.accountsStateRoot); assert.deepEqual(args.slice(-2),['--','agy://example']);
  await assert.rejects(engineDirectory(f.state,{schemaVersion:1,version,directory:f.bundle}),/active_version_invalid/);
});

test('an engine refusal is not retried with an older engine', async t => {
  const f=await fixture(t), old='0.4.0-preview.16', versions=path.join(f.state,'versions');
  for(const [v,source] of [[old,"console.log(JSON.stringify({phase:'must-not-run'}));"],[version,"console.error(JSON.stringify({error:'installation_changed'}));process.exitCode=1;"]]) {
    await fs.mkdir(path.join(versions,v,'compatibility'),{recursive:true});await fs.writeFile(path.join(versions,v,'compatibility','cli.mjs'),source);
  }
  const config={schemaVersion:1,stateRoot:f.state,installRoot:path.join(f.temporary,'host'),accountsStateRoot:path.join(f.temporary,'accounts'),localizationStateRoot:path.join(f.temporary,'zh'),
    toolkitRoot:path.join(f.temporary,'old'),nodePath:process.execPath,enabledComponents:{accounts:true,localization:true}};
  const active={schemaVersion:1,version,directory:path.join(versions,version),previousVersion:old};
  await fs.writeFile(path.join(f.state,'bootstrap.json'),JSON.stringify(config));await fs.writeFile(path.join(f.state,'active-version.json'),JSON.stringify(active));
  await assert.rejects(launchBootstrap({stateRoot:f.state,command:'apply'}),/engine_operation_failed/);
  assert.deepEqual(JSON.parse(await fs.readFile(path.join(f.state,'active-version.json'),'utf8')),active);
});

function zipEntries(entries) {
  const locals=[],centrals=[];let offset=0;
  for(const {name,text='value',attributes=0} of entries) {
    const filename=Buffer.from(name),bytes=Buffer.from(text);let crc=0xffffffff;
    for(const byte of bytes){crc^=byte;for(let i=0;i<8;i++)crc=(crc>>>1)^((crc&1)?0xedb88320:0);}crc=(crc^0xffffffff)>>>0;
    const local=Buffer.alloc(30);local.writeUInt32LE(0x04034b50);local.writeUInt16LE(20,4);local.writeUInt32LE(crc,14);local.writeUInt32LE(bytes.length,18);local.writeUInt32LE(bytes.length,22);local.writeUInt16LE(filename.length,26);
    const central=Buffer.alloc(46);central.writeUInt32LE(0x02014b50);central.writeUInt16LE(0x0314,4);central.writeUInt16LE(20,6);central.writeUInt32LE(crc,16);central.writeUInt32LE(bytes.length,20);central.writeUInt32LE(bytes.length,24);central.writeUInt16LE(filename.length,28);central.writeUInt32LE(attributes>>>0,38);central.writeUInt32LE(offset,42);
    locals.push(local,filename,bytes);centrals.push(central,filename);offset+=local.length+filename.length+bytes.length;
  }
  const centralBytes=Buffer.concat(centrals),end=Buffer.alloc(22);end.writeUInt32LE(0x06054b50);end.writeUInt16LE(entries.length,8);end.writeUInt16LE(entries.length,10);end.writeUInt32LE(centralBytes.length,12);end.writeUInt32LE(offset,16);
  return Buffer.concat([...locals,centralBytes,end]);
}

test('Windows extractor rejects traversal, duplicate names and links before writing any payload', {skip:process.platform!=='win32'}, async t => {
  const f=await fixture(t);
  const cases=[[{name:'AntigravityCompatibility/../escape.txt'}],[{name:'OtherRoot/file.txt'}],
    [{name:'AntigravityCompatibility/file.txt'},{name:'AntigravityCompatibility/FILE.txt'}],[{name:'AntigravityCompatibility/link',attributes:0xa1ff0000}]];
  for(let i=0;i<cases.length;i++) {
    const zip=path.join(f.temporary,`invalid-${i}.zip`),output=path.join(f.temporary,`extracted-${i}`);await fs.writeFile(zip,zipEntries(cases[i]));
    await assert.rejects(extractBundle(zip,output));await assert.rejects(fs.stat(output),{code:'ENOENT'});
  }
  const zip=path.join(f.temporary,'valid.zip'),output=path.join(f.temporary,'valid-extracted');await fs.writeFile(zip,zipEntries([{name:'AntigravityCompatibility/file.txt',text:'original bytes'}]));
  const extracted=await extractBundle(zip,output);assert.equal(await fs.readFile(path.join(extracted,'file.txt'),'utf8'),'original bytes');
});

test('watcher waits for normal exit, reconciles owned shortcuts and releases its lock before restart', async t => {
  const f=await fixture(t), order=[];
  await fs.writeFile(path.join(f.state,'active-version.json'),JSON.stringify({schemaVersion:1,version:'0.4.0-preview.16'}));
  await fs.writeFile(path.join(f.state,'pending-bundle.json'),JSON.stringify({bundleRoot:f.bundle,version}));
  let tick=0;
  const release=async()=>{order.push('released');};release.assertHeld=()=>{};
  const result=await watchCompatibility({config:{stateRoot:f.state,installRoot:path.join(f.temporary,'host')},bundleRoot:path.join(f.temporary,'old'),maxTicks:2,
    acquireLock:async()=>release,check:async()=>({status:'deferred'}),pause:async()=>{},runEngine:async()=>({phase:++tick===1?'pending_idle':'applied',activationAllowed:tick===2}),
    activate:async()=>{order.push('activated');},reconcile:async()=>{order.push('reconciled');},notify:async()=>assert.fail('No warning expected')});
  assert.equal(result.restart,true);assert.equal(tick,2);assert.deepEqual(order,['reconciled','activated','released']);
});

test('notification failure never stops recovery and the same error stays deduplicated after watcher restart', async t => {
  const f=await fixture(t);let notifications=0,engines=0;
  await fs.writeFile(path.join(f.state,'active-version.json'),JSON.stringify({schemaVersion:1,version}));
  const release=async()=>{};
  const options={config:{stateRoot:f.state,installRoot:path.join(f.temporary,'host')},bundleRoot:f.bundle,maxTicks:2,pause:async()=>{},acquireLock:async()=>release,
    check:async()=>({status:'unavailable',errorCode:'compatibility_update_unavailable'}),runEngine:async()=>{engines++;return{phase:'installed',activationAllowed:false};},
    notify:async()=>{notifications++;throw Error('desktop unavailable');},reconcile:async()=>{}};
  await watchCompatibility(options);await watchCompatibility(options);
  assert.equal(notifications,1);assert.equal(engines,2);assert.equal(JSON.parse(await fs.readFile(path.join(f.state,'update-notice.json'),'utf8')).shown,true);
});

test('bootstrap records the physical account receipt root without moving data or enabling absent components', async t => {
  const f=await fixture(t), logical=path.join(f.temporary,'logical-accounts'), physical=path.join(f.temporary,'physical-accounts'),
    host=path.join(f.temporary,'host'), localization=path.join(f.temporary,'zh-state');
  for(const directory of [logical,physical,path.join(host,'resources'),path.join(localization,'windows-persistence')])await fs.mkdir(directory,{recursive:true});
  await fs.writeFile(path.join(logical,'adapter-installation.json'),JSON.stringify({phase:'logical-view-only'}));
  await fs.writeFile(path.join(physical,'adapter-installation.json'),JSON.stringify({phase:'applied'}));
  await fs.writeFile(path.join(physical,'accounts.v1.dpapi'),'do not read or move');
  await fs.writeFile(path.join(localization,'windows-persistence','install.json'),JSON.stringify({installed:false}));
  const calls=[], options={bundleRoot:f.bundle,stateRoot:f.state,installRoot:host,accountsStateRoot:logical,
    localizationStateRoot:localization,toolkitRoot:path.join(f.temporary,'old-toolkit'),nodePath:process.execPath,
    resolveAccountsRoot:parameters=>resolveAccountsStateRoot({...parameters,io:{
      async realpath(filename){calls.push(filename);assert.notEqual(path.basename(filename),'accounts.v1.dpapi');
        return filename===path.join(logical,'adapter-installation.json')?path.join(physical,'adapter-installation.json'):fs.realpath(filename);},
      async readFile(filename,...args){assert.notEqual(path.basename(filename),'accounts.v1.dpapi');return fs.readFile(filename,...args);},
    }}),activate:parameters=>activateBundle({...parameters,probe:async()=>{}})};
  const result=await installBootstrap(options), saved=JSON.parse(await fs.readFile(path.join(f.state,'bootstrap.json'),'utf8'));
  assert.equal(result.accountsStateRoot,physical);assert.equal(saved.accountsStateRoot,physical);
  assert.deepEqual(saved.enabledComponents,{accounts:true,localization:false});
  assert.equal(calls.includes(path.join(logical,'adapter-installation.json')),true);
  assert.equal(await fs.readFile(path.join(physical,'accounts.v1.dpapi'),'utf8'),'do not read or move');
  await assert.rejects(fs.stat(path.join(logical,'accounts.v1.dpapi')),{code:'ENOENT'});
  saved.enabledComponents.accounts=false;await fs.writeFile(path.join(f.state,'bootstrap.json'),JSON.stringify(saved));
  await installBootstrap(options);
  assert.deepEqual(JSON.parse(await fs.readFile(path.join(f.state,'bootstrap.json'),'utf8')).enabledComponents,{accounts:false,localization:false},'Existing explicit settings take precedence on upgrade');
});
