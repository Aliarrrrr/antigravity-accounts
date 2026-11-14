import {testDataRoot} from './fixture-paths.mjs';
import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import vm from 'node:vm';
import {fileURLToPath} from 'node:url';
import {WHITELIST,patchNativeFiles} from '../src/native-patches.mjs';
import {nativeCompatibility} from '../src/native-compatibility.mjs';

const root=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'..');
const original=path.join(testDataRoot,'verification','2.32.1-compat-review','current');
const files=new Map(WHITELIST.map(entry=>[entry,fs.readFileSync(path.join(original,entry))]));
const payload='globalThis.__translationTest = true;';
const {files:patched}=patchNativeFiles(files,payload);

test('an unrelated WSL artifact helper can gain parameters while translation dependencies stay fixed',()=>{
  const changed=new Map(files),source=files.get('dist/wsl.js').toString();
  const updated=source.replace('async function resolveStableArtifactDir(version, fetchImpl = fetch)','async function resolveStableArtifactDir(version, fetchImpl = fetch, options = {})');
  assert.notEqual(source,updated);changed.set('dist/wsl.js',Buffer.from(updated));
  assert.equal(nativeCompatibility(changed).compatible,true);
});

test('reviewed 2.32.1 source is accepted without changing its input',()=>{
  const before=new Map([...files].map(([entry,bytes])=>[entry,Buffer.from(bytes)]));
  assert.deepEqual(nativeCompatibility(files),{
    compatible:true,mode:'reviewed-scopes',revision:'reviewed-2.32.1',
  });
  for(const [entry,bytes] of before) assert.deepEqual(files.get(entry),bytes,entry+' changed');
});

function runPreload(remote) {
  const exposed=new Map(),listeners=new Map(),executed=[];
  const window={};
  window.top=window;
  const electron={
    contextBridge:{exposeInMainWorld:(name,api)=>exposed.set(name,api)},
    ipcRenderer:{on:(channel,callback)=>listeners.set(channel,callback),invoke:async()=>({})},
    webFrame:{executeJavaScript:async code=>executed.push(code)},
  };
  const context={exports:{},require:name=>{assert.equal(name,'electron');return electron;},
    process:{argv:remote?['--agy-backend=ssh']:[]},window,document:{readyState:'complete'},console};
  vm.runInNewContext(patched.get('dist/preload.js').toString(),context);
  return {exposed,listeners,executed};
}

test('local preload keeps native APIs and injects the Chinese runtime exactly once',()=>{
  const {exposed,listeners,executed}=runPreload(false);
  assert.equal(exposed.get('sshRemote').kind,'local');
  for(const name of ['electronUpdater','dialog','logs','deepLink','ide','wsl','desktopMic']) assert.ok(exposed.has(name),name);
  assert.equal(typeof exposed.get('electronNative').getPathForFile,'function');
  assert.equal(typeof exposed.get('electronNative').wcvBrowser.acquire,'function');
  assert.ok(listeners.has('agy-script-jail:port'));
  assert.deepEqual(executed,[payload]);
});

test('SSH preload retains the restricted API set and receives no local translation injection',()=>{
  const {exposed,listeners,executed}=runPreload(true);
  assert.equal(exposed.get('sshRemote').kind,'ssh');
  for(const name of ['electronUpdater','dialog','logs','deepLink','ide','wsl','desktopMic']) assert.equal(exposed.has(name),false,name);
  const native=exposed.get('electronNative');
  for(const name of ['getPathForFile','wcvBrowser','runScriptInJail']) assert.equal(Object.hasOwn(native,name),false,name);
  assert.equal(typeof native.close,'function');
  assert.equal(typeof native.showContextMenu,'function');
  assert.equal(listeners.has('agy-script-jail:port'),false);
  assert.deepEqual(executed,[]);
});

