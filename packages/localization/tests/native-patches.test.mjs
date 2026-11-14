import path from 'node:path';
import {testDataRoot} from './fixture-paths.mjs';
import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';
import * as asar from '@electron/asar';

const module = await import('../src/native-patches.mjs').catch(error => {
  if (error.code === 'ERR_MODULE_NOT_FOUND') return {};
  throw error;
});
const archive = (process.env.ANTIGRAVITY_TEST_INSTALL || path.join(testDataRoot,'verification','baseline')) + '/resources/app.asar';
assert.equal(createHash('sha256').update(readFileSync(archive)).digest('hex'),
  '341234faf45bd1776fd5418a3c288dedc5487ebfcf153f53d75de17cfe15c1de', 'native tests require the pinned original ASAR');
const header = asar.getRawHeader(archive).header;
const metadataBefore = JSON.stringify(header);
const original = new Map();
function readEntries(files, prefix = '') {
  for (const [name, info] of Object.entries(files)) {
    const entry = prefix + name;
    if (info.files) readEntries(info.files, entry + '/');
    else original.set(entry, asar.extractFile(archive, entry.replaceAll('/', '\\')));
  }
}
readEntries(header.files);
const allowed = ['dist/preload.js', 'dist/menu.js', 'dist/tray.js', 'dist/ipcHandlers.js',
  'dist/updater.js', 'dist/main.js', 'dist/wsl.js', 'dist/provisionSplash.js',
  'dist/ideInstall/wizardPreload.js', 'dist/ideInstall/wizardHtml.js'];
const changed = allowed.filter(entry => entry !== 'dist/ideInstall/wizardPreload.js');
const payload = 'globalThis.__translatorRan = (globalThis.__translatorRan || 0) + 1;';
const sha = value => createHash('sha256').update(value).digest('hex');
function patch(files = original) {
  assert.equal(typeof module.patchNativeFiles, 'function', 'native patch API must exist');
  return module.patchNativeFiles(files, payload);
}

// Catches omitted patches, unexpected file edits, invalid output and inaccurate manifests.
test('patches current original ASAR entries with exact evidence and preserves unaffected bytes and metadata', () => {
  const result = patch();
  assert.notEqual(result.files, original);
  assert.deepEqual([...result.files.keys()], [...original.keys()]);
  assert.deepEqual([...module.WHITELIST].sort(), [...allowed].sort());
  assert.deepEqual(result.changes.map(change => change.entry).sort(), [...changed].sort());
  for (const [entry, buffer] of original) {
    const after = result.files.get(entry);
    if (!changed.includes(entry)) assert.deepEqual(after, buffer, entry);
    else {
      assert.notDeepEqual(after, buffer, entry);
      new vm.Script(after.toString(), { filename: entry });
      const change = result.changes.find(change => change.entry === entry);
      assert.equal(change.beforeHash, sha(buffer));
      assert.equal(change.afterHash, sha(after));
      let replay = buffer.toString();
      for (const anchor of change.anchors) {
        assert.ok(anchor.old.length > 0);
        const count = replay.split(anchor.old).length - 1;
        assert.equal(anchor.count, count, entry);
        assert.equal(anchor.expected, count, entry);
        replay = replay.split(anchor.old).join(anchor.replacement);
      }
      assert.equal(replay, after.toString(), entry);
    }
    assert.equal(sha(buffer), sha(asar.extractFile(archive, entry.replaceAll('/', '\\'))), 'input buffers stay unchanged');
  }
  assert.equal(JSON.stringify(asar.getRawHeader(archive).header), metadataBefore);
});

// Catches best-effort fallback when a version or anchor differs.
test('rejects missing entries, changed anchors and unexpected duplicate anchors before producing output', () => {
  const missing = new Map(original);
  missing.delete('dist/main.js');
  assert.throws(() => patch(missing), /dist\/main\.js.*missing/i);
  const altered = new Map(original);
  altered.set('dist/main.js', Buffer.from(original.get('dist/main.js').toString().replace("title: 'Confirm Quit',", "title: 'Confirm exit',")));
  assert.throws(() => patch(altered), /anchor.*dist\/main\.js.*expected 1.*found 0/i);
  const duplicated = new Map(original);
  duplicated.set('dist/main.js', Buffer.concat([original.get('dist/main.js'), Buffer.from("\n// title: 'Confirm Quit',")]));
  assert.throws(() => patch(duplicated), /anchor.*dist\/main\.js.*expected 1.*found 2/i);
});

// Catches repeat append or silent upgrade of an already localized archive.
test('refuses repeat injection and empty runtime payloads', () => {
  const first = patch();
  assert.throws(() => patch(first.files), /already localized|already injected/i);
  assert.throws(() => module.patchNativeFiles(original, ''), /payload/i);
});

