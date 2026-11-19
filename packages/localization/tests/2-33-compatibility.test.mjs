import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import path from 'node:path';
import vm from 'node:vm';
import {extractFile} from '@electron/asar';
import {readAsar} from '../../hostauth-adapter/src/asar.mjs';
import {patchSources} from '../../hostauth-adapter/src/patch-plan.mjs';
import {testDataRoot} from './fixture-paths.mjs';
import {WHITELIST,patchNativeFiles} from '../src/native-patches.mjs';
import {nativeCompatibility} from '../src/native-compatibility.mjs';

const archive=process.env.LOCALIZATION_TEST_2_33_ASAR||path.join(testDataRoot,'verification','2.33.0-compat-review','app.asar');
const files=new Map(WHITELIST.map(entry=>[entry,extractFile(archive,entry.split('/').join(path.sep))]));
const payload='globalThis.__translationTest = true;';

test('2.33.0 is accepted while a change to its local-only preload branch is rejected',()=>{
  assert.deepEqual(nativeCompatibility(files),{
    compatible:true,mode:'reviewed-scopes',revision:'reviewed-2.33.0',
  });
  const changed=new Map(files);
  const source=files.get('dist/preload.js').toString();
  const altered=source.replace('if (isRemoteWindow) {','if (false) {');
  assert.notEqual(altered,source);
  changed.set('dist/preload.js',Buffer.from(altered));
  assert.equal(nativeCompatibility(changed).compatible,false);
});

function runPreload(source,remote){
  const exposed=new Map(),invoked=[],executed=[];
  const window={};
  window.top=window;
  const electron={
    contextBridge:{exposeInMainWorld:(name,api)=>exposed.set(name,api)},
    ipcRenderer:{on(){},invoke:async channel=>invoked.push(channel)},
    webFrame:{executeJavaScript:async code=>executed.push(code)},
  };
  vm.runInNewContext(source.toString(),{
    exports:{},require:name=>{assert.equal(name,'electron');return electron;},
    process:{argv:remote?['--agy-backend=ssh']:[]},window,document:{readyState:'complete'},console,
  });
  return {exposed,invoked,executed};
}

test('2.33.0 localization preserves local browser APIs and the SSH read-only microphone bridge',async()=>{
  const {files:patched}=patchNativeFiles(files,payload);
  const source=patched.get('dist/preload.js');
  const local=runPreload(source,false);
  assert.equal(typeof local.exposed.get('agyComputerPreview')?.open,'function');
  assert.equal(typeof local.exposed.get('electronNative')?.wcvBrowser?.find,'function');
  assert.deepEqual(local.executed,[payload]);

  const remote=runPreload(source,true);
  assert.equal(remote.exposed.get('sshRemote')?.kind,'ssh');
  await remote.exposed.get('desktopMic').requestAccess();
  assert.deepEqual(remote.invoked,['mic:get-access-status']);
  assert.equal(remote.exposed.has('agyComputerPreview'),false);
  assert.equal(Object.hasOwn(remote.exposed.get('electronNative'),'wcvBrowser'),false);
  assert.deepEqual(remote.executed,[]);
});

test('2.33.0 Accounts startup and SSH cleanup survive the Chinese quit dialog translation',()=>{
  const combined=new Map(files);
  for(const [entry,bytes] of patchSources(readAsar(readFileSync(archive)))) {
    if(combined.has(entry)) combined.set(entry,bytes);
  }
  const compatibility=nativeCompatibility(combined);
  assert.equal(compatibility.revision,'reviewed-file-combination',JSON.stringify(compatibility));
  assert.equal(compatibility.reviewedFiles['dist/main.js'],'reviewed-2.33.0-accounts');

  const {files:localized}=patchNativeFiles(combined,payload);
  const source=combined.get('dist/main.js').toString();
  const translated=localized.get('dist/main.js').toString();
  assert.match(translated,/title: '确认退出'/);
  const startupAndCleanup=code=>code.split('\n').filter(line=>
    /hostauthAddon|hostauthSession|accountsUpdateHandoff|sshBridge_1|hostStores|remoteManager/.test(line));
  assert.deepEqual(startupAndCleanup(translated),startupAndCleanup(source));

  for(const [original,changed] of [
    ['remoteManager?.disposeAll()','Promise.resolve()'],
    ['nativeProgramPath: hostauthSession.nativeProgramPath,','nativeProgramPath: undefined,'],
  ]) {
    const altered=source.replace(original,changed);
    assert.notEqual(altered,source);
    const unexpected=new Map(combined);
    unexpected.set('dist/main.js',Buffer.from(altered));
    assert.deepEqual(nativeCompatibility(unexpected).unreviewed,['dist/main.js'],original);
  }
});
