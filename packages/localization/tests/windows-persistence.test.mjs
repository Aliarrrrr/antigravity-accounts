import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import {spawn,spawnSync} from 'node:child_process';
import {createHash} from 'node:crypto';
import {fileURLToPath} from 'node:url';

const root=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'..');
const scripts=path.join(root,'scripts');
const installer=path.join(scripts,'Install-Persistence.ps1');
const launcher=path.join(scripts,'Launch-Localized.ps1');
const windows=process.platform==='win32';
const powershell=path.join(process.env.SystemRoot||'C:\\Windows','System32','WindowsPowerShell','v1.0','powershell.exe');
const quote=value=>"'"+value.replaceAll("'","''")+"'";
function ps(args){
  const commandIndex=args.indexOf('-Command');
  if(commandIndex!==-1){
    const source=`. ${quote(path.join(scripts,'Unicode-Shortcut.ps1'))};[Console]::OutputEncoding=[Text.UTF8Encoding]::new();`+args[commandIndex+1];
    args=[...args.slice(0,commandIndex),'-EncodedCommand',Buffer.from(source,'utf16le').toString('base64')];
  }
  return spawnSync(powershell,['-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass',...args],{encoding:'utf8',timeout:30000});
}
async function temporary(fn){
  const temp=await fs.realpath(await fs.mkdtemp(path.join(os.tmpdir(),'agy persistence ')));
  try{return await fn(temp);}finally{await fs.rm(temp,{recursive:true,force:true,maxRetries:10,retryDelay:100});}
}
async function fixture(temp,{unrelated=false}={}){
  const install=path.join(temp,'app installation'),toolkit=path.join(temp,'tool 工具 kit'),state=path.join(temp,'guard 中文 state');
  const desktop=path.join(temp,'Desktop'),menu=path.join(temp,'Start Menu'),startup=path.join(temp,'Startup');
  for(const dir of [install,path.join(toolkit,'scripts'),state,desktop,menu,startup])await fs.mkdir(dir,{recursive:true});
  await fs.writeFile(path.join(install,'Antigravity.exe'),'fixture never executed');
  await fs.writeFile(path.join(toolkit,'durable.mjs'),"import fs from 'node:fs';import path from 'node:path';const a=process.argv.slice(2);const i=a.indexOf('--state-root');fs.mkdirSync(a[i+1],{recursive:true});fs.writeFileSync(path.join(a[i+1],'launch-call.json'),JSON.stringify(a));if(process.env.AGY_TEST_STATUS)fs.writeFileSync(path.join(a[i+1],'status.json'),JSON.stringify({status:process.env.AGY_TEST_STATUS}));process.exit(Number(process.env.AGY_TEST_EXIT||0));");
  for(const name of ['Launch-Localized.ps1','Launch-Localized.vbs','Watch-Localized.vbs']){
    try{await fs.copyFile(path.join(scripts,name),path.join(toolkit,'scripts',name));}catch(error){if(error.code!=='ENOENT')throw error;await fs.writeFile(path.join(toolkit,'scripts',name),'implementation pending');}
  }
  const shortcuts=[path.join(desktop,'Antigravity.lnk'),path.join(menu,'Antigravity.lnk'),path.join(menu,'Antigravity（简体中文）.lnk')];
  const create=ps(['-Command',shortcuts.map((file,index)=>`$l=New-UnicodeShortcut -Path ${quote(file)};$l.TargetPath=${quote(unrelated&&index===2?process.execPath:path.join(install,'Antigravity.exe'))};$l.Arguments='';$l.WorkingDirectory=${quote(install)};$l.Save();`).join('')]);
  assert.equal(create.status,0,create.stderr);
  const originals=await Promise.all(shortcuts.map(file=>fs.readFile(file)));
  const args=['-InstallRoot',install,'-ToolkitRoot',toolkit,'-NodePath',process.execPath,'-StateRoot',state,'-DesktopDirectory',desktop,'-StartMenuDirectory',menu,'-StartupDirectory',startup];
  return {install,toolkit,state,desktop,menu,startup,shortcuts,originals,args};
}
function shortcutInfo(files){
  const result=ps(['-Command',"$r=@("+files.map(file=>`$l=New-UnicodeShortcut -Path ${quote(file)};[pscustomobject]@{target=$l.TargetPath;arguments=$l.Arguments}`).join(';')+");ConvertTo-Json -InputObject $r -Compress"]);
  assert.equal(result.status,0,result.stderr);return JSON.parse(result.stdout.trim());
}

