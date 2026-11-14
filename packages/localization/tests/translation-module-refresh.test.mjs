import {testDataRoot} from './fixture-paths.mjs';
import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import {pathToFileURL} from 'node:url';
import {extractFile,createPackage} from '@electron/asar';
import {TOOL_ROOT} from '../src/toolkit.mjs';
import {WHITELIST} from '../src/native-patches.mjs';

test('a long-lived builder reloads translation source before recording its new fingerprint',async t=>{
  const root=await fs.mkdtemp(path.join(os.tmpdir(),'agy-module-refresh-'));
  t.after(async()=>{
    assert.equal(await fs.realpath(root),root,'temporary directory changed before cleanup');
    await fs.rm(root,{recursive:true,force:true});
  });
  const toolkitRoot=path.join(root,'toolkit'),install=path.join(root,'install');
  for(const name of ['src','dictionaries']) await fs.mkdir(path.join(toolkitRoot,name),{recursive:true});
  for(const name of ['toolkit.mjs','translator.mjs','native-patches.mjs','native-compatibility.mjs','native-scope-signatures.json','process-lock.mjs']) {
    await fs.copyFile(path.join(TOOL_ROOT,'src',name),path.join(toolkitRoot,'src',name));
  }
  await fs.copyFile(path.join(TOOL_ROOT,'dictionaries','zh-CN.json'),path.join(toolkitRoot,'dictionaries','zh-CN.json'));
  await fs.symlink(path.join(TOOL_ROOT,'node_modules'),path.join(toolkitRoot,'node_modules'),'junction');
  await fs.cp(path.join(testDataRoot,'verification','repair-2026-10-10','baseline'),install,{recursive:true});
  const toolkit=await import(pathToFileURL(path.join(toolkitRoot,'src','toolkit.mjs')).href);
  const outputRoot=path.join(root,'artifacts');
  const first=await toolkit.buildInstallation(install,{outputRoot,compatibleSource:true});
  const translator=path.join(toolkitRoot,'src','translator.mjs');
  await fs.writeFile(translator,(await fs.readFile(translator,'utf8')).replace('const key = \'__antigravityZhCN\';','const key = \'__antigravityZhCN\'; /* refreshed-source */'));
  const second=await toolkit.buildInstallation(install,{outputRoot,compatibleSource:true});
  assert.notEqual(second.manifest.localizationFingerprint,first.manifest.localizationFingerprint);
  assert.notEqual(second.manifest.payloadSha256,first.manifest.payloadSha256);
  const preload=extractFile(path.join(second.artifactDir,'app.asar'),path.join('dist','preload.js')).toString();
  assert.ok(preload.includes('refreshed-source'),'new fingerprint was paired with a cached old translator');
});

test('a long-lived installation checker reloads reviewed profiles and compatibility rules',async t=>{
  const root=await fs.mkdtemp(path.join(os.tmpdir(),'agy-compatibility-refresh-'));
  t.after(async()=>{
    assert.equal(await fs.realpath(root),root);
    await fs.rm(root,{recursive:true,force:true});
  });
  const toolkitRoot=path.join(root,'toolkit'),install=path.join(root,'install'),source=path.join(root,'source');
  await fs.mkdir(path.join(toolkitRoot,'src'),{recursive:true});
  for(const name of ['toolkit.mjs','native-patches.mjs','native-compatibility.mjs','native-scope-signatures.json','process-lock.mjs']) {
    await fs.copyFile(path.join(TOOL_ROOT,'src',name),path.join(toolkitRoot,'src',name));
  }
  await fs.symlink(path.join(TOOL_ROOT,'node_modules'),path.join(toolkitRoot,'node_modules'),'junction');
  for(const name of WHITELIST) {
    await fs.mkdir(path.dirname(path.join(source,name)),{recursive:true});
    await fs.copyFile(path.join(testDataRoot,'verification','repair-2026-11-04','current',name),path.join(source,name));
  }
  await fs.writeFile(path.join(source,'package.json'),JSON.stringify({name:'antigravity',main:'dist/main.js',version:'2.29.0'}));
  await fs.mkdir(path.join(install,'resources'),{recursive:true});
  await fs.writeFile(path.join(install,'Antigravity.exe'),'not executed in inspection');
  await createPackage(source,path.join(install,'resources','app.asar'));
  const toolkit=await import(pathToFileURL(path.join(toolkitRoot,'src','toolkit.mjs')).href);
  const inspect=async()=> (await toolkit.inspectInstallation(install)).compatibleNativeSource;
  assert.equal(await inspect(),true);
  const profilesFile=path.join(toolkitRoot,'src','native-scope-signatures.json');
  const profiles=await fs.readFile(profilesFile);
  await fs.writeFile(profilesFile,'[]');
  assert.equal(await inspect(),false,'removed review was cached');
  await fs.writeFile(profilesFile,profiles);
  assert.equal(await inspect(),true);
  const rulesFile=path.join(toolkitRoot,'src','native-compatibility.mjs');
  const rules=await fs.readFile(rulesFile,'utf8');
  const signature='export function nativeCompatibility(files,{profiles=revisions,operations}={}) {';
  assert.ok(rules.includes(signature));
  await fs.writeFile(rulesFile,rules.replace(signature,signature+'\n return {compatible:false,mode:"refresh-test"};'));
  assert.equal(await inspect(),false,'changed compatibility module was cached');
  await fs.writeFile(rulesFile,rules);
  assert.equal(await inspect(),true);
});