test('Chinese folder dialog remains local-only and host storage is resolved per sender',async()=>{
  const handlers=new Map(),dialogs=[];
  const localStore={getItems:async()=>({kind:'local'})};
  const remoteStore={getItems:async()=>({kind:'ssh'})};
  const modules={
    electron:{ipcMain:{handle:(channel,handler)=>handlers.set(channel,handler),on(){},removeListener(){},removeAllListeners(){}},
      dialog:{showOpenDialog:async options=>{dialogs.push(options);return {canceled:false,filePaths:['D:\\project']};}}},
    './hubZoom':{setHubZoomStorageManager(){}},
    './deviceChooser':{deviceChooserController:{setHostResolver(){}}},
    './wcvIpcHandlers':{registerWcvIpcHandlers(){}},
    './remote/remoteWindowSecurity':{isRemoteContents:sender=>Boolean(sender?.remote)},
    './wsl':{getActiveWslDistro:()=>''},
  };
  const context={exports:{},require:name=>modules[name]??{},process:{platform:'win32'},console};
  vm.runInNewContext(patched.get('dist/ipcHandlers.js').toString(),context);
  context.exports.registerIpcHandlers(localStore,{resolveStorage:sender=>sender?.knownRemote?remoteStore:undefined});
  assert.equal(await handlers.get('dialog:open-workspace')({sender:{remote:false}}),'D:\\project');
  assert.equal(dialogs[0].title,'打开工作区');
  assert.throws(()=>handlers.get('dialog:open-workspace')({sender:{remote:true}}),/not available in SSH windows/);
  assert.equal(dialogs.length,1);
  assert.deepEqual(await handlers.get('storage:get-items')({sender:{remote:false}}),{kind:'local'});
  assert.deepEqual(await handlers.get('storage:get-items')({sender:{remote:true,knownRemote:true}}),{kind:'ssh'});
  await assert.rejects(handlers.get('storage:get-items')({sender:{remote:true}}),/Storage is not available/);
});

test('2.32.1 still rejects changes to the translation-owning branch, IPC event and quit cleanup',()=>{
  for(const [entry,from,to] of [
    ['dist/preload.js','if (isRemoteWindow) {','if (false) {'],
    ['dist/ipcHandlers.js',"localOnly('dialog:open-workspace',","localOnly('different:open-workspace',"],
    ['dist/main.js','remoteManager?.disposeAll()','Promise.resolve()'],
  ]) {
    const source=files.get(entry).toString(),changed=source.replace(from,to);
    assert.notEqual(changed,source);
    const input=new Map(files);
    input.set(entry,Buffer.from(changed));
    assert.equal(patchNativeFiles(input,payload).changes.length,9);
    assert.equal(nativeCompatibility(input).compatible,false,entry+' was accepted');
  }
});

test('Chinese patch preserves SSH shutdown, per-host stores and WSL binary URL construction',()=>{
  const main=patched.get('dist/main.js').toString();
  assert.match(main,/remoteManager\?\.disposeAll\(\)\.catch/);
  assert.match(main,/resolveStorage: \(sender\) => hostStores\.forSender\(sender\)/);
  assert.match(main,/\(0, sshBridge_1\.registerSshBridgeIpc\)\(remoteManager\)/);
  assert.match(main,/title: '确认退出'/);
  const wsl=patched.get('dist/wsl.js').toString();
  assert.match(wsl,/const url = linuxServerUrl\(artifactDir, arch\);/);
  assert.match(wsl,/async function resolveStableArtifactDir\(version, fetchImpl = fetch\)/);
});

test('reviewed 2.32.1 Accounts preview.15 main keeps account and SSH startup hooks when translated',()=>{
  const combined=new Map(files);
  combined.set('dist/main.js',fs.readFileSync(path.join(testDataRoot,'verification','2.32.1-compat-review','accounts-main.js')));
  assert.deepEqual(nativeCompatibility(combined),{
    compatible:true,mode:'reviewed-scopes',revision:'reviewed-2.32.1-accounts-preview.15',
  });
  const {files:localized}=patchNativeFiles(combined,payload);
  const hooks=source=>source.toString().split('\n').filter(line=>/hostauthAddon|hostauthSession|accountsUpdateHandoff|remoteManager|hostStores|sshBridge_1/.test(line));
  assert.deepEqual(hooks(localized.get('dist/main.js')),hooks(combined.get('dist/main.js')));
  const changed=new Map(combined);
  changed.set('dist/main.js',Buffer.from(combined.get('dist/main.js').toString().replace('nativeProgramPath: hostauthSession.nativeProgramPath,','nativeProgramPath: undefined,')));
  assert.equal(nativeCompatibility(changed).compatible,false);
});