test('Unicode shortcut filenames and arguments survive characters outside an ANSI code page',{skip:!windows},async()=>temporary(async temp=>{
  const directory=path.join(temp,'folder \u{1f9ed} 中文');await fs.mkdir(directory);
  const filename=path.join(directory,'Antigravity \u{1f680}（简体中文）.lnk'),argumentsText='--label "\u{1f9ed} 中文"';
  const created=ps(['-Command',`$l=New-UnicodeShortcut -Path ${quote(filename)};$l.TargetPath=${quote(process.execPath)};$l.Arguments=${quote(argumentsText)};$l.WorkingDirectory=${quote(directory)};$l.Save();`]);
  assert.equal(created.status,0,created.stderr);assert.ok((await fs.stat(filename)).size>0);
  const info=shortcutInfo([filename])[0];assert.equal(info.target,process.execPath);assert.equal(info.arguments,argumentsText);
}));

test('Windows installer guards all launch shortcuts and preserves immutable originals across reinstall',{skip:!windows},async()=>temporary(async temp=>{
  const f=await fixture(temp);
  const installed=ps(['-File',installer,...f.args]);assert.equal(installed.status,0,installed.stderr);
  const info=shortcutInfo(f.shortcuts);for(const item of info){assert.equal(path.basename(item.target).toLowerCase(),'wscript.exe');assert.ok(item.arguments.includes(path.join(f.toolkit,'scripts','Launch-Localized.vbs')));assert.ok(item.arguments.includes(f.install));assert.ok(item.arguments.includes(f.state));}
  const manifestFile=path.join(f.state,'windows-persistence','install.json');
  const manifest=JSON.parse(await fs.readFile(manifestFile,'utf8'));
  assert.equal(manifest.shortcuts.length,3);
  for(let i=0;i<3;i++)assert.deepEqual(await fs.readFile(manifest.shortcuts[i].backupPath),f.originals[i]);
  const hook=path.join(f.startup,'AntigravityZhCN-Watcher.vbs');const hookBefore=await fs.readFile(hook);
  assert.ok(hookBefore.toString('utf16le').includes(path.join(f.toolkit,'scripts','Watch-Localized.vbs')));
  const reinstalled=ps(['-File',installer,...f.args]);assert.equal(reinstalled.status,0,reinstalled.stderr);
  for(let i=0;i<3;i++)assert.deepEqual(await fs.readFile(manifest.shortcuts[i].backupPath),f.originals[i]);
  assert.deepEqual(await fs.readFile(hook),hookBefore);
  // An official update can recreate the direct shortcut; retain the first backup.
  await fs.writeFile(f.shortcuts[0],f.originals[0]);
  const repaired=ps(['-File',installer,...f.args]);assert.equal(repaired.status,0,repaired.stderr);
  assert.equal(path.basename(shortcutInfo([f.shortcuts[0]])[0].target).toLowerCase(),'wscript.exe');
  assert.deepEqual(await fs.readFile(manifest.shortcuts[0].backupPath),f.originals[0]);
}));

