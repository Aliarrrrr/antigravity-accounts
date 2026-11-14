#!/usr/bin/env node
import path from 'node:path';
import {execFile} from 'node:child_process';
import {promisify} from 'node:util';
import {fileURLToPath} from 'node:url';
import {ensureLocalized,launchLocalized,watchInstallation,DEFAULT_STATE_ROOT} from './src/durable.mjs';
const argv=process.argv.slice(2),command=argv.shift();
const options={}; let args=[];
for(let i=0;i<argv.length;i++) {
  if(argv[i]==='--') { args=argv.slice(i+1); break; }
  if(!['--install-root','--state-root','--args-base64'].includes(argv[i]) || !argv[i+1]) throw new Error('invalid_argument');
  options[argv[i]]=argv[++i];
}
const installRoot=path.resolve(options['--install-root']||path.join(process.env.LOCALAPPDATA||'','Programs','antigravity'));
const settings={stateRoot:options['--state-root']||DEFAULT_STATE_ROOT};
try {
  if(options['--args-base64']) args=JSON.parse(Buffer.from(options['--args-base64'],'base64').toString('utf8'));
  let result;
  if(command==='ensure') result=await ensureLocalized(installRoot,settings);
  else if(command==='launch') result=await launchLocalized(installRoot,settings,args);
  else if(command==='watch') {
    const toolkitRoot=path.dirname(fileURLToPath(import.meta.url));
    const onInstallationChanged=()=>promisify(execFile)('powershell.exe',['-NoProfile','-File',path.join(toolkitRoot,'scripts','Install-Persistence.ps1'),'-InstallRoot',installRoot,'-ToolkitRoot',toolkitRoot,'-NodePath',process.execPath,'-StateRoot',settings.stateRoot,'-Reconcile'],{windowsHide:true,maxBuffer:1024*1024});
    result=await watchInstallation(installRoot,{...settings,onInstallationChanged});
  }
  else throw new Error('Usage: node durable.mjs ensure|launch|watch [--install-root PATH] [--state-root PATH] [--args-base64 JSON_BASE64] [-- ARGS]');
  console.log(JSON.stringify(result));
  if(command==='ensure'&&!['ready','repaired'].includes(result.status)) process.exitCode=3;
} catch(e) { console.error(JSON.stringify({ok:false,error:e.message})); process.exitCode=e.launchBlocked?4:1; }
