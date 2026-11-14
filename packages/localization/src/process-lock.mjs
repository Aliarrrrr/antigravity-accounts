import fs from 'node:fs/promises';
import path from 'node:path';
import crypto from 'node:crypto';
import {spawn} from 'node:child_process';
import {fileURLToPath} from 'node:url';

const helper=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'../scripts/Hold-Mutex.ps1');

export async function acquireProcessLock(stateRoot,name) {
  return acquireKernelLock(stateRoot,name,false);
}

export async function acquireAccountsInstallationLock(resourcesRoot) {
  return acquireKernelLock(resourcesRoot,'installation',true);
}

async function acquireKernelLock(stateRoot,name,accounts) {
  if(process.platform!=='win32') throw new Error('windows_required');
  if(typeof name!=='string' || !/^[A-Za-z0-9._-]+$/.test(name)) throw new Error('invalid_lock_name');
  await fs.mkdir(stateRoot,{recursive:true});
  stateRoot=await fs.realpath(path.resolve(stateRoot));
  const identity=(accounts?stateRoot:stateRoot+'\0'+name).toLowerCase();
  const mutexName=(accounts?'Local\\AntigravityAccounts-Install-':'Local\\AntigravityZhCN-')+crypto.createHash('sha256').update(identity).digest('hex');
  const powershell=path.join(process.env.SystemRoot||'C:\\Windows','System32','WindowsPowerShell','v1.0','powershell.exe');
  const child=spawn(powershell,['-NoLogo','-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',helper,'-MutexName',mutexName],{stdio:['pipe','pipe','pipe'],windowsHide:true});
  child.stdin.on('error',()=>{});
  let stderr='',exited=false,helperError;
  child.stderr.on('data',bytes=>{stderr=(stderr+bytes).slice(-65536);});
  const closed=new Promise(resolve=>{
    child.once('error',error=>{helperError=error;resolve({error});});
    child.once('close',(code,signal)=>{exited=true;resolve({code,signal});});
  });
  const handshake=new Promise((resolve,reject)=>{
    let output='';
    const timeout=setTimeout(()=>{child.kill();reject(new Error('mutex_helper_timeout'));},15000);
    child.stdout.on('data',bytes=>{
      output+=bytes;
      const newline=output.indexOf('\n');
      if(newline===-1) return;
      clearTimeout(timeout);
      const answer=output.slice(0,newline).trim();
      if(answer==='acquired' || answer==='busy') resolve(answer);
      else reject(new Error('mutex_helper_invalid_response: '+answer));
    });
    child.once('error',error=>{clearTimeout(timeout);reject(error);});
    child.once('close',()=>{clearTimeout(timeout);reject(new Error('mutex_helper_failed: '+stderr.trim()));});
  });
  const stopHelper=async()=>{
    if(!exited) child.stdin.end('release\n');
    const result=await closed;
    if(result.error) throw result.error;
    if(result.code!==0) throw new Error('mutex_helper_failed: '+(stderr.trim()||result.signal||result.code));
  };
  const assertHeld=()=>{
    if(exited || helperError || child.exitCode!==null || child.signalCode!==null || child.stdin.destroyed || child.stdin.writableEnded) throw new Error('mutex_ownership_lost');
  };
  let answer;
  try { answer=await handshake; }
  catch(error) { child.stdin.destroy(); await closed; throw error; }
  if(answer==='busy') { await stopHelper(); return null; }

  if(accounts) {
    let releasing;
    const release=()=>releasing??=stopHelper();
    release.assertHeld=assertHeld;
    return release;
  }

  // Disk state is diagnostic only. The kernel mutex remains the authority even if
  // a previous process left a stale file or its PID now belongs to another app.
  const token=crypto.randomUUID(),file=path.join(stateRoot,name),stage=file+'.'+token+'.tmp';
  try {
    await fs.writeFile(stage,JSON.stringify({pid:process.pid,helperPid:child.pid,token,createdUtc:new Date().toISOString()})+'\n',{flag:'wx'});
    await fs.rename(stage,file);
    assertHeld();
  } catch(error) {
    try { await fs.rm(stage,{force:true}); }
    finally { await stopHelper(); }
    throw error;
  }
  let releasing;
  const release=()=>releasing??=(async()=>{
    try {
      let record;
      try { record=JSON.parse(await fs.readFile(file,'utf8')); }
      catch(error) { if(error.code!=='ENOENT' && !(error instanceof SyntaxError)) throw error; }
      if(!exited && record?.token===token) await fs.rm(file,{force:true});
    } finally { await stopHelper(); }
  })();
  release.assertHeld=assertHeld;
  return release;
}