test('Windows uninstall restores exact shortcut bytes and removes only the owned startup hook',{skip:!windows},async()=>temporary(async temp=>{
  const f=await fixture(temp);const installed=ps(['-File',installer,...f.args]);assert.equal(installed.status,0,installed.stderr);
  const unrelated=path.join(f.startup,'Unrelated.vbs');await fs.writeFile(unrelated,'unrelated startup item');
  const removed=ps(['-File',installer,...f.args,'-Uninstall']);assert.equal(removed.status,0,removed.stderr);
  for(let i=0;i<3;i++)assert.deepEqual(await fs.readFile(f.shortcuts[i]),f.originals[i]);
  await assert.rejects(fs.stat(path.join(f.startup,'AntigravityZhCN-Watcher.vbs')),{code:'ENOENT'});
  assert.equal(await fs.readFile(unrelated,'utf8'),'unrelated startup item');
  await fs.stat(path.join(f.state,'windows-persistence','disabled'));
  const again=ps(['-File',installer,...f.args,'-Uninstall']);assert.equal(again.status,0,again.stderr);
}));

test('Uninstall restores original shortcut bytes after an official updater recreates a direct shortcut',{skip:!windows},async()=>temporary(async temp=>{
  const f=await fixture(temp);const installed=ps(['-File',installer,...f.args]);assert.equal(installed.status,0,installed.stderr);
  await fs.unlink(f.shortcuts[0]);
  const recreated=ps(['-Command',`$l=New-UnicodeShortcut -Path ${quote(f.shortcuts[0])};$l.TargetPath=${quote(path.join(f.install,'Antigravity.exe'))};$l.Arguments='';$l.Description='Recreated by official updater';$l.WorkingDirectory=${quote(f.toolkit)};$l.Save();`]);
  assert.equal(recreated.status,0,recreated.stderr);assert.notDeepEqual(await fs.readFile(f.shortcuts[0]),f.originals[0]);
  const removed=ps(['-File',installer,...f.args,'-Uninstall']);assert.equal(removed.status,0,removed.stderr);
  for(let i=0;i<3;i++)assert.deepEqual(await fs.readFile(f.shortcuts[i]),f.originals[i]);
  await fs.stat(path.join(f.state,'windows-persistence','disabled'));
  assert.equal(JSON.parse(await fs.readFile(path.join(f.state,'windows-persistence','install.json'),'utf8')).installed,false);
  await assert.rejects(fs.stat(path.join(f.startup,'AntigravityZhCN-Watcher.vbs')),{code:'ENOENT'});
}));

test('Uninstall still refuses an unknown replacement shortcut and leaves integration unchanged',{skip:!windows},async()=>temporary(async temp=>{
  const f=await fixture(temp);const installed=ps(['-File',installer,...f.args]);assert.equal(installed.status,0,installed.stderr);
  const replaced=ps(['-Command',`$l=New-UnicodeShortcut -Path ${quote(f.shortcuts[0])};$l.TargetPath=${quote(process.execPath)};$l.Arguments='';$l.Description='Unrelated application';$l.Save();`]);
  assert.equal(replaced.status,0,replaced.stderr);const before=await fs.readFile(f.shortcuts[0]);
  const removed=ps(['-File',installer,...f.args,'-Uninstall']);assert.notEqual(removed.status,0);assert.match(removed.stderr,/shortcut_not_owned/);
  assert.deepEqual(await fs.readFile(f.shortcuts[0]),before);
  await assert.rejects(fs.stat(path.join(f.state,'windows-persistence','disabled')),{code:'ENOENT'});
  await fs.stat(path.join(f.startup,'AntigravityZhCN-Watcher.vbs'));
}));

