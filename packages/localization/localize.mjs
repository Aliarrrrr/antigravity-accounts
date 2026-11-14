#!/usr/bin/env node
import path from 'node:path';
import {inspectInstallation,buildInstallation,applyArtifact,restoreInstallation,TOOL_ROOT} from './src/toolkit.mjs';

const argv=process.argv.slice(2), command=argv.shift();
const options={};
for(let i=0;i<argv.length;i+=2){
  if(!['--install-root','--artifact','--output-root','--backup-root','--compatible-source'].includes(argv[i]) || !argv[i+1]){
    console.error('Invalid argument. Usage: node localize.mjs inspect|build|apply|restore [--install-root PATH] [--artifact PATH]'); process.exit(2);
  }
  options[argv[i]]=argv[i+1];
}
if(options['--compatible-source'] && !['true','false'].includes(options['--compatible-source'])) throw new Error('compatible-source must be true or false');
const compatibleSource=options['--compatible-source']==='true';
const installRoot=options['--install-root'] || path.join(process.env.LOCALAPPDATA || '', 'Programs','antigravity');
try {
  let result;
  switch(command){
    case 'inspect': result=await inspectInstallation(installRoot); break;
    case 'build': result=await buildInstallation(installRoot,{outputRoot:options['--output-root'] || path.join(TOOL_ROOT,'artifacts'),compatibleSource}); break;
    case 'apply': if(!options['--artifact']) throw new Error('artifact_required'); result=await applyArtifact(installRoot,options['--artifact'],{backupRoot:options['--backup-root'] || path.join(TOOL_ROOT,'backups'),compatibleSource}); break;
    case 'restore': result=await restoreInstallation(installRoot,{backupRoot:options['--backup-root'] || path.join(TOOL_ROOT,'backups'),compatibleSource}); break;
    default: throw new Error('Usage: node localize.mjs inspect|build|apply|restore [--install-root PATH] [--artifact PATH]');
  }
  console.log(JSON.stringify(result,null,2));
}catch(e){console.error(JSON.stringify({ok:false,error:e.message}));process.exitCode=1;}
