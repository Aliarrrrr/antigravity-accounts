import {testDataRoot} from './fixture-paths.mjs';
import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
import {extractFile} from '@electron/asar';
import {WHITELIST,patchNativeFiles} from '../src/native-patches.mjs';
import {nativeCompatibility,nativeScopeProfile} from '../src/native-compatibility.mjs';

const root=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'..');
const original=path.join(testDataRoot,'verification','repair-2026-10-10','baseline','resources','app.asar');
const current=path.join(testDataRoot,'state','backups','2.28.2','0CB80A18EBE174F7EE0C4B619CDCF57B9BF015BD5427FFD60195CF41B7B0FD9D','app.asar');
const readFiles=archive=>new Map(WHITELIST.map(entry=>[entry,extractFile(archive,entry.split('/').join(path.sep))]));
const files=readFiles(original);
const updated=new Map(WHITELIST.map(entry=>[entry,fs.readFileSync(path.join(testDataRoot,'verification','repair-2026-11-04','current',entry))]));
function changed(entry,mutate) {
  const result=new Map(files);
  const before=result.get(entry).toString();
  const after=mutate(before);
  assert.notEqual(after,before,'test mutation did not change its source');
  result.set(entry,Buffer.from(after));
  return result;
}
const accepted=input=>assert.equal(nativeCompatibility(input).compatible,true,JSON.stringify(nativeCompatibility(input)));
const refused=input=>assert.equal(nativeCompatibility(input).compatible,false,JSON.stringify(nativeCompatibility(input)));

test('scope checks recognize the reviewed 2.22, 2.28.2 and 2.29 originals',()=>{
  accepted(files);
  accepted(readFiles(current));
  accepted(updated);
  assert.equal(nativeCompatibility(files).mode,'reviewed-scopes');
});

test('comments and literal quote formatting outside exact anchors are accepted',()=>{
  accepted(changed('dist/main.js',source=>source.replace('"use strict";',"'use strict';\n// New release commentary.")));
  accepted(changed('dist/menu.js',source=>source.replace('function setupApplicationMenu(url) {','function setupApplicationMenu(url) {\n    // Comments do not change this menu function.')));
  accepted(changed('dist/preload.js',source=>source+'\n// No native behavior changed.\n'));
});

test('an unrelated new function or an unrelated existing function body can change',()=>{
  accepted(changed('dist/preload.js',source=>source+'\nfunction newReleaseHelper() { return 42; }\n'));
  accepted(changed('dist/main.js',source=>source.replace('function handleDeepLink(url) {','function handleDeepLink(url) {\n    console.log("New deep-link logging");')));
});

test('new literal and function definitions outside translation dependencies are accepted',()=>{
  accepted(changed('dist/preload.js',source=>source+'\nconst newBrowserVersion = 2;\nconst newBrowserAPI = { status: () => "ready", values: [1, 2] };\n'));
  accepted(changed('dist/main.js',source=>source+'\nconst newUiTheme = { enabled: true, color: /^#[0-9a-f]{6}$/i };\n'));
});

test('definition additions that execute code, access properties or spread values are refused',()=>{
  for(const declaration of [
    'const newApi = electron_1.app.getPath("userData");',
    'const newApi = electron_1.app;',
    'const newApi = { ...electron_1 };',
    'const newApi = { [electron_1.app.getName()]: true };',
  ]) refused(changed('dist/preload.js',source=>source+'\n'+declaration+'\n'));
});

test('a release can combine independently reviewed native files',()=>{
  const mixed=new Map(files);
  for(const entry of ['dist/preload.js','dist/menu.js','dist/ipcHandlers.js','dist/updater.js']) mixed.set(entry,updated.get(entry));
  accepted(mixed);
  assert.equal(nativeCompatibility(mixed).revision,'reviewed-file-combination');
});