test('A watchdog reconciliation after uninstall preserves disabled integration and allows explicit reinstall',{skip:!windows},async()=>temporary(async temp=>{
  const f=await fixture(temp);assert.equal(ps(['-File',installer,...f.args]).status,0);
  const removed=ps(['-File',installer,...f.args,'-Uninstall']);assert.equal(removed.status,0,removed.stderr);
  const disabled=path.join(f.state,'windows-persistence','disabled'),manifest=path.join(f.state,'windows-persistence','install.json');
  const marker=await fs.readFile(disabled),markerTime=(await fs.stat(disabled)).mtimeMs,manifestBytes=await fs.readFile(manifest);
  const result=ps(['-File',installer,...f.args,'-Reconcile','-StartWatcher']);assert.equal(result.status,0,result.stderr);
  assert.deepEqual(JSON.parse(result.stdout.trim()),{installed:false,disabled:true});
  assert.deepEqual(await fs.readFile(disabled),marker);assert.equal((await fs.stat(disabled)).mtimeMs,markerTime);
  assert.deepEqual(await fs.readFile(manifest),manifestBytes);
  for(let i=0;i<3;i++)assert.deepEqual(await fs.readFile(f.shortcuts[i]),f.originals[i]);
  await assert.rejects(fs.stat(path.join(f.startup,'AntigravityZhCN-Watcher.vbs')),{code:'ENOENT'});
  // An absent marker must still not authorize a watcher to reinstall an uninstalled manifest.
  await fs.unlink(disabled);
  assert.equal(ps(['-File',installer,...f.args,'-Reconcile']).status,0);
  assert.deepEqual(await fs.readFile(manifest),manifestBytes);
  await assert.rejects(fs.stat(path.join(f.startup,'AntigravityZhCN-Watcher.vbs')),{code:'ENOENT'});
  const enabled=ps(['-File',installer,...f.args]);assert.equal(enabled.status,0,enabled.stderr);
  assert.equal(path.basename(shortcutInfo([f.shortcuts[0]])[0].target).toLowerCase(),'wscript.exe');
}));

test('Reconciliation waits for the interprocess integration mutex before checking disabled state',{skip:!windows},async()=>temporary(async temp=>{
  const f=await fixture(temp);assert.equal(ps(['-File',installer,...f.args]).status,0);assert.equal(ps(['-File',installer,...f.args,'-Uninstall']).status,0);
  const identity=path.resolve(f.state).replace(/[\\/]+$/,'').toLowerCase();
  const mutexName='Local\\AntigravityZhCN-Persistence-'+createHash('sha256').update(identity,'utf8').digest('hex').toUpperCase();
  const holdCode=`$m=[Threading.Mutex]::new($false,${quote(mutexName)});$held=$false;try{$held=$m.WaitOne(10000);if(-not $held){throw 'fixture_mutex_timeout'};[Console]::Out.WriteLine('held');[Console]::Out.Flush();[void][Console]::In.ReadLine()}finally{if($held){$m.ReleaseMutex()};$m.Dispose()}`;
  const holder=spawn(powershell,['-NoProfile','-NonInteractive','-Command',holdCode],{stdio:['pipe','pipe','pipe'],windowsHide:true});
  const holderDone=new Promise(resolve=>holder.once('close',resolve));
  const acquired=new Promise((resolve,reject)=>{let text='';holder.stdout.on('data',chunk=>{text+=chunk;if(text.includes('held'))resolve();});holder.once('error',reject);holder.once('close',()=>reject(new Error('fixture mutex holder exited before acquisition')));});
  let reconcile;
  try {
    await acquired;
    reconcile=spawn(powershell,['-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',installer,...f.args,'-Reconcile'],{stdio:['ignore','pipe','pipe'],windowsHide:true});
    let out='',err='';reconcile.stdout.on('data',chunk=>{out+=chunk;});reconcile.stderr.on('data',chunk=>{err+=chunk;});
    const done=new Promise(resolve=>reconcile.once('close',resolve));
    await new Promise(resolve=>setTimeout(resolve,1500));assert.equal(reconcile.exitCode,null,'reconciliation bypassed the held integration mutex');
    holder.stdin.end('\n');await holderDone;
    assert.equal(await done,0,err);assert.deepEqual(JSON.parse(out.trim()),{installed:false,disabled:true});
  } finally {
    if(holder.exitCode===null){holder.stdin.end('\n');await holderDone;}
    if(reconcile?.exitCode===null){reconcile.kill();await new Promise(resolve=>reconcile.once('close',resolve));}
  }
}));

