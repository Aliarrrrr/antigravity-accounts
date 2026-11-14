import fs from 'node:fs/promises';
import path from 'node:path';
import {pathToFileURL,fileURLToPath} from 'node:url';
import {inspectInstallation,prepareInstallation,applyInstallation,ensureInstallation}from './coordinator.mjs';

export async function runCommand(argv){
  const command=argv.shift(),options={},names={'--install-root':'installRoot','--state-root':'stateRoot','--accounts-state-root':'accountsStateRoot','--localization-state-root':'localizationStateRoot','--bundle-root':'bundleRoot','--node-path':'nodePath','--toolkit-root':'toolkitRoot'};let args=[];
  for(let i=0;i<argv.length;i++){if(argv[i]==='--'){args=argv.slice(i+1);break;}const key=names[argv[i]];if(!key||!argv[i+1]||options[key]!==undefined)throw Error('compatibility_argument_invalid');options[key]=argv[++i];}
  options.bundleRoot ||= path.resolve(path.dirname(fileURLToPath(import.meta.url)),'..');
  const configPath=options.stateRoot&&path.join(options.stateRoot,'bootstrap.json');
  if(configPath){try{const config=JSON.parse(await fs.readFile(configPath,'utf8'));if(config.enabledComponents)options.enabledComponents=config.enabledComponents;}catch(e){if(e.code!=='ENOENT')throw e;}}
  if(command==='inspect')return inspectInstallation(options);
  if(command==='prepare')return prepareInstallation(options);
  if(command==='apply')return applyInstallation(options);
  if(command==='ensure')return ensureInstallation(options);
  if(command==='launch'){
    const installed=await ensureInstallation(options);
    if(installed.installationState!=='ready')throw Object.assign(Error('compatibility_launch_'+installed.installationState),{code:'compatibility_launch_'+installed.installationState});
    const durable=await import(pathToFileURL(path.join(options.bundleRoot,'localization/src/durable.mjs')).href);
    let preparedResult={status:'ready',components:installed.components};
    if(installed.components?.accounts?.identityConfirmationRequired){
      const recovery=await import(pathToFileURL(path.join(options.bundleRoot,'localization/src/accounts-recovery.mjs')).href);
      const program=await recovery.inspectAccountsNativeFallback(options.installRoot,{stateRoot:options.localizationStateRoot,accountsStateRoot:options.accountsStateRoot});
      preparedResult={status:'degraded',mode:'native',accounts:'unavailable',program,components:installed.components,reason:installed.components.accounts.reason||'accounts_disabled'};
    }
    return durable.launchLocalized(options.installRoot,{stateRoot:options.localizationStateRoot,accountsStateRoot:options.accountsStateRoot,preparedResult},args);
  }
  throw Error('compatibility_command_invalid');
}

if(process.argv[1]&&path.resolve(process.argv[1])===fileURLToPath(import.meta.url)){
  try{if(process.versions.electron)process.noAsar=true;console.log(JSON.stringify(await runCommand(process.argv.slice(2))));}
  catch(error){console.error(JSON.stringify({phase:'failed',installationState:'failed',runtimeState:'unavailable',activationAllowed:false,error:error.code||error.message,credentialFilesRead:false}));process.exitCode=1;}
}