test('2.29 keeps every exact translation anchor and preserves the new native APIs',()=>{
  const {files:patched,changes}=patchNativeFiles(updated,'/* translator */');
  assert.equal(changes.length,9);
  assert.match(patched.get('dist/preload.js').toString(),/installHubSurfaceThemeAutoSync\(\);/);
  assert.match(patched.get('dist/ipcHandlers.js').toString(),/webview:get-memory-usage/);
  assert.match(patched.get('dist/menu.js').toString(),/createWindow\)\(url, undefined, \{ cascade: true \}\)/);
  assert.match(patched.get('dist/main.js').toString(),/flushTrackedWindowState/);
});

test('a new unrelated IPC callback can be added outside the translation functions',()=>{
  accepted(changed('dist/ipcHandlers.js',source=>source.replace('function registerIpcHandlers(storageManager) {',"function registerIpcHandlers(storageManager) {\n    electron_1.ipcMain.handle('new-release:status', () => 'ready');")));
});

test('changing menu behavior is refused even with all original patch anchors',()=>{
  const input=changed('dist/menu.js',source=>source.replace('action?.();','if (false) action?.();'));
  assert.equal(patchNativeFiles(input,'/* unchanged anchors */').changes.length,9);
  refused(input);
});

test('a callback moved to another IPC event is refused although its body is unchanged',()=>{
  const input=changed('dist/ipcHandlers.js',source=>source.replace("handle('dialog:open-workspace',", "handle('different:open-workspace',"));
  assert.equal(patchNativeFiles(input,'/* unchanged anchors */').changes.length,9);
  refused(input);
});

test('changing a translation dependency import is refused',()=>{
  const input=changed('dist/menu.js',source=>source.replace('require("./updater")','require("./new-updater")'));
  assert.equal(patchNativeFiles(input,'/* unchanged anchors */').changes.length,9);
  refused(input);
});

test('top-level code overriding injected dependencies is refused',()=>{
  refused(changed('dist/menu.js',source=>source+'\nObject.hasOwn = () => false;\n'));
  refused(changed('dist/menu.js',source=>source+'\nelectron_1.Menu.setApplicationMenu = () => {};\n'));
});

test('deleted and repeated patch anchors are refused',()=>{
  refused(changed('dist/main.js',source=>source.replace("title: 'Confirm Quit',","title: 'Confirm exit',")));
  const repeated=changed('dist/main.js',source=>source+"\n// title: 'Confirm Quit',\n");
  refused(repeated);
  assert.match(nativeCompatibility(repeated).reason,/native_anchor_count/);
});

test('helper names, global shadowing and repeated top-level bindings are refused',()=>{
  const helper=changed('dist/menu.js',source=>source+'\nfunction translateMenu() {}\n');
  refused(helper);
  assert.match(nativeCompatibility(helper).reason,/helper_conflict/);
  refused(changed('dist/preload.js',source=>source+'\nconst window = {};\n'));
  refused(changed('dist/preload.js',source=>source+'\nconst document = {};\n'));
  refused(changed('dist/preload.js',source=>source+'\nconst Promise = {};\n'));
  refused(changed('dist/menu.js',source=>source+'\nconst Object = {};\n'));
  refused(changed('dist/preload.js',source=>source+'\nconst electron_1 = {};\n'));
  refused(changed('dist/menu.js',source=>source+'\nfunction setupApplicationMenu() {}\n'));
});

test('syntax errors in an unrelated function are refused',()=>{
  const input=changed('dist/main.js',source=>source+'\nfunction brokenReleaseHelper( {\n');
  refused(input);
  assert.match(nativeCompatibility(input).reason,/native_syntax_invalid/);
});

test('localized input and missing files are refused',()=>{
  refused(patchNativeFiles(files,'globalThis.__antigravityZhCN = {};').files);
  const missing=new Map(files);
  missing.delete('dist/main.js');
  refused(missing);
});

test('scope extraction and compatibility checks leave source buffers unchanged',()=>{
  const snapshot=new Map([...files].map(([name,bytes])=>[name,Buffer.from(bytes)]));
  nativeScopeProfile(files);
  nativeCompatibility(files);
  assert.deepEqual([...files.keys()],[...snapshot.keys()]);
  for(const [name,bytes] of snapshot) assert.deepEqual(files.get(name),bytes,name+' was changed');
});
