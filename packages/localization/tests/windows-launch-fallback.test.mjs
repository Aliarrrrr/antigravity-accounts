import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import os from 'node:os';
import {spawn,spawnSync} from 'node:child_process';
import {createHash} from 'node:crypto';
import {fileURLToPath} from 'node:url';
import {createPackageWithOptions} from '@electron/asar';

const root=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'..');
const powershell=path.join(process.env.SystemRoot||'C:\\Windows','System32','WindowsPowerShell','v1.0','powershell.exe');
const launcher=path.join(root,'scripts/Launch-Localized.ps1');
const hash=bytes=>createHash('sha256').update(bytes).digest('hex').toUpperCase();
function ps(args,env=process.env){return spawnSync(powershell,['-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass',...args],{encoding:'utf8',timeout:30000,env});}
async function waitJson(file){
  for(let n=0;n<100;n++){
    try{return JSON.parse(await fs.readFile(file,'utf8'));}catch(error){if(error.code!=='ENOENT')throw error;await new Promise(resolve=>setTimeout(resolve,30));}
  }
  assert.fail('application did not receive launch arguments');
}
test('Windows bootstrap recovery launches a complete app when localization runtime cannot load',{skip:process.platform!=='win32'},async t=>{
  const inputTemp=await fs.mkdtemp(path.join(os.tmpdir(),'agy launch recovery '));
  // Receipts and production mutexes use fs.realpath, even when TEMP is an 8.3
  // alias. Keep the launch inputs aliased to exercise the PowerShell boundary.
  const temp=await fs.realpath(inputTemp);
  const install=path.join(temp,'app installation'),toolkit=path.join(temp,'tool 工具 kit'),state=path.join(temp,'guard 中文 state');
  const resources=path.join(install,'resources'),source=path.join(temp,'source');
  try{
    for(const dir of [resources,path.join(toolkit,'scripts'),state,path.join(source,'dist')])await fs.mkdir(dir,{recursive:true});
    await fs.copyFile(process.execPath,path.join(install,'Antigravity.exe'));
    await fs.copyFile(launcher,path.join(toolkit,'scripts/Launch-Localized.ps1'));
    const fixtureLauncher=path.join(toolkit,'scripts/Launch-Localized.ps1');
    const probe=path.join(toolkit,'scripts/Maintenance-State.ps1');
    await fs.writeFile(probe,"param([string]$InstallRoot)\n'[]'\n");
    await fs.writeFile(path.join(source,'package.json'),JSON.stringify({name:'antigravity',version:'2.99.0',main:'dist/main.js'}));
    await fs.writeFile(path.join(source,'dist/main.js'),'module.exports = {};\n');
    await fs.writeFile(path.join(source,'dist/preload.js'),'// original preload\n');
    await fs.writeFile(path.join(source,'dist/external.js'),'// unpacked dependency\n');
    await createPackageWithOptions(source,path.join(resources,'app.asar'),{unpack:'**/external.js'});
    const appScript=path.join(temp,'receive args.mjs');
    await fs.writeFile(appScript,"import fs from 'node:fs';fs.writeFileSync(process.argv[2],JSON.stringify(process.argv.slice(3)));\n");
    const received=path.join(temp,'received.json');
    const actualArgs=['', 'a b','中文','literal $value','quote"inside','slash\\end\\','line\nfeed','amp&pipe|','\\"'];
    const launchArgs=[appScript,received,...actualArgs];
    const missingNode=path.join(temp,'missing node.exe');
    const settings=['-File',fixtureLauncher,'-InstallRoot',path.join(inputTemp,'app installation'),'-ToolkitRoot',toolkit,'-StateRoot',path.join(inputTemp,'guard 中文 state'),'-ArgumentsJsonBase64',Buffer.from(JSON.stringify(launchArgs),'utf8').toString('base64'),'-NoPopup'];
    const originals=await Promise.all(['app.asar','app.asar.unpacked/dist/external.js'].map(file=>fs.readFile(path.join(resources,file))));
    const removeReceived=()=>fs.rm(received,{force:true});
    await t.test('missing Node launches original with literal arguments and records degraded mode',async()=>{
      const result=ps([...settings,'-NodePath',missingNode]);assert.equal(result.status,0,result.stderr);
      assert.deepEqual(await waitJson(received),actualArgs);
      const status=await waitJson(path.join(state,'status.json'));assert.equal(status.status,'degraded');assert.equal(status.localization,'unavailable');assert.equal(status.appVersion,'2.99.0');
      assert.equal(status.reason,'guard_runtime_missing');assert.equal(status.launched,true);assert.ok(!JSON.stringify(status).includes('literal $value'));
      for(let i=0;i<2;i++)assert.deepEqual(await fs.readFile(path.join(resources,['app.asar','app.asar.unpacked/dist/external.js'][i])),originals[i]);
      await removeReceived();
    });
    await t.test('missing module import launches original through independent .NET path',async()=>{
      await fs.writeFile(path.join(toolkit,'durable.mjs'),"import 'missing-localization-module';\n");
      const result=ps([...settings,'-NodePath',process.execPath]);assert.equal(result.status,0,result.stderr);
      assert.deepEqual(await waitJson(received),actualArgs);await removeReceived();
    });
    await t.test('explicit guard refusal cannot be bypassed by wrapper recovery',async()=>{
      await fs.writeFile(path.join(toolkit,'durable.mjs'),"console.error('application validation refused');process.exit(4);\n");
      const result=ps([...settings,'-NodePath',process.execPath]);assert.notEqual(result.status,0);
      assert.match(result.stderr,/guard_exit_4/);await assert.rejects(fs.access(received),{code:'ENOENT'});
    });
    await t.test('updater and unknown maintenance roles block fallback launch',async()=>{
      for(const kind of ['updater','unknown']){
        await fs.writeFile(probe,`param([string]$InstallRoot)\n'[{"ProcessId":123,"kind":"${kind}"}]'\n`);
        const result=ps([...settings,'-NodePath',missingNode]);assert.notEqual(result.status,0);assert.match(result.stderr,/application_update_in_progress/);
        await assert.rejects(fs.access(received),{code:'ENOENT'});
      }
      await fs.writeFile(probe,"param([string]$InstallRoot)\n'null'\n");
      const invalid=ps([...settings,'-NodePath',missingNode]);assert.notEqual(invalid.status,0);assert.match(invalid.stderr,/invalid_maintenance_state/);
      await assert.rejects(fs.access(received),{code:'ENOENT'});
      await fs.writeFile(probe,"param([string]$InstallRoot)\n'[]'\n");
    });
    await t.test('truncated packed and unpacked content block fallback launch',async()=>{
      const file=path.join(resources,'app.asar');await fs.writeFile(file,originals[0].subarray(0,-1));
      let result=ps([...settings,'-NodePath',missingNode]);assert.notEqual(result.status,0);assert.match(result.stderr,/incomplete_archive_entry/);
      await fs.writeFile(file,originals[0]);
      const unpacked=path.join(resources,'app.asar.unpacked/dist/external.js');await fs.writeFile(unpacked,'truncated');
      result=ps([...settings,'-NodePath',missingNode]);assert.notEqual(result.status,0);assert.match(result.stderr,/incomplete_unpacked_file/);
      await fs.writeFile(unpacked,originals[1]);await assert.rejects(fs.access(received),{code:'ENOENT'});
    });
    await t.test('a concurrent installation operation cannot be bypassed',async()=>{
      const identity=(resources+'\0.antigravity-zh-cn.lock').toLowerCase();
      const name='Local\\AntigravityZhCN-'+createHash('sha256').update(identity).digest('hex');
      const holder=spawn(powershell,['-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',path.join(root,'scripts/Hold-Mutex.ps1'),'-MutexName',name],{stdio:['pipe','pipe','pipe'],windowsHide:true});
      try{
        await new Promise((resolve,reject)=>{holder.stdout.once('data',bytes=>bytes.toString().includes('acquired')?resolve():reject(Error(bytes.toString())));holder.once('error',reject);});
        const result=ps([...settings,'-NodePath',missingNode]);assert.notEqual(result.status,0);assert.match(result.stderr,/launch_operation_busy/);
        await assert.rejects(fs.access(received),{code:'ENOENT'});
      }finally{const closed=new Promise(resolve=>holder.once('close',resolve));holder.stdin.end('release\n');await closed;}
    });
    await t.test('changed packaging supports unpacked main without the former preload',async()=>{
      const preload=path.join(source,'dist/preload.js');await fs.rm(preload);
      try{
        await createPackageWithOptions(source,path.join(resources,'app.asar'),{unpack:'**/main.js'});
        const result=ps([...settings,'-NodePath',missingNode]);assert.equal(result.status,0,result.stderr);
        assert.deepEqual(await waitJson(received),actualArgs);await removeReceived();
      }finally{
        await fs.writeFile(preload,'// original preload\n');await fs.writeFile(path.join(resources,'app.asar'),originals[0]);
      }
    });
    await t.test('a missing guard cannot silently launch the official account after Accounts was replaced',async()=>{
      const localAppData=path.join(temp,'isolated local app data'),accountsState=path.join(localAppData,'AntigravityAccounts');
      await fs.mkdir(accountsState,{recursive:true});
      const receiptPath=path.join(accountsState,'adapter-installation.json');
      const receipt={schemaVersion:1,phase:'applied',resourcesRoot:resources,packageRoot:toolkit,
        patchedAsarSha256:'1'.repeat(64),patchedNativeSha256:'2'.repeat(64)};
      await fs.writeFile(receiptPath,JSON.stringify(receipt));
      const env={...process.env,LOCALAPPDATA:localAppData};
      let result=ps([...settings,'-NodePath',missingNode],env);
      assert.notEqual(result.status,0);assert.match(result.stderr,/accounts_repair_required/);
      await assert.rejects(fs.access(received),{code:'ENOENT'});
      await fs.writeFile(receiptPath,JSON.stringify({...receipt,phase:'restored'}));
      result=ps([...settings,'-NodePath',missingNode],env);
      assert.equal(result.status,0,result.stderr);assert.deepEqual(await waitJson(received),actualArgs);
      await removeReceived();
    });
    await t.test('canonical Accounts config and persisted hint locate the same enabled installation',async()=>{
      const localAppData=path.join(temp,'legacy data'),legacyState=path.join(localAppData,'AntigravityAccounts');
      const canonicalState=path.join(temp,'LocalCache','Local','AntigravityAccounts');
      const configPath=path.join(resources,'hostauth-addon','config.json'),hintPath=path.join(state,'accounts-installation.json');
      const nativePath=path.join(resources,'bin','language_server.exe');
      for(const directory of [legacyState,canonicalState,path.dirname(configPath),path.dirname(nativePath)])await fs.mkdir(directory,{recursive:true});
      await fs.writeFile(nativePath,'synthetic native program, never executed');
      const receipt={schemaVersion:1,phase:'applied',resourcesRoot:resources,packageRoot:toolkit,
        patchedAsarSha256:hash(originals[0]),patchedNativeSha256:hash(await fs.readFile(nativePath))};
      await fs.writeFile(path.join(canonicalState,'adapter-installation.json'),JSON.stringify(receipt));
      // This separate legacy location would reject the launch if selected.
      await fs.writeFile(path.join(legacyState,'adapter-installation.json'),JSON.stringify({...receipt,patchedAsarSha256:'1'.repeat(64)}));
      const env={...process.env,LOCALAPPDATA:localAppData};
      const hint={schemaVersion:1,installRoot:install,accountsStateRoot:canonicalState};
      try {
        await fs.writeFile(configPath,JSON.stringify({accountsStateRoot:canonicalState}));
        let result=ps([...settings,'-NodePath',missingNode],env);
        assert.equal(result.status,0,result.stderr);assert.deepEqual(await waitJson(received),actualArgs);await removeReceived();
        await fs.writeFile(hintPath,JSON.stringify(hint));
        result=ps([...settings,'-NodePath',missingNode],env);
        assert.equal(result.status,0,result.stderr);assert.deepEqual(await waitJson(received),actualArgs);await removeReceived();
        // An updater can remove the addon config while the external hint survives.
        await fs.rm(configPath);
        result=ps([...settings,'-NodePath',missingNode],env);
        assert.equal(result.status,0,result.stderr);assert.deepEqual(await waitJson(received),actualArgs);await removeReceived();
        await fs.writeFile(path.join(canonicalState,'adapter-installation.json'),JSON.stringify({...receipt,patchedAsarSha256:'2'.repeat(64)}));
        result=ps([...settings,'-NodePath',missingNode],env);
        assert.notEqual(result.status,0);assert.match(result.stderr,/accounts_repair_required/);
        await assert.rejects(fs.access(received),{code:'ENOENT'});
      } finally {await fs.rm(configPath,{force:true});await fs.rm(hintPath,{force:true});}
    });
    await t.test('invalid or conflicting Accounts locations cannot fall back to a different account store',async()=>{
      const localAppData=path.join(temp,'location fallback data'),legacyState=path.join(localAppData,'AntigravityAccounts');
      const canonicalState=path.join(temp,'canonical accounts'),otherState=path.join(temp,'other accounts');
      const configPath=path.join(resources,'hostauth-addon','config.json'),hintPath=path.join(state,'accounts-installation.json');
      for(const directory of [legacyState,canonicalState,otherState,path.dirname(configPath)])await fs.mkdir(directory,{recursive:true});
      // A restored legacy receipt would otherwise permit the original App.
      await fs.writeFile(path.join(legacyState,'adapter-installation.json'),JSON.stringify({schemaVersion:1,phase:'restored',resourcesRoot:resources,packageRoot:toolkit}));
      const env={...process.env,LOCALAPPDATA:localAppData};
      const hint={schemaVersion:1,installRoot:install,accountsStateRoot:canonicalState};
      const rejected=async(pattern)=>{
        const result=ps([...settings,'-NodePath',missingNode],env);
        assert.notEqual(result.status,0);assert.match(result.stderr,pattern);
        await assert.rejects(fs.access(received),{code:'ENOENT'});
      };
      try {
        await fs.writeFile(configPath,JSON.stringify({accountsStateRoot:canonicalState}));
        await fs.writeFile(hintPath,JSON.stringify({...hint,accountsStateRoot:otherState}));
        await rejected(/accounts_recovery_location_conflict/);
        for(const badHint of [
          '{broken',JSON.stringify({...hint,schemaVersion:2}),JSON.stringify({...hint,installRoot:otherState}),
          JSON.stringify({...hint,accountsStateRoot:'relative/accounts'}),JSON.stringify({...hint,accountsStateRoot:resources}),
          JSON.stringify({...hint,accountsStateRoot:path.join(temp,'missing accounts')}),JSON.stringify({...hint,accountsStateRoot:null}),
        ]) {
          await fs.writeFile(hintPath,badHint);await rejected(/accounts_recovery_location_invalid/);
        }
        await fs.writeFile(hintPath,JSON.stringify(hint));
        for(const badConfig of ['{broken',JSON.stringify({accountsStateRoot:'C:relative'}),JSON.stringify({accountsStateRoot:resources})]) {
          await fs.writeFile(configPath,badConfig);await rejected(/accounts_recovery_location_invalid/);
        }
        await fs.writeFile(configPath,JSON.stringify({accountsStateRoot:canonicalState}));
        await rejected(/accounts_repair_required/); // Explicit valid directory, no receipt.
        const alias=path.join(temp,'installation alias');
        await fs.symlink(install,alias,'junction');
        const nested=path.join(install,'nested accounts');await fs.mkdir(nested);
        await fs.rm(hintPath);
        await fs.writeFile(configPath,JSON.stringify({accountsStateRoot:path.join(alias,'nested accounts')}));
        await rejected(/accounts_recovery_location_invalid/);
      } finally {await fs.rm(configPath,{force:true});await fs.rm(hintPath,{force:true});}
    });
    await t.test('localized content requires a matching receipt before stale localization can launch',async()=>{
      await fs.writeFile(path.join(source,'dist/preload.js'),'// __antigravityZhCN installed\n');
      await createPackageWithOptions(source,path.join(resources,'app.asar'),{unpack:'**/external.js'});
      let result=ps([...settings,'-NodePath',missingNode]);assert.notEqual(result.status,0);await assert.rejects(fs.access(received),{code:'ENOENT'});
      const record={schemaVersion:1,installRoot:install,appVersion:'2.99.0',patchedSha256:hash(await fs.readFile(path.join(resources,'app.asar'))),executableSha256:hash(await fs.readFile(path.join(install,'Antigravity.exe'))),unpacked:{'dist/external.js':hash(originals[1])}};
      await fs.writeFile(path.join(resources,'antigravity-zh-cn.install.json'),JSON.stringify(record));
      result=ps([...settings,'-NodePath',missingNode]);assert.equal(result.status,0,result.stderr);assert.deepEqual(await waitJson(received),actualArgs);
      assert.equal((await waitJson(path.join(state,'status.json'))).localization,'stale');await removeReceived();
    });
  }finally{await fs.rm(temp,{recursive:true,force:true,maxRetries:10,retryDelay:100});}
});