// Catches running the payload before DOM readiness, twice, or losing existing contextBridge APIs.
test('main preload preserves bridge APIs and executes payload once after DOMContentLoaded', async () => {
  const source = patch().files.get('dist/preload.js').toString();
  const bridges = new Map();
  let onReady;
  const sandbox = {
    exports: {}, document: { readyState: 'loading', addEventListener(name, callback) { assert.equal(name, 'DOMContentLoaded'); onReady = callback; } },
    window: {}, require(name) {
      assert.equal(name, 'electron');
      return { contextBridge: { exposeInMainWorld(name, api) { bridges.set(name, api); } },
        webFrame: { executeJavaScript(code) { vm.runInContext(code, context); return Promise.resolve(); } },
        ipcRenderer: {} };
    },
  };
  sandbox.window.top = sandbox.window;
  const context = vm.createContext(sandbox);
  vm.runInContext(source, context);
  assert.equal(sandbox.__translatorRan, undefined);
  assert.deepEqual([...bridges.keys()], ['electronUpdater','dialog','nativeNotifications','nativeStorage','logs','extensions','deepLink','agent','electronNative','ide','wsl']);
  assert.equal(typeof bridges.get('electronNative').showContextMenu, 'function');
  onReady(); onReady();
  await Promise.resolve();
  assert.equal(sandbox.__translatorRan, 1);
});

test('optional translation failures leave subsequent original preload code running', async () => {
  const source = patch().files.get('dist/preload.js').toString()+'\nglobalThis.__originalAfterTranslation = true;\n';
  for(const [label,webFrame] of [
    ['missing API',undefined],
    ['synchronous API error',{executeJavaScript(){throw new Error('API changed');}}],
    ['asynchronous API error',{executeJavaScript(){return Promise.reject(new Error('renderer unavailable'));}}],
    ['non-promise API result',{executeJavaScript(){return undefined;}}],
  ]) {
    for(const readyState of ['loading','complete']) {
      const bridges=new Map();let onReady;
      const sandbox={exports:{},document:{readyState,addEventListener(_name,callback){onReady=callback;}},window:{},require(){return {
        contextBridge:{exposeInMainWorld(name,api){bridges.set(name,api);}},webFrame,ipcRenderer:{},
      };}};
      sandbox.window.top=sandbox.window;
      assert.doesNotThrow(()=>vm.runInNewContext(source,sandbox),label+' '+readyState);
      if(onReady) assert.doesNotThrow(()=>onReady(),label+' deferred');
      await new Promise(resolve=>setImmediate(resolve));
      assert.equal(sandbox.__originalAfterTranslation,true,label+' '+readyState);
      assert.equal(typeof bridges.get('wsl').getState,'function');
    }
  }
});

test('translation isolation does not suppress failures in original bridge setup',()=>{
  const source=patch().files.get('dist/preload.js').toString();
  const sandbox={exports:{},require(){return {contextBridge:{exposeInMainWorld(){throw new Error('original bridge failure');}},ipcRenderer:{}};}};
  assert.throws(()=>vm.runInNewContext(source,sandbox),/original bridge failure/);
});

// Catches loss of translated submenu lookup, asynchronous WSL labels or user distro names.
test('native menu traversal retains WSL distro names and update click actions after labels translate', async () => {
  const sources = patch().files;
  let updateClicked = 0;
  const updater = { MenuUpdateStep: { CheckForUpdates: 'Check for Updates' }, updateActions: { 'Check for Updates': () => updateClicked++ } };
  const submenu = () => ({ items: [], insert(position, item) { this.items.splice(position, 0, item); } });
  const menu = { items: [{ label: 'File', submenu: submenu() }, { label: 'Help', submenu: submenu() }] };
  const setMenus = [];
  const electron = { Menu: { getApplicationMenu: () => menu, setApplicationMenu(value) { setMenus.push(value); } }, MenuItem: class { constructor(value) { Object.assign(this, value); } } };
  const sandbox = { exports: {}, process: { argv: [] }, console, require(name) {
    if (name === 'electron') return electron;
    if (name === 'path') return {};
    if (name === './utils') return { isMacOS: () => true };
    if (name === './updater') return updater;
    if (name === './wsl') return { isWslAvailable: () => true, listWslDistros: async () => [{name:'File'}, {name:'Ubuntu'}], getActiveWslDistro: () => 'File' };
    throw new Error(name);
  } };
  vm.runInNewContext(sources.get('dist/menu.js').toString(), sandbox);
  sandbox.exports.setupApplicationMenu('https://127.0.0.1:1234');
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(menu.items[0].label, '文件');
  const fileItems = menu.items[0].submenu.items;
  assert.deepEqual(fileItems.map(item => item.label), ['新建窗口', '连接到 WSL', '本地重新打开', '检查更新']);
  assert.deepEqual(Array.from(fileItems[1].submenu, item => item.label), ['File', 'Ubuntu']);
  fileItems.find(item => item.id === 'check-for-updates').click({label:'检查更新'});
  assert.equal(updateClicked, 1);
  assert.equal(setMenus.length, 2);
});
