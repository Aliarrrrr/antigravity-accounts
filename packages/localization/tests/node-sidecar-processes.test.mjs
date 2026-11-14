import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import {spawnSync} from 'node:child_process';
import {fileURLToPath,pathToFileURL} from 'node:url';

const windows=process.platform==='win32';
const scripts=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'../scripts');
const powershell=path.join(process.env.SystemRoot||'C:\\Windows','System32','WindowsPowerShell','v1.0','powershell.exe');

test('process checks exempt only the verified external Electron Node sidecar',{skip:!windows},async t=>{
  const prefix=path.join(os.tmpdir(),'agy-node-sidecar-');
  const temporaryRoot=await fs.mkdtemp(prefix);
  const knownRoot=await fs.realpath(temporaryRoot);
  t.after(async()=>{
    const resolved=await fs.realpath(temporaryRoot);
    assert.equal(resolved,knownRoot,'temporary directory changed before cleanup');
    assert.equal(path.dirname(resolved),path.resolve(os.tmpdir()));
    assert.ok(path.basename(resolved).startsWith('agy-node-sidecar-'));
    await fs.rm(resolved,{recursive:true,force:true});
  });
  const install=path.join(knownRoot,'installation');
  const roaming=path.join(knownRoot,'roaming');
  const profile=path.join(knownRoot,'user');
  const local=path.join(knownRoot,'local');
  const shim=path.join(roaming,'Antigravity','bin','agy-node.cmd');
  const loader=path.join(profile,'.gemini','antigravity','lib','node_modules','sidecar_sdk','register_loader.js');
  const executable=path.join(install,'Antigravity.exe');
  const cmd=path.join(process.env.SystemRoot||'C:\\Windows','System32','cmd.exe');
  const argumentsText='--import='+pathToFileURL(loader).href+' dist/server.cjs';
  const shimText='@echo off\r\nset ELECTRON_RUN_AS_NODE=1\r\n"'+executable+'" %*\r\n';
  for(const directory of [install,path.dirname(shim),path.dirname(loader),local]) await fs.mkdir(directory,{recursive:true});
  await fs.writeFile(executable,'Never executed by this test.');
  await fs.writeFile(loader,'// Never imported by this test.');
  const record=(ProcessId,ExecutablePath,CommandLine='',ParentProcessId=0)=>({ProcessId,ParentProcessId,Name:path.basename(ExecutablePath),ExecutablePath,CommandLine});
  const sidecar=(pid=101,parentId=201)=>record(pid,executable,'"'+executable+'" '+argumentsText,parentId);
  const parent=(pid=201)=>record(pid,cmd,'"'+cmd+'" /c '+shim+' '+argumentsText);
  const gui=record(301,executable,'"'+executable+'"');
  const renderer=record(302,executable,'"'+executable+'" --type=renderer',301);
  const cases=[];
  const scenario=(name,processes,want,options={})=>cases.push({name,processes,shimText:options.shimText??shimText,wantProcess:options.wantProcess??want,wantMaintenance:want,wantKinds:options.wantKinds});
  scenario('one verified sidecar',[parent(),sidecar()],[]);
  scenario('Windows executable casing may change when App rewrites wrapper',[parent(),sidecar()],[],{shimText:shimText.replace(executable,executable.toLowerCase())});
  scenario('wrapper commands retain exact content despite path casing tolerance',[parent(),sidecar()],[101],{shimText:shimText.replace('ELECTRON_RUN_AS_NODE=1','ELECTRON_RUN_AS_NODE=0')});
  const wrappedParent={...parent(),CommandLine:cmd+' /c ""'+shim+'" '+argumentsText+'"'};
  scenario('cmd outer quote pair around known invocation',[wrappedParent,sidecar()],[]);
  scenario('wrapped invocation rejects trailing commands',[{...wrappedParent,CommandLine:wrappedParent.CommandLine+' & echo extra'},sidecar()],[101]);
  scenario('wrapped invocation requires matching closing quote',[{...wrappedParent,CommandLine:wrappedParent.CommandLine.slice(0,-1)},sidecar()],[101]);
  scenario('wrapped invocation rejects another quote pair',[{...wrappedParent,CommandLine:cmd+' /c """'+shim+'" '+argumentsText+'""'},sidecar()],[101]);
  scenario('two verified sidecars',[parent(),sidecar(),parent(202),sidecar(102,202)],[]);
  scenario('GUI remains beside two sidecars',[parent(),sidecar(),parent(202),sidecar(102,202),gui],[301]);
  scenario('desktop main and renderer remain busy',[gui,renderer],[301,302]);
  scenario('renderer arguments never count as a sidecar',[parent(),{...sidecar(),CommandLine:'"'+executable+'" '+argumentsText+' --type=renderer'}],[101]);
  scenario('same child command without its parent',[sidecar()],[101]);
  scenario('missing parent command',[{...parent(),CommandLine:null},sidecar()],[101]);
  scenario('parent named cmd at another path',[{...parent(),ExecutablePath:path.join(knownRoot,'cmd.exe')},sidecar()],[101]);
  scenario('wrapper must set Node mode',[parent(),sidecar()],[101],{shimText:shimText.replace('set ELECTRON_RUN_AS_NODE=1\r\n','')});
  scenario('wrapper must call the same executable',[parent(),sidecar()],[101],{shimText:shimText.replace(executable,path.join(knownRoot,'other','Antigravity.exe'))});
  scenario('extra wrapper commands are rejected',[parent(),sidecar()],[101],{shimText:shimText+'echo extra command\r\n'});
  scenario('parent and child arguments must match',[{...parent(),CommandLine:parent().CommandLine.replace('dist/server.cjs','dist/other.cjs')},sidecar()],[101]);
  scenario('child entry point must match',[parent(),{...sidecar(),CommandLine:sidecar().CommandLine.replace('dist/server.cjs','dist/other.cjs')}],[101]);
  scenario('import must come from the known external sidecar',[parent(),{...sidecar(),CommandLine:sidecar().CommandLine.replace('sidecar_sdk','other_sdk')}],[101]);
  scenario('parent must invoke the known wrapper',[{...parent(),CommandLine:parent().CommandLine.replace('agy-node.cmd','other-node.cmd')},sidecar()],[101]);
  const updater=record(401,path.join(local,'antigravity-updater','setup.exe'));
  const tool=record(402,path.join(install,'resources','helper.exe'));
  scenario('updater and installation tool remain busy',[parent(),sidecar(),updater,tool],[401,402],{wantProcess:[402]});
  const namedInstaller=record(403,path.join(knownRoot,'download','AntigravitySetup.exe'));
  scenario('named Antigravity installer remains busy',[parent(),sidecar(),namedInstaller],[403],{wantProcess:[]});
  const languageServer=record(501,path.join(install,'resources','bin','language_server.exe'),'',301);
  const languageWorker=record(502,path.join(install,'resources','bin','new-runtime-worker.exe'),'',501);
  scenario('GUI language server and its runtime child permit forwarding while orphan helpers remain maintenance',[gui,languageServer,languageWorker,tool],[301,402,501,502],{wantKinds:{301:'application',501:'application',502:'application',402:'updater'}});
  scenario('known language server remains application work after GUI exits',[{...languageServer,ParentProcessId:0}],[501],{wantKinds:{501:'application'}});
  scenario('older language server name remains application work',[record(503,path.join(install,'resources','bin','language_server_windows_x64.exe'),'',301),gui],[301,503],{wantKinds:{301:'application',503:'application'}});
  scenario('updater cache process never becomes application through GUI parent',[gui,{...updater,ParentProcessId:301}],[301,401],{wantProcess:[301],wantKinds:{301:'application',401:'updater'}});
  scenario('installation updater helper never becomes application through GUI parent',[gui,record(504,path.join(install,'resources','elevate.exe'),'',301)],[301,504],{wantKinds:{301:'application',504:'updater'}});

  const dataset=path.join(knownRoot,'cases.json');
  const harness=path.join(knownRoot,'check-processes.ps1');
  await fs.writeFile(dataset,JSON.stringify(cases));
  await fs.writeFile(harness,`
param([string]$Dataset,[string]$Scripts,[string]$InstallRoot,[string]$Shim)
$ErrorActionPreference='Stop'
[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
function Get-CimInstance {
  [CmdletBinding()]
  param([string]$ClassName,[string]$Filter)
  if($ClassName -ne 'Win32_Process'){throw 'unexpected_mock_cim_class'}
  if(-not $Filter){return $global:agyTestProcesses}
  $match=[regex]::Match($Filter,'(?i)^\\s*(ProcessId|ParentProcessId)\\s*=\\s*(\\d+)\\s*$')
  if(-not $match.Success){throw 'unexpected_mock_cim_filter'}
  $property=$match.Groups[1].Value
  $number=[int]$match.Groups[2].Value
  return $global:agyTestProcesses | Where-Object {$_.$property -eq $number}
}
$cases=Get-Content -LiteralPath $Dataset -Raw -Encoding UTF8 | ConvertFrom-Json
$results=@(foreach($case in $cases){
  $global:agyTestProcesses=@($case.processes)
  [IO.File]::WriteAllText($Shim,$case.shimText,[Text.UTF8Encoding]::new($false))
  foreach($name in @('Process-State.ps1','Maintenance-State.ps1')){
    $raw=& (Join-Path $Scripts $name) -InstallRoot $InstallRoot
    $items=ConvertFrom-Json -InputObject ($raw -join "\u0060n")
    $ids=@($items | ForEach-Object {[int]$_.ProcessId} | Sort-Object)
    $kinds=@{}
    foreach($item in $items){if($item.kind){$kinds[[string]$item.ProcessId]=$item.kind}}
    [pscustomobject]@{name=$case.name;script=$name;ids=$ids;kinds=$kinds}
  }
})
ConvertTo-Json -InputObject $results -Depth 5 -Compress
`);
  const run=spawnSync(powershell,['-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',harness,'-Dataset',dataset,'-Scripts',scripts,'-InstallRoot',install,'-Shim',shim],{
    encoding:'utf8',timeout:30000,windowsHide:true,
    env:{...process.env,APPDATA:roaming,USERPROFILE:profile,LOCALAPPDATA:local}
  });
  assert.equal(run.status,0,run.stderr||run.error?.message||run.stdout);
  const results=JSON.parse(run.stdout.trim());
  assert.equal(results.length,cases.length*2);
  for(const item of cases) {
    await t.test(item.name,()=>{
      for(const [script,want] of [['Process-State.ps1',item.wantProcess],['Maintenance-State.ps1',item.wantMaintenance]]) {
        const result=results.find(entry=>entry.name===item.name&&entry.script===script);
        assert.ok(result,'missing '+script+' result');
        assert.deepEqual(result.ids,[...want].sort((a,b)=>a-b),script+' returned the wrong blocking processes');
        if(script==='Maintenance-State.ps1' && item.wantKinds) assert.deepEqual(result.kinds,item.wantKinds,'maintenance roles would block a live application or permit its updater');
      }
    });
  }
});