test('Windows install refuses an unrelated launch shortcut before modifying any integration',{skip:!windows},async()=>temporary(async temp=>{
  const f=await fixture(temp,{unrelated:true});const installed=ps(['-File',installer,...f.args]);assert.notEqual(installed.status,0);
  assert.match(installed.stderr,/unrelated_shortcut_target/);
  for(let i=0;i<3;i++)assert.deepEqual(await fs.readFile(f.shortcuts[i]),f.originals[i]);
  assert.deepEqual(await fs.readdir(f.startup),[]);
}));

test('Windows uninstall refuses a startup hook replaced by another owner',{skip:!windows},async()=>temporary(async temp=>{
  const f=await fixture(temp);const installed=ps(['-File',installer,...f.args]);assert.equal(installed.status,0,installed.stderr);
  const hook=path.join(f.startup,'AntigravityZhCN-Watcher.vbs');await fs.writeFile(hook,'another owner');
  const removed=ps(['-File',installer,...f.args,'-Uninstall']);assert.notEqual(removed.status,0);assert.match(removed.stderr,/startup_hook_not_owned/);
  assert.equal(await fs.readFile(hook,'utf8'),'another owner');
  assert.equal(path.basename(shortcutInfo([f.shortcuts[0]])[0].target).toLowerCase(),'wscript.exe');
}));

test('Windows install and uninstall refuse a changed immutable shortcut backup',{skip:!windows},async()=>temporary(async temp=>{
  const f=await fixture(temp);const installed=ps(['-File',installer,...f.args]);assert.equal(installed.status,0,installed.stderr);
  const guarded=await fs.readFile(f.shortcuts[0]);
  await fs.writeFile(path.join(f.state,'windows-persistence','shortcut-0.original.lnk'),'changed backup');
  for(const extra of [[],['-Uninstall']]){
    const result=ps(['-File',installer,...f.args,...extra]);assert.notEqual(result.status,0);assert.match(result.stderr,/original_shortcut_backup_changed/);
  }
  assert.deepEqual(await fs.readFile(f.shortcuts[0]),guarded);
}));

test('PowerShell launch forwards spaced paths and literal launch arguments to the guard',{skip:!windows},async()=>temporary(async temp=>{
  const f=await fixture(temp);const launchArgs=['--new-window','a b','中文','literal $value','quote"inside','amp&pipe|'];
  const encoded=Buffer.from(JSON.stringify(launchArgs),'utf8').toString('base64');
  const launched=ps(['-File',launcher,'-InstallRoot',f.install,'-ToolkitRoot',f.toolkit,'-NodePath',process.execPath,'-StateRoot',f.state,'-ArgumentsJsonBase64',encoded,'-NoPopup']);
  assert.equal(launched.status,0,launched.stderr);
  const call=JSON.parse(await fs.readFile(path.join(f.state,'launch-call.json'),'utf8'));
  assert.deepEqual(call.slice(0,5),['launch','--install-root',f.install,'--state-root',f.state]);
  assert.equal(call[5],'--args-base64');assert.deepEqual(JSON.parse(Buffer.from(call[6],'base64').toString('utf8')),launchArgs);
}));

test('PowerShell launch reports guard failure with an actionable status path',{skip:!windows},async()=>temporary(async temp=>{
  const f=await fixture(temp);const old=process.env.AGY_TEST_EXIT;process.env.AGY_TEST_EXIT='7';
  let launched;try{launched=ps(['-File',launcher,'-InstallRoot',f.install,'-ToolkitRoot',f.toolkit,'-NodePath',process.execPath,'-StateRoot',f.state,'-NoPopup']);}finally{if(old===undefined)delete process.env.AGY_TEST_EXIT;else process.env.AGY_TEST_EXIT=old;}
  assert.notEqual(launched.status,0);assert.ok(launched.stderr.includes(path.join(f.state,'status.json')));assert.match(launched.stderr,/7/);assert.match(launched.stderr,/无法完成汉化/);assert.match(launched.stderr,/不兼容/);
}));

