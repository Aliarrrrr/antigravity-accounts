import {testDataRoot} from './fixture-paths.mjs';
import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import {createPackage} from '@electron/asar';
import {TOOL_ROOT,buildInstallation} from '../src/toolkit.mjs';
import {WHITELIST} from '../src/native-patches.mjs';

async function fixture(t) {
  const temporary=await fs.realpath(os.tmpdir());
  const root=await fs.mkdtemp(path.join(temporary,'agy-build-race-'));
  t.after(async()=>{
    const resolved=await fs.realpath(root);
    assert.equal(path.dirname(resolved),temporary);
    assert.ok(path.basename(resolved).startsWith('agy-build-race-'));
    await fs.rm(resolved,{recursive:true,force:true});
  });
  const install=path.join(root,'install'),source=path.join(root,'source');
  for(const name of WHITELIST) {
    await fs.mkdir(path.dirname(path.join(source,name)),{recursive:true});
    await fs.copyFile(path.join(testDataRoot,'verification','repair-2026-11-04','current',name),path.join(source,name));
  }
  await fs.writeFile(path.join(source,'package.json'),JSON.stringify({name:'antigravity',version:'2.29.0',main:'dist/main.js'}));
  await fs.mkdir(path.join(install,'resources'),{recursive:true});
  await fs.writeFile(path.join(install,'Antigravity.exe'),'not executed');
  const archive=path.join(install,'resources','app.asar');
  await createPackage(source,archive);
  const original=await fs.readFile(archive);
  await fs.mkdir(path.join(source,'dist','hostauth-addon'),{recursive:true});
  await fs.writeFile(path.join(source,'dist','hostauth-addon','runtime.cjs'),'module.exports = {};');
  const replacementPath=path.join(root,'replacement.asar');
  await createPackage(source,replacementPath);
  const replacement=await fs.readFile(replacementPath);
  const brokenSource=path.join(root,'broken-source');
  await fs.mkdir(brokenSource);
  await fs.writeFile(path.join(brokenSource,'package.json'),'{}');
  const brokenPath=path.join(root,'broken.asar');
  await createPackage(brokenSource,brokenPath);
  return {root,install,archive,original,replacement,broken:await fs.readFile(brokenPath),outputRoot:path.join(root,'artifacts')};
}

test('source replaced after extraction is retried before a candidate is packaged',async t=>{
  const f=await fixture(t),write=fs.writeFile;
  let replaced=false;
  fs.writeFile=async(file,...args)=>{
    const result=await write(file,...args);
    if(!replaced && String(file).endsWith(path.join('extracted','dist','preload.js'))) {
      replaced=true;await write(f.archive,f.replacement);
    }
    return result;
  };
  try {
    await assert.rejects(buildInstallation(f.install,{outputRoot:f.outputRoot,compatibleSource:true}),/^Error: installation_changed$/);
  } finally {fs.writeFile=write;}
  assert.equal(replaced,true);
  assert.deepEqual(await fs.readFile(f.archive),f.replacement);
  const [artifact]=await fs.readdir(f.outputRoot);
  await assert.rejects(fs.access(path.join(f.outputRoot,artifact,'app.asar')),{code:'ENOENT'});
  await assert.rejects(fs.access(path.join(f.outputRoot,artifact,'manifest.json')),{code:'ENOENT'});
});

test('source replaced while finishing a candidate remains an installation change',async t=>{
  const f=await fixture(t),write=fs.writeFile;
  let replaced=false;
  fs.writeFile=async(file,...args)=>{
    const result=await write(file,...args);
    if(!replaced && path.basename(String(file))==='app.asar' && String(file).startsWith(f.outputRoot+path.sep)) {
      replaced=true;await write(f.archive,f.replacement);
    }
    return result;
  };
  try {
    await assert.rejects(buildInstallation(f.install,{outputRoot:f.outputRoot,compatibleSource:true}),/^Error: installation_changed$/);
  } finally {fs.writeFile=write;}
  assert.equal(replaced,true);
  assert.deepEqual(await fs.readFile(f.archive),f.replacement);
  const [artifact]=await fs.readdir(f.outputRoot);
  await assert.rejects(fs.access(path.join(f.outputRoot,artifact,'manifest.json')),{code:'ENOENT'});
});

test('a candidate missing entries is still fatal when its source did not change',async t=>{
  const f=await fixture(t),write=fs.writeFile;
  let corrupted=false;
  fs.writeFile=async(file,...args)=>{
    const result=await write(file,...args);
    if(!corrupted && path.basename(String(file))==='app.asar' && String(file).startsWith(f.outputRoot+path.sep)) {
      corrupted=true;await write(file,f.broken);
    }
    return result;
  };
  try {
    await assert.rejects(buildInstallation(f.install,{outputRoot:f.outputRoot,compatibleSource:true}),/^Error: archive_entry_set_changed$/);
  } finally {fs.writeFile=write;}
  assert.equal(corrupted,true);
  assert.deepEqual(await fs.readFile(f.archive),f.original);
  const [artifact]=await fs.readdir(f.outputRoot);
  await assert.rejects(fs.access(path.join(f.outputRoot,artifact,'manifest.json')),{code:'ENOENT'});
});
