import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import {spawn} from 'node:child_process';
import {createInterface} from 'node:readline';

const windows=process.platform==='win32';
const moduleUrl=new URL('../src/process-lock.mjs',import.meta.url).href;
let lockModule={};
try { lockModule=await import(moduleUrl); } catch(error) { if(error.code!=='ERR_MODULE_NOT_FOUND') throw error; }

async function fixture(t) {
  assert.equal(typeof lockModule.acquireProcessLock,'function','process lock API missing');
  const stateRoot=await fs.mkdtemp(path.join(os.tmpdir(),'agy-process-lock-'));
  t.after(()=>fs.rm(stateRoot,{recursive:true,force:true}));
  return stateRoot;
}

function peer(t,stateRoot) {
  const source=`
    const {acquireProcessLock}=await import(process.argv[1]);
    const release=await acquireProcessLock(process.argv[2],'repair.lock');
    console.log(release?'acquired':'busy');
    if(release) {
      await new Promise(resolve=>process.stdin.once('data',resolve));
      await release();
    }
  `;
  const child=spawn(process.execPath,['--input-type=module','-e',source,moduleUrl,stateRoot],{stdio:['pipe','pipe','pipe'],windowsHide:true});
  t.after(()=>{if(child.exitCode===null) child.kill();});
  let stderr='';
  child.stderr.on('data',bytes=>{stderr+=bytes;});
  const closed=new Promise(resolve=>child.once('close',(code,signal)=>resolve({code,signal})));
  const lines=createInterface({input:child.stdout});
  const outcome=new Promise((resolve,reject)=>{
    const timeout=setTimeout(()=>reject(new Error('peer handshake timeout: '+stderr)),15000);
    lines.once('line',line=>{clearTimeout(timeout);resolve(line);});
    child.once('error',error=>{clearTimeout(timeout);reject(error);});
    child.once('close',()=>{clearTimeout(timeout);reject(new Error('peer closed before handshake: '+stderr));});
  });
  return {child,outcome,closed};
}

test('process mutex excludes another Node process until release',{skip:!windows},async t=>{
  const stateRoot=await fixture(t);
  const release=await lockModule.acquireProcessLock(stateRoot,'repair.lock');
  assert.equal(typeof release,'function');
  t.after(release);
  const blocked=peer(t,stateRoot);
  assert.equal(await blocked.outcome,'busy');
  assert.equal((await blocked.closed).code,0);
  await release();
  const next=peer(t,stateRoot);
  assert.equal(await next.outcome,'acquired');
  next.child.stdin.end('release\n');
  assert.equal((await next.closed).code,0);
});

test('concurrent acquisitions ignore stale disk ownership without sharing the mutex',{skip:!windows},async t=>{
  const stateRoot=await fixture(t);
  await fs.writeFile(path.join(stateRoot,'repair.lock'),JSON.stringify({pid:process.pid,createdUtc:'2000-01-01T00:00:00Z'}));
  const releases=await Promise.all(Array.from({length:4},()=>lockModule.acquireProcessLock(stateRoot,'repair.lock')));
  for(const release of releases) if(release) t.after(release);
  assert.equal(releases.filter(Boolean).length,1);
  await releases.find(Boolean)();
  const retry=await lockModule.acquireProcessLock(stateRoot,'repair.lock');
  assert.equal(typeof retry,'function');
  await retry();
});

test('killing the owning Node process releases its helper mutex',{skip:!windows},async t=>{
  const stateRoot=await fixture(t),owner=peer(t,stateRoot);
  assert.equal(await owner.outcome,'acquired');
  owner.child.kill();
  await owner.closed;
  let release;
  const deadline=Date.now()+10000;
  while(Date.now()<deadline && !release) {
    release=await lockModule.acquireProcessLock(stateRoot,'repair.lock');
    if(!release) await new Promise(resolve=>setTimeout(resolve,100));
  }
  assert.equal(typeof release,'function','orphan helper did not release after stdin EOF');
  await release();
});

test('release preserves diagnostic ownership replaced by another token',{skip:!windows},async t=>{
  const stateRoot=await fixture(t),release=await lockModule.acquireProcessLock(stateRoot,'repair.lock');
  t.after(release);
  const file=path.join(stateRoot,'repair.lock'),bytes=JSON.stringify({token:'successor'});
  await fs.writeFile(file,bytes);
  await release();
  assert.equal(await fs.readFile(file,'utf8'),bytes);
});

test('unexpected helper termination loses ownership and preserves the next holder',{skip:!windows},async t=>{
  const stateRoot=await fixture(t),release=await lockModule.acquireProcessLock(stateRoot,'repair.lock');
  t.after(()=>release().catch(()=>{}));
  assert.equal(typeof release.assertHeld,'function','lock ownership assertion missing');
  release.assertHeld();
  const file=path.join(stateRoot,'repair.lock');
  const diagnostic=JSON.parse(await fs.readFile(file,'utf8'));
  process.kill(diagnostic.helperPid);
  let lost=false;
  const deadline=Date.now()+10000;
  while(Date.now()<deadline && !lost) {
    try { release.assertHeld(); }
    catch(error) { assert.match(error.message,/mutex_ownership_lost/); lost=true; }
    if(!lost) await new Promise(resolve=>setTimeout(resolve,20));
  }
  assert.equal(lost,true,'helper death was not detected');
  assert.throws(()=>release.assertHeld(),/mutex_ownership_lost/);
  const next=await lockModule.acquireProcessLock(stateRoot,'repair.lock');
  assert.equal(typeof next,'function','abandoned helper mutex was not recoverable');
  t.after(next);
  const successor=await fs.readFile(file,'utf8');
  await assert.rejects(release(),/mutex_helper_failed/);
  assert.equal(await fs.readFile(file,'utf8'),successor);
  next.assertHeld();
  await next();
});