test('PowerShell launch explains an idle repair wait in Chinese without showing launch data',{skip:!windows},async()=>temporary(async temp=>{
  const f=await fixture(temp),oldExit=process.env.AGY_TEST_EXIT,oldStatus=process.env.AGY_TEST_STATUS;
  process.env.AGY_TEST_EXIT='1';process.env.AGY_TEST_STATUS='pending_idle';
  let launched;try{launched=ps(['-File',launcher,'-InstallRoot',f.install,'-ToolkitRoot',f.toolkit,'-NodePath',process.execPath,'-StateRoot',f.state,'-NoPopup']);}finally{
    for(const [key,value] of [['AGY_TEST_EXIT',oldExit],['AGY_TEST_STATUS',oldStatus]]){if(value===undefined)delete process.env[key];else process.env[key]=value;}
  }
  assert.notEqual(launched.status,0);assert.match(launched.stderr,/正常退出全部 Antigravity 窗口后重新打开/);
  assert.match(launched.stderr,/系统托盘菜单选择“退出”/);
}));

test('VBScript launch carries Unicode and escaped arguments through the hidden PowerShell guard',{skip:!windows},async()=>temporary(async temp=>{
  const f=await fixture(temp);const args=['--new-window','a b','中文','quote"inside','slash\\end','line\nfeed'];
  const cscript=path.join(process.env.SystemRoot,'System32','cscript.exe');
  // Use the explicit Base64 form because WScript.Arguments removes literal quotes.
  const encoded=Buffer.from(JSON.stringify(args),'utf8').toString('base64');
  const result=spawnSync(cscript,['//Nologo',path.join(f.toolkit,'scripts','Launch-Localized.vbs'),process.execPath,f.toolkit,f.install,f.state,'--args-base64',encoded],{encoding:'utf8',timeout:30000});
  assert.equal(result.status,0,result.stderr||result.stdout);
  const call=JSON.parse(await fs.readFile(path.join(f.state,'launch-call.json'),'utf8'));
  assert.equal(call[0],'launch');assert.equal(call[5],'--args-base64');
  assert.deepEqual(JSON.parse(Buffer.from(call[6],'base64').toString('utf8')),args);
}));

test('VBScript supports the normal shortcut with no extra launch arguments',{skip:!windows},async()=>temporary(async temp=>{
  const f=await fixture(temp);const cscript=path.join(process.env.SystemRoot,'System32','cscript.exe');
  const result=spawnSync(cscript,['//Nologo',path.join(f.toolkit,'scripts','Launch-Localized.vbs'),process.execPath,f.toolkit,f.install,f.state],{encoding:'utf8',timeout:10000});
  assert.equal(result.status,0,result.stderr||result.stdout);
  const call=JSON.parse(await fs.readFile(path.join(f.state,'launch-call.json'),'utf8'));
  assert.deepEqual(JSON.parse(Buffer.from(call[6],'base64').toString('utf8')),[]);
}));

test('The installed Startup VBScript actually dispatches the external update watcher',{skip:!windows},async()=>temporary(async temp=>{
  const f=await fixture(temp);const installed=ps(['-File',installer,...f.args]);assert.equal(installed.status,0,installed.stderr);
  const cscript=path.join(process.env.SystemRoot,'System32','cscript.exe');
  const result=spawnSync(cscript,['//Nologo',path.join(f.startup,'AntigravityZhCN-Watcher.vbs')],{encoding:'utf8',timeout:10000});
  assert.equal(result.status,0,result.stderr||result.stdout);
  let call;
  for(let i=0;i<100;i++){
    try{call=JSON.parse(await fs.readFile(path.join(f.state,'launch-call.json'),'utf8'));break;}catch(error){if(error.code!=='ENOENT')throw error;await new Promise(resolve=>setTimeout(resolve,50));}
  }
  assert.deepEqual(call,['watch','--install-root',f.install,'--state-root',f.state]);
}));
