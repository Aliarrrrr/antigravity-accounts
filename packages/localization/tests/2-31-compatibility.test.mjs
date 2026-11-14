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
const original=path.join(testDataRoot,'verification','2.31.0-compat-review','current');
const files=new Map(WHITELIST.map(entry=>[entry,fs.readFileSync(path.join(original,entry))]));

test('reviewed 2.31 original is accepted without modifying its source',()=>{
  const before=new Map([...files].map(([entry,bytes])=>[entry,Buffer.from(bytes)]));
  assert.deepEqual(nativeCompatibility(files),{
    compatible:true,mode:'reviewed-scopes',revision:'reviewed-2.31.0',
  });
  for(const [entry,bytes] of before) assert.deepEqual(files.get(entry),bytes,entry+' changed');
});

test('2.31 localization preserves new native APIs and browser session cleanup',()=>{
  const {files:patched,changes}=patchNativeFiles(files,'/* translator */');
  assert.equal(changes.length,9);
  assert.deepEqual(changes.map(change=>change.entry).sort(),WHITELIST.filter(entry=>entry!=='dist/ideInstall/wizardPreload.js').sort());
  const preload=patched.get('dist/preload.js').toString();
  for(const channel of ['wcv:set-bounds-sync','webview:device-chooser-response',
    'webview:prompt-response','webview:resolve-download','webview:execute-js-frame-isolated-world',
    'script-jail:warm','mic:request-access']) assert.ok(preload.includes(channel),channel+' missing');
  assert.match(preload,/scriptJailProtocol: 2/);
  assert.match(preload,/installCustomFlagOverridesSync\(\);/);
  const main=patched.get('dist/main.js').toString();
  assert.match(main,/\(0, browserEngine_1\.closeAllBrowserSessions\)\(\)\.catch/);
  assert.match(main,/includeFutureSessions: false/);
  assert.match(main,/\(0, paths_1\.tightenActivePortPermissions\)/);
  assert.match(main,/resolveWcvTab: \(id\) => wcvBrowserRegistry_1\.wcvBrowserRegistry\.resolveDeviceChooserHost\(id\)/);
  assert.match(main,/title: '确认退出'/);
  assert.match(patched.get('dist/ipcHandlers.js').toString(),/guestFileChooserController_1\.guestFileChooserController\.setInputFiles/);
  assert.deepEqual(patched.get('dist/ideInstall/wizardPreload.js'),files.get('dist/ideInstall/wizardPreload.js'));
});

test('2.31 refuses changed quit behavior even when all translation anchors remain',()=>{
  const source=files.get('dist/main.js').toString();
  const changed=source.replace('(0, browserEngine_1.closeAllBrowserSessions)()','Promise.resolve()');
  assert.notEqual(changed,source);
  const input=new Map(files);
  input.set('dist/main.js',Buffer.from(changed));
  assert.equal(patchNativeFiles(input,'/* translator */').changes.length,9);
  assert.equal(nativeCompatibility(input).compatible,false);
});

test('2.31 Chinese menu keeps Hub zoom actions and the hidden Ctrl+= alias',()=>{
  const {files:patched}=patchNativeFiles(files,'/* translator */');
  const calls=[];
  class MenuItem { constructor(options) { Object.assign(this,options); } }
  const submenu=items=>({items,insert(index,item) { this.items.splice(index,0,item); }});
  const menu={items:[
    {label:'File',submenu:submenu([])},
    {label:'View',submenu:submenu([
      {label:'Actual Size',role:'resetzoom'},
      {label:'Zoom In',role:'zoomin'},
      {label:'Zoom Out',role:'zoomout'},
    ])},
    {label:'Help',submenu:submenu([])},
  ]};
  const modules={
    electron:{MenuItem,Menu:{getApplicationMenu:()=>menu,setApplicationMenu:value=>assert.equal(value,menu)},
      shell:{openExternal:async()=>{}}},
    path:{},
    './utils':{isMacOS:()=>false,createWindow:()=>{}},
    './updater':{updateActions:{}},
    './wsl':{isWslAvailable:()=>false},
    './debugConsole':{configureDebugConsoleMenu:()=>{}},
    './hubZoom':{applyHubWindowZoom:async(win,action)=>calls.push({win,action}),resolveTargetHubWindow:win=>win},
  };
  const context={exports:{},require:name=>{
    assert.ok(Object.hasOwn(modules,name),name);
    return modules[name];
  },console};
  vm.runInNewContext(patched.get('dist/menu.js').toString(),context);
  context.exports.setupApplicationMenu('https://127.0.0.1:1234/');
  const view=menu.items.find(item=>item.label==='视图');
  assert.ok(view);
  assert.deepEqual(view.submenu.items.map(item=>item.label),['实际大小','放大','放大','缩小']);
  const hiddenAlias=view.submenu.items.find(item=>item.id==='hub-zoom-in-equal');
  assert.equal(hiddenAlias.visible,false);
  assert.equal(hiddenAlias.acceleratorWorksWhenHidden,true);
  assert.equal(hiddenAlias.accelerator,'CmdOrCtrl+=');
  const win={id:'hub-window'};
  for(const item of view.submenu.items) item.click(item,win);
  assert.deepEqual(calls,[{win,action:'reset'},{win,action:'in'},{win,action:'in'},{win,action:'out'}]);
  assert.equal(menu.items.find(item=>item.label==='文件').submenu.items[0].label,'新建窗口');
  assert.equal(menu.items.find(item=>item.label==='帮助').submenu.items[0].label,'使用文档');
});
